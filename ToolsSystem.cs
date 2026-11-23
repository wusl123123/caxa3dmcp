// 工具集：system 类（对应 caxa3dmytoolsaddin 的「系统工具」列）
// 全部 RCW 直调（通道阶梯顶层），由桥封送回 CAXA 主线程(STA)执行。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    [ComVisible(false)]
    internal sealed class ToolPing : McpToolBase
    {
        public override string Name { get { return "ping"; } }
        public override string Category { get { return "system"; } }
        public override string Description { get { return "连通性测试：返回 CAXA 3D 应用版本、API 版本与已注册工具数。"; } }
        public override object InputSchema() { return S.Schema(new Dictionary<string, object>()); }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            return Ok(
                "applicationVersion", Convert.ToString(ctx.App.ApplicationVersion),
                "apiVersion", Convert.ToString(ctx.App.ApiVersion),
                "toolCount", McpToolRegistry.Count);
        }
    }

    [ComVisible(false)]
    internal sealed class ToolGetActiveDocument : McpToolBase
    {
        public override string Name { get { return "get_active_document"; } }
        public override string Category { get { return "system"; } }
        public override string Description { get { return "返回当前活动文档的名称、类型、是否场景文档、是否已修改、是否只读。"; } }
        public override object InputSchema() { return S.Schema(new Dictionary<string, object>()); }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            IZDoc doc = ctx.ActiveDoc;
            if (doc == null) return Ok("hasDocument", false);
            string name = "";
            try { name = doc.Name; } catch (Exception ex) { name = "(Name 读取失败: " + ex.Message + ")"; }
            bool modified = false, readOnly = false;
            try { modified = doc.Modified; } catch { }
            try { readOnly = doc.IsReadOnly(); } catch { }
            return Ok(
                "hasDocument", true,
                "name", name,
                "type", doc.GetType().Name,
                "isSceneDoc", doc is IZSceneDoc,
                "modified", modified,
                "readOnly", readOnly);
        }
    }

    [ComVisible(false)]
    internal sealed class ToolGetSceneInfo : McpToolBase
    {
        public override string Name { get { return "get_scene_info"; } }
        public override string Category { get { return "system"; } }
        public override string Description { get { return "返回当前 3D 场景的顶层元素数量、元素列表概要与文档名。对应树形BOM 的顶层枚举口径。"; } }
        public override object InputSchema() { return S.Schema(new Dictionary<string, object>()); }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            IZSceneDoc scene = ctx.Scene;
            int count = -1;
            try { count = scene.GetChildrenElementsCount(); } catch (Exception ex) { ctx.Log("GetChildrenElementsCount FAIL " + ex.Message); }
            var tops = new List<object>();
            try
            {
                foreach (object o in McpToolContext.UnpackArray(scene.GetChildElements()))
                {
                    IZElement e = o as IZElement;
                    if (e == null) continue;
                    tops.Add(new Dictionary<string, object>
                    {
                        ["id"] = SafeId(e),
                        ["name"] = McpToolContext.SafeName(e),
                        ["type"] = SafeType(e)
                    });
                }
            }
            catch (Exception ex) { ctx.Log("GetChildElements FAIL " + ex.Message); }
            return Ok(
                "document", SafeDocName(ctx),
                "reportedTopCount", count,
                "enumeratedTopCount", tops.Count,
                "elements", tops);
        }

        internal static int SafeId(IZElement e) { try { return e.Id; } catch { return -1; } }
        internal static string SafeType(IZElement e) { try { return e.Type.ToString(); } catch { return ""; } }
        internal static string SafeDocName(McpToolContext ctx) { try { return ctx.ActiveDoc.Name; } catch { return ""; } }
    }

    [ComVisible(false)]
    internal sealed class ToolSaveDocument : McpToolBase
    {
        public override string Name { get { return "save_document"; } }
        public override string Category { get { return "system"; } }
        public override string Description { get { return "保存当前活动文档（IZDoc.Save）。未命名文档请先另存。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["file_name"] = S.Str("可选：另存为指定完整路径（走 IZDoc.SaveAs）。不传则就地保存。")
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            IZDoc doc = ctx.ActiveDoc;
            if (doc == null) return Fail("当前没有活动文档");
            string file = args.ParamString("file_name", "");
            try
            {
                if (!string.IsNullOrEmpty(file)) doc.SaveAs(file);
                else doc.Save();
                return Ok("saved", true, "file", string.IsNullOrEmpty(file) ? ToolGetSceneInfo.SafeDocName(ctx) : file);
            }
            catch (Exception ex) { return Fail("保存失败：" + ex.Message); }
        }
    }

    /// <summary>
    /// 能力协商（对应 SolidWorksMCP 的 get_capabilities / get_environment_status）。
    /// 建模前建议先调用：客户端据此决定可用工具、默认单位与场景状态，避免盲目调用。
    /// </summary>
    [ComVisible(false)]
    internal sealed class ToolGetCapabilities : McpToolBase
    {
        public override string Name { get { return "get_capabilities"; } }
        public override string Category { get { return "system"; } }
        public override string Description { get { return "能力协商：返回 CAXA 版本、API 版本、MCP 协议版本、已注册工具总数与按分类计数、默认单位与当前活动文档状态。建模前建议先调用。"; } }
        public override object InputSchema() { return S.Schema(new Dictionary<string, object>()); }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var caps = new Dictionary<string, object>
            {
                ["caxa3dmcpVersion"] = "0.2.0",
                ["mcpProtocolVersion"] = "2024-11-05",
                ["applicationVersion"] = Convert.ToString(ctx.App.ApplicationVersion),
                ["apiVersion"] = Convert.ToString(ctx.App.ApiVersion),
                ["registeredToolCount"] = McpToolRegistry.Count,
                ["toolCategories"] = McpToolRegistry.Categories(),
                ["defaultUnit"] = "mm",
                ["staThread"] = true
            };
            var doc = new Dictionary<string, object> { ["hasDocument"] = ctx.ActiveDoc != null };
            if (ctx.ActiveDoc != null)
            {
                doc["name"] = ToolGetSceneInfo.SafeDocName(ctx);
                doc["isSceneDoc"] = ctx.ActiveDoc is IZSceneDoc;
                if (ctx.Scene != null)
                {
                    try { doc["topElementCount"] = ctx.Scene.GetChildrenElementsCount(); } catch { }
                }
            }
            caps["activeDocument"] = doc;
            caps["ok"] = true;
            return caps;
        }
    }

    /// <summary>
    /// 视口截图 / 视图捕获（对应 FreeCAD MCP 的 get_view 视觉闭环）。
    /// 经 IZSceneDoc.ExportImage 把当前 3D 场景导出为图片文件，返回路径供查看。
    /// 图片写入 %TEMP%（与诊断日志同目录，非用户个人目录）。
    /// 证据：2027 TLB members.csv —— IZSceneDoc.ExportImage(41/37/34)(bstrFileName, eZImageFormat, lWidth, lHeight, vbFitScene)。
    /// </summary>
    [ComVisible(false)]
    internal sealed class ToolGetView : McpToolBase
    {
        public override string Name { get { return "get_view"; } }
        public override string Category { get { return "view"; } }
        public override string Description { get { return "捕获当前 3D 场景视图为图片（IZSceneDoc.ExportImage），返回文件路径供查看。视觉闭环：建模后可截图比对几何。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["format"] = S.Enum("图片格式", new[] { "png", "bmp", "jpg" }, false, "png"),
                ["width"] = S.Num("图片宽（像素），默认 1280", false, 1280.0, true),
                ["height"] = S.Num("图片高（像素），默认 960", false, 960.0, true),
                ["fit_scene"] = S.Bool("是否自动适配整个场景，默认 true", false, true, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            string fmt = args.ParamString("format", "png").ToLowerInvariant();
            int w = Math.Max(1, args.ParamInt("width", 1280));
            int h = Math.Max(1, args.ParamInt("height", 960));
            bool fit = args.ParamBool("fit_scene", true);

            string ext = fmt == "jpg" ? "jpg" : (fmt == "bmp" ? "bmp" : "png");
            string path = Path.Combine(Path.GetTempPath(), "caxa3dmcp_view_" + Guid.NewGuid().ToString("N") + "." + ext);

            // 自主选择 eZImageFormat 枚举值：不依赖具体数值/成员名，按名称后缀匹配 bmp/png/jpg/jpeg；
            // 找不到则用枚举第一个值（通常是 BMP）兜底。
            eZImageFormat selected = default(eZImageFormat);
            try
            {
                bool matched = false;
                foreach (eZImageFormat cand in Enum.GetValues(typeof(eZImageFormat)))
                {
                    if (!matched) selected = cand; // 第一个作为兜底
                    string name = cand.ToString().ToLowerInvariant();
                    if (name.EndsWith(fmt) || (fmt == "jpg" && name.Contains("jpeg")) || name.EndsWith(ext))
                    { selected = cand; matched = true; break; }
                }
            }
            catch (Exception ex) { return Fail("enum_format", "枚举 eZImageFormat 解析失败：" + ex.Message); }

            var attempts = new List<object>();
            string err = null;
            try
            {
                ctx.Scene.ExportImage(path, selected, w, h, fit);
                attempts.Add(new Dictionary<string, object> { ["channel"] = "IZSceneDoc.ExportImage(41)", ["format"] = selected.ToString(), ["ok"] = true });
            }
            catch (Exception ex)
            {
                attempts.Add(new Dictionary<string, object> { ["channel"] = "IZSceneDoc.ExportImage(41)", ["ok"] = false, ["error"] = ex.Message });
                try
                {
                    ctx.Scene.ExportImageWithOptions(path, selected, w, h, fit, 0, 0);
                    attempts.Add(new Dictionary<string, object> { ["channel"] = "IZSceneDoc.ExportImageWithOptions(101)", ["ok"] = true });
                    err = null;
                }
                catch (Exception ex2)
                {
                    attempts.Add(new Dictionary<string, object> { ["channel"] = "IZSceneDoc.ExportImageWithOptions(101)", ["ok"] = false, ["error"] = ex2.Message });
                    err = ex.Message + " | " + ex2.Message;
                }
            }

            if (err != null)
                return new Dictionary<string, object> { ["ok"] = false, ["code"] = "export_failed", ["error"] = err, ["attempts"] = attempts };

            long size = -1;
            try { size = new FileInfo(path).Length; } catch { }
            return Ok(
                "filePath", path,
                "format", fmt,
                "width", w,
                "height", h,
                "fileSizeBytes", size,
                "note", "图片已写入 %TEMP%；MCP 客户端若支持 image 内容块可读取该路径查看。",
                "attempts", attempts);
        }
    }

    /// <summary>
    /// 事务化批量执行（对应 czuryk/SolidworksMCP 的 transactional plans：检查点/幂等/回滚的简化版）。
    /// 在一次 STA 回投内顺序执行多个工具调用，减少管道往返；stop_on_error=true 时任一步失败即中止（近似事务语义）。
    /// </summary>
    [ComVisible(false)]
    internal sealed class ToolBatchExec : McpToolBase
    {
        public override string Name { get { return "batch_exec"; } }
        public override string Category { get { return "system"; } }
        public override string Description { get { return "事务化批量执行：在一次 STA 回投内顺序执行多个工具调用，返回每步结果与结构化诊断。stop_on_error 默认 true（任一步失败即中止）。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["calls"] = S.Array("要顺序执行的工具调用列表，每项含 tool(工具名) 与 params(参数字典)",
                    new Dictionary<string, object>
                    {
                        ["tool"] = S.Str("工具名，如 create_box", true),
                        ["params"] = S.Schema(new Dictionary<string, object>(), new string[0])
                    }, "tool"),
                ["stop_on_error"] = S.Bool("任一步失败是否中止，默认 true", false, true, true)
            }, "calls");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            JValue calls = args["calls"];
            if (calls.Kind != JKind.Array || calls.Count == 0) return Fail("bad_calls", "calls 必须是非空数组");
            bool stopOnError = args.ParamBool("stop_on_error", true);

            var steps = new List<object>();
            int executed = 0;
            bool aborted = false;
            foreach (JValue call in calls.Arr)
            {
                string toolName = call["tool"].AsString() ?? "";
                JValue callArgs = call["params"];
                if (callArgs.Kind != JKind.Object) callArgs = new JValue { Kind = JKind.Object, Obj = new Dictionary<string, JValue>() };
                var step = new Dictionary<string, object> { ["tool"] = toolName };
                IMcpTool sub = McpToolRegistry.Find(toolName);
                if (sub == null)
                {
                    step["ok"] = false;
                    step["code"] = "unknown_tool";
                    step["error"] = "未注册的工具：" + toolName;
                }
                else
                {
                    try
                    {
                        object r = sub.Execute(ctx, callArgs);
                        step["ok"] = true;
                        step["result"] = r;
                    }
                    catch (Exception ex)
                    {
                        step["ok"] = false;
                        step["code"] = "exception";
                        step["error"] = ex.Message;
                    }
                }
                steps.Add(step);
                executed++;
                bool ok = (bool)step["ok"];
                if (!ok && stopOnError) { aborted = true; break; }
            }
            return new Dictionary<string, object>
            {
                ["ok"] = !aborted && steps.Count == executed,
                ["executed"] = executed,
                ["aborted"] = aborted,
                ["stopOnError"] = stopOnError,
                ["steps"] = steps
            };
        }
    }
}
