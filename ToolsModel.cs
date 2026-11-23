// 工具集：model 类 —— 图素建模（承接「在 CAXA 场景内生成 10×10×10 方块」）。
//
// 通道取证（TLB 优先铁律，2027 TLB members.csv）：
//   IZSceneDoc: CreateBlockPart(13) / CreateCylinderPart(14) / CreateConePart(15) /
//               CreateSpherePart(16) / CreateTorusPart(17) / CreatePart(9) / CreateAssembly(8)
//   IZPartFeatureMgr: CreateBlock(19) / CreateCylinder(20) / CreateSphere(22)
//   IZPart: Update(7)
//   IZElement: set_Name(10) / Remove(14)
//   ★ 全部走 RCW 直调（通道阶梯顶层）。caxa3dmytoolsaddin 的 FeatureTool.cs / PartSpecFillTool.cs
//     已实证 `part as IZPartFeatureMgr` 与 `part.Update()` 的 RCW 直调可用，故无需下沉 vtable。
//   ★ 点数组 VARIANT 形态是已知风险面（规则坑 200~202 / 否决 88「double[] 单形态」）：
//     本工具自动按 double[] → float[] → object[] 阶梯尝试，并把每一步成败写进返回与日志，
//     实机一跑即可判定「哪条轴断了」，不猜测、不假装成功。
//   ★ 单位：CAXA 模型内部为米；界面习惯 mm。默认按 mm 解释，×0.001 转米（caxa3dtracker 实证口径）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    /// <summary>图素建模公共实现：VARIANT 点数组形态阶梯 + 两条建零件通道 + 判据回读。</summary>
    [ComVisible(false)]
    internal static class PrimitiveFactory
    {
        public const double MmToMeter = 0.001;

        public static double ToMeter(JValue args, string key, double dfltMm)
        {
            double v = args.Has(key) ? args.ParamDouble(key, dfltMm) : dfltMm;
            string unit = args.ParamString("unit", "mm");
            if (unit.Equals("m", StringComparison.OrdinalIgnoreCase)) return v;
            return v * MmToMeter;
        }

        /// <summary>三种点数组形态（VARIANT/SAFEARRAY），按可靠性排序。</summary>
        public static List<object[]> PointVariants(double x1, double y1, double z1, double x2, double y2, double z2)
        {
            var list = new List<object[]>();
            list.Add(new object[] {
                new double[] { x1, y1, z1 }, new double[] { x2, y2, z2 } });           // caxa3dtracker 实证形态
            list.Add(new object[] {
                new float[] { (float)x1, (float)y1, (float)z1 }, new float[] { (float)x2, (float)y2, (float)z2 } });
            list.Add(new object[] {
                new object[] { x1, y1, z1 }, new object[] { x2, y2, z2 } });           // VT_ARRAY|VT_VARIANT
            return list;
        }

        public static string VariantName(object pt)
        {
            if (pt is double[]) return "double[3]";
            if (pt is float[]) return "float[3]";
            if (pt is object[]) return "object[3]";
            return pt == null ? "null" : pt.GetType().Name;
        }

        /// <summary>创建长方体：先场景级一步通道，失败再走 CreatePart + FeatureMgr.CreateBlock + Update。</summary>
        public static object CreateBox(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            double lx = ToMeter(args, "size_x", 10.0);
            double ly = ToMeter(args, "size_y", 10.0);
            double lz = ToMeter(args, "size_z", 10.0);
            if (lx <= 0 || ly <= 0 || lz <= 0) return Fail("size_x / size_y / size_z 必须为正数");
            bool centered = args.ParamBool("center_base", true);
            string name = args.ParamString("name", "");

            // caxa3dtracker 15:19 裁定口径：底面矩形中心与原点重合，高沿 +Z
            double x1 = centered ? -lx / 2.0 : 0.0, y1 = centered ? -ly / 2.0 : 0.0;
            double x2 = centered ? lx / 2.0 : lx, y2 = centered ? ly / 2.0 : ly;
            var attempts = new List<object>();
            IZPart part = null;
            string usedChannel = null, usedVariant = null;

            int before = SafeTopCount(ctx);

            // ---- 通道 A：IZSceneDoc.CreateBlockPart(13)，一步建带方块的零件 ----
            foreach (object[] v in PointVariants(x1, y1, 0.0, x2, y2, lz))
            {
                try
                {
                    IZPart p = ctx.Scene.CreateBlockPart(v[0], v[1]);
                    if (p != null) { part = p; usedChannel = "IZSceneDoc.CreateBlockPart(13)"; usedVariant = VariantName(v[0]); }
                    attempts.Add(new Dictionary<string, object> { ["channel"] = "A:CreateBlockPart", ["variant"] = VariantName(v[0]), ["ok"] = p != null });
                    if (part != null) break;
                }
                catch (Exception ex)
                {
                    attempts.Add(new Dictionary<string, object> { ["channel"] = "A:CreateBlockPart", ["variant"] = VariantName(v[0]), ["ok"] = false, ["error"] = ex.Message });
                    ctx.Log("CreateBlockPart(" + VariantName(v[0]) + ") FAIL " + ex.Message);
                }
            }

            // ---- 通道 B：CreatePart(9) → as IZPartFeatureMgr → CreateBlock(19) → Update(7) ----
            if (part == null)
            {
                foreach (object[] v in PointVariants(x1, y1, 0.0, x2, y2, lz))
                {
                    IZPart p = null;
                    try
                    {
                        p = ctx.Scene.CreatePart();
                        if (p == null) throw new InvalidOperationException("CreatePart 返回 null");
                        IZPartFeatureMgr fm = p as IZPartFeatureMgr;
                        if (fm == null) throw new InvalidOperationException("零件不支持 IZPartFeatureMgr 表面（RCW as 失败）");
                        object feat = fm.CreateBlock(v[0], v[1]);
                        if (feat == null) { RemovePart(p); throw new InvalidOperationException("CreateBlock 返回 null"); }
                        p.Update();
                        part = p; usedChannel = "CreatePart(9)+IZPartFeatureMgr.CreateBlock(19)+Update(7)"; usedVariant = VariantName(v[0]);
                        attempts.Add(new Dictionary<string, object> { ["channel"] = "B:CreatePart+CreateBlock", ["variant"] = VariantName(v[0]), ["ok"] = true });
                        break;
                    }
                    catch (Exception ex)
                    {
                        attempts.Add(new Dictionary<string, object> { ["channel"] = "B:CreatePart+CreateBlock", ["variant"] = VariantName(v[0]), ["ok"] = false, ["error"] = ex.Message });
                        ctx.Log("CreateBlock 通道 B(" + VariantName(v[0]) + ") FAIL " + ex.Message);
                        if (p != null) RemovePart(p);   // 失败只清理本次自建零件，不碰用户已有元素
                    }
                }
            }

            if (part == null)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "两条通道 × 三种点数组形态均失败，未生成方块。请看 attempts 判定断在哪条轴。",
                    ["attempts"] = attempts
                };

            // ---- 命名 + 判据回读（IZPart 需 as 切到 IZElement 表面）----
            string finalName = McpToolContext.SafeNameOf(part);
            if (!string.IsNullOrEmpty(name))
            {
                string nameErr;
                if (McpToolContext.TrySetName(part, name, out nameErr)) finalName = name;
                else ctx.Log("set_Name FAIL " + nameErr);
            }
            int after = SafeTopCount(ctx);
            var result = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["channel"] = usedChannel,
                ["pointVariant"] = usedVariant,
                ["partName"] = finalName,
                ["sizeMm"] = new List<object> { lx / MmToMeter, ly / MmToMeter, lz / MmToMeter },
                ["point1Meter"] = new List<object> { x1, y1, 0.0 },
                ["point2Meter"] = new List<object> { x2, y2, lz },
                ["topCountBefore"] = before,
                ["topCountAfter"] = after,
                ["attempts"] = attempts
            };
            ReadBackBox(ctx, part, result);
            BuildSeq.NameFirstFeature(ctx, part, "长方体", result);   // 建模顺序号：N-长方体
            PrimitiveKit.EnrichPart(ctx, part, result);   // tracker 权威判据：GetPartType + sizebox 尺寸（只读，零副作用）
            ctx.Log("create_box OK channel=" + usedChannel + " variant=" + usedVariant +
                    " sizeMm=" + string.Format(CultureInfo.InvariantCulture, "{0}x{1}x{2}", lx / MmToMeter, ly / MmToMeter, lz / MmToMeter));
            return result;
        }

        /// <summary>
        /// 建模通道 B（唯一实证可用通道）：CreatePart(9) → as IZPartFeatureMgr → 创建特征 → Update(7)。
        /// ★ 实机 2026-11-12 CAXA 2027(29.0)：场景级 IZSceneDoc.Create*Part 系列（CreateBlockPart(13)/
        ///   CreateCylinderPart(14)/CreateSpherePart(16)…）运行时一律 E_NOTIMPL「未实现该方法或操作」，
        ///   与点数组 VARIANT 形态无关（double[3]/float[3]/object[3] 三种都试过）。
        ///   而 CreatePart + IZPartFeatureMgr.CreateBlock(19) + Update 成功生成 10×10×10 方块、
        ///   包围盒回读 10,10,10 mm、顶层元素 0→1。故圆柱/球也必须走本通道。
        /// 失败只清理本次自建零件，绝不碰用户已有元素。
        /// </summary>
        internal static IZPart ViaFeatureMgr(McpToolContext ctx,
            Func<IZPartFeatureMgr, object[], object> createFeature,
            List<object[]> variants, List<object> attempts, string channelLabel, out string usedVariant)
        {
            usedVariant = null;
            foreach (object[] v in variants)
            {
                IZPart p = null;
                try
                {
                    p = ctx.Scene.CreatePart();
                    if (p == null) throw new InvalidOperationException("CreatePart 返回 null");
                    IZPartFeatureMgr fm = p as IZPartFeatureMgr;
                    if (fm == null) throw new InvalidOperationException("零件不支持 IZPartFeatureMgr 表面（RCW as 失败）");
                    object feat = createFeature(fm, v);
                    if (feat == null) throw new InvalidOperationException("特征创建返回 null");
                    p.Update();
                    usedVariant = VariantName(v[0]);
                    attempts.Add(new Dictionary<string, object> { ["channel"] = "B:" + channelLabel, ["variant"] = usedVariant, ["ok"] = true });
                    return p;
                }
                catch (Exception ex)
                {
                    attempts.Add(new Dictionary<string, object> { ["channel"] = "B:" + channelLabel, ["variant"] = VariantName(v[0]), ["ok"] = false, ["error"] = ex.Message });
                    ctx.Log(channelLabel + " 通道 B(" + VariantName(v[0]) + ") FAIL " + ex.Message);
                    if (p != null) RemovePart(p);
                }
            }
            return null;
        }

        /// <summary>生成圆柱体：通道 A 仅作诊断（2027 实测 E_NOTIMPL），真正落地走通道 B。</summary>
        internal static object CreateCylinder(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            double dia = ToMeter(args, "diameter", 10.0);
            double h = ToMeter(args, "height", 10.0);
            if (dia <= 0 || h <= 0) return Fail("diameter / height 必须为正数");
            string name = args.ParamString("name", "");
            var attempts = new List<object>();
            IZPart part = null; string usedChannel = null, variant = null;
            int before = SafeTopCount(ctx);
            // 轴点取原点、轴向取单位 +Z 向量（CreateCylinder 的 varAxisVec 需单位向量，不是高度）
            List<object[]> axis = PointVariants(0, 0, 0, 0, 0, 1);

            // 通道 A：IZSceneDoc.CreateCylinderPart(14) —— 只试一种形态作诊断，已知 2027 E_NOTIMPL
            try
            {
                IZPart p = ctx.Scene.CreateCylinderPart(dia / 2.0, h, axis[0][0], axis[0][1]);
                if (p != null) { part = p; usedChannel = "IZSceneDoc.CreateCylinderPart(14)"; variant = VariantName(axis[0][0]); }
                attempts.Add(new Dictionary<string, object> { ["channel"] = "A:CreateCylinderPart", ["variant"] = VariantName(axis[0][0]), ["ok"] = p != null });
            }
            catch (Exception ex)
            {
                attempts.Add(new Dictionary<string, object> { ["channel"] = "A:CreateCylinderPart", ["variant"] = VariantName(axis[0][0]), ["ok"] = false, ["error"] = ex.Message });
            }

            // 通道 B：IZPartFeatureMgr.CreateCylinder(20)(dRadius, dHeight, varAxisPt, varAxisVec)
            if (part == null)
            {
                string bv;
                part = ViaFeatureMgr(ctx,
                    (fm, v) => fm.CreateCylinder(dia / 2.0, h, v[0], v[1]),
                    axis, attempts, "CreatePart+IZPartFeatureMgr.CreateCylinder(20)+Update(7)", out bv);
                if (part != null) { usedChannel = "CreatePart(9)+IZPartFeatureMgr.CreateCylinder(20)+Update(7)"; variant = bv; }
            }

            if (part == null)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "圆柱体两条通道均失败（场景级 CreateCylinderPart 在 CAXA 2027 为 E_NOTIMPL；请查 attempts 判通道 B 断在哪条轴）",
                    ["attempts"] = attempts
                };

            string finalName = McpToolContext.SafeNameOf(part);
            if (!string.IsNullOrEmpty(name))
            {
                string ne;
                if (McpToolContext.TrySetName(part, name, out ne)) finalName = name;
                else ctx.Log("set_Name FAIL " + ne);
            }
            var result = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["channel"] = usedChannel,
                ["pointVariant"] = variant,
                ["partName"] = finalName,
                ["diameterMm"] = dia / MmToMeter,
                ["heightMm"] = h / MmToMeter,
                ["topCountBefore"] = before,
                ["topCountAfter"] = SafeTopCount(ctx),
                ["attempts"] = attempts
            };
            ReadBackBox(ctx, part, result);
            BuildSeq.NameFirstFeature(ctx, part, "圆柱体", result);   // 建模顺序号：N-圆柱体
            PrimitiveKit.EnrichPart(ctx, part, result);   // tracker 权威判据：GetPartType + sizebox 尺寸（只读，零副作用）
            ctx.Log("create_cylinder OK channel=" + usedChannel + " variant=" + variant);
            return result;
        }

        /// <summary>生成球体：通道 A 仅作诊断（2027 实测 E_NOTIMPL），真正落地走通道 B。</summary>
        internal static object CreateSphere(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            double dia = ToMeter(args, "diameter", 10.0);
            if (dia <= 0) return Fail("diameter 必须为正数");
            string name = args.ParamString("name", "");
            var attempts = new List<object>();
            IZPart part = null; string usedChannel = null, variant = null;
            int before = SafeTopCount(ctx);
            List<object[]> center = PointVariants(0, 0, 0, 0, 0, 0);

            try
            {
                IZPart p = ctx.Scene.CreateSpherePart(dia / 2.0, center[0][0]);
                if (p != null) { part = p; usedChannel = "IZSceneDoc.CreateSpherePart(16)"; variant = VariantName(center[0][0]); }
                attempts.Add(new Dictionary<string, object> { ["channel"] = "A:CreateSpherePart", ["variant"] = VariantName(center[0][0]), ["ok"] = p != null });
            }
            catch (Exception ex)
            {
                attempts.Add(new Dictionary<string, object> { ["channel"] = "A:CreateSpherePart", ["variant"] = VariantName(center[0][0]), ["ok"] = false, ["error"] = ex.Message });
            }

            if (part == null)
            {
                string bv;
                part = ViaFeatureMgr(ctx,
                    (fm, v) => fm.CreateSphere(dia / 2.0, v[0]),
                    center, attempts, "CreatePart+IZPartFeatureMgr.CreateSphere(22)+Update(7)", out bv);
                if (part != null) { usedChannel = "CreatePart(9)+IZPartFeatureMgr.CreateSphere(22)+Update(7)"; variant = bv; }
            }

            if (part == null)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "球体两条通道均失败（场景级 CreateSpherePart 在 CAXA 2027 为 E_NOTIMPL；请查 attempts）",
                    ["attempts"] = attempts
                };

            string finalName = McpToolContext.SafeNameOf(part);
            if (!string.IsNullOrEmpty(name))
            {
                string ne;
                if (McpToolContext.TrySetName(part, name, out ne)) finalName = name;
            }
            var result = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["channel"] = usedChannel,
                ["pointVariant"] = variant,
                ["partName"] = finalName,
                ["diameterMm"] = dia / MmToMeter,
                ["topCountBefore"] = before,
                ["topCountAfter"] = SafeTopCount(ctx),
                ["attempts"] = attempts
            };
            ReadBackBox(ctx, part, result);
            BuildSeq.NameFirstFeature(ctx, part, "球体", result);   // 建模顺序号：N-球体
            PrimitiveKit.EnrichPart(ctx, part, result);   // tracker 权威判据：GetPartType + sizebox 尺寸（只读，零副作用）
            return result;
        }

        /// <summary>回读包围盒作为「实体真落地」的判据；读不到单列说明，不算失败。</summary>
        internal static void ReadBackBox(McpToolContext ctx, IZPart part, Dictionary<string, object> result)
        {
            try
            {
                IZBody body = part.GetBody(true);
                if (body == null) { result["bboxReadBack"] = "GetBody 返回 null"; return; }
                double[] box = McpToolContext.ToDoubleArray(body.GetBoundingBox());
                if (box == null || box.Length < 6) { result["bboxReadBack"] = "包围盒形态不可解析"; return; }
                result["bboxMinMeter"] = new List<object> { box[0], box[1], box[2] };
                result["bboxMaxMeter"] = new List<object> { box[3], box[4], box[5] };
                result["bboxSizeMm"] = new List<object> {
                    (box[3] - box[0]) / MmToMeter, (box[4] - box[1]) / MmToMeter, (box[5] - box[2]) / MmToMeter };
            }
            catch (Exception ex) { result["bboxReadBack"] = "读取失败: " + ex.Message; }
        }

        private static void RemovePart(IZPart p)
        {
            string err;
            McpToolContext.TryRemove(p, out err);   // 失败只清理本次自建零件，不碰用户已有元素
        }

        private static int SafeTopCount(McpToolContext ctx)
        {
            try { return ctx.Scene.GetChildrenElementsCount(); } catch { return -1; }
        }

        private static Dictionary<string, object> NeedScene(McpToolContext ctx)
        {
            return ctx.Scene == null ? Fail("当前没有 3D 场景文档（活动文档不是场景文档或没有打开文档）") : null;
        }

        private static Dictionary<string, object> Fail(string reason)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = reason };
        }
    }

    [ComVisible(false)]
    internal sealed class ToolCreateBox : McpToolBase
    {
        public override string Name { get { return "create_box"; } }
        public override string Category { get { return "model"; } }
        public override string Description { get { return "在当前 3D 场景生成长方体/方块，默认 10×10×10 mm。底面中心默认与原点重合、高沿 +Z；自动回读包围盒作为落地判据。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["size_x"] = S.Num("X 方向尺寸，默认 10", false, 10.0, true),
                ["size_y"] = S.Num("Y 方向尺寸，默认 10", false, 10.0, true),
                ["size_z"] = S.Num("Z 方向尺寸（高），默认 10", false, 10.0, true),
                ["unit"] = S.Str("单位：mm（默认）或 m", false, "mm"),
                ["center_base"] = S.Bool("底面矩形中心是否与原点重合，默认 true", false, true, true),
                ["name"] = S.Str("可选：给新建零件命名")
            });
        }
        public override object Execute(McpToolContext ctx, JValue args) { return PrimitiveFactory.CreateBox(ctx, args); }
    }

    [ComVisible(false)]
    internal sealed class ToolCreateCylinder : McpToolBase
    {
        public override string Name { get { return "create_cylinder"; } }
        public override string Category { get { return "model"; } }
        public override string Description { get { return "生成圆柱体，默认直径 10 mm、高 10 mm，轴点=(0,0,0)、轴向=+Z 单位向量。实证通道：CreatePart(9)+IZPartFeatureMgr.CreateCylinder(20)+Update(7)；场景级 CreateCylinderPart(14) 在 CAXA 2027 为 E_NOTIMPL。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["diameter"] = S.Num("直径，默认 10", false, 10.0, true),
                ["height"] = S.Num("高度，默认 10", false, 10.0, true),
                ["unit"] = S.Str("单位：mm（默认）或 m", false, "mm"),
                ["name"] = S.Str("可选：给新建零件命名")
            });
        }
        public override object Execute(McpToolContext ctx, JValue args) { return PrimitiveFactory.CreateCylinder(ctx, args); }
    }

    [ComVisible(false)]
    internal sealed class ToolCreateSphere : McpToolBase
    {
        public override string Name { get { return "create_sphere"; } }
        public override string Category { get { return "model"; } }
        public override string Description { get { return "生成球体，默认直径 10 mm，球心在原点。实证通道：CreatePart(9)+IZPartFeatureMgr.CreateSphere(22)+Update(7)；场景级 CreateSpherePart(16) 在 CAXA 2027 为 E_NOTIMPL。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["diameter"] = S.Num("直径，默认 10", false, 10.0, true),
                ["unit"] = S.Str("单位：mm（默认）或 m", false, "mm"),
                ["name"] = S.Str("可选：给新建零件命名")
            });
        }
        public override object Execute(McpToolContext ctx, JValue args) { return PrimitiveFactory.CreateSphere(ctx, args); }
    }

    [ComVisible(false)]
    internal sealed class ToolRegenerateParts : McpToolBase
    {
        public override string Name { get { return "regenerate_parts"; } }
        public override string Category { get { return "model"; } }
        public override string Description { get { return "重建场景中的零件（IZSceneDoc.RegenerateParts），建模后可用它刷新几何。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object> { ["all"] = S.Bool("是否重建全部零件，默认 true", false, true, true) });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            try { ctx.Scene.RegenerateParts(args.ParamBool("all", true)); return Ok("regenerated", true); }
            catch (Exception ex) { return Fail("RegenerateParts 失败：" + ex.Message); }
        }
    }
}
