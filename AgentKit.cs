// 九层 Agent 工具集的公共助手与元数据（含 26-11-13 图纸环境唯一桥层）。
// 依据：2027 TLB members.csv / enums.csv 取证 + 本项目既有 41 工具实现（26-11-13）。
// 铁律：所有 COM 调用都在 McpBridge.Dispatch 经 _host.Invoke 回投的 STA 主线程上执行。
//       判成功只认权威副作用信号（顶层元素计数 / 写后回读 / 磁盘文件），不认 API 返回值。
//       没有实机证据的通道一律返回 capability 第三态，绝不假成功。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    [ComVisible(false)]
    internal static class AgentKit
    {
        public static Dictionary<string, object> Ok(params object[] kv)
        {
            var d = new Dictionary<string, object> { ["ok"] = true };
            for (int i = 0; i + 1 < kv.Length; i += 2) d[Convert.ToString(kv[i])] = kv[i + 1];
            return d;
        }

        public static Dictionary<string, object> Fail(string code, string reason)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["code"] = code, ["error"] = reason };
        }

        /// <summary>能力协商第三态：工具执行成功（ok=true），但业务动作明确未落地。</summary>
        public static Dictionary<string, object> Capability(string tool, string reason, string[] members, string[] missing)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["tool"] = tool,
                ["level"] = "capability",
                ["capability"] = true,
                ["geometryVerified"] = false,
                ["verified"] = false,
                ["reason"] = reason,
                ["availableMembers"] = new List<object>(members ?? new string[0]),
                ["missingPrerequisites"] = new List<object>(missing ?? new string[0]),
                ["note"] = "本工具只做能力协商，未改动模型；不要把它当作建模成功。"
            };
        }

        public static string NeedSceneMessage { get { return "当前没有 3D 场景文档（活动文档不是 IZSceneDoc 或没有打开文档）"; } }

        public static List<IZElement> Tops(McpToolContext ctx)
        {
            var list = new List<IZElement>();
            if (ctx.Scene == null) return list;
            try
            {
                foreach (object o in McpToolContext.UnpackArray(ctx.Scene.GetChildElements()))
                {
                    IZElement e = o as IZElement;
                    if (e != null) list.Add(e);
                }
            }
            catch (Exception ex) { ctx.Log("AgentKit.Tops FAIL " + ex.Message); }
            return list;
        }

        public static int TopCount(McpToolContext ctx) { return Tops(ctx).Count; }

        /// <summary>按 args 里的 name / id / shape_id / element_name 定位元素（复用 Elem.Find 的口径）。</summary>
        public static IZElement FindElement(McpToolContext ctx, JValue args, out string err)
        {
            string name = args.ParamString("name", "");
            if (string.IsNullOrEmpty(name)) name = args.ParamString("shape_id", "");
            if (string.IsNullOrEmpty(name)) name = args.ParamString("element_name", "");
            int id = args.Has("id") ? args.ParamInt("id", -1) : (args.Has("element_id") ? args.ParamInt("element_id", -1) : -1);
            var probe = new JValue { Kind = JKind.Object, Obj = new Dictionary<string, JValue>() };
            if (id >= 0) probe.Obj["id"] = new JValue { Kind = JKind.Number, Num = id };
            if (!string.IsNullOrEmpty(name)) probe.Obj["name"] = new JValue { Kind = JKind.String, Str = name };
            if (id < 0 && string.IsNullOrEmpty(name)) probe.Obj["last_created"] = new JValue { Kind = JKind.Bool, Bool = true };
            return Elem.Find(ctx, probe, out err);
        }

        public static IZPart FindPart(McpToolContext ctx, JValue args, out string err)
        {
            IZElement e = FindElement(ctx, args, out err);
            if (e == null) return null;
            IZPart p = e as IZPart;
            if (p == null) { err = "定位到的元素不是 IZPart（无法做零件级操作）"; return null; }
            return p;
        }

        /// <summary>取第一个顶层零件（导出/尺寸工具的默认目标）。</summary>
        public static IZPart FirstPart(McpToolContext ctx)
        {
            foreach (IZElement e in Tops(ctx))
            {
                IZPart p = e as IZPart;
                if (p != null) return p;
            }
            return null;
        }

        public static IZSizeBox SizeBox(IZPart part)
        {
            try { return PrimitiveKit.FindSizeBox(part, 0); }
            catch (Exception ex) { Diagnostics.Log("AgentKit.SizeBox FAIL " + ex.Message); return null; }
        }

        /// <summary>解析 [x,y,z] 数组参数；缺省返回 dflt。</summary>
        public static double[] Vec3(JValue args, string key, double[] dflt)
        {
            JValue v = args[key];
            if (v.Kind == JKind.Number) return new double[] { v.Num, v.Num, v.Num };
            if (v.Kind != JKind.Array || v.Arr == null || v.Arr.Count < 3) return dflt;
            return new double[] { v.Arr[0].AsDouble(), v.Arr[1].AsDouble(), v.Arr[2].AsDouble() };
        }

        /// <summary>size 参数：标量 → 三向同值；[l,w,h] → 三值；[l] → 三向同值。</summary>
        public static double[] Size3(JValue args, string key, double dflt)
        {
            JValue v = args[key];
            if (v.Kind == JKind.Number) return new double[] { v.Num, v.Num, v.Num };
            if (v.Kind == JKind.Array && v.Arr != null && v.Arr.Count >= 3)
                return new double[] { v.Arr[0].AsDouble(), v.Arr[1].AsDouble(), v.Arr[2].AsDouble() };
            if (v.Kind == JKind.Array && v.Arr != null && v.Arr.Count == 1)
            { double s = v.Arr[0].AsDouble(); return new double[] { s, s, s }; }
            return new double[] { dflt, dflt, dflt };
        }

        /// <summary>导出类工具的唯一成功判据：目标文件存在且长度 > 0。</summary>
        public static bool FileWritten(string path, out long bytes)
        {
            bytes = 0;
            try
            {
                if (!File.Exists(path)) return false;
                bytes = new FileInfo(path).Length;
                return bytes > 0;
            }
            catch { return false; }
        }

        public static string TempPath(string stem, string ext)
        {
            return Path.Combine(Path.GetTempPath(), stem + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ext);
        }

        public static string SafeName(IZElement e) { return McpToolContext.SafeName(e); }

        public static int SafeId(IZElement e)
        {
            try { return ToolGetSceneInfo.SafeId(e); } catch { return -1; }
        }

        /// <summary>把元素切到 IZEDOSiteElement 表面（TriBall / 变换层）。</summary>
        public static IZEDOSiteElement AsSite(IZElement e)
        {
            try { return e as IZEDOSiteElement; } catch { return null; }
        }

        /// <summary>从元素的 PositionTransform 取可写矩阵；失败返回第三态信息。</summary>
        public static ZMathMatrix PositionMatrix(IZEDOSiteElement site, out string err)
        {
            err = null;
            try
            {
                ZMathMatrix m = site.GetPositionTransform();
                if (m == null) { err = "GetPositionTransform 返回空"; return null; }
                return m;
            }
            catch (Exception ex) { err = ex.Message; return null; }
        }

        public static List<object> Attempts() { return new List<object>(); }

        public static void Attempt(List<object> list, string channel, bool ok, string detail)
        {
            list.Add(new Dictionary<string, object>
            {
                ["channel"] = channel, ["ok"] = ok, ["detail"] = detail
            });
        }
    }

    /// <summary>只读元工具：回显九层能力分级，供总控 Agent 做派发前的能力协商。</summary>
    [ComVisible(false)]
    internal sealed class ToolListAgentLayers : McpToolBase
    {
        public override string Name { get { return "list_agent_layers"; } }
        public override string Category { get { return "system"; } }
        public override string Description { get { return "只读：列出九层 Agent 的工具清单、能力级别（implemented/attempt/capability）与前置条件。总控派发前先调它。"; } }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            string[] names = { "document", "scene", "catalog", "intellishape", "triball", "feature", "feedback", "tracker", "drawing" };
            string[][] tools = {
                new[] { "new_document", "open_document", "save_document", "export_step", "export_parasolid" },
                new[] { "add_scene", "add_assembly", "add_part", "list_catalogs", "get_active_scene_graph" },
                new[] { "list_catalogs", "drag_intellishape_from_catalog" },
                new[] { "set_shape_handle", "edit_cross_section", "get_sizebox" },
                new[] { "set_triball", "position_by_triball", "mirror" },
                new[] { "extrude_sketch", "revolve", "sweep", "shell", "mirror",
                        "get_build_seq", "number_feature", "renumber_features", "reorder_features_by_seq",
                        "boolean_parts", "set_part_activation", "get_part_activation_state" },
                new[] { "capture_viewport", "get_bounding_box", "measure_distance" },
                new[] { "get_parameter_table", "get_feature_bounding_boxes", "create_sketch",
                        "move_component", "move_selected_components", "get_selection" },
                new[] { "draft_ping", "draft_state", "draft_command", "draft_exec_native", "draft_warmup",
                        "draft_draw_rect", "draft_rect_state",
                        "draft_qr_png", "draft_get_scale", "draft_qr_plan", "draft_list_ents",
                        "draft_delete_qr", "draft_insert_bitmap", "draft_qr_state" }
            };
            string[] levels = { "attempt", "implemented", "attempt", "implemented", "attempt", "capability", "implemented", "attempt", "attempt" };
            var layers = new List<object>();
            for (int i = 0; i < names.Length; i++)
            {
                layers.Add(new Dictionary<string, object>
                {
                    ["layer"] = names[i],
                    ["agent"] = "caxa3d-" + names[i],
                    ["level"] = levels[i],
                    ["tools"] = new List<object>(tools[i]),
                    ["requiresScene"] = names[i] != "drawing",
                    ["capabilityMap"] = "agents/CAPABILITY-MAP.md",
                    ["agentDir"] = "agents/",
                    ["agentDirNote"] = "唯一权威源；.workbuddy/agents 是指向它的目录联接，不是副本"
                });
            }
            return Ok("layers", layers, "layerCount", layers.Count,
                      "registeredToolCount", McpToolRegistry.Count,
                      "levelRule", "implemented=既有实证/写后回读；attempt=未实机定案，只认 verified+attempts；capability=仅能力协商，不改模型。层 level 是历史整体口径，单工具 level 以各自返回值为准",
                      "newCategories", "build（顺序号/布尔/重排）与 activation（激活协议）是 26-11-14 新增的两个 category，分层清单仍挂 feature 层以保持 9 层不变",
                      "sceneActive", ctx.Scene != null,
                      "drawingBridge", "caxa3dmcp_ / caxa3dmcp_bridge.crx（第五登记点；其余 MCP 工具一律经此桥通讯图纸环境）",
                      "drawingLayerNote", "drawing 层不要求 3D 场景；marker 新鲜度按 PING 令牌里的 pid 所对应的进程启动时间判定，不假设同进程",
                      "rectAuthority", "draft_draw_rect 的 OK 只代表已投出；业务判据 = draft_rect_state 的 *Model_Space 块表计数 delta>=1",
                      "qrAuthority", "二维码链路唯一判据 = draft_qr_state 的 delta>0（插图实体增量）+ SAVE 后盘上文件变化；GETSCALE / LISTENTS / DELETE_QR / BITMAP_NATIVE 的 OK 都不是成功信号");
        }
    }
}
