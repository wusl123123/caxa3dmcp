// Agent 分层工具集（三）：TriBall 操作 + 特征层 + 查询反馈层。
// 取证：2027 TLB members.csv —— IZPartFeatureMgr.CreateExtrudeFeature(11)/CreateSpinFeature(17)/
//       CreateSweepFeature(16)/CreateShellFeature(12)/GetFeaturesCount(7)；IZSceneDoc.CreateProfile(22)；
//       IZProfile.CreateLine(29)/CreateCircle(26)/CreateArc(28)/GetCurvesCount(17)；
//       enums.csv eZOperationType Z_UNITE=1 / Z_SUBTRACT=2、eZFeatureProfileRelType Z_FEATURE_PROFILE_COPY=1。
// ★ revolve / sweep / shell 只做能力协商（无实机证据、不改模型、不假成功）。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    // ---------------- TriBall 操作 ----------------

    [ComVisible(false)]
    internal sealed class ToolSetTriball : McpToolBase
    {
        public override string Name { get { return "set_triball"; } }
        public override string Category { get { return "triball"; } }
        public override string Description { get { return "把元素定位/旋转（PositionTransform 矩阵 SetTranslation + 逐轴 SetRotation）。判据 = 写入后 GetTranslation 回读比对。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["shape_id"] = S.Str("元素名 / id"), ["name"] = S.Str("元素名"), ["id"] = S.Num("元素 Id"),
                ["position"] = S.Array("[x,y,z] mm，绝对位置", new Dictionary<string, object>()),
                ["rotation"] = S.Array("[rx,ry,rz] 度，按 X/Y/Z 轴依次施加", new Dictionary<string, object>()),
                ["relative"] = S.Bool("true 时在当前变换上叠加，默认 false（绝对）", false, false, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            if (ctx.Scene == null) return AgentKit.Fail("no_scene", AgentKit.NeedSceneMessage);
            if (!args.Has("position") && !args.Has("rotation"))
                return AgentKit.Fail("missing_transform", "请提供 position 或 rotation");
            string err;
            IZElement e = AgentKit.FindElement(ctx, args, out err);
            if (e == null) return AgentKit.Fail("element_not_found", err);
            double[] pos = args.Has("position") ? AgentKit.Vec3(args, "position", null) : null;
            double[] rot = args.Has("rotation") ? AgentKit.Vec3(args, "rotation", null) : null;
            string terr;
            object r = TransformKit.Apply(e, pos, rot, args.ParamBool("relative", false), out terr);
            if (r == null) return AgentKit.Fail("transform_failed", terr);
            var d = r as Dictionary<string, object>;
            if (d != null) { d["layer"] = "triball"; d["element"] = AgentKit.SafeName(e); }
            return r;
        }
    }

    [ComVisible(false)]
    internal sealed class ToolPositionByTriball : McpToolBase
    {
        public override string Name { get { return "position_by_triball"; } }
        public override string Category { get { return "triball"; } }
        public override string Description { get { return "按 TriBall 等价变换仅平移元素（支持 relative 相对位移），写入后回读 translation 校验。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["shape_id"] = S.Str("元素名 / id"), ["name"] = S.Str("元素名"), ["id"] = S.Num("元素 Id"),
                ["position"] = S.Array("[x,y,z] mm", new Dictionary<string, object>()),
                ["relative"] = S.Bool("true = 相对位移；默认 false = 绝对位置", false, false, true)
            }, "position");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            if (ctx.Scene == null) return AgentKit.Fail("no_scene", AgentKit.NeedSceneMessage);
            string err;
            IZElement e = AgentKit.FindElement(ctx, args, out err);
            if (e == null) return AgentKit.Fail("element_not_found", err);
            double[] pos = AgentKit.Vec3(args, "position", null);
            if (pos == null) return AgentKit.Fail("missing_position", "position 必须是 [x,y,z] 数组");
            string terr;
            object r = TransformKit.Apply(e, pos, null, args.ParamBool("relative", false), out terr);
            if (r == null) return AgentKit.Fail("transform_failed", terr);
            var d = r as Dictionary<string, object>;
            if (d != null) { d["layer"] = "triball"; d["aliasOf"] = "set_triball"; d["element"] = AgentKit.SafeName(e); }
            return r;
        }
    }

    // ---------------- 特征层 ----------------

    [ComVisible(false)]
    internal sealed class ToolExtrudeSketch : McpToolBase
    {
        public override string Name { get { return "extrude_sketch"; } }
        public override string Category { get { return "feature"; } }
        public override string Description { get { return "从 2D 草图命令生成拉伸特征（CreateProfile → CreateLine/Circle/Arc → IZPartFeatureMgr.CreateExtrudeFeature）。判据 = 特征数 +1，attempt 级。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["name"] = S.Str("目标零件名；不传取第一个顶层零件"),
                ["id"] = S.Num("目标零件 Id"),
                ["distance"] = S.Num("正向拉伸距离 mm，默认 10", false, 10.0, true),
                ["operation"] = S.Enum("增料/除料，默认 unite", new[] { "unite", "subtract" }, false, "unite"),
                ["sketch_commands"] = S.Array("[{cmd:line|circle|arc|rect, from:[x,y], to:[x,y], center:[x,y], radius:r}]", new Dictionary<string, object>())
            }, "sketch_commands");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            if (ctx.Scene == null) return AgentKit.Fail("no_scene", AgentKit.NeedSceneMessage);
            IZPart part;
            string err;
            if (args.Has("name") || args.Has("id")) { part = AgentKit.FindPart(ctx, args, out err); if (part == null) return AgentKit.Fail("element_not_found", err); }
            else { part = AgentKit.FirstPart(ctx); if (part == null) return AgentKit.Fail("no_part", "场景内没有零件可加特征"); }

            IZPartFeatureMgr fm = part as IZPartFeatureMgr;
            if (fm == null) return AgentKit.Fail("feature_mgr_unavailable", "零件 QI 不到 IZPartFeatureMgr 表面");
            int before = -1;
            try { before = fm.GetFeaturesCount(); } catch { }
            var attempts = AgentKit.Attempts();
            IZProfile profile = null;
            try { profile = ctx.Scene.CreateProfile(); AgentKit.Attempt(attempts, "IZSceneDoc.CreateProfile()", profile != null, "取到 IZProfile"); }
            catch (Exception ex) { AgentKit.Attempt(attempts, "IZSceneDoc.CreateProfile()", false, ex.Message); }
            if (profile == null) return AgentKit.Fail("profile_failed", "CreateProfile 未返回 IZProfile（查 attempts）");

            int drawn = 0;
            JValue cmds = args["sketch_commands"];
            if (cmds.Kind == JKind.Array && cmds.Arr != null)
            {
                foreach (JValue c in cmds.Arr)
                {
                    string cmd = c.ParamString("cmd", "");
                    try
                    {
                        if (cmd == "line")
                        {
                            double[] a = XY(c["from"]), b = XY(c["to"]);
                            profile.CreateLine(new double[] { a[0], a[1], 0 }, new double[] { b[0], b[1], 0 }, 0);
                            drawn++;
                        }
                        else if (cmd == "circle")
                        {
                            double[] ctr = XY(c["center"]);
                            profile.CreateCircle(new double[] { ctr[0], ctr[1], 0 }, c.ParamDouble("radius", 1), 0);
                            drawn++;
                        }
                        else if (cmd == "arc")
                        {
                            double[] ctr = XY(c["center"]);
                            profile.CreateArc(new double[] { ctr[0], ctr[1], 0 }, c.ParamDouble("radius", 1),
                                              c.ParamDouble("start_angle", 0), c.ParamDouble("end_angle", 90), 0);
                            drawn++;
                        }
                        else if (cmd == "rect")
                        {
                            double[] a = XY(c["from"]), b = XY(c["to"]);
                            double[][] pts = { new[] { a[0], a[1] }, new[] { b[0], a[1] }, new[] { b[0], b[1] }, new[] { a[0], b[1] }, new[] { a[0], a[1] } };
                            for (int i = 0; i < 4; i++)
                                profile.CreateLine(new double[] { pts[i][0], pts[i][1], 0 }, new double[] { pts[i + 1][0], pts[i + 1][1], 0 }, 0);
                            drawn++;
                        }
                        else AgentKit.Attempt(attempts, "sketch cmd=" + cmd, false, "未知命令，已跳过");
                    }
                    catch (Exception ex) { AgentKit.Attempt(attempts, "sketch cmd=" + cmd, false, ex.Message); }
                }
            }
            int curves = -1;
            try { curves = profile.GetCurvesCount(); } catch { }
            if (curves <= 0) return AgentKit.Fail("empty_profile", "草图没有生成任何曲线（查 attempts 与 sketch_commands 坐标）");

            double dist = args.ParamDouble("distance", 10);
            eZOperationType op = args.ParamString("operation", "unite") == "subtract"
                ? (eZOperationType)2 : (eZOperationType)1;   // Z_SUBTRACT=2 / Z_UNITE=1
            object feat = null;
            try
            {
                feat = fm.CreateExtrudeFeature(op, false, dist, 0.0, 0.0, profile, (eZFeatureProfileRelType)1); // COPY=1
                AgentKit.Attempt(attempts, "IZPartFeatureMgr.CreateExtrudeFeature(11)", feat != null, "返回特征对象");
            }
            catch (Exception ex) { AgentKit.Attempt(attempts, "IZPartFeatureMgr.CreateExtrudeFeature(11)", false, ex.Message); }
            try { part.Update(); AgentKit.Attempt(attempts, "IZPart.Update()", true, "重建几何"); }
            catch (Exception ex) { AgentKit.Attempt(attempts, "IZPart.Update()", false, ex.Message); }
            int after = -1;
            try { after = fm.GetFeaturesCount(); } catch { }
            bool verified = feat != null && before >= 0 && after == before + 1;
            var ret = AgentKit.Ok("verified", verified, "featuresBefore", before, "featuresAfter", after,
                               "drawnCommands", drawn, "profileCurves", curves, "distanceMm", dist,
                               "operation", op.ToString(), "relType", "Z_FEATURE_PROFILE_COPY(1)",
                               "attempts", attempts, "level", "attempt",
                               "verifiedBy", "Update 后 GetFeaturesCount +1 且特征对象非空",
                               "note", "attempt 级：草图法向与 Profile 关联语义未实机定案；请用 get_bounding_box / capture_viewport 复核形状。");
            // 零件内建模：拉伸特征也按建模顺序编号（写后回读；失败单列第三态，不影响几何判据）
            if (feat != null)
            {
                var nd = new Dictionary<string, object>();
                BuildSeq.ApplyNext(ctx, feat, "拉伸", nd, "feature");
                foreach (var kv in nd) ret[kv.Key] = kv.Value;
            }
            return ret;
        }

        private static double[] XY(JValue v)
        {
            if (v.Kind == JKind.Array && v.Arr != null && v.Arr.Count >= 2)
                return new double[] { v.Arr[0].AsDouble(), v.Arr[1].AsDouble() };
            return new double[] { 0, 0 };
        }
    }

    [ComVisible(false)]
    internal sealed class ToolRevolve : McpToolBase
    {
        public override string Name { get { return "revolve"; } }
        public override string Category { get { return "feature"; } }
        public override string Description { get { return "能力协商：旋转特征（CreateSpinFeature）所需轴线对象/关联语义未实机取证，本工具不改模型、不假成功。"; } }
        public override object InputSchema() { return S.Schema(new Dictionary<string, object>()); }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var cap = AgentKit.Capability("revolve",
                "CreateSpinFeature 的轴线点与轴向向量如何构造、Profile 与轴的关联语义无本项目实机证据。",
                new[] { "IZPartFeatureMgr.CreateSpinFeature(17)", "IZPartFeatureMgr.CreateSpinFeatureWithAxis(29)", "IZMathUtility.CreateMathVector(8)", "IZMathUtility.CreateMathPoint(9)" },
                new[] { "IZMathUtility 实例的获取通道（未取证）", "轴线点/轴向向量实机样本", "特征数 + 包围盒回读样本" });
            cap["layer"] = "feature";
            return cap;
        }
    }

    [ComVisible(false)]
    internal sealed class ToolSweep : McpToolBase
    {
        public override string Name { get { return "sweep"; } }
        public override string Category { get { return "feature"; } }
        public override string Description { get { return "能力协商：扫掠特征（CreateSweepFeature）需要路径元素，构造与关联通道未实机取证，本工具不改模型。"; } }
        public override object InputSchema() { return S.Schema(new Dictionary<string, object>()); }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var cap = AgentKit.Capability("sweep",
                "CreateSweepFeature 要求 IZElement 路径与两个 eZFeatureProfileRelType；路径构造通道未取证。",
                new[] { "IZPartFeatureMgr.CreateSweepFeature(16)", "IZPartFeatureMgr.CreateSweepFeatureEx(28)", "IZSceneDoc.CreateWire(23)" },
                new[] { "IZElement 路径对象的构造实机样本", "eSketchNormalType / dTwistAngle 语义定案", "特征数 + 包围盒回读样本" });
            cap["layer"] = "feature";
            return cap;
        }
    }

    [ComVisible(false)]
    internal sealed class ToolShell : McpToolBase
    {
        public override string Name { get { return "shell"; } }
        public override string Category { get { return "feature"; } }
        public override string Description { get { return "能力协商：抽壳特征（CreateShellFeature）需要开放面 Id 数组，面 Id 枚举与数组形态未实机取证，本工具不改模型。"; } }
        public override object InputSchema() { return S.Schema(new Dictionary<string, object>()); }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var cap = AgentKit.Capability("shell",
                "CreateShellFeature 的 varArrOpenFaceIds 形态（ZArray vs double[]）与面 Id 语义未实机定案。",
                new[] { "IZPartFeatureMgr.CreateShellFeature(12)", "IZBody.GetFaceIds(8)", "IZUtility.CreateEmptyIUnknownArray(13)" },
                new[] { "开放面 Id 数组的实机 VARIANT 形态", "抽壳厚度/方向的语义定案", "特征数 + 包围盒回读样本" });
            cap["layer"] = "feature";
            return cap;
        }
    }

    [ComVisible(false)]
    internal sealed class ToolMirrorFeature : McpToolBase
    {
        public override string Name { get { return "mirror"; } }
        public override string Category { get { return "feature"; } }
        public override string Description { get { return "按平面对元素做镜像（IZMathMatrix.MirrorAboutPlane + IZEDOSiteElement.ApplyTransform），回读 Mirrored 标志判定。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["shape_id"] = S.Str("元素名 / id"), ["name"] = S.Str("元素名"), ["id"] = S.Num("元素 Id"),
                ["plane_origin"] = S.Array("平面上一点 [x,y,z]，默认原点", new Dictionary<string, object>()),
                ["plane_normal"] = S.Array("平面法向 [x,y,z]，默认 [1,0,0]（YZ 平面）", new Dictionary<string, object>())
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            if (ctx.Scene == null) return AgentKit.Fail("no_scene", AgentKit.NeedSceneMessage);
            string err;
            IZElement e = AgentKit.FindElement(ctx, args, out err);
            if (e == null) return AgentKit.Fail("element_not_found", err);
            double[] origin = AgentKit.Vec3(args, "plane_origin", new double[] { 0, 0, 0 });
            double[] normal = AgentKit.Vec3(args, "plane_normal", new double[] { 1, 0, 0 });
            string merr;
            object r = TransformKit.Mirror(e, origin, normal, out merr);
            if (r == null) return AgentKit.Fail("mirror_failed", merr);
            var d = r as Dictionary<string, object>;
            if (d != null) { d["layer"] = "feature"; d["element"] = AgentKit.SafeName(e); }
            return r;
        }
    }

    // ---------------- 查询反馈层 ----------------

    [ComVisible(false)]
    internal sealed class ToolCaptureViewport : McpToolBase
    {
        public override string Name { get { return "capture_viewport"; } }
        public override string Category { get { return "feedback"; } }
        public override string Description { get { return "捕获当前 3D 视口为 PNG（复用 get_view 的 IZSceneDoc.ExportImage 通道），返回文件路径；server 会附 image 内容块。"; } }
        public override object InputSchema() { return new ToolGetView().InputSchema(); }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            object r = new ToolGetView().Execute(ctx, args);
            var d = r as Dictionary<string, object>;
            if (d != null) { d["layer"] = "feedback"; d["aliasOf"] = "get_view"; }
            return r;
        }
    }

    [ComVisible(false)]
    internal sealed class ToolMeasureDistance : McpToolBase
    {
        public override string Name { get { return "measure_distance"; } }
        public override string Category { get { return "feedback"; } }
        public override string Description { get { return "测距：两点欧氏距离（纯数学）或两零件局部包围盒中心距离；单位与 IZBody.GetBoundingBox 一致（米）。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["points"] = S.Array("[[x,y,z],[x,y,z]] 直接给两点", new Dictionary<string, object>()),
                ["a"] = S.Str("第一个元素名 / id"),
                ["b"] = S.Str("第二个元素名 / id")
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            JValue pts = args["points"];
            if (pts.Kind == JKind.Array && pts.Arr != null && pts.Arr.Count >= 2)
            {
                double[] p1 = XYZ(pts.Arr[0]), p2 = XYZ(pts.Arr[1]);
                if (p1 == null || p2 == null) return AgentKit.Fail("bad_points", "points 必须是两个 [x,y,z] 数组");
                double d = Dist(p1, p2);
                return AgentKit.Ok("verified", true, "distance", d, "mode", "points",
                                   "points", new List<object> { p1, p2 }, "unit", "与输入坐标同单位（不擅自换算）",
                                   "level", "implemented", "verifiedBy", "纯数学计算，不读写模型");
            }
            string ea = args.ParamString("a", ""), eb = args.ParamString("b", "");
            if (string.IsNullOrEmpty(ea) || string.IsNullOrEmpty(eb))
                return AgentKit.Fail("missing_input", "请给 points=[[x,y,z],[x,y,z]] 或 a / b 两个元素名");
            if (ctx.Scene == null) return AgentKit.Fail("no_scene", AgentKit.NeedSceneMessage);
            double[] c1 = Center(ctx, ea), c2 = Center(ctx, eb);
            if (c1 == null || c2 == null)
                return AgentKit.Fail("bbox_unavailable", "至少一个元素读不到 IZPart 包围盒（第三态，不折算成 0）");
            return AgentKit.Ok("verified", true, "distance", Dist(c1, c2), "mode", "bbox_center",
                               "centerA", c1, "centerB", c2, "unit", "米（局部坐标，未做 GetTransformToGlobal 世界变换）",
                               "level", "implemented", "verifiedBy", "IZPart.GetBody(true).GetBoundingBox 六值求中心",
                               "note", "局部坐标：装配内带变换的零件需世界变换才是真实距离，本项目待实测。");
        }

        private static double[] XYZ(JValue v)
        {
            if (v.Kind != JKind.Array || v.Arr == null || v.Arr.Count < 3) return null;
            return new double[] { v.Arr[0].AsDouble(), v.Arr[1].AsDouble(), v.Arr[2].AsDouble() };
        }

        private static double Dist(double[] a, double[] b)
        {
            return Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
        }

        private static double[] Center(McpToolContext ctx, string nameOrId)
        {
            foreach (IZElement e in AgentKit.Tops(ctx))
            {
                if (!Matches(e, nameOrId)) continue;
                IZPart part = e as IZPart;
                if (part == null) return null;
                try
                {
                    IZBody body = part.GetBody(true);
                    if (body == null) return null;
                    double[] box = McpToolContext.ToDoubleArray(body.GetBoundingBox());
                    if (box == null || box.Length < 6) return null;
                    return new double[] { (box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2 };
                }
                catch { return null; }
            }
            return null;
        }

        private static bool Matches(IZElement e, string key)
        {
            string n = AgentKit.SafeName(e);
            return !string.IsNullOrEmpty(n) && n.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0
                   || AgentKit.SafeId(e).ToString() == key;
        }
    }
}
