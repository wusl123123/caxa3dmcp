// 工具集：property / element 类 —— 自定义属性（共享表铁律）、BOM 属性、材料名、元素改名与删除。
//
// 共享表唯一铁律（规则第 14 节 7，参考 caxa3dmytoolsaddin\caxa3dAttributeManageraddin\CaxaAutomation.cs）：
//   ★ 唯一判据 = IsSharedMgr() == true；参数名义值（vbShared / selector）不算证据。
//   ★ 三条候选逐一试、每条都复核：GetCustomPropManager(0) → GetCustomPropManager(1) → get_CustomPropMgr(true)。
//   ★ 三条都没拿到共享管理器 ⇒ 本次不读写任何其它表（不拿未确认的管理器凑数）。
// 证据：2027 TLB members.csv —— IZElement.get_CustomPropMgr(24)/GetCustomPropManager(42)/
//       GetMaterialNameProperty(36)/set_Name(10)/Remove(14)；
//       IZCustomPropMgr IsSharedMgr(7)/GetAt(8)/GetValueByName(9)/SetValueByName(10)/
//       RemoveByName(13)/AddCustomProp(14)/get_Count(15)；
//       IZPart get_BOMPartNumber(21)/set(22)、get_BOMDescription(23)/set(24)、GetBOMSetting(10)。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    /// <summary>元素定位与共享属性表获取的公共实现。</summary>
    [ComVisible(false)]
    internal static class Elem
    {
        public static IZElement Find(McpToolContext ctx, JValue args, out string err)
        {
            err = null;
            if (ctx.Scene == null) { err = "当前没有 3D 场景文档"; return null; }
            int id = args.Has("id") ? args.ParamInt("id", -1) : -1;
            string needle = args.ParamString("name", "");
            bool lastCreated = args.ParamBool("last_created", false);
            if (id < 0 && string.IsNullOrEmpty(needle) && !lastCreated)
            { err = "请提供 id 或 name，或设 last_created=true 取最后一个顶层元素"; return null; }

            var tops = new List<IZElement>();
            foreach (object o in McpToolContext.UnpackArray(ctx.Scene.GetChildElements()))
            {
                IZElement e = o as IZElement;
                if (e != null) tops.Add(e);
            }
            if (lastCreated && tops.Count > 0) return tops[tops.Count - 1];

            foreach (IZElement e in tops)
            {
                IZElement hit = FindIn(e, id, needle);
                if (hit != null) return hit;
            }
            err = "未找到匹配的元素";
            return null;
        }

        private static IZElement FindIn(IZElement e, int id, string needle)
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
                    IZElement hit = FindIn(c, id, needle);
                    if (hit != null) return hit;
                }
            }
            catch { }
            return null;
        }

        /// <summary>按共享表铁律取共享 CustomPropMgr；返回 null 表示本次不允许读写。</summary>
        public static IZCustomPropMgr SharedMgr(IZElement e, out string diagnostic)
        {
            diagnostic = "";
            var tried = new List<string>();
            foreach (int selector in new int[] { 0, 1 })
            {
                try
                {
                    object raw = e.GetCustomPropManager(selector);
                    IZCustomPropMgr mgr = raw as IZCustomPropMgr;
                    if (mgr == null) { tried.Add("selector=" + selector + ":非 IZCustomPropMgr"); continue; }
                    bool shared = false;
                    try { shared = mgr.IsSharedMgr(); } catch (Exception ex) { tried.Add("selector=" + selector + ":IsSharedMgr 失败 " + ex.Message); continue; }
                    tried.Add("selector=" + selector + ":IsSharedMgr=" + shared);
                    if (shared) { diagnostic = string.Join(" | ", tried.ToArray()); return mgr; }
                }
                catch (Exception ex) { tried.Add("selector=" + selector + ":异常 " + ex.Message); }
            }
            // 第三条候选：旧属性 getter（vbShared=TRUE），同样必须复核 IsSharedMgr
            try
            {
                object raw = e.get_CustomPropMgr(true);
                IZCustomPropMgr mgr = raw as IZCustomPropMgr;
                if (mgr != null)
                {
                    bool shared = mgr.IsSharedMgr();
                    tried.Add("vbShared=TRUE:IsSharedMgr=" + shared);
                    if (shared) { diagnostic = string.Join(" | ", tried.ToArray()); return mgr; }
                }
                else tried.Add("vbShared=TRUE:非 IZCustomPropMgr");
            }
            catch (Exception ex) { tried.Add("vbShared=TRUE:异常 " + ex.Message); }
            diagnostic = string.Join(" | ", tried.ToArray()) + " ⇒ 三条候选均无共享表，按铁律本次不读写";
            return null;
        }
    }

    [ComVisible(false)]
    internal sealed class ToolGetCustomProps : McpToolBase
    {
        public override string Name { get { return "get_custom_props"; } }
        public override string Category { get { return "property"; } }
        public override string Description { get { return "读取元素共享自定义属性表（严格校验 IsSharedMgr）。可按 name 只取一项，不传则全量枚举。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["id"] = S.Num("元素 Id"),
                ["name"] = S.Str("元素名称（用于定位）"),
                ["last_created"] = S.Bool("取最后一个顶层元素，默认 false", false, false, true),
                ["prop_name"] = S.Str("只读取指定属性名；不传则枚举全部")
            }, new string[0]);
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            string err; IZElement e = Elem.Find(ctx, args, out err);
            if (e == null) return Fail(err);
            string diag; IZCustomPropMgr mgr = Elem.SharedMgr(e, out diag);
            if (mgr == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "未取得共享属性表", ["diagnostic"] = diag };
            string only = args.ParamString("prop_name", "");
            var props = new List<object>();
            try
            {
                if (!string.IsNullOrEmpty(only))
                {
                    object v = null; string readErr = null;
                    try { v = mgr.GetValueByName(only); } catch (Exception ex) { readErr = ex.Message; }
                    props.Add(new Dictionary<string, object> { ["name"] = only, ["value"] = Scalar(v), ["error"] = readErr });
                }
                else
                {
                    int count = mgr.Count;
                    for (int i = 0; i < count; i++)
                    {
                        string pn; object pv;
                        try { mgr.GetAt(i, out pn, out pv); }
                        catch (Exception ex) { props.Add(new Dictionary<string, object> { ["index"] = i, ["error"] = ex.Message }); continue; }
                        props.Add(new Dictionary<string, object> { ["index"] = i, ["name"] = pn, ["value"] = Scalar(pv) });
                    }
                }
            }
            catch (Exception ex) { return Fail("属性枚举失败：" + ex.Message); }
            return Ok("element", McpToolContext.SafeName(e), "count", props.Count, "props", props, "sharedTableDiagnostic", diag);
        }

        internal static object Scalar(object v)
        {
            if (v == null) return null;
            if (v is string || v is bool || v is double || v is float || v is int || v is long || v is short || v is byte || v is decimal) return v;
            try { return Convert.ToString(v); } catch { return "(" + v.GetType().Name + ")"; }
        }
    }

    [ComVisible(false)]
    internal sealed class ToolSetCustomProp : McpToolBase
    {
        public override string Name { get { return "set_custom_prop"; } }
        public override string Category { get { return "property"; } }
        public override string Description { get { return "向元素共享自定义属性表写入/更新一项（严格校验 IsSharedMgr；不是共享表就拒绝写入）。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["id"] = S.Num("元素 Id"),
                ["name"] = S.Str("元素名称（用于定位）"),
                ["last_created"] = S.Bool("取最后一个顶层元素，默认 false", false, false, true),
                ["prop_name"] = S.Str("自定义属性名", true),
                ["value"] = S.Str("属性值（字符串/数字均可，统一按字符串写入）", true)
            }, "prop_name", "value");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            string err; IZElement e = Elem.Find(ctx, args, out err);
            if (e == null) return Fail(err);
            string prop = args.ParamString("prop_name", "");
            if (string.IsNullOrEmpty(prop)) return Fail("prop_name 不能为空");
            string value = args.ParamString("value", "");
            string diag; IZCustomPropMgr mgr = Elem.SharedMgr(e, out diag);
            if (mgr == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "未取得共享属性表，按共享表铁律拒绝写入", ["diagnostic"] = diag };
            var attempts = new List<object>();
            bool wrote = false; string used = null;
            try { mgr.SetValueByName(prop, value); wrote = true; used = "SetValueByName(10)"; attempts.Add(new Dictionary<string, object> { ["channel"] = used, ["ok"] = true }); }
            catch (Exception ex)
            {
                attempts.Add(new Dictionary<string, object> { ["channel"] = "SetValueByName(10)", ["ok"] = false, ["error"] = ex.Message });
                try { mgr.AddCustomProp(prop, value, true); wrote = true; used = "AddCustomProp(14, replace=true)"; attempts.Add(new Dictionary<string, object> { ["channel"] = used, ["ok"] = true }); }
                catch (Exception ex2) { attempts.Add(new Dictionary<string, object> { ["channel"] = "AddCustomProp(14)", ["ok"] = false, ["error"] = ex2.Message }); }
            }
            string readback = null;
            if (wrote) { try { readback = ToolGetCustomProps.Scalar(mgr.GetValueByName(prop)) as string; } catch { } }
            return new Dictionary<string, object>
            {
                ["ok"] = wrote,
                ["element"] = McpToolContext.SafeName(e),
                ["propName"] = prop,
                ["value"] = value,
                ["channel"] = used,
                ["readBack"] = readback,
                ["sharedTableDiagnostic"] = diag,
                ["attempts"] = attempts
            };
        }
    }

    [ComVisible(false)]
    internal sealed class ToolGetBomProperties : McpToolBase
    {
        public override string Name { get { return "get_bom_properties"; } }
        public override string Category { get { return "property"; } }
        public override string Description { get { return "读取零件的 BOM 属性（件号、描述、数量、是否计入 BOM、内核类型）。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["id"] = S.Num("元素 Id"), ["name"] = S.Str("元素名称"),
                ["last_created"] = S.Bool("取最后一个顶层元素，默认 false", false, false, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            string err; IZElement e = Elem.Find(ctx, args, out err);
            if (e == null) return Fail(err);
            IZPart part = e as IZPart;
            if (part == null) return Fail("目标元素不是零件(IZPart)");
            var d = new Dictionary<string, object> { ["ok"] = true, ["element"] = McpToolContext.SafeName(e) };
            try { d["bomPartNumber"] = part.BOMPartNumber; } catch (Exception ex) { d["bomPartNumberError"] = ex.Message; }
            try { d["bomDescription"] = part.BOMDescription; } catch (Exception ex) { d["bomDescriptionError"] = ex.Message; }
            try { d["includedInBOM"] = part.IncludedInBOM; } catch (Exception ex) { d["includedInBOMError"] = ex.Message; }
            try { d["kernelType"] = part.KernelType.ToString(); } catch (Exception ex) { d["kernelTypeError"] = ex.Message; }
            try
            {
                IZElement elem = McpToolContext.AsElement(part);
                d["materialName"] = elem == null ? "(不支持 IZElement 表面)" : ReadMaterial(elem);
            }
            catch (Exception ex) { d["materialNameError"] = ex.Message; }
            return d;
        }

        internal static string ReadMaterial(IZElement e)
        {
            string n = null;
            e.GetMaterialNameProperty(out n);
            return n;
        }
    }

    [ComVisible(false)]
    internal sealed class ToolSetElementName : McpToolBase
    {
        public override string Name { get { return "set_element_name"; } }
        public override string Category { get { return "element"; } }
        public override string Description { get { return "重命名元素（IZElement.set_Name），写后立即回读校验。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["id"] = S.Num("元素 Id"), ["name"] = S.Str("用于定位的现有名称"),
                ["last_created"] = S.Bool("取最后一个顶层元素，默认 false", false, false, true),
                ["new_name"] = S.Str("新名称", true)
            }, "new_name");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            string err; IZElement e = Elem.Find(ctx, args, out err);
            if (e == null) return Fail(err);
            string newName = args.ParamString("new_name", "");
            if (string.IsNullOrEmpty(newName)) return Fail("new_name 不能为空");
            string before = McpToolContext.SafeName(e);
            try { e.Name = newName; }
            catch (Exception ex) { return Fail("set_Name 失败：" + ex.Message); }
            string after = McpToolContext.SafeName(e);
            return Ok("before", before, "after", after, "verified", after == newName);
        }
    }

    [ComVisible(false)]
    internal sealed class ToolDeleteElement : McpToolBase
    {
        public override string Name { get { return "delete_element"; } }
        public override string Category { get { return "element"; } }
        public override string Description { get { return "删除元素（IZElement.Remove）。危险操作：必须显式传 confirm=true，且只删定位到的单个元素。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["id"] = S.Num("元素 Id"), ["name"] = S.Str("元素名称"),
                ["confirm"] = S.Bool("必须显式设为 true 才会执行删除", true)
            }, "confirm");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            if (!args.ParamBool("confirm", false)) return Fail("未确认：请把 confirm 设为 true 才会执行删除");
            string err; IZElement e = Elem.Find(ctx, args, out err);
            if (e == null) return Fail(err);
            string n = McpToolContext.SafeName(e);
            int id = ToolGetSceneInfo.SafeId(e);
            try { e.Remove(); }
            catch (Exception ex) { return Fail("Remove 失败：" + ex.Message); }
            return Ok("deleted", new Dictionary<string, object> { ["id"] = id, ["name"] = n });
        }
    }
}
