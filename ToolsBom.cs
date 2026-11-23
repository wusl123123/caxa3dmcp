// 工具集：bom 类 —— 对应 caxa3dmytoolsaddin 子插件 caxa3dTreeBom / caxa3dButton.BomTool。
//
// 通道取证（2027 TLB members.csv，全部 RCW 直调）：
//   IZSceneDoc.GetChildElements / IZElement.GetChildren(13) / GetChildrenCount(15) / Type / Name
//   IZPart: GetBOMSetting(10, 4 个 out) / get·set_IncludedInBOM(29/30) / BOMPartNumber / BOMDescription
//   IZSceneElement.ExportSelfBOM(46)：Int32& plCount, Object& 四个数组 —— ★ 5 个 out 必须走 RCW
//     （caxa3dsheetmaster/Probe.cs:185 实证：多 out 用裸槽位直调会破坏栈导致闪退）
//   IZElement.GetCustomPropManager(42) + IZCustomPropMgr.IsSharedMgr(7) —— 共享表唯一铁律
// 实证口径（caxa3dTreeBom/CaxaService.cs）：
//   ★ GetAllLeafParts 返回 int[]（元素 Id）无 managed 映射，已否决；改用 GetChildrenZArray / GetChildren 递归。
//   ★ SC* 字段真实落点＝共享自定义属性表（:454 本机实证），不在零件级属性表。
//   ★ 判成功只看权威副作用信号（回读），不看 API 返回值。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    /// <summary>递归 BOM 树的公共实现（复刻 caxa3dTreeBom 的层级遍历，不含 xlsx / 合并规则）。</summary>
    [ComVisible(false)]
    internal static class BomKit
    {
        public static List<object> Walk(McpToolContext ctx, int maxDepth, int maxNodes,
            bool includeProps, out bool truncated)
        {
            var nodes = new List<object>();
            truncated = false;
            foreach (object o in McpToolContext.UnpackArray(ctx.Scene.GetChildElements()))
            {
                IZElement e = o as IZElement;
                if (e == null) continue;
                WalkNode(e, 0, maxDepth, maxNodes, includeProps, nodes, ref truncated);
                if (truncated) break;
            }
            return nodes;
        }

        private static void WalkNode(IZElement e, int depth, int maxDepth, int maxNodes,
            bool includeProps, List<object> nodes, ref bool truncated)
        {
            if (nodes.Count >= maxNodes) { truncated = true; return; }
            var d = new Dictionary<string, object>
            {
                ["depth"] = depth,
                ["id"] = ToolGetSceneInfo.SafeId(e),
                ["name"] = McpToolContext.SafeName(e),
                ["type"] = ToolGetSceneInfo.SafeType(e)
            };
            IZPart part = e as IZPart;
            d["isPart"] = part != null;
            try { d["childCount"] = e.GetChildrenCount(); } catch { }
            if (part != null)
            {
                try { d["includedInBOM"] = part.IncludedInBOM; } catch { }
                try { d["bomPartNumber"] = part.BOMPartNumber; } catch { }
                try { d["bomDescription"] = part.BOMDescription; } catch { }
                try
                {
                    string pn, ds; bool userSet; int qty;
                    bool ok = part.GetBOMSetting(out pn, out ds, out userSet, out qty);
                    d["bomSetting"] = new Dictionary<string, object>
                    { ["returned"] = ok, ["partNumber"] = pn, ["description"] = ds, ["userSetQuantity"] = userSet, ["quantity"] = qty };
                }
                catch { }
            }
            if (includeProps)
            {
                string diag; IZCustomPropMgr mgr = Elem.SharedMgr(e, out diag);
                if (mgr == null) d["sharedProps"] = "第三态：未取得共享属性表（" + diag + "）";
                else
                {
                    var props = new List<object>();
                    try
                    {
                        int n = mgr.Count;
                        for (int i = 0; i < n && i < 50; i++)
                        {
                            string pn; object pv;
                            try { mgr.GetAt(i, out pn, out pv); props.Add(new Dictionary<string, object> { ["name"] = pn, ["value"] = ToolGetCustomProps.Scalar(pv) }); }
                            catch { }
                        }
                    }
                    catch { }
                    d["sharedProps"] = props;
                }
            }
            nodes.Add(d);
            if (depth >= maxDepth) return;
            try
            {
                foreach (object o in McpToolContext.UnpackArray(e.GetChildren()))
                {
                    IZElement c = o as IZElement;
                    if (c == null) continue;
                    WalkNode(c, depth + 1, maxDepth, maxNodes, includeProps, nodes, ref truncated);
                    if (truncated) return;
                }
            }
            catch { }
        }

        public static readonly string[] CsvColumns = new string[]
        { "depth", "id", "name", "type", "isPart", "childCount", "includedInBOM", "bomPartNumber", "bomDescription" };

        public static string Cell(Dictionary<string, object> node, string key)
        {
            object v;
            if (!node.TryGetValue(key, out v) || v == null) return "";
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            return s.Replace("\"", "\"\"");
        }

        public static string CsvLine(Dictionary<string, object> node)
        {
            var parts = new List<string>();
            foreach (string c in CsvColumns) parts.Add("\"" + Cell(node, c) + "\"");
            return string.Join(",", parts.ToArray());
        }
    }

    [ComVisible(false)]
    internal sealed class ToolGetBomTree : McpToolBase
    {
        public override string Name { get { return "get_bom_tree"; } }
        public override string Category { get { return "bom"; } }
        public override string Description { get { return "递归构建装配 BOM 树（层级编码/名称/类型/是否零件/计入BOM/BOM设置/可选共享属性）。对应 caxa3dTreeBom 的层级遍历，不含 xlsx 导出与合并规则。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["max_depth"] = S.Num("最大递归深度，默认 5", false, 5.0, true),
                ["max_nodes"] = S.Num("最多节点数，默认 200", false, 200.0, true),
                ["include_props"] = S.Bool("是否附带共享自定义属性，默认 false（开启更慢）", false, false, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            int depth = args.ParamInt("max_depth", 5); if (depth < 0) depth = 0; if (depth > 20) depth = 20;
            int max = args.ParamInt("max_nodes", 200); if (max < 1) max = 1; if (max > 2000) max = 2000;
            bool props = args.ParamBool("include_props", false);
            bool truncated;
            var nodes = BomKit.Walk(ctx, depth, max, props, out truncated);
            return Ok("document", ToolGetSceneInfo.SafeDocName(ctx), "count", nodes.Count,
                      "truncated", truncated, "nodes", nodes);
        }
    }

    [ComVisible(false)]
    internal sealed class ToolExportBomCsv : McpToolBase
    {
        public override string Name { get { return "export_bom_csv"; } }
        public override string Category { get { return "bom"; } }
        public override string Description { get { return "把递归 BOM 树导出为 UTF-8 BOM 的 CSV（写到 %TEMP%），返回绝对路径与行数。只读场景，不改模型。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["max_depth"] = S.Num("最大递归深度，默认 5", false, 5.0, true),
                ["max_nodes"] = S.Num("最多节点数，默认 500", false, 500.0, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            int depth = args.ParamInt("max_depth", 5); if (depth < 0) depth = 0; if (depth > 20) depth = 20;
            int max = args.ParamInt("max_nodes", 500); if (max < 1) max = 1; if (max > 5000) max = 5000;
            bool truncated;
            var nodes = BomKit.Walk(ctx, depth, max, false, out truncated);
            string path = Path.Combine(Path.GetTempPath(), "caxa3dmcp_bom_" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                using (var sw = new StreamWriter(path, false, new UTF8Encoding(true)))
                {
                    sw.WriteLine("\"" + string.Join("\",\"", BomKit.CsvColumns) + "\"");
                    foreach (object o in nodes)
                    {
                        var node = o as Dictionary<string, object>;
                        if (node != null) sw.WriteLine(BomKit.CsvLine(node));
                    }
                }
            }
            catch (Exception ex) { return Fail("csv_write_failed", "写 CSV 失败：" + ex.Message); }
            long size = 0; try { size = new FileInfo(path).Length; } catch { }
            return Ok("filePath", path, "rowCount", nodes.Count, "fileSizeBytes", size,
                      "columns", new List<object>(BomKit.CsvColumns), "truncated", truncated,
                      "verifiedBySideEffect", File.Exists(path) && size > 0);
        }
    }

    [ComVisible(false)]
    internal sealed class ToolExportSelfBom : McpToolBase
    {
        public override string Name { get { return "export_self_bom"; } }
        public override string Category { get { return "bom"; } }
        public override string Description { get { return "读取元素自身的 BOM 属性快照（IZSceneElement.ExportSelfBOM，5 个 out 走 RCW）。对应 caxa3dTreeBom 的表头取值兜底通道；失败记第三态。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["id"] = S.Num("元素 Id"), ["name"] = S.Str("元素名称（用于定位）"),
                ["last_created"] = S.Bool("取最后一个顶层元素", false, false, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            string err; IZElement e = Elem.Find(ctx, args, out err);
            if (e == null) return Fail(err);
            IZSceneElement se = e as IZSceneElement;
            if (se == null) return Fail("scene_element_surface_missing", "目标不支持 IZSceneElement 表面（第三态）");
            try
            {
                int count; object labels, values, types, units;
                // ★ 5 个 out 必须走 RCW；裸槽位直调会破坏栈（caxa3dsheetmaster/Probe.cs:185 闪退实证）
                se.ExportSelfBOM(out count, out labels, out values, out types, out units);
                var L = McpToolContext.UnpackArray(labels);
                var V = McpToolContext.UnpackArray(values);
                var T = McpToolContext.UnpackArray(types);
                var U = McpToolContext.UnpackArray(units);
                var rows = new List<object>();
                for (int i = 0; i < count && i < 200; i++)
                {
                    rows.Add(new Dictionary<string, object>
                    {
                        ["index"] = i,
                        ["label"] = i < L.Count ? ToolGetCustomProps.Scalar(L[i]) : null,
                        ["value"] = i < V.Count ? ToolGetCustomProps.Scalar(V[i]) : null,
                        ["type"] = i < T.Count ? ToolGetCustomProps.Scalar(T[i]) : null,
                        ["unit"] = i < U.Count ? ToolGetCustomProps.Scalar(U[i]) : null
                    });
                }
                return Ok("element", McpToolContext.SafeName(e), "count", count, "rows", rows,
                          "labelCount", L.Count, "valueCount", V.Count);
            }
            catch (Exception ex) { return Fail("export_self_bom_failed", "ExportSelfBOM 失败（第三态）：" + ex.Message); }
        }
    }

    [ComVisible(false)]
    internal sealed class ToolSetIncludedInBom : McpToolBase
    {
        public override string Name { get { return "set_included_in_bom"; } }
        public override string Category { get { return "bom"; } }
        public override string Description { get { return "设置零件是否计入 BOM（IZPart.IncludedInBOM），写后回读校验。对应 caxa3dButton.BomTool。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["id"] = S.Num("元素 Id"), ["name"] = S.Str("元素名称（用于定位）"),
                ["last_created"] = S.Bool("取最后一个顶层元素", false, false, true),
                ["included"] = S.Bool("是否计入 BOM", true)
            }, "included");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            string err; IZElement e = Elem.Find(ctx, args, out err);
            if (e == null) return Fail(err);
            IZPart part = e as IZPart;
            if (part == null) return Fail("part_surface_missing", "目标不是零件(IZPart)");
            bool want = args.ParamBool("included", false);
            bool before = false;
            try { before = part.IncludedInBOM; } catch { }
            try { part.IncludedInBOM = want; }
            catch (Exception ex) { return Fail("bom_flag_write_failed", "写 IncludedInBOM 失败：" + ex.Message); }
            bool after = false; try { after = part.IncludedInBOM; } catch { }
            return Ok("element", McpToolContext.SafeName(e), "before", before, "requested", want,
                      "readBack", after, "verified", after == want);
        }
    }
}
