// 从 caxa3dtracker 移植的四类能力（26-11-13）：参数表抓取 / 特征包围盒 / 草图建立 / 移动零部件。
//
// 取证链（两工程 Lib/interop.ICApiIronCAD.dll md5 完全一致 = 29a23396bfbe966ba92cb4598f8c38f3）：
//   参数表   ParameterTableProbe.cs：IZSceneDoc.GetChildElements(7) → IZElement.GetChildren(13)
//            → 元素 as IZSceneElement → ParameterMgr(19) → IZParameterMgr.Count(7)/GetParameter(i)(8)
//            → IZParameter 各 getter；Value 单位=米，界面 mm=×1000（tracker 实机 pR1=0.02→20.000000）。
//   包围盒   FeatureBoxProbe.cs：IZFeature.GetBoundingBox(13) 局部六值 / IZPart.GetBody(true)→IZBody.GetBoundingBox(13)
//            / GetTightBoundingBox(21) / IZAssembly.GetBoundingBox(false)(53) 世界；ITransformToGlobal 八角点→世界 AABB。
//            ★ 类型分诊只看 eZElementType 名称，QI 顺序会串位（零件也能 QI 成 IZAssembly/IZFeature）。
//   草图     RectSketchTool.cs：CreateProfile(22) → CreateLine(29)×4（2D 坐标，单位米）→ scene.Update()；
//            起始 SelectionUIContext==2 必须拒绝（编辑态建草图会画进现有草图、选中错位）。
//   移动     MoveComponentTool.cs：IZSceneElement.get/set_PositionTransformComponentValue(25)
//            eZTransformComponentType Z_TRANS_L=0/W=1/H=2；分量单位=米，用户输入 mm 需 ×0.001；
//            只改平移、保留姿态；判据=三分量写后回读（±0.01mm）＋ GetPositionTransform/GetTranslation 交叉验证。
//
// ★ 本轮刻意不移植的部分（服务能力边界，写进总结）：
//   tracker 的智能标注/编辑态进入依赖 WM_COMMAND 投递、窗口子类化、Application.DoEvents 10s 观察，
//   属 UI 交互命令（50123 实机两轮未生效、50731 未定案）；MCP 服务是 stdio 同步调用，
//   不阻塞主线程、不碰宿主窗口过程、不假成功。本文件只做纯 COM 建模段，标注一律记为未实现第三态。
//
// 铁律：读不到记 <读不到>/<FAIL:hr>/N/A，绝不折算 0；撞预算单列 truncated；
//       写操作只认写后回读，不认 API 未抛异常；所有 COM 调用在 McpBridge 封送的 STA 主线程上执行。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    // ---------------------------------------------------------------- 公共底座

    [ComVisible(false)]
    internal sealed class TrkNode
    {
        public object Raw;
        public IZElement Elem;
        public int Depth;
        public string Type = "<读不到>";
        public int Id = -1;
        public string Name = "<读不到>";
    }

    [ComVisible(false)]
    internal static class TrackerKit
    {
        public const double MeterToMm = 1000.0;
        private const int PartTypeInnovation = 16;   // eZPartType.Z_PART_INNOVATION_PART
        private const int PartTypeStructured = 32;   // eZPartType.Z_PART_STRUCTURED_PART

        public static string Hr(Exception ex)
        {
            var c = ex as COMException;
            return c != null ? "0x" + c.ErrorCode.ToString("X8") : ex.GetType().Name;
        }

        public static string Safe(Func<string> f, string fallback)
        {
            try { return f() ?? ""; }
            catch (Exception ex) { return fallback + "(" + Hr(ex) + ")"; }
        }

        public static string Fmt(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
        public static string Mm(double meters) { return (meters * MeterToMm).ToString("F6", CultureInfo.InvariantCulture); }

        /// <summary>元素树递归走查（tracker ProbeCommon.EnumerateTree 同口径）。GetChildren 抛 E_FAIL 记第三态跳过。</summary>
        public static List<TrkNode> Enumerate(McpToolContext ctx, int maxDepth, int maxNodes,
            out int truncated, out int failedBranches, out string rootError)
        {
            truncated = 0; failedBranches = 0; rootError = null;
            var list = new List<TrkNode>();
            int nodes = 0, trunc = 0, failed = 0;
            object roots = null;
            try { roots = ctx.Scene.GetChildElements(); }
            catch (Exception ex) { rootError = "GetChildElements " + Hr(ex) + "：" + ex.Message; }
            if (roots == null) { if (rootError == null) rootError = "GetChildElements 返回非数组/空（第三态）"; }
            else
                foreach (object o in McpToolContext.UnpackArray(roots))
                    Walk(ctx, o, 0, list, maxDepth, maxNodes, ref nodes, ref trunc, ref failed);
            truncated = trunc; failedBranches = failed;
            return list;
        }

        private static void Walk(McpToolContext ctx, object raw, int depth, List<TrkNode> into,
            int maxDepth, int maxNodes, ref int nodes, ref int truncated, ref int failedBranches)
        {
            if (raw == null) return;
            if (depth > maxDepth || nodes >= maxNodes) { truncated++; return; }
            nodes++;
            var n = new TrkNode { Raw = raw, Depth = depth, Elem = McpToolContext.AsElement(raw) };
            if (n.Elem != null)
            {
                n.Type = Safe(() => n.Elem.Type.ToString(), "<读不到>");
                try { n.Id = n.Elem.Id; } catch (Exception ex) { ctx.Log("tree Id FAIL " + Hr(ex)); }
                n.Name = Safe(() => n.Elem.Name, "<读不到>");
            }
            into.Add(n);
            if (n.Elem == null) return;
            object kids = null;
            try { kids = n.Elem.GetChildren(); }
            catch (Exception ex) { failedBranches++; ctx.Log("tree GetChildren FAIL " + Hr(ex) + " @depth=" + depth); return; }
            if (kids == null) return;
            foreach (object k in McpToolContext.UnpackArray(kids))
                Walk(ctx, k, depth + 1, into, maxDepth, maxNodes, ref nodes, ref truncated, ref failedBranches);
        }

        /// <summary>解 6 值包围盒；任何异常都记第三态，不折算 0。</summary>
        public static double[] Box(object raw)
        {
            try
            {
                double[] b = McpToolContext.ToDoubleArray(raw);
                return (b != null && b.Length >= 6) ? b : null;
            }
            catch { return null; }
        }

        /// <summary>局部 6 值经 GetTransformToGlobal 搬 8 角点求世界 AABB（tracker 实证通道）。失败退回局部。</summary>
        public static bool TryBoxToWorld(McpToolContext ctx, object elem, double[] b, out double[] world6, out string reason)
        {
            world6 = null; reason = null;
            if (b == null || b.Length < 6) { reason = "局部盒为空"; return false; }
            IZSceneElement se = elem as IZSceneElement;
            IZMathUtility mu = ctx.App as IZMathUtility;
            if (se == null) { reason = "QI IZSceneElement 失败"; return false; }
            if (mu == null) { reason = "IZBaseApp QI 不到 IZMathUtility 表面"; return false; }
            ZMathMatrix m;
            try { m = se.GetTransformToGlobal(); }
            catch (Exception ex) { reason = "GetTransformToGlobal " + Hr(ex); return false; }
            if (m == null) { reason = "GetTransformToGlobal 返回空"; return false; }

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                double x = ((i & 1) == 0) ? b[0] : b[3];
                double y = ((i & 2) == 0) ? b[1] : b[4];
                double z = ((i & 4) == 0) ? b[2] : b[5];
                double ox, oy, oz;
                if (!TransformPoint(mu, m, x, y, z, out ox, out oy, out oz, out reason)) return false;
                if (ox < minX) minX = ox; if (ox > maxX) maxX = ox;
                if (oy < minY) minY = oy; if (oy > maxY) maxY = oy;
                if (oz < minZ) minZ = oz; if (oz > maxZ) maxZ = oz;
            }
            world6 = new double[] { minX, minY, minZ, maxX, maxY, maxZ };
            reason = null;
            return true;
        }

        private static bool TransformPoint(IZMathUtility mu, ZMathMatrix m, double x, double y, double z,
            out double ox, out double oy, out double oz, out string reason)
        {
            ox = oy = oz = 0; reason = null;
            try
            {
                ZMathPoint p;
                try { p = mu.CreateMathPoint(new double[] { x, y, z }); }
                catch { p = mu.CreateMathPoint(new object[] { x, y, z }); }
                if (p == null) { reason = "CreateMathPoint 返回空"; return false; }
                p.TransformBy(m);
                ox = p.X; oy = p.Y; oz = p.Z;
                return true;
            }
            catch (Exception ex) { reason = "CreateMathPoint/TransformBy " + Hr(ex); return false; }
        }

        public static string Classify(string typeName)
        {
            if (string.IsNullOrEmpty(typeName) || typeName.StartsWith("<", StringComparison.Ordinal)) return "unknown";
            if (typeName.StartsWith("Z_ELEMENT_FEATURE", StringComparison.Ordinal)) return "feature";
            if (typeName.IndexOf("ASSEMBLY", StringComparison.Ordinal) >= 0) return "assembly";
            if (typeName.IndexOf("PART", StringComparison.Ordinal) >= 0) return "part";
            return "other";
        }

        public static string PartKind(IZPart part, out int mask)
        {
            mask = -1;
            try
            {
                eZPartType pt;
                part.GetPartType(out pt);
                mask = (int)pt;
                bool s = (mask & PartTypeStructured) != 0, i = (mask & PartTypeInnovation) != 0;
                return s && !i ? "工程零件" : i && !s ? "创新零件"
                    : s && i ? "工程+创新(两位都置)" : "未判定(无工程/创新位)";
            }
            catch (Exception ex) { return "<FAIL:" + Hr(ex) + ">"; }
        }

        // ---------------------------------------------------------------- 选择集

        /// <summary>四条选择通道全读并按 IUnknown 指针去重（tracker ProbeCommon.ReadSelectionRaw 同口径）。</summary>
        public static List<object> Selection(McpToolContext ctx, out int uiContext, out List<object> channels, out string err)
        {
            var result = new List<object>();
            var seen = new HashSet<IntPtr>();
            channels = new List<object>();
            uiContext = -1; err = null;
            IZSelectionMgr sm = null;
            try { sm = ctx.Scene.SelectionMgr; }
            catch (Exception ex) { err = "SelectionMgr " + Hr(ex); return result; }
            if (sm == null) { err = "SelectionMgr 为 null（第三态）"; return result; }

            try { uiContext = (int)sm.SelectionUIContext; }
            catch (Exception ex) { channels.Add(new Dictionary<string, object> { ["channel"] = "SelectionUIContext", ["error"] = Hr(ex) }); }

            ReadChannel(sm, "GetSelectedElementsZArray(19)", () => sm.GetSelectedElementsZArray(), result, seen, channels);
            ReadChannel(sm, "GetSelectedElements(10)", () => sm.GetSelectedElements(), result, seen, channels);
            ReadChannel(sm, "GetTrueSelectedElements(28)", () => sm.GetTrueSelectedElements(), result, seen, channels);
            ReadChannel(sm, "GetSelectedCurves(30)", () => sm.GetSelectedCurves(), result, seen, channels);
            return result;
        }

        private static void ReadChannel(IZSelectionMgr sm, string what, Func<object> read,
            List<object> result, HashSet<IntPtr> seen, List<object> channels)
        {
            object raw;
            try { raw = read(); }
            catch (Exception ex)
            {
                channels.Add(new Dictionary<string, object> { ["channel"] = what, ["ok"] = false, ["error"] = Hr(ex) });
                return;
            }
            int added = 0, flatCount = 0;
            foreach (object o in McpToolContext.UnpackArray(raw))
            {
                flatCount++;
                IntPtr p = IntPtr.Zero;
                try { p = Marshal.GetIUnknownForObject(o); if (p != IntPtr.Zero) Marshal.Release(p); } catch { }
                if (p != IntPtr.Zero && !seen.Add(p)) continue;
                result.Add(o); added++;
            }
            channels.Add(new Dictionary<string, object> { ["channel"] = what, ["ok"] = true, ["flat"] = flatCount, ["added"] = added });
        }

        public static bool IsMovable(object raw, IZElement el, out string why)
        {
            why = null;
            int type = -1;
            try { if (el != null) type = (int)el.Type; } catch { }
            bool isAsm = type == 2;   // Z_ELEMENT_ASSEMBLY
            IZPart part = null;
            if (!isAsm) { try { part = raw as IZPart; } catch { } }
            if (!isAsm && part == null) { why = "非装配且 QI IZPart 失败（草图曲线等不可移动）"; return false; }
            return true;
        }

        /// <summary>单件移动：三分量直写（mm→米）→ 回读（±0.01mm）→ 矩阵交叉验证。verified 只认回读。</summary>
        public static Dictionary<string, object> MoveOne(McpToolContext ctx, object raw, string name,
            double[] targetMm, bool relative, double toleranceMm)
        {
            var d = new Dictionary<string, object> { ["name"] = name };
            IZSceneElement se = raw as IZSceneElement;
            if (se == null) { d["ok"] = false; d["error"] = "QI IZSceneElement 失败（第三态，iid=75DCDA23）"; return d; }

            double l0, w0, h0;
            try
            {
                l0 = se.get_PositionTransformComponentValue(eZTransformComponentType.Z_TRANS_L) * MeterToMm;
                w0 = se.get_PositionTransformComponentValue(eZTransformComponentType.Z_TRANS_W) * MeterToMm;
                h0 = se.get_PositionTransformComponentValue(eZTransformComponentType.Z_TRANS_H) * MeterToMm;
            }
            catch (Exception ex) { d["ok"] = false; d["error"] = "基线读失败 " + Hr(ex); return d; }

            double[] want = relative
                ? new double[] { l0 + targetMm[0], w0 + targetMm[1], h0 + targetMm[2] }
                : targetMm;
            try
            {
                se.set_PositionTransformComponentValue(eZTransformComponentType.Z_TRANS_L, want[0] / MeterToMm);
                se.set_PositionTransformComponentValue(eZTransformComponentType.Z_TRANS_W, want[1] / MeterToMm);
                se.set_PositionTransformComponentValue(eZTransformComponentType.Z_TRANS_H, want[2] / MeterToMm);
            }
            catch (Exception ex) { d["ok"] = false; d["error"] = "三分量写入失败 " + Hr(ex); return d; }

            double l1 = double.NaN, w1 = double.NaN, h1 = double.NaN;
            try
            {
                l1 = se.get_PositionTransformComponentValue(eZTransformComponentType.Z_TRANS_L) * MeterToMm;
                w1 = se.get_PositionTransformComponentValue(eZTransformComponentType.Z_TRANS_W) * MeterToMm;
                h1 = se.get_PositionTransformComponentValue(eZTransformComponentType.Z_TRANS_H) * MeterToMm;
            }
            catch (Exception ex) { d["readBackError"] = Hr(ex); }

            // 矩阵交叉验证：只作旁证，不否决主判据
            try
            {
                ZMathMatrix m = se.GetPositionTransform();
                IZMathMatrix mi = m as IZMathMatrix;
                if (mi != null)
                {
                    double tx, ty, tz; mi.GetTranslation(out tx, out ty, out tz);
                    d["matrixMm"] = new double[] { tx * MeterToMm, ty * MeterToMm, tz * MeterToMm };
                }
                else d["matrixError"] = "矩阵 QI 不到 IZMathMatrix 表面";
            }
            catch (Exception ex) { d["matrixError"] = Hr(ex); }

            bool readOk = !double.IsNaN(l1) && !double.IsNaN(w1) && !double.IsNaN(h1);
            bool verified = readOk &&
                Math.Abs(l1 - want[0]) <= toleranceMm && Math.Abs(w1 - want[1]) <= toleranceMm && Math.Abs(h1 - want[2]) <= toleranceMm;
            d["ok"] = verified;
            d["verified"] = verified;
            d["beforeMm"] = new double[] { l0, w0, h0 };
            d["requestedMm"] = want;
            d["readBackMm"] = readOk ? new double[] { l1, w1, h1 } : null;
            d["toleranceMm"] = toleranceMm;
            d["coordFrame"] = "PositionTransform 分量（顶层=全局 X/Y/Z；装配内嵌套件=相对父级）";
            d["verifiedBy"] = "三分量写后回读与期望值比对（不认 set 未抛异常）";
            if (!verified) d["error"] = readOk ? "回读值与目标不符" : "回读失败（第三态）";
            return d;
        }

        /// <summary>xy 解析：[x,y] 数组或 {x,y} 对象，单位 mm。</summary>
        public static bool XY(JValue v, out double x, out double y)
        {
            x = y = 0;
            if (v == null) return false;
            if (v.Kind == JKind.Array && v.Arr != null && v.Arr.Count >= 2)
            { x = v.Arr[0].AsDouble(); y = v.Arr[1].AsDouble(); return true; }
            if (v.Kind == JKind.Object)
            { x = v.ParamDouble("x", 0); y = v.ParamDouble("y", 0); return true; }
            return false;
        }

        /// <summary>画线双形态：tracker 实机已证 2D 双元素；本工程既有 extrude_sketch 用 3 元素。两种都留痕。</summary>
        public static bool DrawLine(IZProfile profile, double[] a, double[] b, int desireId, List<object> attempts)
        {
            try
            {
                profile.CreateLine(new double[] { a[0], a[1] }, new double[] { b[0], b[1] }, desireId);
                attempts.Add(new Dictionary<string, object> { ["channel"] = "IZProfile.CreateLine(2D 双元素, tracker 实证形态)", ["ok"] = true });
                return true;
            }
            catch (Exception ex2)
            {
                attempts.Add(new Dictionary<string, object> { ["channel"] = "IZProfile.CreateLine(2D 双元素)", ["ok"] = false, ["error"] = Hr(ex2) });
            }
            try
            {
                profile.CreateLine(new double[] { a[0], a[1], 0 }, new double[] { b[0], b[1], 0 }, desireId);
                attempts.Add(new Dictionary<string, object> { ["channel"] = "IZProfile.CreateLine(3D 三元素, 工程既有形态)", ["ok"] = true });
                return true;
            }
            catch (Exception ex3)
            {
                attempts.Add(new Dictionary<string, object> { ["channel"] = "IZProfile.CreateLine(3D 三元素)", ["ok"] = false, ["error"] = Hr(ex3) });
                return false;
            }
        }

        public static void WriteCsv(string path, string header, List<string[]> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(header);
            foreach (var r in rows)
            {
                for (int i = 0; i < r.Length; i++) { if (i > 0) sb.Append(','); sb.Append(CsvCell(r[i])); }
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));   // BOM：Excel 中文不乱码
        }

        private static string CsvCell(string s)
        {
            if (s == null) return "";
            if (s.IndexOfAny(new char[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }

    // ---------------------------------------------------------------- ① 参数表抓取

    [ComVisible(false)]
    internal sealed class ToolGetParameterTable : McpToolBase
    {
        public override string Name { get { return "get_parameter_table"; } }
        public override string Category { get { return "probe"; } }
        public override string Description { get { return "只读抓取 CAXA 参数表：递归元素树，读 IZSceneElement.ParameterMgr → IZParameterMgr.GetParameter，逐列返回 Id/Name/Type/Expression/Value(米+毫米)/Notes/TextParameterValue。轮廓层 Z_ELEMENT_PROFILE 是实证落点；读不到记第三态不折算 0。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["max_depth"] = S.Num("树深度上限，默认 12", false, 12.0, true),
                ["max_nodes"] = S.Num("节点预算，默认 5000", false, 5000.0, true),
                ["csv_path"] = S.Str("可选：同时把结果写成 UTF-8 BOM CSV 的完整路径")
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            int maxDepth = args.ParamInt("max_depth", 12); if (maxDepth < 0) maxDepth = 0; if (maxDepth > 40) maxDepth = 40;
            int maxNodes = args.ParamInt("max_nodes", 5000); if (maxNodes < 1) maxNodes = 1; if (maxNodes > 50000) maxNodes = 50000;
            int truncated, failedBranches; string rootError;
            var nodes = TrackerKit.Enumerate(ctx, maxDepth, maxNodes, out truncated, out failedBranches, out rootError);

            var rows = new List<object>();
            var csv = new List<string[]>();
            int owners = 0, profiles = 0, parameters = 0, unreadable = 0, mgrFailed = 0;
            foreach (TrkNode n in nodes)
            {
                IZSceneElement se = n.Raw as IZSceneElement;
                if (se == null) continue;
                IZParameterMgr pm = null;
                try { pm = se.ParameterMgr; }
                catch (Exception ex) { mgrFailed++; ctx.Log("param ParameterMgr FAIL " + TrackerKit.Hr(ex)); continue; }
                if (pm == null) continue;
                int count = -1;
                try { count = pm.Count; }
                catch (Exception ex) { mgrFailed++; ctx.Log("param Count FAIL " + TrackerKit.Hr(ex)); continue; }
                if (count <= 0) continue;
                owners++;
                if (n.Type == "Z_ELEMENT_PROFILE") profiles++;
                for (int i = 0; i < count; i++)   // 严格 i<Count：越界 GetParameter 抛 E_FAIL
                {
                    IZParameter p = null;
                    try { p = pm.GetParameter(i); }
                    catch (Exception ex)
                    { unreadable++; ctx.Log("param GetParameter[" + i + "] FAIL " + TrackerKit.Hr(ex)); continue; }
                    if (p == null) { unreadable++; continue; }
                    parameters++;

                    string pid = TrackerKit.Safe(() => p.Id.ToString(CultureInfo.InvariantCulture), "<读不到>");
                    string pname = TrackerKit.Safe(() => p.Name, "<读不到>");
                    string ptype = TrackerKit.Safe(() => p.Type.ToString(), "<读不到>");
                    string expr = TrackerKit.Safe(() => p.Expression, "<读不到>");
                    string notes = TrackerKit.Safe(() => p.Notes, "<读不到>");
                    string valM = "<读不到>", valMm = "N/A";
                    try
                    {
                        double v = p.Value;   // 米
                        valM = TrackerKit.Fmt(v);
                        valMm = (v * TrackerKit.MeterToMm).ToString("F6", CultureInfo.InvariantCulture);
                    }
                    catch (Exception ex) { valM = "<FAIL:" + TrackerKit.Hr(ex) + ">"; unreadable++; }
                    string text;
                    try { text = p.TextParameterValue ?? ""; }
                    catch (Exception ex) { text = "N/A(" + TrackerKit.Hr(ex) + ")"; }  // LENGTH 类型抛 E_FAIL＝第三态

                    foreach (string s in new string[] { pid, pname, ptype, expr, notes, valM })
                        if (s.StartsWith("<", StringComparison.Ordinal)) unreadable++;

                    var row = new Dictionary<string, object>
                    {
                        ["ownerId"] = n.Id, ["ownerName"] = n.Name, ["ownerType"] = n.Type,
                        ["index"] = i, ["paramId"] = pid, ["name"] = pname, ["type"] = ptype,
                        ["expression"] = expr, ["valueMeters"] = valM, ["valueMm"] = valMm,
                        ["notes"] = notes, ["textParameterValue"] = text,
                        ["unit"] = "Value 原始单位=米；界面 mm=米×1000"
                    };
                    rows.Add(row);
                    csv.Add(new string[] { n.Id.ToString(CultureInfo.InvariantCulture), n.Name, n.Type,
                        i.ToString(CultureInfo.InvariantCulture), pid, pname, ptype, expr, valM, valMm, notes, text });
                }
            }

            string csvPath = args.ParamString("csv_path", "");
            string csvError = null;
            if (!string.IsNullOrEmpty(csvPath))
            {
                try
                {
                    TrackerKit.WriteCsv(csvPath,
                        "OwnerId,OwnerName,OwnerType,Index,ParamId,Name,Type,Expression,ValueMeters,ValueMm,Notes,TextParameterValue", csv);
                    csvPath = Path.GetFullPath(csvPath);
                }
                catch (Exception ex) { csvError = ex.Message; }
            }
            else csvPath = null;

            bool emptyIsUnknown = parameters == 0 && (failedBranches > 0 || truncated > 0 || mgrFailed > 0 || rootError != null);
            return AgentKit.Ok(
                "rows", rows, "parameterOwners", owners, "profilesWithParameters", profiles,
                "parameterCount", parameters, "scannedNodes", nodes.Count, "truncated", truncated,
                "failedBranches", failedBranches, "parameterMgrFailures", mgrFailed,
                "unreadableColumns", unreadable, "rootError", rootError,
                "emptyVerdict", parameters == 0
                    ? (emptyIsUnknown ? "读不到（第三态：存在分支失败/截断，不能判定真空）" : "真空（树已完整走查，没有任何参数）")
                    : "已读到参数",
                "csvPath", csvPath, "csvError", csvError,
                "level", "attempt",
                "evidence", "caxa3dtracker ParameterTableProbe 实机已证读链与米→毫米口径；本 MCP 服务侧未实机跑过");
        }
    }

    // ---------------------------------------------------------------- ② 特征包围盒

    [ComVisible(false)]
    internal sealed class ToolGetFeatureBoundingBoxes : McpToolBase
    {
        public override string Name { get { return "get_feature_bounding_boxes"; } }
        public override string Category { get { return "probe"; } }
        public override string Description { get { return "只读抓取特征级/零件级/装配级包围盒：IZFeature.GetBoundingBox(13)、IZPart.GetBody(true)→IZBody.GetBoundingBox(13)/GetTightBoundingBox(21)、IZAssembly.GetBoundingBox(false)(53)。局部盒可按 GetTransformToGlobal 八角点换算世界 AABB；给米原值与毫米尺寸，读不到记第三态。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["include_parts"] = S.Bool("含零件级盒，默认 true", false, true, true),
                ["include_assemblies"] = S.Bool("含装配级盒，默认 true", false, true, true),
                ["world"] = S.Bool("尝试局部→世界变换，默认 true", false, true, true),
                ["max_depth"] = S.Num("树深度上限，默认 12", false, 12.0, true),
                ["max_nodes"] = S.Num("节点预算，默认 5000", false, 5000.0, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            bool withParts = args.ParamBool("include_parts", true);
            bool withAsm = args.ParamBool("include_assemblies", true);
            bool wantWorld = args.ParamBool("world", true);
            int maxDepth = args.ParamInt("max_depth", 12); if (maxDepth < 0) maxDepth = 0; if (maxDepth > 40) maxDepth = 40;
            int maxNodes = args.ParamInt("max_nodes", 5000); if (maxNodes < 1) maxNodes = 1; if (maxNodes > 50000) maxNodes = 50000;
            int truncated, failedBranches; string rootError;
            var nodes = TrackerKit.Enumerate(ctx, maxDepth, maxNodes, out truncated, out failedBranches, out rootError);

            var boxes = new List<object>();
            int features = 0, parts = 0, assemblies = 0, withBox = 0, worldOk = 0, engineering = 0, innovation = 0;
            foreach (TrkNode n in nodes)
            {
                // ★ 类型名分诊优先：Z 系列 QI 任何接口都可能成功，按 as 排序会串位（tracker 实证坑）
                string cls = TrackerKit.Classify(n.Type);
                if (cls == "unknown") continue;
                if (cls == "other") continue;
                if (cls == "part" && !withParts) continue;
                if (cls == "assembly" && !withAsm) continue;

                string source = "", status = "ok", note = "";
                double[] local = null, tight = null;
                string partKind = "", kernel = "", featIndex = "", featType = "", opType = "";
                int mask = -1;
                bool alreadyWorld = false;

                if (cls == "feature")
                {
                    features++;
                    IZFeature ft = n.Raw as IZFeature;
                    if (ft == null) { status = "read_failed"; note = "QI IZFeature 失败（第三态）"; }
                    else
                    {
                        featIndex = TrackerKit.Safe(() => ft.FeatureIndex.ToString(CultureInfo.InvariantCulture), "<读不到>");
                        featType = TrackerKit.Safe(() => ft.Type.ToString(), "<读不到>");
                        opType = TrackerKit.Safe(() => ft.OperationType.ToString(), "<读不到>");
                        try { local = TrackerKit.Box(ft.GetBoundingBox()); source = "IZFeature.GetBoundingBox()（槽13，局部）"; }
                        catch (Exception ex) { status = "read_failed"; note = "GetBoundingBox " + TrackerKit.Hr(ex); }
                    }
                }
                else if (cls == "part")
                {
                    parts++;
                    IZPart part = n.Raw as IZPart;
                    if (part == null) { status = "read_failed"; note = "QI IZPart 失败（第三态）"; }
                    else
                    {
                        partKind = TrackerKit.PartKind(part, out mask);
                        if (partKind == "工程零件") engineering++; else if (partKind == "创新零件") innovation++;
                        kernel = TrackerKit.Safe(() => part.KernelType.ToString(), "<读不到>");
                        IZBody body = null;
                        try { body = part.GetBody(true); }
                        catch (Exception ex) { note = "GetBody " + TrackerKit.Hr(ex); }
                        if (body != null)
                        {
                            try { local = TrackerKit.Box(body.GetBoundingBox()); }
                            catch (Exception ex) { note += " Body.GetBoundingBox " + TrackerKit.Hr(ex); }
                            try { tight = TrackerKit.Box(body.GetTightBoundingBox()); }
                            catch (Exception ex) { note += " Body.GetTightBoundingBox " + TrackerKit.Hr(ex); }
                            source = "IZPart.GetBody(true)→IZBody.GetBoundingBox()（槽13，局部）";
                        }
                        else if (note.Length == 0) note = "该零件没有实体（曲线/空零件/仅特征）";
                    }
                }
                else if (cls == "assembly")
                {
                    assemblies++;
                    IZAssembly asm = n.Raw as IZAssembly;
                    if (asm == null) { status = "read_failed"; note = "QI IZAssembly 失败（第三态）"; }
                    else
                    {
                        try { local = TrackerKit.Box(asm.GetBoundingBox(false)); }
                        catch (Exception ex) { status = "read_failed"; note = "GetBoundingBox " + TrackerKit.Hr(ex); }
                        source = "IZAssembly.GetBoundingBox(false)（槽53，世界）";
                        alreadyWorld = true;   // 装配级直接世界坐标，不再做局部→世界换算
                    }
                }

                if (local == null || local.Length < 6)
                {
                    if (status == "ok") { status = "no_box"; if (note.Length == 0) note = "读不到包围盒（第三态，不折算 0）"; }
                }
                else withBox++;

                var d = new Dictionary<string, object>
                {
                    ["kind"] = cls, ["id"] = n.Id, ["name"] = n.Name, ["elementType"] = n.Type,
                    ["partKind"] = partKind, ["partTypeMask"] = mask < 0 ? "<读不到>" : "0x" + mask.ToString("X"),
                    ["kernelType"] = kernel, ["featureIndex"] = featIndex, ["featureType"] = featType,
                    ["operationType"] = opType, ["source"] = source, ["status"] = status, ["note"] = note.Trim()
                };

                if (local != null && local.Length >= 6)
                {
                    double[] used = local;
                    string space = alreadyWorld ? "世界" : "局部";
                    if (!alreadyWorld && wantWorld)
                    {
                        double[] w; string why;
                        if (TrackerKit.TryBoxToWorld(ctx, n.Raw, local, out w, out why))
                        { used = w; space = "世界"; worldOk++; d["source"] = source + " + GetTransformToGlobal（8角点→世界AABB）"; }
                        else d["worldTransformError"] = why;
                    }
                    d["coordSpace"] = space;
                    d["min"] = new double[] { used[0], used[1], used[2] };
                    d["max"] = new double[] { used[3], used[4], used[5] };
                    d["sizeMm"] = new double[] { (used[3] - used[0]) * TrackerKit.MeterToMm,
                                                 (used[4] - used[1]) * TrackerKit.MeterToMm,
                                                 (used[5] - used[2]) * TrackerKit.MeterToMm };
                    d["unit"] = "min/max 单位=米；sizeMm 单位=毫米";
                    if (tight != null && tight.Length >= 6)
                    {
                        d["tightSizeMm"] = new double[] { (tight[3] - tight[0]) * TrackerKit.MeterToMm,
                                                          (tight[4] - tight[1]) * TrackerKit.MeterToMm,
                                                          (tight[5] - tight[2]) * TrackerKit.MeterToMm };
                        d["tightCoordSpace"] = "局部（紧致盒三维尺寸，未做旋转修正）";
                    }
                }
                else d["coordSpace"] = "<无>";
                boxes.Add(d);
            }

            return AgentKit.Ok(
                "boxes", boxes, "boxCount", boxes.Count, "withBox", withBox, "worldTransformed", worldOk,
                "features", features, "parts", parts, "engineeringParts", engineering, "innovationParts", innovation,
                "assemblies", assemblies, "scannedNodes", nodes.Count, "truncated", truncated,
                "failedBranches", failedBranches, "rootError", rootError,
                "level", "attempt",
                "evidence", "caxa3dtracker FeatureBoxProbe 已证槽位与工程/创新位掩码；本 MCP 服务侧未实机跑过",
                "note", "类型分诊按 eZElementType 名称（QI 顺序会串位）；读不到盒的元素 status=no_box/read_failed，数值列不出现 0 冒充值");
        }
    }

    // ---------------------------------------------------------------- ③ 草图建立

    [ComVisible(false)]
    internal sealed class ToolCreateSketch : McpToolBase
    {
        public override string Name { get { return "create_sketch"; } }
        public override string Category { get { return "sketch"; } }
        public override string Description { get { return "在当前场景 XY 平面建立 2D 草图（CreateProfile(22) + CreateLine(29)），支持 rect/polyline；输入毫米内部转米。判据=GetCurvesCount 写后回读 + 顶层元素计数；不拉伸、不投 UI 命令、不做智能标注。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["shape"] = S.Enum("rect=闭合矩形；polyline=按 points 顺序连线", new string[] { "rect", "polyline" }, false, "rect"),
                ["width"] = S.Num("rect 宽 X（默认单位 mm），默认 20", false, 20.0, true),
                ["height"] = S.Num("rect 高 Y（默认单位 mm），默认 10", false, 10.0, true),
                ["points"] = S.Array("polyline 顶点，元素为 [x,y] 或 {x,y}，单位与 unit 一致", new Dictionary<string, object>()),
                ["unit"] = S.Enum("输入长度单位", new string[] { "mm", "m" }, false, "mm"),
                ["name"] = S.Str("可选：草图轮廓重命名并回读"),
                ["update"] = S.Bool("建完后调 IZSceneDoc.Update()，默认 true", false, true, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            var attempts = AgentKit.Attempts();

            // 护栏：草图编辑态中建草图会落到已有上下文（tracker 17:35 实机坑），直接拒绝
            int uiCtx = -1;
            try
            {
                IZSelectionMgr smPre = ctx.Scene.SelectionMgr;
                if (smPre != null) uiCtx = (int)smPre.SelectionUIContext;
            }
            catch (Exception ex) { AgentKit.Attempt(attempts, "SelectionUIContext 起始预检", false, TrackerKit.Hr(ex)); }
            if (uiCtx == 2)
                return AgentKit.Fail("already_in_sketch_edit",
                    "当前已在草图编辑态（SelectionUIContext=2）。请先退出草图再建新草图，否则曲线会画进现有轮廓且选中错位");

            string shape = args.ParamString("shape", "rect");
            double scale = args.ParamString("unit", "mm") == "m" ? 1.0 : 0.001;   // mm→米
            var pts = new List<double[]>();
            if (shape == "rect")
            {
                double w = args.ParamDouble("width", 20) * scale, h = args.ParamDouble("height", 10) * scale;
                if (w <= 0 || h <= 0) return AgentKit.Fail("bad_size", "width/height 必须为正数");
                pts.Add(new double[] { 0, 0 }); pts.Add(new double[] { w, 0 });
                pts.Add(new double[] { w, h }); pts.Add(new double[] { 0, h });
            }
            else if (shape == "polyline")
            {
                JValue pv = args["points"];
                if (pv.Kind != JKind.Array || pv.Arr == null || pv.Arr.Count < 2)
                    return AgentKit.Fail("bad_points", "polyline 至少需要 2 个顶点（points=[ [x,y], ... ]）");
                foreach (JValue v in pv.Arr)
                {
                    double x, y;
                    if (!TrackerKit.XY(v, out x, out y)) { AgentKit.Attempt(attempts, "points 解析", false, "顶点格式错误"); continue; }
                    pts.Add(new double[] { x * scale, y * scale });
                }
                if (pts.Count < 2) return AgentKit.Fail("bad_points", "没有解析出有效顶点");
            }
            else return AgentKit.Fail("bad_shape", "shape 只能是 rect 或 polyline");

            int topBefore = AgentKit.TopCount(ctx);
            IZProfile profile = null;
            try { profile = ctx.Scene.CreateProfile(); AgentKit.Attempt(attempts, "IZSceneDoc.CreateProfile(22)", profile != null, "返回 IZProfile"); }
            catch (Exception ex) { AgentKit.Attempt(attempts, "IZSceneDoc.CreateProfile(22)", false, TrackerKit.Hr(ex)); }
            if (profile == null) return AgentKit.Fail("profile_failed", "CreateProfile 未返回 IZProfile（查 attempts）");

            int expected = shape == "rect" ? pts.Count : pts.Count - 1;
            int drawn = 0;
            for (int i = 0; i < expected; i++)
            {
                double[] a = pts[i], b = pts[(i + 1) % pts.Count];
                if (TrackerKit.DrawLine(profile, a, b, i + 1, attempts)) drawn++;
            }

            int curvesBefore = -1, curvesAfter = -1;
            try { curvesBefore = profile.GetCurvesCount(); } catch { }
            if (args.ParamBool("update", true))
            {
                try { ctx.Scene.Update(); AgentKit.Attempt(attempts, "IZSceneDoc.Update()", true, "重建场景"); }
                catch (Exception ex) { AgentKit.Attempt(attempts, "IZSceneDoc.Update()", false, TrackerKit.Hr(ex)); }
            }
            try { curvesAfter = profile.GetCurvesCount(); }
            catch (Exception ex) { AgentKit.Attempt(attempts, "IZProfile.GetCurvesCount(17)", false, TrackerKit.Hr(ex)); }

            // 草图轮廓也是零件内建模节点，同样按建模顺序编号（N-草图），写后回读
            string setName = args.ParamString("name", "");
            string label = string.IsNullOrEmpty(setName) ? "草图" : BuildSeq.Strip(setName);
            var seqInfo = new Dictionary<string, object>();
            BuildSeq.ApplyNext(ctx, profile, label, seqInfo, "profile");
            string nameError = seqInfo.ContainsKey("profileSeqNameError") ? Convert.ToString(seqInfo["profileSeqNameError"]) : null;
            string finalName = seqInfo.ContainsKey("profileSeqNameReadBack")
                ? Convert.ToString(seqInfo["profileSeqNameReadBack"]) : McpToolContext.SafeNameOf(profile);
            bool seqVerified = seqInfo.ContainsKey("profileSeqNameVerified") && Convert.ToBoolean(seqInfo["profileSeqNameVerified"]);
            AgentKit.Attempt(attempts, "IZElement.set_Name(建模顺序号)", seqVerified, nameError ?? finalName);
            int topAfter = AgentKit.TopCount(ctx);
            // 权威判据 = 曲线数写后回读达到期望；顶层计数只作旁证（轮廓可能挂在零件下）
            bool verified = curvesAfter >= expected && drawn == expected;
            var ret = AgentKit.Ok(
                "verified", verified, "shape", shape, "drawnLines", drawn, "expectedLines", expected,
                "curvesBefore", curvesBefore, "curvesAfter", curvesAfter,
                "topCountBefore", topBefore, "topCountAfter", topAfter,
                "profileName", finalName, "profileSeqNameVerified", seqVerified, "nameError", nameError,
                "inputUnit", args.ParamString("unit", "mm"), "modelUnit", "米",
                "attempts", attempts, "level", "attempt",
                "verifiedBy", "GetCurvesCount 写后回读 ≥ 期望线数（不认 CreateLine 未抛异常）",
                "capabilityBoundary", "不拉伸、不进入草图编辑态、不投 WM_COMMAND；智能标注在 COM 层无创建接口，属 UI 交互命令，本服务不实现",
                "evidence", "caxa3dtracker RectSketchTool 实机已证 CreateProfile(22)+CreateLine(29) 建模段；本 MCP 服务侧未实机跑过",
                "note", verified ? null : "曲线回读未达期望，第三态：可能已建出轮廓但曲线未落地，请按 attempts 定位，不要当作建模成功");
            foreach (var kv in seqInfo) ret[kv.Key] = kv.Value;
            return ret;
        }
    }

    // ---------------------------------------------------------------- ④ 移动零部件

    [ComVisible(false)]
    internal sealed class ToolMoveComponent : McpToolBase
    {
        public override string Name { get { return "move_component"; } }
        public override string Category { get { return "transform"; } }
        public override string Description { get { return "按 name/id 把零件/装配移动到 (x,y,z) mm：写 IZSceneElement.PositionTransformComponentValue 的 L/W/H 三分量（只改平移、保留姿态），三分量写后回读 ±0.01mm 才算成功，并用 GetPositionTransform/GetTranslation 交叉验证。顶层=全局坐标，嵌套件=相对父级。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["name"] = S.Str("目标零部件名（与 id 二选一；都不传则定位最近创建的元素）"),
                ["id"] = S.Num("目标元素 Id"),
                ["x"] = S.Num("X 坐标（mm）", false, 0.0, true),
                ["y"] = S.Num("Y 坐标（mm）", false, 0.0, true),
                ["z"] = S.Num("Z 坐标（mm）", false, 0.0, true),
                ["relative"] = S.Bool("true=相对当前位置增量，默认 false=绝对定位", false, false, true),
                ["tolerance_mm"] = S.Num("回读容差 mm，默认 0.01", false, 0.01, true)
            }, "x", "y", "z");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            string err;
            IZElement el = AgentKit.FindElement(ctx, args, out err);
            if (el == null) return AgentKit.Fail("element_not_found", err ?? "找不到目标元素");
            object raw = el;   // FindElement 返回 IZElement 表面；QI 到 IZSceneElement 在 MoveOne 内做
            string why;
            if (!TrackerKit.IsMovable(raw, el, out why))
                return AgentKit.Fail("not_movable", "「" + McpToolContext.SafeName(el) + "」" + why);
            double[] target = { args.ParamDouble("x", 0), args.ParamDouble("y", 0), args.ParamDouble("z", 0) };
            double tol = args.ParamDouble("tolerance_mm", 0.01); if (tol <= 0) tol = 0.01;
            var r = TrackerKit.MoveOne(ctx, raw, McpToolContext.SafeName(el), target,
                args.ParamBool("relative", false), tol);
            r["level"] = "attempt";
            r["evidence"] = "caxa3dtracker MoveComponentTool 实机已证三分量单位=米且回读判据成立；本 MCP 服务侧未实机跑过";
            r["elementId"] = AgentKit.SafeId(el);
            return r;
        }
    }

    [ComVisible(false)]
    internal sealed class ToolMoveSelectedComponents : McpToolBase
    {
        public override string Name { get { return "move_selected_components"; } }
        public override string Category { get { return "transform"; } }
        public override string Description { get { return "把当前视口/场景树中选中的零部件批量移动到 (x,y,z) mm（四条选择通道去重）。非零部件跳过并留痕；逐件只认三分量写后回读。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["x"] = S.Num("X 坐标（mm）", false, 0.0, true),
                ["y"] = S.Num("Y 坐标（mm）", false, 0.0, true),
                ["z"] = S.Num("Z 坐标（mm）", false, 0.0, true),
                ["relative"] = S.Bool("true=相对当前位置增量，默认 false", false, false, true),
                ["tolerance_mm"] = S.Num("回读容差 mm，默认 0.01", false, 0.01, true)
            }, "x", "y", "z");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            int uiCtx; List<object> channels; string selErr;
            var sel = TrackerKit.Selection(ctx, out uiCtx, out channels, out selErr);
            if (selErr != null) return AgentKit.Fail("selection_unavailable", selErr);
            if (sel.Count == 0)
                return AgentKit.Fail("nothing_selected", "当前选择集为空，请先在视口或场景树选中零部件，或改用 move_component 按 name/id 定位");
            double[] target = { args.ParamDouble("x", 0), args.ParamDouble("y", 0), args.ParamDouble("z", 0) };
            double tol = args.ParamDouble("tolerance_mm", 0.01); if (tol <= 0) tol = 0.01;
            bool relative = args.ParamBool("relative", false);
            var moved = new List<object>(); var skipped = new List<object>(); var failed = new List<object>();
            foreach (object raw in sel)
            {
                IZElement el = McpToolContext.AsElement(raw);
                string name = el == null ? "<非元素>" : McpToolContext.SafeName(el);
                string why;
                if (!TrackerKit.IsMovable(raw, el, out why))
                { skipped.Add(new Dictionary<string, object> { ["name"] = name, ["reason"] = why }); continue; }
                var r = TrackerKit.MoveOne(ctx, raw, name, target, relative, tol);
                if (r.ContainsKey("verified") && (bool)r["verified"]) moved.Add(r);
                else failed.Add(r);
            }
            return AgentKit.Ok(
                "selected", sel.Count, "moved", moved.Count, "skipped", skipped.Count, "failed", failed.Count,
                "movedDetails", moved, "skippedDetails", skipped, "failedDetails", failed,
                "selectionUIContext", uiCtx, "channels", channels,
                "level", "attempt", "targetMm", target, "relative", relative,
                "verifiedBy", "逐件三分量写后回读 ±" + tol.ToString(CultureInfo.InvariantCulture) + "mm",
                "evidence", "caxa3dtracker MoveComponentTool 与 ProbeCommon.ReadSelectionRaw 实机已证；本 MCP 服务侧未实机跑过");
        }
    }

    [ComVisible(false)]
    internal sealed class ToolGetSelection : McpToolBase
    {
        public override string Name { get { return "get_selection"; } }
        public override string Category { get { return "transform"; } }
        public override string Description { get { return "只读列出当前选择集（四条通道去重）：id/name/type/是否零件/是否装配/是否可移动，以及 SelectionUIContext（=2 表示草图编辑态）。供移动/特征工具选目标前调用。"; } }
        public override object InputSchema() { return S.Schema(new Dictionary<string, object>()); }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var bad = NeedScene(ctx); if (bad != null) return bad;
            int uiCtx; List<object> channels; string err;
            var sel = TrackerKit.Selection(ctx, out uiCtx, out channels, out err);
            var items = new List<object>();
            foreach (object raw in sel)
            {
                IZElement el = McpToolContext.AsElement(raw);
                string why;
                bool movable = TrackerKit.IsMovable(raw, el, out why);
                items.Add(new Dictionary<string, object>
                {
                    ["id"] = el == null ? -1 : AgentKit.SafeId(el),
                    ["name"] = el == null ? "<非元素>" : McpToolContext.SafeName(el),
                    ["type"] = el == null ? "<读不到>" : TrackerKit.Safe(() => el.Type.ToString(), "<读不到>"),
                    ["isPart"] = raw is IZPart,
                    ["isAssembly"] = raw is IZAssembly,
                    ["movable"] = movable,
                    ["notMovableReason"] = movable ? null : why
                });
            }
            return AgentKit.Ok("count", items.Count, "items", items, "selectionUIContext", uiCtx,
                "uiContextHint", uiCtx == 2 ? "草图/轮廓编辑态（建草图工具会拒绝）" : "非草图编辑态",
                "channels", channels, "error", err, "level", "implemented",
                "evidence", "四条选择通道来自 caxa3dtracker ProbeCommon.ReadSelectionRaw 实证口径");
        }
    }
}
