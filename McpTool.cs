// 可插拔 MCP 工具集 —— 契约 / 运行上下文 / 运行时注册表。
// 设计来源：参考 C:\code\ICAPI\caxa3dmytoolsaddin 的集合体架构
//   Host + IPlugin 契约 + PluginManager 运行时扫描 plugins 目录 + IPluginHost 提供环境
//   ⇒ 这里映射为 McpBridge + IMcpTool 契约 + McpToolRegistry 反射扫描 + McpToolContext 提供环境
//   ★ 新增工具 = 新增一个实现 IMcpTool 的类；桥与 stdio server 都不用改（对应「主程序无需改动」）。
//   ★ 单个工具构造失败只写日志、不影响其它工具（对应集合体「单个插件加载失败只写日志」）。
//
// 铁律：
//   1) 所有 ICAPI 调用都由 McpBridge.Dispatch 经 _host.Invoke 封送回 CAXA 主线程(STA)，
//      工具内的 Execute 因此可以安全地直接访问 COM（规则第 8 节 STA 铁律）。
//   2) 工具不引用 McpServer 的任何类型；返回 object 由 JsonRpc.Serialize 序列化。
//   3) 类型一律 [ComVisible(false)]，只有 Addin 入口是 [ComVisible(true)]。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    /// <summary>工具运行环境（对应 IPluginHost）。由桥在 STA 主线程上构造并传入。</summary>
    [ComVisible(false)]
    internal sealed class McpToolContext
    {
        public IZBaseApp App;

        public IZDoc ActiveDoc { get { return App == null ? null : App.ActiveDoc; } }
        public IZSceneDoc Scene { get { return ActiveDoc as IZSceneDoc; } }

        public void Log(string msg) { Diagnostics.Log("[tool] " + msg); }

        /// <summary>解包 ICAPI 返回的 VARIANT/SAFEARRAY（复刻 caxa3dTreeBom 实证写法）。</summary>
        public static List<object> UnpackArray(object raw)
        {
            var result = new List<object>();
            if (raw == null) return result;
            ZArray zarr = raw as ZArray;
            if (zarr != null)
            {
                int count; zarr.Count(out count);
                for (int i = 0; i < count; i++) { object o; zarr.Get(i, out o); if (o != null) result.Add(o); }
                return result;
            }
            object[] arr = raw as object[];
            if (arr != null) { foreach (object o in arr) if (o != null) result.Add(o); return result; }
            result.Add(raw);
            return result;
        }

        /// <summary>解包包围盒/点数组（复刻 caxa3dsizeboxread 实证写法）。</summary>
        public static double[] ToDoubleArray(object raw)
        {
            if (raw == null) return null;
            double[] d = raw as double[];
            if (d != null) return d;
            float[] f = raw as float[];
            if (f != null) { double[] r = new double[f.Length]; for (int i = 0; i < f.Length; i++) r[i] = f[i]; return r; }
            ZArray za = raw as ZArray;
            if (za != null)
            {
                int count; za.Count(out count); double[] rz = new double[count];
                for (int i = 0; i < count; i++) { object o; za.Get(i, out o); rz[i] = Convert.ToDouble(o); }
                return rz;
            }
            Array a = raw as Array;
            if (a != null) { double[] ra = new double[a.Length]; for (int i = 0; i < a.Length; i++) ra[i] = Convert.ToDouble(a.GetValue(i)); return ra; }
            return null;
        }

        public static string SafeName(IZElement e)
        {
            try { return (e.Name ?? "").Trim(); } catch { return ""; }
        }

        // ★ 实机类型系统坑（2026-11-12 编译取证）：interop 里 IZPart 与 IZElement 不是继承关系，
        //   IZPart 没有 Name / Remove / Id 等元素表面成员。CAXA 的 Z 系列接口是「单 IDispatch 多表面」，
        //   必须经 RCW 的 as 转换（等价 QI）切到 IZElement 表面才能访问 —— 与 caxa3dmytoolsaddin
        //   FeatureTool.cs 里 `owner as IZPartFeatureMgr` 是同一手法。
        public static IZElement AsElement(object com)
        {
            try { return com as IZElement; } catch { return null; }
        }

        public static string SafeNameOf(object com)
        {
            IZElement e = AsElement(com);
            return e == null ? "" : SafeName(e);
        }

        public static bool TrySetName(object com, string name, out string err)
        {
            err = null;
            IZElement e = AsElement(com);
            if (e == null) { err = "目标不支持 IZElement 表面（无法 set_Name）"; return false; }
            try { e.Name = name; return true; }
            catch (Exception ex) { err = ex.Message; return false; }
        }

        public static bool TryRemove(object com, out string err)
        {
            err = null;
            IZElement e = AsElement(com);
            if (e == null) { err = "目标不支持 IZElement 表面（无法 Remove）"; return false; }
            try { e.Remove(); return true; }
            catch (Exception ex) { err = ex.Message; return false; }
        }
    }

    /// <summary>单个 MCP 工具契约（对应 IPlugin）。程序集内可见：参数 JValue 是 internal。</summary>
    [ComVisible(false)]
    internal interface IMcpTool
    {
        string Name { get; }
        string Category { get; }
        string Description { get; }
        object InputSchema();
        object Execute(McpToolContext ctx, JValue args);
    }

    /// <summary>工具基类：提供 schema 构造与统一错误包装。</summary>
    [ComVisible(false)]
    internal abstract class McpToolBase : IMcpTool
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public virtual string Category { get { return "general"; } }
        public virtual object InputSchema() { return S.Schema(new Dictionary<string, object>()); }
        public abstract object Execute(McpToolContext ctx, JValue args);

        protected static Dictionary<string, object> Ok(params object[] kv)
        {
            var d = new Dictionary<string, object> { ["ok"] = true };
            for (int i = 0; i + 1 < kv.Length; i += 2) d[Convert.ToString(kv[i])] = kv[i + 1];
            return d;
        }

        protected static Dictionary<string, object> Fail(string reason)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = reason };
        }

        /// <summary>带结构化错误码的失败（对应 SolidWorksMCP 的「结构化诊断」）。</summary>
        protected static Dictionary<string, object> Fail(string code, string reason)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["code"] = code, ["error"] = reason };
        }

        protected static Dictionary<string, object> NeedScene(McpToolContext ctx)
        {
            return ctx.Scene == null ? Fail("当前没有 3D 场景文档（活动文档不是场景文档或没有打开文档）") : null;
        }
    }

    /// <summary>inputSchema 构造助手，避免每个工具手抄 JSON 结构。</summary>
    [ComVisible(false)]
    internal static class S
    {
        public static Dictionary<string, object> Schema(Dictionary<string, object> props, params string[] required)
        {
            var req = new List<object>();
            if (required != null) foreach (string r in required) req.Add(r);
            return new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = req
            };
        }

        public static Dictionary<string, object> Num(string desc, bool required = false, double dflt = 0.0, bool hasDefault = false)
        {
            var d = new Dictionary<string, object> { ["type"] = "number", ["description"] = desc };
            if (hasDefault) d["default"] = dflt;
            return d;
        }

        public static Dictionary<string, object> Str(string desc, bool required = false, string dflt = null)
        {
            var d = new Dictionary<string, object> { ["type"] = "string", ["description"] = desc };
            if (dflt != null) d["default"] = dflt;
            return d;
        }

        public static Dictionary<string, object> Bool(string desc, bool required = false, bool dflt = false, bool hasDefault = false)
        {
            var d = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = desc };
            if (hasDefault) d["default"] = dflt;
            return d;
        }

        /// <summary>枚举参数（inputSchema 的 enum + 默认值）。</summary>
        public static Dictionary<string, object> Enum(string desc, string[] values, bool required = false, string dflt = null)
        {
            var d = new Dictionary<string, object> { ["type"] = "string", ["description"] = desc, ["enum"] = new List<object>(values) };
            if (dflt != null) d["default"] = dflt;
            return d;
        }

        /// <summary>嵌套对象数组（如 batch_exec 的 calls 列表）。</summary>
        public static Dictionary<string, object> Array(string desc, Dictionary<string, object> itemProps, params string[] itemRequired)
        {
            var req = new List<object>();
            if (itemRequired != null) foreach (string r in itemRequired) req.Add(r);
            return new Dictionary<string, object>
            {
                ["type"] = "array",
                ["description"] = desc,
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = itemProps,
                    ["required"] = req
                }
            };
        }
    }

    /// <summary>
    /// 运行时工具注册表。反射扫描本程序集内所有 IMcpTool 实现（对应 PluginManager 扫描 plugins 目录）。
    /// describe_tools 是桥的内建元工具，stdio server 用它动态生成 tools/list。
    /// </summary>
    [ComVisible(false)]
    internal static class McpToolRegistry
    {
        private static readonly Dictionary<string, IMcpTool> _tools = new Dictionary<string, IMcpTool>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new object();
        private static bool _loaded;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                Type[] types;
                try { types = typeof(McpToolRegistry).Assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex)
                {
                    var kept = new List<Type>();
                    foreach (Type t in ex.Types) if (t != null) kept.Add(t);
                    types = kept.ToArray();
                    Diagnostics.Log("McpToolRegistry: GetTypes 部分加载失败，已保留可加载类型 " + kept.Count);
                }
                foreach (Type t in types)
                {
                    if (t == null || t.IsAbstract || t.IsInterface || !typeof(IMcpTool).IsAssignableFrom(t)) continue;
                    try
                    {
                        // nonPublic=true：工具类都是 internal，其隐式构造函数可访问性随类；
                        // 不显式放行会在部分运行时抛 MissingMethodException，导致整批工具注册为空。
                        IMcpTool tool = (IMcpTool)Activator.CreateInstance(t, true);
                        if (_tools.ContainsKey(tool.Name))
                        {
                            Diagnostics.Log("McpToolRegistry: 工具名重复，跳过 " + t.FullName + " (" + tool.Name + ")");
                            continue;
                        }
                        _tools[tool.Name] = tool;
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Log("McpToolRegistry: 工具加载失败 " + t.FullName + " → " + ex.Message);
                    }
                }
                _loaded = true;
                Diagnostics.Log("McpToolRegistry: 已注册 " + _tools.Count + " 个工具");
            }
        }

        public static IMcpTool Find(string name)
        {
            EnsureLoaded();
            IMcpTool tool;
            return _tools.TryGetValue(name ?? "", out tool) ? tool : null;
        }

        /// <summary>MCP tools/list 所需的清单（name / description / inputSchema / 分类）。</summary>
        public static List<object> Describe()
        {
            EnsureLoaded();
            var list = new List<object>();
            foreach (var kv in _tools)
            {
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = kv.Value.Name,
                    ["description"] = "[" + kv.Value.Category + "] " + kv.Value.Description,
                    ["inputSchema"] = kv.Value.InputSchema()
                });
            }
            return list;
        }

        public static int Count { get { EnsureLoaded(); return _tools.Count; } }

        /// <summary>按分类聚合的「分类 → 工具数」，供 get_capabilities 能力协商使用。</summary>
        public static Dictionary<string, object> Categories()
        {
            EnsureLoaded();
            var groups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _tools)
            {
                string cat = kv.Value.Category ?? "general";
                if (!groups.ContainsKey(cat)) groups[cat] = 0;
                groups[cat]++;
            }
            var result = new Dictionary<string, object>();
            foreach (var kv in groups) result[kv.Key] = kv.Value;
            return result;
        }
    }
}
