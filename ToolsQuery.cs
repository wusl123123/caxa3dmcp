// 工具集：query 类 —— 元素树 / 元素信息 / 包围盒 / 几何拓扑。
// 参考 caxa3dmytoolsaddin 的 caxa3dTreeBom（树形枚举）与 caxa3dsizeboxread（包围盒）。
// 证据：2027 TLB members.csv —— IZSceneDoc.GetChildElements(7)、GetChildrenElementsCount(18)、
//       IZElement.GetChildren(13)/GetChildrenCount(15)/Id(8)/Name(9)/Type(7)、
//       IZPart.GetBody(8)、IZBody.GetBoundingBox(13)/GetTightBoundingBox(21)/GetFaceIds(8)/GetEdgeIds(9)/GetVertexIds(10)。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    [ComVisible(false)]
    internal sealed class ToolListTopElements : McpToolBase
    {
        public override string Name { get { return "list_top_elements"; } }
        public override string Category { get { return "query"; } }
        public override string Description { get { return "列出当前 3D 场景文档的顶层元素（id / 名称 / 类型）。"; } }
        public override object InputSchema() { return S.Schema(new Dictionary<string, object>()); }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            var list = new List<object>();
            foreach (object o in McpToolContext.UnpackArray(ctx.Scene.GetChildElements()))
            {
                IZElement e = o as IZElement;
                if (e == null) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = ToolGetSceneInfo.SafeId(e),
                    ["name"] = McpToolContext.SafeName(e),
                    ["type"] = ToolGetSceneInfo.SafeType(e),
                    ["isPart"] = e is IZPart,
                    ["childCount"] = SafeChildCount(e)
                });
            }
            return Ok("count", list.Count, "elements", list);
        }

        internal static int SafeChildCount(IZElement e) { try { return e.GetChildrenCount(); } catch { return -1; } }
    }

    /// <summary>递归元素树；带深度与节点预算护栏，避免大装配把响应撑爆。</summary>
    [ComVisible(false)]
    internal sealed class ToolGetElementTree : McpToolBase
    {
        public override string Name { get { return "get_element_tree"; } }
        public override string Category { get { return "query"; } }
        public override string Description { get { return "按装配层级递归输出元素树（对应树形BOM 的层级枚举）。可限制深度与节点数。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["max_depth"] = S.Num("最大递归深度，默认 4，硬上限 10", false, 4.0, true),
                ["max_nodes"] = S.Num("最多输出节点数，默认 200，硬上限 1000", false, 200.0, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            int maxDepth = Math.Min(10, Math.Max(1, args.ParamInt("max_depth", 4)));
            int maxNodes = Math.Min(1000, Math.Max(1, args.ParamInt("max_nodes", 200)));
            int used = 0, truncated = 0;
            var roots = new List<object>();
            foreach (object o in McpToolContext.UnpackArray(ctx.Scene.GetChildElements()))
            {
                IZElement e = o as IZElement;
                if (e == null) continue;
                if (used >= maxNodes) { truncated++; continue; }
                roots.Add(Walk(e, 0, maxDepth, maxNodes, ref used, ref truncated));
            }
            return Ok("roots", roots, "nodeCount", used, "truncated", truncated, "maxDepth", maxDepth);
        }

        private static object Walk(IZElement e, int depth, int maxDepth, int maxNodes, ref int used, ref int truncated)
        {
            used++;
            var node = new Dictionary<string, object>
            {
                ["id"] = ToolGetSceneInfo.SafeId(e),
                ["name"] = McpToolContext.SafeName(e),
                ["type"] = ToolGetSceneInfo.SafeType(e),
                ["depth"] = depth,
                ["isPart"] = e is IZPart
            };
            if (depth >= maxDepth) return node;
            var kids = new List<object>();
            try
            {
                foreach (object o in McpToolContext.UnpackArray(e.GetChildren()))
                {
                    IZElement c = o as IZElement;
                    if (c == null) continue;
                    if (used >= maxNodes) { truncated++; continue; }
                    kids.Add(Walk(c, depth + 1, maxDepth, maxNodes, ref used, ref truncated));
                }
            }
            catch (Exception ex) { node["childrenError"] = ex.Message; }
            node["children"] = kids;
            return node;
        }
    }

    /// <summary>按 id 或名称定位元素并回读其基本信息与包围盒。</summary>
    [ComVisible(false)]
    internal sealed class ToolGetElementInfo : McpToolBase
    {
        public override string Name { get { return "get_element_info"; } }
        public override string Category { get { return "query"; } }
        public override string Description { get { return "按元素 id 或名称查找元素，返回类型、名称、子级数与（若为零件）包围盒。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["id"] = S.Num("元素 Id（优先匹配）"),
                ["name"] = S.Str("元素名称（id 未给时按名称匹配，忽略大小写、包含匹配）")
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            int id = args.Has("id") ? args.ParamInt("id", -1) : -1;
            string needle = args.ParamString("name", "");
            if (id < 0 && string.IsNullOrEmpty(needle)) return Fail("请提供 id 或 name 之一");

            foreach (object o in McpToolContext.UnpackArray(ctx.Scene.GetChildElements()))
            {
                IZElement e = o as IZElement;
                if (e == null) continue;
                IZElement hit = Find(e, id, needle);
                if (hit == null) continue;
                return Ok("element", Describe(hit));
            }
            return Fail("未找到匹配的元素（当前只搜索顶层元素及其子级）");
        }

        private static IZElement Find(IZElement e, int id, string needle)
        {
            if (id >= 0 && ToolGetSceneInfo.SafeId(e) == id) return e;
            string n = McpToolContext.SafeName(e);
            if (!string.IsNullOrEmpty(needle) && n.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return e;
            try
            {
                foreach (object o in McpToolContext.UnpackArray(e.GetChildren()))
                {
                    IZElement c = o as IZElement;
                    if (c == null) continue;
                    IZElement hit = Find(c, id, needle);
                    if (hit != null) return hit;
                }
            }
            catch { }
            return null;
        }

        private static object Describe(IZElement e)
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = ToolGetSceneInfo.SafeId(e),
                ["name"] = McpToolContext.SafeName(e),
                ["type"] = ToolGetSceneInfo.SafeType(e),
                ["isPart"] = e is IZPart,
                ["childCount"] = ToolListTopElements.SafeChildCount(e)
            };
            IZPart part = e as IZPart;
            if (part != null)
            {
                try
                {
                    IZBody body = part.GetBody(true);
                    if (body != null)
                    {
                        double[] box = McpToolContext.ToDoubleArray(body.GetBoundingBox());
                        if (box != null && box.Length >= 6)
                            d["boundingBox"] = new Dictionary<string, object>
                            {
                                ["min"] = new List<object> { box[0], box[1], box[2] },
                                ["max"] = new List<object> { box[3], box[4], box[5] }
                            };
                        d["facesCount"] = body.GetFacesCount();
                        d["edgesCount"] = body.GetEdgesCount();
                        d["verticesCount"] = body.GetVerticesCount();
                    }
                }
                catch (Exception ex) { d["bodyError"] = ex.Message; }
                try { d["bomPartNumber"] = part.BOMPartNumber; } catch { }
                try { d["bomDescription"] = part.BOMDescription; } catch { }
            }
            return d;
        }
    }

    [ComVisible(false)]
    internal sealed class ToolGetBoundingBox : McpToolBase
    {
        public override string Name { get { return "get_bounding_box"; } }
        public override string Category { get { return "query"; } }
        public override string Description { get { return "返回当前场景中第一个零件(IZPart)的局部包围盒 min/max 与尺寸（单位：米，模型内部单位）。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["tight"] = S.Bool("true 时改读 GetTightBoundingBox(21) 紧包围盒；默认 false 用 GetBoundingBox(13)", false, false, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            bool tight = args.ParamBool("tight", false);
            foreach (object o in McpToolContext.UnpackArray(ctx.Scene.GetChildElements()))
            {
                IZPart part = o as IZPart;
                if (part == null) continue;
                try
                {
                    IZBody body = part.GetBody(true);
                    if (body == null) continue;
                    object raw = tight ? body.GetTightBoundingBox() : body.GetBoundingBox();
                    double[] box = McpToolContext.ToDoubleArray(raw);
                    if (box == null || box.Length < 6) continue;
                    return Ok(
                        "element", McpToolContext.SafeNameOf(part),
                        "mode", tight ? "tight" : "normal",
                        "min", new List<object> { box[0], box[1], box[2] },
                        "max", new List<object> { box[3], box[4], box[5] },
                        "size", new List<object> { box[3] - box[0], box[4] - box[1], box[5] - box[2] },
                        "unit", "米（CAXA 模型内部单位；界面 mm 数值 = 米 × 1000）");
                }
                catch (Exception ex) { return Fail("包围盒读取失败：" + ex.Message); }
            }
            return Fail("未找到可作为零件(IZPart)的顶层元素");
        }
    }
}
