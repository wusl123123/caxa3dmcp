// 工具集：toolbox 类 —— CAXA 3D 标准件库 XML / .cache / xlsx 纯文件编辑工具（7 个）。
// 来源：移植自 caxa3dtoolboxmaker（独立 EXE）的已验证内核：
//   TbXmlCore.cs / TbCacheCore.cs / TbXlsxIO.cs / TbToolboxPath.cs（命名空间 caxa3dmcp.toolbox）。
// 铁律：
//   1) 这些工具不碰 ICAPI / COM，纯文件处理；经桥 STA 回投执行只是为了复用统一通道，Execute 内不使用 ctx.App。
//   2) 判成功只认写后回读 verified + 权威副作用（磁盘文件存在/字节变化/回读值一致），不看"没抛异常"。
//   3) 所有写操作需 confirm=true；写前时间戳备份到目标文件同目录 _backup_原始数据\；UTF-8 无 BOM + CRLF 保格式。
//   4) 第三态：定位不到 .cache 对应行 / 名称或 GUID 不一致 / 有歧义 → 明确拒绝并点名，不臆造。
//   5) 级别：内核在 toolboxmaker 已离屏验证（implemented）；作为 MCP 工具整链未实机 → 标 attempt。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using caxa3dmcp.toolbox;

namespace caxa3dmcp
{
    // ============ 纯逻辑辅助（无 UI / 无 DataTable，直接操作 XmlCore.Doc） ============
    [ComVisible(false)]
    internal static class ToolboxOps
    {
        internal static string NormGuid(string s)
        {
            if (s == null) return "";
            return s.Trim().Trim('{', '}').Trim().ToUpperInvariant();
        }
        internal static bool SameGuid(string a, string b)
        {
            string x = NormGuid(a), y = NormGuid(b);
            return x.Length > 0 && x == y;
        }
        internal static bool SameName(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        internal static string BackupDir(string filePath)
        {
            try
            {
                string dir = Path.GetDirectoryName(filePath);
                return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "_backup_原始数据");
            }
            catch { return null; }
        }

        // 在 .cache 中按 name/GUID 定位唯一行：两类命中须指同一行，任一重复即歧义（第三态不猜）
        internal static Dictionary<string, string> FindCacheRow(CacheCore.CacheDoc cd, string nameHint, string guidHint)
        {
            if (cd == null) return null;
            string ng = NormGuid(guidHint), nn = (nameHint ?? "").Trim();
            Dictionary<string, string> byGuid = null, byName = null;
            int guidHits = 0, nameHits = 0;
            foreach (CacheCore.Entry e in cd.Entries)
            {
                string name = XmlCore.Get(e.Attrs, "name") ?? "";
                string guid = XmlCore.Get(e.Attrs, "GUID") ?? "";
                if (ng.Length > 0 && NormGuid(guid) == ng)
                {
                    byGuid = new Dictionary<string, string> { ["name"] = name, ["GUID"] = guid };
                    guidHits++;
                }
                if (nn.Length > 0 && string.Equals((name ?? "").Trim(), nn, StringComparison.OrdinalIgnoreCase))
                {
                    byName = new Dictionary<string, string> { ["name"] = name, ["GUID"] = guid };
                    nameHits++;
                }
            }
            if (guidHits > 1 || nameHits > 1) return null;
            if (byGuid != null && byName != null)
                return SameGuid(byGuid["GUID"], byName["GUID"]) ? byGuid : null;
            return byGuid ?? byName;
        }

        // 当前 XML 对应的 .cache 行：族名(所在文件夹名) + CurNodeInfo@GUID 联合定位；GUID 权威回退
        internal static Dictionary<string, string> FindCacheRowForXml(CacheCore.CacheDoc cd, string xmlPath, XmlCore.Doc doc)
        {
            string folder = "";
            if (!string.IsNullOrEmpty(xmlPath))
                folder = Path.GetFileName(Path.GetDirectoryName(xmlPath)) ?? "";
            string xname = doc != null ? (XmlCore.Get(doc.Cur, "name") ?? "") : "";
            string xguid = doc != null ? (XmlCore.Get(doc.Cur, "GUID") ?? "") : "";
            Dictionary<string, string> hit = FindCacheRow(cd, folder, xguid);
            if (hit != null) return hit;
            hit = FindCacheRow(cd, "", xguid);
            if (hit != null) return hit;
            return FindCacheRow(cd, xname, "");
        }

        // XML 所在文件夹的上一级目录内的 .cache（字面名为 .cache 者优先）
        internal static string InferCacheForXml(string xmlPath)
        {
            if (string.IsNullOrEmpty(xmlPath) || !File.Exists(xmlPath)) return null;
            string dir = Path.GetDirectoryName(xmlPath);
            if (string.IsNullOrEmpty(dir)) return null;
            DirectoryInfo parent = Directory.GetParent(dir);
            if (parent == null) return null;
            string[] cands;
            try { cands = Directory.GetFiles(parent.FullName, "*.cache"); }
            catch { return null; }
            foreach (string c in cands)
                if (string.Equals(Path.GetFileName(c), ".cache", StringComparison.OrdinalIgnoreCase)) return c;
            return cands.Length > 0 ? cands[0] : null;
        }

        internal static string RowKeyToHeader(XmlCore.Doc doc, string rowKey)
        {
            if (doc == null || doc.Params == null) return rowKey;
            foreach (List<Attr> p in doc.Params)
            {
                string dim = XmlCore.Get(p, "Dimension");
                string disp = XmlCore.Get(p, "Display_name");
                if (!string.IsNullOrEmpty(dim) && dim == rowKey && !string.IsNullOrEmpty(disp)) return disp;
            }
            return rowKey;
        }
        internal static string HeaderToRowKey(XmlCore.Doc doc, string header)
        {
            if (doc == null || doc.Params == null) return header;
            foreach (List<Attr> p in doc.Params)
            {
                string dim = XmlCore.Get(p, "Dimension");
                string disp = XmlCore.Get(p, "Display_name");
                if (!string.IsNullOrEmpty(disp) && disp == header && !string.IsNullOrEmpty(dim)) return dim;
            }
            return header;
        }

        internal static bool ParamCellIs(XmlCore.Doc doc, int row, string col, string value)
        {
            if (doc.Params == null || row < 0 || row >= doc.Params.Count) return false;
            return string.Equals(XmlCore.Get(doc.Params[row], col) ?? "", value ?? "", StringComparison.Ordinal);
        }
        internal static int FindTemplateParamRow(XmlCore.Doc doc, out string source)
        {
            source = "内置SC默认值";
            if (doc.Params == null || doc.Params.Count == 0) return -1;
            string[] names = { "SC图厚", "SC规格" };
            foreach (string want in names)
                for (int i = 0; i < doc.Params.Count; i++)
                    if (ParamCellIs(doc, i, "Custom_property", want) || ParamCellIs(doc, i, "Dimension", want) ||
                        ParamCellIs(doc, i, "Display_name", want))
                    { source = want + " 行"; return i; }
            for (int i = 0; i < doc.Params.Count; i++)
            {
                string cp = XmlCore.Get(doc.Params[i], "Custom_property") ?? "";
                if (cp.StartsWith("SC", StringComparison.Ordinal)) { source = cp + " 行"; return i; }
            }
            return -1;
        }
        internal static int FindParamRowByName(XmlCore.Doc doc, string name)
        {
            if (string.IsNullOrEmpty(name) || doc.Params == null) return -1;
            string[] cols = { "Dimension", "Custom_property", "Display_name" };
            for (int i = 0; i < doc.Params.Count; i++)
                foreach (string c in cols)
                    if (ParamCellIs(doc, i, c, name)) return i;
            return -1;
        }

        // 纯 doc 增列 + 参数表联动（等价 toolboxmaker AddColumnCore，无 DataTable）
        internal static string AddColumn(XmlCore.Doc doc, string name)
        {
            if (doc.RowKeys.Contains(name)) return null;
            doc.RowKeys.Add(name);
            foreach (List<Attr> row in doc.Rows) row.Add(new Attr(name, ""));

            string source;
            int ti = FindTemplateParamRow(doc, out source);
            List<Attr> pa = new List<Attr>();
            if (ti >= 0)
            {
                foreach (Attr a in doc.Params[ti]) pa.Add(new Attr(a.Key, a.Value));
            }
            else
            {
                string[][] def = {
                    new string[] { "Dimension", "" }, new string[] { "Custom_property", name },
                    new string[] { "Display_name", name }, new string[] { "hasBeenAdded", "TRUE" },
                    new string[] { "Grid_item_type", "Dim_String_Edit" }, new string[] { "Grid_item_state", "DIM_CTRL_HIDE" },
                    new string[] { "unit_type", "None" }, new string[] { "AddtoDisplayName", "FALSE" },
                    new string[] { "HasDelimiters", "TRUE" }, new string[] { "Delimiters", "" } };
                foreach (string[] kv in def) pa.Add(new Attr(kv[0], kv[1]));
            }
            string cp = XmlCore.Get(pa, "Custom_property");
            if (ti < 0 || !string.IsNullOrEmpty(cp)) XmlCore.Set(pa, "Custom_property", name);
            else XmlCore.Set(pa, "Dimension", name);
            XmlCore.Set(pa, "Display_name", name);
            doc.Params.Add(pa);
            doc.ParamKeysUnion = RebuildParamKeys(doc);
            return source;
        }

        // 纯 doc 删列 + 参数表联动（等价 toolboxmaker DelColumnCore）
        internal static int DelColumn(XmlCore.Doc doc, string name)
        {
            if (doc.RowKeys.Count <= 1) return -2; // 至少保留一列
            int ci = doc.RowKeys.IndexOf(name);
            if (ci < 0) return -1;
            doc.RowKeys.RemoveAt(ci);
            foreach (List<Attr> row in doc.Rows) row.RemoveAll(a => a.Key == name);
            int pi = FindParamRowByName(doc, name);
            if (pi >= 0)
            {
                doc.Params.RemoveAt(pi);
                doc.ParamKeysUnion = RebuildParamKeys(doc);
            }
            return pi;
        }

        static List<string> RebuildParamKeys(XmlCore.Doc doc)
        {
            List<string> keys = new List<string>();
            if (doc.Params == null) return keys;
            foreach (List<Attr> p in doc.Params)
                foreach (Attr a in p)
                    if (!keys.Contains(a.Key)) keys.Add(a.Key);
            return keys;
        }

        internal static List<string> JArrayToStrings(JValue v)
        {
            List<string> r = new List<string>();
            if (v == null || v.Kind != JKind.Array || v.Arr == null) return r;
            foreach (JValue x in v.Arr)
            {
                if (x.Kind == JKind.String) r.Add(x.Str);
                else if (x.Kind == JKind.Number) r.Add(x.Num.ToString(CultureInfo.InvariantCulture));
                else if (x.Kind == JKind.Bool) r.Add(x.Bool ? "TRUE" : "FALSE");
                else r.Add("");
            }
            return r;
        }

        internal static List<Attr> JObjectToAttrs(JValue v)
        {
            List<Attr> r = new List<Attr>();
            if (v == null || v.Kind != JKind.Object || v.Obj == null) return r;
            foreach (KeyValuePair<string, JValue> kv in v.Obj)
                r.Add(new Attr(kv.Key, kv.Value == null ? "" : kv.Value.ParamString("")));
            return r;
        }

        internal static Dictionary<string, object> AttrsToDict(List<Attr> attrs)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            if (attrs == null) return d;
            foreach (Attr a in attrs) d[a.Key] = a.Value;
            return d;
        }

        internal static List<object> AttrsListToObjects(List<List<Attr>> list)
        {
            List<object> r = new List<object>();
            if (list == null) return r;
            foreach (List<Attr> row in list) r.Add(AttrsToDict(row));
            return r;
        }
    }

    // ============ 1. toolbox_resolve_paths（只读反查 + 三栏联动推断） ============
    [ComVisible(false)]
    internal sealed class ToolToolboxResolvePaths : McpToolBase
    {
        public override string Name { get { return "toolbox_resolve_paths"; } }
        public override string Category { get { return "toolbox"; } }
        public override string Description { get { return "[toolbox] 只读：反查系统安装的 CAXA ToolBox 根目录，并按给定 xml/ics/.cache 推断同目录同名 sibling 与上一级 .cache（只认确实存在的同名文件，第三态不臆造）。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["path"] = S.Str("可选：已知的族 XML / .ics / .cache 路径；传入则推断其余栏。留空只回 ToolBox 根目录。")
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            string root = null;
            try { root = ToolboxPath.Resolve(); } catch (Exception ex) { ctx.Log("ToolboxPath.Resolve: " + ex.Message); }
            string path = args.ParamString("path", "");
            string kind = "none", xml = "", ics = "", cache = "";
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
                string dir = Path.GetDirectoryName(path);
                string stem = Path.GetFileNameWithoutExtension(path);
                if (ext == ".xml")
                {
                    kind = "xml"; xml = path;
                    string sibling = Path.Combine(dir, stem + ".ics");
                    if (File.Exists(sibling)) ics = sibling;
                    string c = ToolboxOps.InferCacheForXml(xml); if (c != null) cache = c;
                }
                else if (ext == ".ics")
                {
                    kind = "ics"; ics = path;
                    string sibling = Path.Combine(dir, stem + ".XML");
                    if (!File.Exists(sibling)) sibling = Path.Combine(dir, stem + ".xml");
                    if (File.Exists(sibling)) xml = sibling;
                    if (!string.IsNullOrEmpty(xml)) { string c = ToolboxOps.InferCacheForXml(xml); if (c != null) cache = c; }
                }
                else if (ext == ".cache")
                {
                    kind = "cache"; cache = path;
                }
                else kind = "other";
            }
            else if (!string.IsNullOrEmpty(path)) kind = "not_found";

            return Ok(
                "toolboxRoot", root ?? "",
                "toolboxRootFound", !string.IsNullOrEmpty(root),
                "inputKind", kind,
                "xml", xml, "ics", ics, "cache", cache,
                "xmlExists", !string.IsNullOrEmpty(xml) && File.Exists(xml),
                "icsExists", !string.IsNullOrEmpty(ics) && File.Exists(ics),
                "cacheExists", !string.IsNullOrEmpty(cache) && File.Exists(cache));
        }
    }

    // ============ 2. toolbox_read_xml（只读解析族 XML） ============
    [ComVisible(false)]
    internal sealed class ToolToolboxReadXml : McpToolBase
    {
        public override string Name { get { return "toolbox_read_xml"; } }
        public override string Category { get { return "toolbox"; } }
        public override string Description { get { return "[toolbox] 只读：保格式解析标准件库族 XML，返回族信息(CurNodeInfo)、参数表、规格表行键与行数据（可限行数）。读不到/格式不符返回第三态。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["xml_path"] = S.Str("族 XML 完整路径（必填）。", true),
                ["max_rows"] = S.Num("最多返回规格表行数，0=全部；默认 200。")
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            string path = args.ParamString("xml_path", "");
            int maxRows = args.ParamInt("max_rows", 200);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Fail("xml_path 未指向存在的文件：" + path);
            try
            {
                XmlCore.Doc d = XmlCore.Load(path);
                List<object> rows = ToolboxOps.AttrsListToObjects(d.Rows);
                bool truncated = maxRows > 0 && rows.Count > maxRows;
                if (truncated) rows = rows.GetRange(0, maxRows);
                return Ok(
                    "path", path,
                    "cur", ToolboxOps.AttrsToDict(d.Cur),
                    "name", XmlCore.Get(d.Cur, "name") ?? "",
                    "guid", XmlCore.Get(d.Cur, "GUID") ?? "",
                    "rowKeys", d.RowKeys,
                    "rowCount", d.Rows.Count,
                    "rows", rows,
                    "rowsTruncated", truncated,
                    "paramCount", d.Params.Count,
                    "paramKeys", d.ParamKeysUnion,
                    "params", ToolboxOps.AttrsListToObjects(d.Params));
            }
            catch (Exception ex) { return Fail("解析 XML 失败（第三态，不臆造）：" + ex.Message); }
        }
    }

    // ============ 3. toolbox_edit_xml（写：改单元格/增删行列/改族信息/改参数） ============
    [ComVisible(false)]
    internal sealed class ToolToolboxEditXml : McpToolBase
    {
        public override string Name { get { return "toolbox_edit_xml"; } }
        public override string Category { get { return "toolbox"; } }
        public override string Description { get { return "[toolbox] 写：保格式编辑族 XML 并回读校验（需 confirm=true）。op=set_cell/add_row/del_row/add_col/del_col/set_cur/set_param；写前时间戳备份；verified 只由写后回读目标值一致判定。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["xml_path"] = S.Str("族 XML 完整路径（必填）。", true),
                ["op"] = S.Enum("操作：set_cell/add_row/del_row/add_col/del_col/set_cur/set_param。", new[] { "set_cell", "add_row", "del_row", "add_col", "del_col", "set_cur", "set_param" }, true),
                ["row_index"] = S.Num("set_cell/del_row 的 0 基行号。"),
                ["col_key"] = S.Str("set_cell 的列名（RowKeys 中的属性名）。"),
                ["value"] = S.Str("set_cell/set_cur/set_param 的目标值。"),
                ["key"] = S.Str("set_cur/set_param 的属性名。"),
                ["param_index"] = S.Num("set_param 的 0 基参数行号。"),
                ["values"] = S.Array("add_row：按当前 RowKeys 顺序的值数组（字符串）。", new Dictionary<string, object>()),
                ["col_name"] = S.Str("add_col/del_col 的列名。"),
                ["confirm"] = S.Bool("必须为 true 才执行写操作。")
            }, "xml_path", "op");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            if (!args.ParamBool("confirm", false)) return Fail("需要 confirm=true 才执行写入（保护原始数据）。");
            string path = args.ParamString("xml_path", "");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Fail("xml_path 未指向存在的文件：" + path);
            string op = args.ParamString("op", "");
            long beforeLen = new FileInfo(path).Length;
            try
            {
                XmlCore.Doc d = XmlCore.Load(path);
                string detail = "";
                Func<XmlCore.Doc, bool> verify = null;

                switch (op)
                {
                    case "set_cell":
                    {
                        int ri = args.ParamInt("row_index", -1);
                        string key = args.ParamString("col_key", "");
                        string val = args.ParamString("value", "");
                        if (ri < 0 || ri >= d.Rows.Count) return Fail("row_index 越界：" + ri + "（行数 " + d.Rows.Count + "）");
                        if (!d.RowKeys.Contains(key)) return Fail("列名不存在：" + key);
                        XmlCore.Set(d.Rows[ri], key, val);
                        detail = "行 " + ri + " 列 " + key;
                        verify = dd => ri < dd.Rows.Count && (XmlCore.Get(dd.Rows[ri], key) ?? "") == val;
                        break;
                    }
                    case "add_row":
                    {
                        List<string> vals = ToolboxOps.JArrayToStrings(args["values"]);
                        if (vals.Count == 0) vals = new List<string>();
                        List<Attr> row = new List<Attr>();
                        for (int c = 0; c < d.RowKeys.Count; c++)
                            row.Add(new Attr(d.RowKeys[c], c < vals.Count ? vals[c] : ""));
                        d.Rows.Add(row);
                        int expect = d.Rows.Count;
                        detail = "行数 -> " + expect;
                        verify = dd => dd.Rows.Count == expect;
                        break;
                    }
                    case "del_row":
                    {
                        int ri = args.ParamInt("row_index", -1);
                        if (ri < 0 || ri >= d.Rows.Count) return Fail("row_index 越界：" + ri);
                        d.Rows.RemoveAt(ri);
                        int expect = d.Rows.Count;
                        detail = "行数 -> " + expect;
                        verify = dd => dd.Rows.Count == expect;
                        break;
                    }
                    case "add_col":
                    {
                        string name = args.ParamString("col_name", "");
                        if (string.IsNullOrEmpty(name)) return Fail("col_name 不能为空");
                        if (d.RowKeys.Contains(name)) return Fail("列名已存在：" + name);
                        int expectParams = d.Params.Count + 1;
                        string tpl = ToolboxOps.AddColumn(d, name);
                        detail = "列名 " + name + "，参数表模板=" + tpl;
                        verify = dd => dd.RowKeys.Contains(name) && dd.Params.Count == expectParams;
                        break;
                    }
                    case "del_col":
                    {
                        string name = args.ParamString("col_name", "");
                        int rc = ToolboxOps.DelColumn(d, name);
                        if (rc == -2) return Fail("至少保留一列");
                        if (rc == -1) return Fail("列名不存在：" + name);
                        detail = "列名 " + name + "，参数表删除行=" + (rc >= 0 ? rc + 1 : 0);
                        verify = dd => !dd.RowKeys.Contains(name);
                        break;
                    }
                    case "set_cur":
                    {
                        string key = args.ParamString("key", "");
                        string val = args.ParamString("value", "");
                        if (string.IsNullOrEmpty(key)) return Fail("key 不能为空");
                        XmlCore.Set(d.Cur, key, val);
                        detail = "族信息 " + key;
                        verify = dd => (XmlCore.Get(dd.Cur, key) ?? "") == val;
                        break;
                    }
                    case "set_param":
                    {
                        int pi = args.ParamInt("param_index", -1);
                        string key = args.ParamString("key", "");
                        string val = args.ParamString("value", "");
                        if (pi < 0 || pi >= d.Params.Count) return Fail("param_index 越界：" + pi);
                        if (string.IsNullOrEmpty(key)) return Fail("key 不能为空");
                        XmlCore.Set(d.Params[pi], key, val);
                        if (!d.ParamKeysUnion.Contains(key)) d.ParamKeysUnion.Add(key);
                        detail = "参数行 " + pi + " 属性 " + key;
                        verify = dd => pi < dd.Params.Count && (XmlCore.Get(dd.Params[pi], key) ?? "") == val;
                        break;
                    }
                    default:
                        return Fail("未知 op：" + op);
                }

                XmlCore.Save(path, d, ToolboxOps.BackupDir(path));
                // 写后回读判据
                XmlCore.Doc re = XmlCore.Load(path);
                bool verified = verify != null && verify(re);
                long afterLen = new FileInfo(path).Length;
                return Ok(
                    "op", op, "detail", detail,
                    "verified", verified,
                    "fileBytesBefore", beforeLen, "fileBytesAfter", afterLen,
                    "backupDir", ToolboxOps.BackupDir(path) ?? "",
                    "rowCount", re.Rows.Count, "paramCount", re.Params.Count,
                    "note", verified ? "写后回读目标值一致" : "写后回读目标值不一致，视为未成功");
            }
            catch (Exception ex) { return Fail("编辑 XML 失败：" + ex.Message); }
        }
    }

    // ============ 4. toolbox_read_cache（只读解析 .cache 索引） ============
    [ComVisible(false)]
    internal sealed class ToolToolboxReadCache : McpToolBase
    {
        public override string Name { get { return "toolbox_read_cache"; } }
        public override string Category { get { return "toolbox"; } }
        public override string Description { get { return "[toolbox] 只读：解析 ToolBox 类目级 .cache 索引（Root/Version/FolderList），返回条目与属性并集列；坏根/缺文件返回第三态。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["cache_path"] = S.Str(".cache 完整路径（必填）。", true),
                ["max_entries"] = S.Num("最多返回条目数，0=全部；默认 200。")
            }, "cache_path");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            string path = args.ParamString("cache_path", "");
            int max = args.ParamInt("max_entries", 200);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Fail("cache_path 未指向存在的文件：" + path);
            try
            {
                CacheCore.CacheDoc cd = CacheCore.Load(path);
                List<object> entries = new List<object>();
                foreach (CacheCore.Entry e in cd.Entries) entries.Add(ToolboxOps.AttrsToDict(e.Attrs));
                bool truncated = max > 0 && entries.Count > max;
                if (truncated) entries = entries.GetRange(0, max);
                return Ok(
                    "path", path, "version", cd.Version ?? "",
                    "entryCount", cd.Entries.Count, "keys", cd.KeysUnion,
                    "entries", entries, "entriesTruncated", truncated);
            }
            catch (Exception ex) { return Fail("解析 .cache 失败（第三态，不臆造）：" + ex.Message); }
        }
    }

    // ============ 5. toolbox_edit_cache（写：改属性/增删条目） ============
    [ComVisible(false)]
    internal sealed class ToolToolboxEditCache : McpToolBase
    {
        public override string Name { get { return "toolbox_edit_cache"; } }
        public override string Category { get { return "toolbox"; } }
        public override string Description { get { return "[toolbox] 写：保格式编辑 .cache 索引并回读校验（需 confirm=true）。op=set_attr/add_entry/del_entry；未编辑字节 round-trip 不变；写前时间戳备份；verified 只由写后回读判定。GUID 不伪造（改它需自行同步族 XML）。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["cache_path"] = S.Str(".cache 完整路径（必填）。", true),
                ["op"] = S.Enum("操作：set_attr/add_entry/del_entry。", new[] { "set_attr", "add_entry", "del_entry" }, true),
                ["index"] = S.Num("set_attr/del_entry 的 0 基条目号。"),
                ["key"] = S.Str("set_attr 的属性名。"),
                ["value"] = S.Str("set_attr 的目标值。"),
                ["attrs"] = S.Str("add_entry：属性名=值的 JSON 对象字符串，如 {\"name\":\"X\",\"GUID\":\"{...}\"}。"),
                ["confirm"] = S.Bool("必须为 true 才执行写操作。")
            }, "cache_path", "op");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            if (!args.ParamBool("confirm", false)) return Fail("需要 confirm=true 才执行写入（保护原始数据）。");
            string path = args.ParamString("cache_path", "");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Fail("cache_path 未指向存在的文件：" + path);
            string op = args.ParamString("op", "");
            long beforeLen = new FileInfo(path).Length;
            try
            {
                CacheCore.CacheDoc cd = CacheCore.Load(path);
                string detail = "";
                Func<CacheCore.CacheDoc, bool> verify = null;
                switch (op)
                {
                    case "set_attr":
                    {
                        int i = args.ParamInt("index", -1);
                        string key = args.ParamString("key", "");
                        string val = args.ParamString("value", "");
                        if (i < 0 || i >= cd.Entries.Count) return Fail("index 越界：" + i);
                        if (string.IsNullOrEmpty(key)) return Fail("key 不能为空");
                        XmlCore.Set(cd.Entries[i].Attrs, key, val);
                        if (!cd.KeysUnion.Contains(key)) cd.KeysUnion.Add(key);
                        detail = "条目 " + i + " 属性 " + key;
                        verify = dd => i < dd.Entries.Count && (XmlCore.Get(dd.Entries[i].Attrs, key) ?? "") == val;
                        break;
                    }
                    case "add_entry":
                    {
                        string raw = args.ParamString("attrs", "{}");
                        JValue jobj;
                        try { jobj = JsonRpc.Parse(raw); }
                        catch { return Fail("attrs 不是合法 JSON 对象：" + raw); }
                        List<Attr> attrs = ToolboxOps.JObjectToAttrs(jobj);
                        if (attrs.Count == 0) return Fail("attrs 为空，未新增条目");
                        cd.Entries.Add(new CacheCore.Entry { Attrs = attrs });
                        foreach (Attr a in attrs) if (!cd.KeysUnion.Contains(a.Key)) cd.KeysUnion.Add(a.Key);
                        int expect = cd.Entries.Count;
                        detail = "条目数 -> " + expect;
                        verify = dd => dd.Entries.Count == expect;
                        break;
                    }
                    case "del_entry":
                    {
                        int i = args.ParamInt("index", -1);
                        if (i < 0 || i >= cd.Entries.Count) return Fail("index 越界：" + i);
                        cd.Entries.RemoveAt(i);
                        int expect = cd.Entries.Count;
                        detail = "条目数 -> " + expect;
                        verify = dd => dd.Entries.Count == expect;
                        break;
                    }
                    default: return Fail("未知 op：" + op);
                }
                CacheCore.Save(path, cd, ToolboxOps.BackupDir(path));
                CacheCore.CacheDoc re = CacheCore.Load(path);
                bool verified = verify != null && verify(re);
                long afterLen = new FileInfo(path).Length;
                return Ok(
                    "op", op, "detail", detail, "verified", verified,
                    "fileBytesBefore", beforeLen, "fileBytesAfter", afterLen,
                    "backupDir", ToolboxOps.BackupDir(path) ?? "",
                    "entryCount", re.Entries.Count,
                    "note", verified ? "写后回读目标值一致" : "写后回读目标值不一致，视为未成功");
            }
            catch (Exception ex) { return Fail("编辑 .cache 失败：" + ex.Message); }
        }
    }

    // ============ 6. toolbox_export_excel（导出规格表到 xlsx，B1/B2 取 .cache） ============
    [ComVisible(false)]
    internal sealed class ToolToolboxExportExcel : McpToolBase
    {
        public override string Name { get { return "toolbox_export_excel"; } }
        public override string Category { get { return "toolbox"; } }
        public override string Description { get { return "[toolbox] 写（仅新文件）：把族 XML 规格表导出为 CAXA 模板格式 xlsx；B1 零件名/B2 GUID 取 .cache 对应行，定位不到或有歧义则拒绝（第三态）。判据=磁盘>0字节且回读表头/行数一致。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["xml_path"] = S.Str("族 XML 完整路径（必填）。", true),
                ["cache_path"] = S.Str("对应 .cache 路径；留空则从 XML 上一级目录自动反查。"),
                ["output_path"] = S.Str("输出 xlsx 完整路径（必填）。", true)
            }, "xml_path", "output_path");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            string xmlPath = args.ParamString("xml_path", "");
            string cachePath = args.ParamString("cache_path", "");
            string outPath = args.ParamString("output_path", "");
            if (string.IsNullOrEmpty(xmlPath) || !File.Exists(xmlPath)) return Fail("xml_path 未指向存在的文件：" + xmlPath);
            if (string.IsNullOrEmpty(outPath)) return Fail("output_path 不能为空");
            try
            {
                XmlCore.Doc d = XmlCore.Load(xmlPath);
                if (string.IsNullOrEmpty(cachePath)) cachePath = ToolboxOps.InferCacheForXml(xmlPath);
                if (string.IsNullOrEmpty(cachePath) || !File.Exists(cachePath))
                    return Fail("导出拒绝：未找到对应 .cache，无法保证 B1/B2 与 .cache 一致（第三态，不臆造）。");
                CacheCore.CacheDoc cd = CacheCore.Load(cachePath);
                Dictionary<string, string> id = ToolboxOps.FindCacheRowForXml(cd, xmlPath, d);
                if (id == null)
                    return Fail("导出拒绝：.cache 中未唯一找到当前族对应行（name/GUID 歧义或缺失，第三态不臆造）。");

                List<string> headers = new List<string>();
                foreach (string k in d.RowKeys) headers.Add(ToolboxOps.RowKeyToHeader(d, k));
                List<List<string>> data = new List<List<string>>();
                foreach (List<Attr> row in d.Rows)
                {
                    List<string> r = new List<string>();
                    foreach (string k in d.RowKeys) r.Add(XmlCore.Get(row, k) ?? "");
                    data.Add(r);
                }
                XlsxIO.WriteTemplate(outPath, id["name"], id["GUID"], headers, data);
                // 判据：磁盘文件 > 0 + 回读表头行数一致
                if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0)
                    return Fail("导出失败：输出文件不存在或 0 字节。");
                List<List<string>> back = XlsxIO.ReadSheet(outPath);
                int dataRows = data.Count;
                bool verified = back.Count >= 6 + dataRows; // 行1/2/4/5 + 数据
                return Ok(
                    "outputPath", outPath, "fileBytes", new FileInfo(outPath).Length,
                    "partName", id["name"], "guid", id["GUID"],
                    "rowCount", dataRows, "colCount", headers.Count,
                    "verified", verified,
                    "note", verified ? "磁盘>0字节且回读行数一致" : "回读行数不符，视为未成功");
            }
            catch (Exception ex) { return Fail("导出 Excel 失败：" + ex.Message); }
        }
    }

    // ============ 7. toolbox_import_excel（校验 .cache 一致 + 同步列/行 + 写回 XML） ============
    [ComVisible(false)]
    internal sealed class ToolToolboxImportExcel : McpToolBase
    {
        public override string Name { get { return "toolbox_import_excel"; } }
        public override string Category { get { return "toolbox"; } }
        public override string Description { get { return "[toolbox] 写：导入 CAXA 模板格式 xlsx 覆盖规格表（需 confirm=true）。导入前强校验 B1/B2 与 .cache 对应行一致，不一致/无行/歧义一律拒绝；按表头同步列结构（联动参数表）与行数；写后回读 verified。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["xml_path"] = S.Str("族 XML 完整路径（必填）。", true),
                ["xlsx_path"] = S.Str("待导入 xlsx 完整路径（必填）。", true),
                ["cache_path"] = S.Str("对应 .cache 路径；留空则从 XML 上一级目录自动反查。"),
                ["confirm"] = S.Bool("必须为 true 才执行写入。")
            }, "xml_path", "xlsx_path");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            if (!args.ParamBool("confirm", false)) return Fail("需要 confirm=true 才执行导入写入。");
            string xmlPath = args.ParamString("xml_path", "");
            string xlsxPath = args.ParamString("xlsx_path", "");
            string cachePath = args.ParamString("cache_path", "");
            if (string.IsNullOrEmpty(xmlPath) || !File.Exists(xmlPath)) return Fail("xml_path 未指向存在的文件：" + xmlPath);
            if (string.IsNullOrEmpty(xlsxPath) || !File.Exists(xlsxPath)) return Fail("xlsx_path 未指向存在的文件：" + xlsxPath);
            try
            {
                XmlCore.Doc d = XmlCore.Load(xmlPath);
                List<List<string>> sheet = XlsxIO.ReadSheet(xlsxPath);
                if (sheet.Count == 0) return Fail("导入拒绝：xlsx 无可读数据。");

                // ① .cache 一致性强校验
                string xName = sheet.Count > 0 && sheet[0].Count > 1 ? sheet[0][1] : "";
                string xGuid = sheet.Count > 1 && sheet[1].Count > 1 ? sheet[1][1] : "";
                if (string.IsNullOrEmpty(cachePath)) cachePath = ToolboxOps.InferCacheForXml(xmlPath);
                if (string.IsNullOrEmpty(cachePath) || !File.Exists(cachePath))
                    return Fail("导入拒绝：未找到 .cache，无法校验 B1/B2 一致性（第三态）。");
                CacheCore.CacheDoc cd = CacheCore.Load(cachePath);
                Dictionary<string, string> id = ToolboxOps.FindCacheRow(cd, xName, xGuid);
                if (id == null)
                    return Fail("导入拒绝：.cache 中未唯一找到 Excel 零件名/GUID 对应行（第三态）。Excel B1=" + xName + " B2=" + xGuid);
                if (!ToolboxOps.SameName(xName, id["name"]) || !ToolboxOps.SameGuid(xGuid, id["GUID"]))
                    return Fail("导入拒绝：Excel 与 .cache 不一致 —— 零件名[" + xName + " vs " + id["name"] +
                        "] GUID[" + xGuid + " vs " + id["GUID"] + "]。");

                // ② 定位表头行（含"螺纹规格"）
                int headerRow = -1;
                for (int r = 0; r < sheet.Count && r < 10; r++)
                {
                    List<string> row = sheet[r];
                    for (int c = 0; c < row.Count; c++)
                        if (row[c] == "螺纹规格") { headerRow = r; break; }
                    if (headerRow >= 0) break;
                }
                if (headerRow < 0) return Fail("导入失败：未找到表头行（需含“螺纹规格”列）。");
                List<string> header = sheet[headerRow];
                Dictionary<string, int> hmap = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int c = 0; c < header.Count; c++)
                    if (!string.IsNullOrEmpty(header[c]) && !hmap.ContainsKey(header[c])) hmap[header[c]] = c;

                // ③ 列结构同步：Excel 表头反查 RowKey
                List<string> targetKeys = new List<string>();
                foreach (string h in header)
                {
                    if (string.IsNullOrEmpty(h)) continue;
                    string rk = ToolboxOps.HeaderToRowKey(d, h);
                    if (!string.IsNullOrEmpty(rk) && !targetKeys.Contains(rk)) targetKeys.Add(rk);
                }
                if (targetKeys.Count == 0) return Fail("导入失败：表头未映射到任何列。");
                int added = 0, removed = 0;
                for (int c = d.RowKeys.Count - 1; c >= 0; c--)
                    if (!targetKeys.Contains(d.RowKeys[c])) { ToolboxOps.DelColumn(d, d.RowKeys[c]); removed++; }
                foreach (string rk in targetKeys)
                    if (!d.RowKeys.Contains(rk)) { ToolboxOps.AddColumn(d, rk); added++; }

                // ④ 行数据重建
                int[] srcCol = new int[d.RowKeys.Count];
                for (int c = 0; c < d.RowKeys.Count; c++)
                {
                    string h = ToolboxOps.RowKeyToHeader(d, d.RowKeys[c]);
                    int ix; srcCol[c] = hmap.TryGetValue(h, out ix) ? ix : -1;
                }
                List<List<Attr>> newRows = new List<List<Attr>>();
                int dataStart = headerRow + 2;
                for (int r = dataStart; r < sheet.Count; r++)
                {
                    List<string> src = sheet[r];
                    List<Attr> row = new List<Attr>();
                    bool any = false;
                    for (int c = 0; c < d.RowKeys.Count; c++)
                    {
                        string v = "";
                        if (srcCol[c] >= 0 && srcCol[c] < src.Count) v = src[srcCol[c]] ?? "";
                        row.Add(new Attr(d.RowKeys[c], v));
                        if (!string.IsNullOrEmpty(v)) any = true;
                    }
                    if (any) newRows.Add(row);
                }
                d.Rows = newRows;
                XmlCore.Save(xmlPath, d, ToolboxOps.BackupDir(xmlPath));

                // 写后回读判据
                XmlCore.Doc re = XmlCore.Load(xmlPath);
                bool verified = re.Rows.Count == newRows.Count && re.RowKeys.Count == d.RowKeys.Count;
                long afterLen = new FileInfo(xmlPath).Length;
                return Ok(
                    "xmlPath", xmlPath, "importedRows", newRows.Count, "colCount", d.RowKeys.Count,
                    "colsAdded", added, "colsRemoved", removed,
                    "cacheName", id["name"], "cacheGuid", id["GUID"],
                    "verified", verified, "fileBytesAfter", afterLen,
                    "backupDir", ToolboxOps.BackupDir(xmlPath) ?? "",
                    "note", verified ? "写后回读行数/列数一致" : "写后回读不一致，视为未成功");
            }
            catch (Exception ex) { return Fail("导入 Excel 失败：" + ex.Message); }
        }
    }
}
