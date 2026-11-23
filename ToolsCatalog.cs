// Agent 分层工具集（二）：Catalog 拖放 + IntelliShape 编辑 + 变换共用链（TriBall 层复用）。
// 取证：2027 TLB members.csv —— IZCatalogEntry.InsertElement(13)/DropObject(12)/IsCatalogGroup(20)/
//       get_ItemCreatorType(14)/get_ClassID(15)/OpenSubCatalog(21)；IZSizeBox set_Length(7)/set_Width(9)/
//       set_Height(11)/get_Length(7)；IZEDOSiteElement.GetPositionTransform(15)/SetPositionTransform(14)/
//       ApplyTransform(10)；IZMathMatrix.SetTranslation(25)/SetRotation(32)/GetTranslation(21)/
//       MirrorAboutPlane(14)/MakeIdentity(10)。
// ★ 变换层是 attempt：TLB 与 interop 均声明，但本项目无实机日志；一律靠矩阵回读 + attempts 判定。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    /// <summary>TriBall 层与 Catalog 定位共用的变换链；判据 = 变换后回读 translation/矩阵标志。</summary>
    [ComVisible(false)]
    internal static class TransformKit
    {
        public static object Apply(IZElement e, double[] pos, double[] rot, bool relative, out string err)
        {
            err = null;
            var attempts = AgentKit.Attempts();
            IZEDOSiteElement site = AgentKit.AsSite(e);
            if (site == null)
            { err = "元素 QI 不到 IZEDOSiteElement 表面（无法做 PositionTransform）"; return null; }
            ZMathMatrix m = AgentKit.PositionMatrix(site, out err);
            if (m == null) { err = "GetPositionTransform 失败：" + err; return null; }
            IZMathMatrix mi = m as IZMathMatrix;
            if (mi == null) { err = "矩阵 QI 不到 IZMathMatrix 表面"; return null; }

            double[] origin = new double[] { 0, 0, 0 };
            try { double x, y, z; mi.GetTranslation(out x, out y, out z); origin = new double[] { x, y, z }; }
            catch (Exception ex) { AgentKit.Attempt(attempts, "IZMathMatrix.GetTranslation", false, ex.Message); }

            if (!relative)
            {
                try { mi.MakeIdentity(); AgentKit.Attempt(attempts, "IZMathMatrix.MakeIdentity", true, "绝对定位：先清为单位阵"); }
                catch (Exception ex) { AgentKit.Attempt(attempts, "IZMathMatrix.MakeIdentity", false, ex.Message); }
            }
            if (pos != null)
            {
                double[] target = relative
                    ? new double[] { origin[0] + pos[0], origin[1] + pos[1], origin[2] + pos[2] }
                    : pos;
                try { mi.SetTranslation(target[0], target[1], target[2]); AgentKit.Attempt(attempts, "IZMathMatrix.SetTranslation", true, "写入 " + target[0] + "," + target[1] + "," + target[2]); }
                catch (Exception ex) { AgentKit.Attempt(attempts, "IZMathMatrix.SetTranslation", false, ex.Message); }
            }
            var appliedAxes = new List<object>();
            if (rot != null)
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    if (Math.Abs(rot[axis]) < 1e-9) continue;
                    try
                    {
                        mi.SetRotation(axis, rot[axis], true);
                        appliedAxes.Add(new Dictionary<string, object> { ["axis"] = axis, ["degrees"] = rot[axis] });
                        AgentKit.Attempt(attempts, "IZMathMatrix.SetRotation(axis=" + axis + ")", true, rot[axis] + "°");
                    }
                    catch (Exception ex) { AgentKit.Attempt(attempts, "IZMathMatrix.SetRotation(axis=" + axis + ")", false, ex.Message); }
                }
            }
            try { site.SetPositionTransform(m); AgentKit.Attempt(attempts, "IZEDOSiteElement.SetPositionTransform", true, "写入矩阵"); }
            catch (Exception ex) { AgentKit.Attempt(attempts, "IZEDOSiteElement.SetPositionTransform", false, ex.Message); }

            // 权威回读
            double rx = double.NaN, ry = double.NaN, rz = double.NaN;
            string readErr = null;
            try
            {
                ZMathMatrix m2 = site.GetPositionTransform();
                IZMathMatrix mi2 = m2 as IZMathMatrix;
                if (mi2 != null) mi2.GetTranslation(out rx, out ry, out rz);
                else readErr = "回读矩阵 QI 失败";
            }
            catch (Exception ex) { readErr = ex.Message; }
            bool readOk = !double.IsNaN(rx);
            bool verified = readOk && pos != null &&
                Math.Abs(rx - (relative ? origin[0] + pos[0] : pos[0])) < 1e-6 &&
                Math.Abs(ry - (relative ? origin[1] + pos[1] : pos[1])) < 1e-6 &&
                Math.Abs(rz - (relative ? origin[2] + pos[2] : pos[2])) < 1e-6;
            return AgentKit.Ok("verified", verified, "readBack", new double[] { rx, ry, rz },
                               "before", origin, "readOk", readOk, "readError", readErr,
                               "appliedAxes", appliedAxes, "relative", relative,
                               "attempts", attempts, "level", "attempt",
                               "verifiedBy", "GetPositionTransform → GetTranslation 回读与期望值比对",
                               "note", "ICAPI 无 IZTriBall 对象；本工具等价于修改元素的 PositionTransform 矩阵，不是操纵 TriBall 手柄。矩阵回读一致仍需 get_bounding_box 复核几何是否真的跟着动。");
        }

        public static object Mirror(IZElement e, double[] planeOrigin, double[] planeNormal, out string err)
        {
            err = null;
            var attempts = AgentKit.Attempts();
            IZEDOSiteElement site = AgentKit.AsSite(e);
            if (site == null) { err = "元素 QI 不到 IZEDOSiteElement 表面"; return null; }
            try
            {
                ZMathMatrix m = AgentKit.PositionMatrix(site, out err);
                if (m == null) { err = "GetPositionTransform 失败：" + err; return null; }
                IZMathMatrix mi = m as IZMathMatrix;
                if (mi == null) { err = "矩阵 QI 不到 IZMathMatrix 表面"; return null; }
                mi.MakeIdentity();
                mi.MirrorAboutPlane(planeOrigin, planeNormal);
                bool mirrored = mi.Mirrored;
                site.ApplyTransform(m);
                AgentKit.Attempt(attempts, "IZMathMatrix.MirrorAboutPlane + IZEDOSiteElement.ApplyTransform", true, "Mirrored=" + mirrored);
                ZMathMatrix m2 = site.GetPositionTransform();
                IZMathMatrix mi2 = m2 as IZMathMatrix;
                bool afterMirrored = mi2 != null && mi2.Mirrored;
                return AgentKit.Ok("verified", mirrored && afterMirrored, "mirroredBeforeApply", mirrored,
                                   "mirroredAfterApply", afterMirrored, "attempts", attempts,
                                   "level", "attempt", "verifiedBy", "Mirrored 标志写入前后回读",
                                   "note", "镜像矩阵语义未实机定案；请用 capture_viewport + get_bounding_box 复核。");
            }
            catch (Exception ex)
            {
                AgentKit.Attempt(attempts, "MirrorAboutPlane/ApplyTransform", false, ex.Message);
                err = ex.Message;
                return AgentKit.Fail("mirror_failed", ex.Message);
            }
        }
    }

    // ---------------- Catalog 拖放 ----------------

    [ComVisible(false)]
    internal sealed class ToolDragIntelliShapeFromCatalog : McpToolBase
    {
        public override string Name { get { return "drag_intellishape_from_catalog"; } }
        public override string Category { get { return "catalog"; } }
        public override string Description { get { return "★ CAXA 独有：从设计元素目录把 IntelliShape 放进场景。主通道 IZCatalogEntry.InsertElement()，备通道 DropObject(屏幕X,Y)；判据 = 场景顶层元素 +1 且能回读新元素。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["catalog_id"] = S.Num("目录索引（先调 list_catalogs 取，会话内有效）", true),
                ["item_id"] = S.Num("条目索引（先调 list_catalogs 取）", true),
                ["position"] = S.Array("可选：插入后的 [x,y,z] mm 定位（走 PositionTransform）", new Dictionary<string, object>()),
                ["screen_x"] = S.Num("可选：显式走 DropObject 备通道时的屏幕像素 X"),
                ["screen_y"] = S.Num("可选：显式走 DropObject 备通道时的屏幕像素 Y")
            }, "catalog_id", "item_id");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            if (ctx.Scene == null) return AgentKit.Fail("no_scene", AgentKit.NeedSceneMessage);
            if (!args.Has("catalog_id") || !args.Has("item_id"))
                return AgentKit.Fail("missing_index", "请先调 list_catalogs 拿 catalog_id / item_id（索引按会话有效）");
            int catalogId = args.ParamInt("catalog_id", 0);
            int itemId = args.ParamInt("item_id", -1);
            var attempts = AgentKit.Attempts();

            IZCatalogMgr mgr = null;
            try { mgr = ctx.App.CatalogMgr as IZCatalogMgr; } catch (Exception ex) { AgentKit.Attempt(attempts, "get_CatalogMgr", false, ex.Message); }
            if (mgr == null) return AgentKit.Fail("catalog_mgr_unavailable", "CatalogMgr 不可用（查 attempts）");
            IZCatalog cat = null;
            try
            {
                cat = mgr.get_Catalog(catalogId) as IZCatalog;
                AgentKit.Attempt(attempts, "IZCatalogMgr.get_Catalog(" + catalogId + ")", cat != null, cat == null ? "QI 失败" : "取到目录");
            }
            catch (Exception ex) { AgentKit.Attempt(attempts, "IZCatalogMgr.get_Catalog(" + catalogId + ")", false, ex.Message); }
            if (cat == null) return AgentKit.Fail("catalog_not_found", "目录索引 " + catalogId + " 不可用");
            IZCatalogEntry ent = null;
            string entryName = "";
            try
            {
                ent = cat.get_Entry(itemId) as IZCatalogEntry;
                if (ent != null) entryName = ent.Name;
                AgentKit.Attempt(attempts, "IZCatalog.get_Entry(" + itemId + ")", ent != null, ent == null ? "QI 失败" : entryName);
            }
            catch (Exception ex) { AgentKit.Attempt(attempts, "IZCatalog.get_Entry(" + itemId + ")", false, ex.Message); }
            if (ent == null) return AgentKit.Fail("catalog_entry_not_found", "条目索引 " + itemId + " 不可用");
            bool isGroup = false;
            try { isGroup = ent.IsCatalogGroup(); } catch { }
            if (isGroup) return AgentKit.Fail("catalog_entry_is_group", "该条目是子目录（IsCatalogGroup=true），请先 OpenSubCatalog 再选条目");

            int before = AgentKit.TopCount(ctx);
            IZElement inserted = null;
            // 主通道：InsertElement —— 语义等价于「把图素放进场景」，不需要屏幕坐标。
            try
            {
                inserted = ent.InsertElement();
                AgentKit.Attempt(attempts, "IZCatalogEntry.InsertElement()", inserted != null, inserted == null ? "返回空" : "返回 IZElement");
            }
            catch (Exception ex) { AgentKit.Attempt(attempts, "IZCatalogEntry.InsertElement()", false, ex.Message); }
            // 备通道：仅当显式给了屏幕坐标且主通道没拿到对象时尝试。
            if (inserted == null && args.Has("screen_x") && args.Has("screen_y"))
            {
                int sx = args.ParamInt("screen_x", 0), sy = args.ParamInt("screen_y", 0);
                try
                {
                    ent.DropObject(sx, sy);
                    AgentKit.Attempt(attempts, "IZCatalogEntry.DropObject(" + sx + "," + sy + ")", true, "已投放（需回读顶层计数确认）");
                }
                catch (Exception ex) { AgentKit.Attempt(attempts, "IZCatalogEntry.DropObject(" + sx + "," + sy + ")", false, ex.Message); }
            }
            int after = AgentKit.TopCount(ctx);
            bool counted = after == before + 1;
            bool verified = counted && inserted != null;

            object positionResult = null;
            double[] pos = null;
            JValue pv = args["position"];
            if (pv.Kind == JKind.Array && pv.Arr != null && pv.Arr.Count >= 3)
            {
                pos = new double[] { pv.Arr[0].AsDouble(), pv.Arr[1].AsDouble(), pv.Arr[2].AsDouble() };
                IZElement target = inserted;
                if (target == null && counted)
                {
                    var tops = AgentKit.Tops(ctx);
                    if (tops.Count > 0) target = tops[tops.Count - 1];
                }
                if (target != null)
                {
                    string terr;
                    positionResult = TransformKit.Apply(target, pos, null, false, out terr);
                    if (positionResult == null) positionResult = AgentKit.Fail("position_failed", terr);
                }
                else positionResult = AgentKit.Fail("position_failed", "插入后没有可定位的元素");
            }

            return AgentKit.Ok("verified", verified, "catalogId", catalogId, "itemId", itemId,
                               "entryName", entryName, "topCountBefore", before, "topCountAfter", after,
                               "insertedName", inserted == null ? null : AgentKit.SafeName(inserted),
                               "insertedId", inserted == null ? -1 : AgentKit.SafeId(inserted),
                               "positionResult", positionResult, "attempts", attempts, "level", "attempt",
                               "verifiedBy", "场景顶层元素计数 +1 且 InsertElement 返回可回读的 IZElement",
                               "note", "InsertElement 是语义等价的放入场景，不是模拟鼠标拖拽；DropObject 需窗口前台与正确视口。本层未实机定案，成功后仍需 capture_viewport 视觉确认。");
        }
    }

    // ---------------- IntelliShape 编辑 ----------------

    [ComVisible(false)]
    internal sealed class ToolSetShapeHandle : McpToolBase
    {
        public override string Name { get { return "set_shape_handle"; } }
        public override string Category { get { return "intellishape"; } }
        public override string Description { get { return "按尺寸手柄改 IntelliShape 的 L/W/H（IZSizeBox 三属性写入 → IZPart.Update → 三值回读）。单位 mm，写后回读才算成功。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["shape_id"] = S.Str("元素名称或 id 字符串（也可用 name / id）"),
                ["name"] = S.Str("元素名称"),
                ["id"] = S.Num("元素 Id"),
                ["size"] = S.Array("标量 [l] 或 [l,w,h]（mm）；也可用 length/width/height 单列", new Dictionary<string, object>()),
                ["length"] = S.Num("单独设置长度 mm"),
                ["width"] = S.Num("单独设置宽度 mm"),
                ["height"] = S.Num("单独设置高度 mm")
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            if (ctx.Scene == null) return AgentKit.Fail("no_scene", AgentKit.NeedSceneMessage);
            string err;
            IZPart part = AgentKit.FindPart(ctx, args, out err);
            if (part == null) return AgentKit.Fail("element_not_found", err);
            IZSizeBox sb = AgentKit.SizeBox(part);
            if (sb == null) return AgentKit.Fail("sizebox_unavailable", "该零件找不到 IZSizeBox 表面（PrimitiveKit.FindSizeBox 口径：深度≤3、每层≤16）");

            bool hasSize = args.Has("size");
            bool hasOne = args.Has("length") || args.Has("width") || args.Has("height");
            if (!hasSize && !hasOne) return AgentKit.Fail("missing_size", "请提供 size 或 length/width/height 至少一项");
            double[] target = hasSize
                ? AgentKit.Size3(args, "size", 0)
                : new double[] { args.ParamDouble("length", 0), args.ParamDouble("width", 0), args.ParamDouble("height", 0) };
            bool[] mask = hasOne && !hasSize
                ? new[] { args.Has("length"), args.Has("width"), args.Has("height") }
                : new[] { true, true, true };

            double[] before;
            try { before = new double[] { sb.Length, sb.Width, sb.Height }; }
            catch (Exception ex) { return AgentKit.Fail("sizebox_read_failed", ex.Message); }

            var attempts = AgentKit.Attempts();
            if (mask[0]) { try { sb.Length = target[0]; AgentKit.Attempt(attempts, "IZSizeBox.set_Length", true, target[0].ToString()); } catch (Exception ex) { AgentKit.Attempt(attempts, "IZSizeBox.set_Length", false, ex.Message); } }
            if (mask[1]) { try { sb.Width = target[1]; AgentKit.Attempt(attempts, "IZSizeBox.set_Width", true, target[1].ToString()); } catch (Exception ex) { AgentKit.Attempt(attempts, "IZSizeBox.set_Width", false, ex.Message); } }
            if (mask[2]) { try { sb.Height = target[2]; AgentKit.Attempt(attempts, "IZSizeBox.set_Height", true, target[2].ToString()); } catch (Exception ex) { AgentKit.Attempt(attempts, "IZSizeBox.set_Height", false, ex.Message); } }
            try { part.Update(); AgentKit.Attempt(attempts, "IZPart.Update()", true, "重建几何"); }
            catch (Exception ex) { AgentKit.Attempt(attempts, "IZPart.Update()", false, ex.Message); }

            double[] after;
            try { after = new double[] { sb.Length, sb.Width, sb.Height }; }
            catch (Exception ex) { return AgentKit.Fail("sizebox_readback_failed", ex.Message); }
            bool verified = true;
            for (int i = 0; i < 3; i++) if (mask[i] && Math.Abs(after[i] - target[i]) > 1e-6) verified = false;
            return AgentKit.Ok("verified", verified, "beforeMm", before, "targetMm", target,
                               "readBackMm", after, "mask", new List<object> { mask[0], mask[1], mask[2] },
                               "attempts", attempts, "level", "implemented",
                               "verifiedBy", "Update 后 IZSizeBox 三值回读（容差 1e-6）",
                               "note", "尺寸盒是驱动参数；数值回读一致仍建议用 get_bounding_box / capture_viewport 复核几何。");
        }
    }

    [ComVisible(false)]
    internal sealed class ToolEditCrossSection : McpToolBase
    {
        public override string Name { get { return "edit_cross_section"; } }
        public override string Category { get { return "intellishape"; } }
        public override string Description { get { return "能力协商：解析 sketch_commands 并回显可用草图 API；截面与既有 IntelliShape 特征的关联通道未实证，本工具不改动几何、不假成功。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["shape_id"] = S.Str("目标元素名 / id"),
                ["sketch_commands"] = S.Array("草图命令数组：[{cmd:line|circle|arc|rect, ...}]", new Dictionary<string, object>())
            }, "sketch_commands");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var parsed = new List<object>();
            JValue cmds = args["sketch_commands"];
            if (cmds.Kind == JKind.Array && cmds.Arr != null)
            {
                foreach (JValue c in cmds.Arr)
                {
                    string cmd = c.ParamString("cmd", "");
                    parsed.Add(new Dictionary<string, object>
                    {
                        ["cmd"] = cmd,
                        ["supported"] = cmd == "line" || cmd == "circle" || cmd == "arc" || cmd == "rect",
                        ["hasFrom"] = c.Has("from"), ["hasTo"] = c.Has("to"),
                        ["hasCenter"] = c.Has("center"), ["hasRadius"] = c.Has("radius")
                    });
                }
            }
            var cap = AgentKit.Capability("edit_cross_section",
                "IZProfile 可创建 2D 曲线，但把新 Profile 关联回既有 IntelliShape 特征的通道没有实机证据；按规则不写入生产代码、不改动模型。",
                new[] { "IZSceneDoc.CreateProfile(22)", "IZSceneDoc.CreateProfileEx(54)",
                        "IZProfile.CreateLine(29)", "IZProfile.CreateCircle(26)", "IZProfile.CreateArc(28)",
                        "IZProfile.CreateLines(22)", "IZProfile.GetCurvesCount(17)", "IZProfile.GetNormal(19)" },
                new[] { "新 Profile 与既有 IntelliShape 特征的关联/替换 API（未取证）",
                        "草图平面法向与 IZFeatureProfileRelType 语义的实机定案",
                        "截面编辑后的特征数 + 包围盒回读样本" });
            cap["parsedCommands"] = parsed;
            cap["parsedCommandCount"] = parsed.Count;
            cap["syntaxOnly"] = true;
            cap["layer"] = "intellishape";
            return cap;
        }
    }
}
