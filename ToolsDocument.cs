// Agent 分层工具集（一）：文档层 document + 场景层 scene。
// 取证：2027 TLB members.csv —— IZBaseApp.OpenNewDoc(25)/CreateNewDoc(66)/OpenFile2(30)/get_CatalogMgr(15)、
//       IZDoc.Save(9)/SaveAs(10)/get_Name(11)、IZPart.Export(15)、IZSceneDoc.CreateAssembly(8)/CreatePart(9)/
//       GetChildElements(7)/CreateProfile(22)、IZCatalogMgr.get_Count(10)/get_Catalog(11)/get_ActiveCatalog(12)、
//       IZCatalog.get_EntryCount(14)/get_Entry(13)、IZCatalogEntry.InsertElement(13)/DropObject(12)。
// 枚举 enums.csv：eZDocType.Z_SCENE=1；eZModelType.Z_MODEL_STEP=3 / Z_MODEL_X_T=0。
// 级别：document=attempt（未实机定案，靠写后回读）；scene 的 CreatePart/CreateAssembly/元素树=implemented。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    // ---------------- 文档层 ----------------

    [ComVisible(false)]
    internal sealed class ToolNewDocument : McpToolBase
    {
        public override string Name { get { return "new_document"; } }
        public override string Category { get { return "document"; } }
        public override string Description { get { return "新建 CAXA 3D 场景文档（OpenNewDoc(Z_SCENE)，失败回退 CreateNewDoc）。回读活动文档名与类型作判据。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["template"] = S.Str("可选模板名；默认用宿主默认模板"),
                ["visible"] = S.Bool("是否可见，默认 true", false, true, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            if (ctx.App == null) return AgentKit.Fail("no_app", "未取得 IZBaseApp");
            string tpl = args.ParamString("template", "");
            bool visible = args.ParamBool("visible", true);
            var attempts = AgentKit.Attempts();
            IZDoc doc = null;
            try
            {
                doc = ctx.App.OpenNewDoc(eZDocType.Z_SCENE, false, true, tpl);
                AgentKit.Attempt(attempts, "IZBaseApp.OpenNewDoc(Z_SCENE)", doc != null, doc == null ? "返回空" : "返回 IZDoc");
            }
            catch (Exception ex)
            {
                AgentKit.Attempt(attempts, "IZBaseApp.OpenNewDoc(Z_SCENE)", false, ex.Message);
                try
                {
                    doc = ctx.App.CreateNewDoc(eZDocType.Z_SCENE, false, true, tpl, visible);
                    AgentKit.Attempt(attempts, "IZBaseApp.CreateNewDoc(Z_SCENE)", doc != null, doc == null ? "返回空" : "返回 IZDoc");
                }
                catch (Exception ex2) { AgentKit.Attempt(attempts, "IZBaseApp.CreateNewDoc(Z_SCENE)", false, ex2.Message); }
            }
            if (doc == null) return AgentKit.Fail("new_document_failed", "两条新建文档通道均未返回 IZDoc（可能宿主弹模板框，本版不做 UI 代点）");

            string name = "", type = "";
            try { name = doc.Name; } catch { }
            try { type = doc.Type.ToString(); } catch { }
            IZDoc active = null;
            try { active = ctx.App.ActiveDoc; } catch { }
            bool verified = active != null;
            return AgentKit.Ok("verified", verified, "docName", name, "docType", type,
                               "activeDocName", verified ? active.Name : null,
                               "verifiedBy", "活动文档非空（不认 OpenNewDoc 返回值）", "attempts", attempts,
                               "level", "attempt");
        }
    }

    [ComVisible(false)]
    internal sealed class ToolOpenDocument : McpToolBase
    {
        public override string Name { get { return "open_document"; } }
        public override string Category { get { return "document"; } }
        public override string Description { get { return "打开已有 CAXA 文档（OpenFile2）。判据 = 活动文档回读到同名文档，不是返回值。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["path"] = S.Str("文档完整路径（.ics / .exb 等）"),
                ["read_only"] = S.Bool("只读打开，默认 false", false, false, true),
                ["visible"] = S.Bool("是否可见，默认 true", false, true, true)
            }, "path");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            string path = args.ParamString("path", "");
            if (string.IsNullOrEmpty(path)) return AgentKit.Fail("missing_path", "请提供 path");
            if (!File.Exists(path)) return AgentKit.Fail("file_not_found", "文件不存在：" + path);
            bool ro = args.ParamBool("read_only", false), vis = args.ParamBool("visible", true);
            bool alreadyOpen = false;
            var attempts = AgentKit.Attempts();
            IZDoc doc = null;
            try
            {
                doc = ctx.App.OpenFile2(path, ro, vis, out alreadyOpen);
                AgentKit.Attempt(attempts, "IZBaseApp.OpenFile2", doc != null, doc == null ? "返回空" : "返回 IZDoc");
            }
            catch (Exception ex) { AgentKit.Attempt(attempts, "IZBaseApp.OpenFile2", false, ex.Message); }
            if (doc == null) return AgentKit.Fail("open_document_failed", "OpenFile2 未返回 IZDoc（查 attempts 与宿主是否弹框）");
            string docName = "";
            try { docName = doc.Name; } catch { }
            string activeName = "";
            try { IZDoc a = ctx.App.ActiveDoc; if (a != null) activeName = a.Name; } catch { }
            bool verified = !string.IsNullOrEmpty(activeName);
            return AgentKit.Ok("verified", verified, "path", path, "docName", docName,
                               "activeDocName", activeName, "alreadyOpen", alreadyOpen,
                               "verifiedBy", "活动文档名回读", "attempts", attempts, "level", "attempt");
        }
    }

    /// <summary>STEP / Parasolid 导出的共用实现：IZPart.Export + 磁盘回读。</summary>
    [ComVisible(false)]
    internal static class ModelExport
    {
        public static object Run(McpToolContext ctx, JValue args, string tool, string label, eZModelType fmt, string ext)
        {
            if (ctx.Scene == null) return AgentKit.Fail("no_scene", AgentKit.NeedSceneMessage);
            IZPart part;
            string err;
            if (args.Has("name") || args.Has("id") || args.Has("shape_id") || args.Has("element_name") || args.Has("element_id"))
            { part = AgentKit.FindPart(ctx, args, out err); if (part == null) return AgentKit.Fail("element_not_found", err); }
            else { part = AgentKit.FirstPart(ctx); if (part == null) return AgentKit.Fail("no_part", "场景内没有可导出的 IZPart"); }

            string path = args.ParamString("path", "");
            if (string.IsNullOrEmpty(path)) path = AgentKit.TempPath("caxa3dmcp_" + tool, ext);
            if (File.Exists(path) && !args.ParamBool("overwrite", false))
                return AgentKit.Fail("already_exists", "目标文件已存在，需显式 overwrite=true：" + path);

            var attempts = AgentKit.Attempts();
            string callError = null;
            try
            {
                part.Export(path, fmt);
                AgentKit.Attempt(attempts, "IZPart.Export(" + label + ")", true, "未抛异常");
            }
            catch (Exception ex)
            {
                callError = ex.Message;
                AgentKit.Attempt(attempts, "IZPart.Export(" + label + ")", false, ex.Message);
            }
            long bytes;
            bool verified = AgentKit.FileWritten(path, out bytes);
            if (!verified)
                return AgentKit.Fail("export_not_verified",
                    "IZPart.Export 后目标文件不存在或为 0 字节（" + (callError ?? "无异常但无文件") + "）；不认 API 返回值");
            return AgentKit.Ok("verified", true, "filePath", path, "bytes", bytes,
                               "format", label, "partName", McpToolContext.SafeNameOf(part),
                               "callError", callError, "verifiedBy", "磁盘文件存在且长度>0",
                               "attempts", attempts, "level", "attempt",
                               "note", "IZPart.Export 只导出单个零件；整场景/装配导出通道未实证。");
        }
    }

    [ComVisible(false)]
    internal sealed class ToolExportStep : McpToolBase
    {
        public override string Name { get { return "export_step"; } }
        public override string Category { get { return "document"; } }
        public override string Description { get { return "把零件导出为 STEP（IZPart.Export + eZModelType.Z_MODEL_STEP）。成功判据 = 磁盘文件存在且长度>0。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["path"] = S.Str("输出 .stp/.step 完整路径；不传则写 %TEMP%"),
                ["name"] = S.Str("目标零件名；不传取第一个顶层零件"),
                ["id"] = S.Num("目标元素 Id"),
                ["overwrite"] = S.Bool("允许覆盖已有文件，默认 false", false, false, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        { return ModelExport.Run(ctx, args, "export_step", "Z_MODEL_STEP", eZModelType.Z_MODEL_STEP, ".stp"); }
    }

    [ComVisible(false)]
    internal sealed class ToolExportParasolid : McpToolBase
    {
        public override string Name { get { return "export_parasolid"; } }
        public override string Category { get { return "document"; } }
        public override string Description { get { return "把零件导出为 Parasolid X_T（IZPart.Export + eZModelType.Z_MODEL_X_T）。成功判据 = 磁盘文件存在且长度>0。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["path"] = S.Str("输出 .x_t 完整路径；不传则写 %TEMP%"),
                ["name"] = S.Str("目标零件名；不传取第一个顶层零件"),
                ["id"] = S.Num("目标元素 Id"),
                ["overwrite"] = S.Bool("允许覆盖已有文件，默认 false", false, false, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        { return ModelExport.Run(ctx, args, "export_parasolid", "Z_MODEL_X_T", eZModelType.Z_MODEL_X_T, ".x_t"); }
    }

    // ---------------- 场景层 ----------------

    [ComVisible(false)]
    internal sealed class ToolAddScene : McpToolBase
    {
        public override string Name { get { return "add_scene"; } }
        public override string Category { get { return "scene"; } }
        public override string Description { get { return "新建 3D 场景文档（等价于文档层 new_document 的 Z_SCENE 口径），供场景层搭骨架。"; } }
        public override object InputSchema() { return S.Schema(new Dictionary<string, object>()); }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            object r = new ToolNewDocument().Execute(ctx, args);
            var d = r as Dictionary<string, object>;
            if (d != null) { d["layer"] = "scene"; d["aliasOf"] = "new_document"; }
            return r;
        }
    }

    [ComVisible(false)]
    internal sealed class ToolAddAssembly : McpToolBase
    {
        public override string Name { get { return "add_assembly"; } }
        public override string Category { get { return "scene"; } }
        public override string Description { get { return "在当前场景新建装配（IZSceneDoc.CreateAssembly）。判据 = 顶层元素计数 +1。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object> { ["name"] = S.Str("可选：新建后立即重命名并回读") });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            int before = AgentKit.TopCount(ctx);
            var attempts = AgentKit.Attempts();
            object made = null;
            try
            {
                made = ctx.Scene.CreateAssembly();
                AgentKit.Attempt(attempts, "IZSceneDoc.CreateAssembly()", made != null, "返回 IZAssembly");
            }
            catch (Exception ex) { AgentKit.Attempt(attempts, "IZSceneDoc.CreateAssembly()", false, ex.Message); }
            if (made == null) return AgentKit.Fail("create_assembly_failed", "CreateAssembly 未返回对象（查 attempts）");
            return Finish(ctx, args, made, before, attempts, "assembly");
        }

        internal static object Finish(McpToolContext ctx, JValue args, object made, int before, List<object> attempts, string kind)
        {
            IZElement e = McpToolContext.AsElement(made);
            string wanted = args.ParamString("name", "");
            string setNameErr = null;
            if (!string.IsNullOrEmpty(wanted) && e != null)
            {
                if (!McpToolContext.TrySetName(made, wanted, out setNameErr))
                    AgentKit.Attempt(attempts, "IZElement.set_Name", false, setNameErr);
                else AgentKit.Attempt(attempts, "IZElement.set_Name", true, McpToolContext.SafeName(e));
            }
            int after = AgentKit.TopCount(ctx);
            bool verified = after == before + 1;
            return AgentKit.Ok("verified", verified, "kind", kind,
                               "topCountBefore", before, "topCountAfter", after,
                               "id", e == null ? -1 : AgentKit.SafeId(e),
                               "name", e == null ? null : AgentKit.SafeName(e),
                               "verifiedBy", "场景顶层元素计数 +1（不认 Create* 返回值）",
                               "attempts", attempts, "level", "implemented",
                               "note", verified ? null : "顶层计数未 +1，第三态：对象已造出但未确认挂进场景树");
        }
    }

    [ComVisible(false)]
    internal sealed class ToolAddPart : McpToolBase
    {
        public override string Name { get { return "add_part"; } }
        public override string Category { get { return "scene"; } }
        public override string Description { get { return "在当前场景新建零件（IZSceneDoc.CreatePart，与 create_box 同一实证通道）。判据 = 顶层元素计数 +1。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object> { ["name"] = S.Str("可选：新建后立即重命名并回读") });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            int before = AgentKit.TopCount(ctx);
            var attempts = AgentKit.Attempts();
            object made = null;
            try
            {
                made = ctx.Scene.CreatePart();
                AgentKit.Attempt(attempts, "IZSceneDoc.CreatePart()", made != null, "返回 IZPart");
            }
            catch (Exception ex) { AgentKit.Attempt(attempts, "IZSceneDoc.CreatePart()", false, ex.Message); }
            if (made == null) return AgentKit.Fail("create_part_failed", "CreatePart 未返回对象（查 attempts）");
            return ToolAddAssembly.Finish(ctx, args, made, before, attempts, "part");
        }
    }

    [ComVisible(false)]
    internal sealed class ToolListCatalogs : McpToolBase
    {
        public override string Name { get { return "list_catalogs"; } }
        public override string Category { get { return "scene"; } }
        public override string Description { get { return "列出 CAXA 设计元素目录（IZBaseApp.CatalogMgr → IZCatalog → IZCatalogEntry）。catalog_id / item_id 是会话内索引，拖放前需重列。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["max_items"] = S.Num("每个目录最多列几条，默认 30，硬上限 200", false, 30.0, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            if (ctx.App == null) return AgentKit.Fail("no_app", "未取得 IZBaseApp");
            int maxItems = Math.Min(200, Math.Max(1, args.ParamInt("max_items", 30)));
            var attempts = AgentKit.Attempts();
            object rawMgr = null;
            try
            {
                rawMgr = ctx.App.CatalogMgr;
                AgentKit.Attempt(attempts, "IZBaseApp.get_CatalogMgr(15)", rawMgr != null, rawMgr == null ? "返回空" : "返回目录管理器");
            }
            catch (Exception ex) { AgentKit.Attempt(attempts, "IZBaseApp.get_CatalogMgr(15)", false, ex.Message); }
            IZCatalogMgr mgr = rawMgr as IZCatalogMgr;
            if (mgr == null) return AgentKit.Fail("catalog_mgr_unavailable", "CatalogMgr 取不到或 QI 不到 IZCatalogMgr 表面（查 attempts）");

            var catalogs = new List<object>();
            int count = 0;
            try { count = mgr.Count; } catch (Exception ex) { AgentKit.Attempt(attempts, "IZCatalogMgr.get_Count", false, ex.Message); }
            string activeName = "";
            try { object ac = mgr.ActiveCatalog; if (ac != null) activeName = (ac as IZCatalog) == null ? "" : (ac as IZCatalog).Name; } catch { }
            int truncated = 0;
            for (int i = 0; i < count; i++)
            {
                var item = new Dictionary<string, object> { ["catalog_id"] = i };
                try
                {
                    object rawCat = mgr.get_Catalog(i);
                    IZCatalog cat = rawCat as IZCatalog;
                    if (cat == null) { item["thirdState"] = "catalog_not_readable"; continue; }
                    item["name"] = cat.Name;
                    item["filePath"] = cat.FilePath;
                    item["isActive"] = string.Equals(cat.Name, activeName, StringComparison.OrdinalIgnoreCase);
                    int entryCount = cat.EntryCount;
                    item["entryCount"] = entryCount;
                    var entries = new List<object>();
                    for (int j = 0; j < entryCount && j < maxItems; j++)
                    {
                        var row = new Dictionary<string, object> { ["item_id"] = j };
                        try
                        {
                            object rawEnt = cat.get_Entry(j);
                            IZCatalogEntry ent = rawEnt as IZCatalogEntry;
                            if (ent == null) { row["thirdState"] = "entry_not_readable"; }
                            else
                            {
                                row["name"] = ent.Name;
                                row["isGroup"] = ent.IsCatalogGroup();
                                try { row["creatorType"] = ent.ItemCreatorType.ToString(); } catch { }
                                try { row["classId"] = ent.ClassID; } catch { }
                            }
                        }
                        catch (Exception ex) { row["thirdState"] = ex.Message; }
                        entries.Add(row);
                    }
                    if (entryCount > maxItems) truncated += entryCount - maxItems;
                    item["entries"] = entries;
                }
                catch (Exception ex) { item["thirdState"] = ex.Message; }
                catalogs.Add(item);
            }
            return AgentKit.Ok("catalogCount", count, "catalogs", catalogs, "activeCatalog", activeName,
                               "truncatedEntries", truncated, "attempts", attempts, "level", "attempt",
                               "note", "索引按会话有效；IsCatalogGroup=true 的条目是子目录，需先 OpenSubCatalog。");
        }
    }

    [ComVisible(false)]
    internal sealed class ToolGetActiveSceneGraph : McpToolBase
    {
        public override string Name { get { return "get_active_scene_graph"; } }
        public override string Category { get { return "scene"; } }
        public override string Description { get { return "读取活动场景图（顶层 + 一层子节点），带深度/节点预算；truncated>0 时不得对总节点数下结论。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["max_nodes"] = S.Num("最多输出节点数，默认 200，硬上限 1000", false, 200.0, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            int maxNodes = Math.Min(1000, Math.Max(1, args.ParamInt("max_nodes", 200)));
            var tops = AgentKit.Tops(ctx);
            int used = 0, truncated = 0;
            var roots = new List<object>();
            foreach (IZElement e in tops)
            {
                if (used >= maxNodes) { truncated++; continue; }
                used++;
                var node = new Dictionary<string, object>
                {
                    ["id"] = AgentKit.SafeId(e),
                    ["name"] = AgentKit.SafeName(e),
                    ["type"] = SafeType(e),
                    ["isPart"] = e is IZPart,
                    ["depth"] = 0
                };
                var kids = new List<object>();
                try
                {
                    foreach (object o in McpToolContext.UnpackArray(e.GetChildren()))
                    {
                        IZElement c = o as IZElement;
                        if (c == null) continue;
                        if (used >= maxNodes) { truncated++; continue; }
                        used++;
                        kids.Add(new Dictionary<string, object>
                        {
                            ["id"] = AgentKit.SafeId(c),
                            ["name"] = AgentKit.SafeName(c),
                            ["type"] = SafeType(c),
                            ["isPart"] = c is IZPart,
                            ["depth"] = 1
                        });
                    }
                }
                catch (Exception ex) { node["childrenError"] = ex.Message; }
                node["children"] = kids;
                roots.Add(node);
            }
            return AgentKit.Ok("roots", roots, "nodeCount", used, "topCount", tops.Count,
                               "truncated", truncated, "verified", true, "level", "implemented");
        }

        private static string SafeType(IZElement e)
        {
            try { return e.Type.ToString(); } catch { return "?"; }
        }
    }
}
