// 工具集：建模顺序号 + 普通零件两条建模路线（用户 2026-11-14 10:50 需求裁定）。
//
// ★ 三条需求对应三条通道（全部先认 2027 TLB members.csv，再按通道阶梯取顶层 RCW）★
//   1) 多零件模式「搭积木后统一布尔」：IZPart.Boolean(13, IZPart piToolPart + eZOperationType) → IZFeature
//      （v26 interop 已确认含 Boolean 成员，RCW 直调；Z_UNITE=1 / Z_SUBTRACT=2 / Z_INTERSECT=3）
//   2) 激活零件后在零件内部做特征：写＝设计环境命令派发 WM_COMMAND 53111（Ctrl+Shift+A 等价，
//      参考 caxa3dtracker\PartActivateTool.cs v4）；★ 读＝TLB 全域无「零件激活状态」API ——
//      eZElementState 全集只有 6 位（SUPPRESSED/INVALID/HIDDEN/NOFACE/VALID/LINKED），
//      Activate/IsActivated 只落在目录/窗口/配置/投影参考等无关接口（tracker 11-14 四轮实证，
//      v3「选中⇄激活」相关性实测不准，v4 用户裁定取消状态推断）。
//      ⇒ 本工程不假造状态读口，改用「53227 取消所有激活 → 53111 定向切换」的**起点已知协议**
//      让目标态在逻辑上可确定；返回里 verified 恒 false、state 恒 unknown，如实报第三态。
//   3) 特征顺序号：所有建模特征名统一 "N-名称"（如 1-长方体）；钣金特征同走 number_feature 入口。
//      出错后可用 reorder_features_by_seq → IZPartFeatureMgr.ReorderFeature(24) 按号重排。
//
// ★ 判据纪律 ★ 命名/重排/布尔一律写后回读；命令派发只报 posted（入队证据），绝不折算业务成功。
// ★ STA 纪律 ★ 所有 ICAPI 调用都在 McpBridge 回投的主线程；本文件不阻塞、不 WaitForExit、不弹框。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    // ============================================================ 建模顺序号基础设施

    /// <summary>建模顺序号：分配 / 解析 / 写后回读命名。供所有创建型工具与钣金特征复用。</summary>
    [ComVisible(false)]
    internal static class BuildSeq
    {
        public const string FormatHint = "N-名称（如 1-长方体）";

        private static readonly object _gate = new object();
        private static int _sessionNext = 1;

        /// <summary>本会话由本工程投出的激活命令轨迹（只作旁证，绝不折算成状态）。</summary>
        internal static readonly Dictionary<int, List<object>> ActivationTrail = new Dictionary<int, List<object>>();

        internal sealed class Row
        {
            public int Index;
            public IZFeature Feat;
            public string Name;
            public int Seq;
        }

        /// <summary>解析 "12-长方体" → 12；无序号 / 非法 → 0。</summary>
        public static int Parse(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            int dash = name.IndexOf('-');
            if (dash <= 0) return 0;
            int v;
            if (!int.TryParse(name.Substring(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out v)) return 0;
            return v > 0 ? v : 0;
        }

        /// <summary>去掉已有的 "N-" 前缀，保留名称主体。</summary>
        public static string Strip(string name)
        {
            if (string.IsNullOrEmpty(name)) return "特征";
            int dash = name.IndexOf('-');
            if (dash > 0)
            {
                int v;
                if (int.TryParse(name.Substring(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out v) && v > 0)
                {
                    string rest = name.Substring(dash + 1).Trim();
                    return rest.Length > 0 ? rest : "特征";
                }
            }
            return name.Trim();
        }

        public static string Make(int seq, string label)
        {
            return seq.ToString(CultureInfo.InvariantCulture) + "-" + Strip(label);
        }

        /// <summary>把元素切到 IZPartFeatureMgr 表面（先元素、再 IZPart 两路 QI）。</summary>
        public static IZPartFeatureMgr FeatureMgr(IZElement e)
        {
            if (e == null) return null;
            IZPartFeatureMgr fm = null;
            try { fm = e as IZPartFeatureMgr; } catch { }
            if (fm != null) return fm;
            try { IZPart p = e as IZPart; if (p != null) fm = p as IZPartFeatureMgr; } catch { }
            return fm;
        }

        public static List<Row> Rows(IZPartFeatureMgr fm)
        {
            var list = new List<Row>();
            if (fm == null) return list;
            int n;
            try { n = fm.GetFeaturesCount(); } catch { return list; }
            if (n > 2000) n = 2000;                       // 节点预算，截断单独由调用方记账
            for (int i = 0; i < n; i++)
            {
                var r = new Row { Index = i };
                try
                {
                    r.Feat = fm.GetFeatureAt(i);
                    r.Name = McpToolContext.SafeNameOf(r.Feat);
                    r.Seq = Parse(r.Name);
                }
                catch (Exception ex) { r.Name = "<读不到:" + ex.Message + ">"; }
                list.Add(r);
            }
            return list;
        }

        /// <summary>扫描场景所有顶层零件的特征名，取最大序号（跨会话/重启续号）。</summary>
        public static int MaxInScene(McpToolContext ctx)
        {
            int max = 0;
            if (ctx == null || ctx.Scene == null) return max;
            foreach (IZElement e in AgentKit.Tops(ctx))
            {
                IZPartFeatureMgr fm = FeatureMgr(e);
                if (fm == null) continue;
                foreach (Row r in Rows(fm)) if (r.Seq > max) max = r.Seq;
            }
            return max;
        }

        /// <summary>只读预览下一个序号（不消耗）。</summary>
        public static int Peek(McpToolContext ctx)
        {
            lock (_gate)
            {
                int scanned = MaxInScene(ctx);
                return _sessionNext > scanned ? _sessionNext : scanned + 1;
            }
        }

        /// <summary>取下一个序号（进程内递增 + 场景扫描续号，二者取大）。</summary>
        public static int Next(McpToolContext ctx)
        {
            lock (_gate)
            {
                int scanned = MaxInScene(ctx);
                if (_sessionNext <= scanned) _sessionNext = scanned + 1;
                return _sessionNext++;
            }
        }

        /// <summary>按给定序号给特征命名，写后立即回读校验（唯一判据）。</summary>
        public static Dictionary<string, object> Apply(McpToolContext ctx, object feature, string label, int seq)
        {
            var d = new Dictionary<string, object>();
            d["seqNumber"] = seq;
            if (feature == null)
            {
                d["seqNameVerified"] = false;
                d["seqNameError"] = "特征对象为空（第三态）";
                return d;
            }
            string wanted = Make(seq, label);
            d["seqName"] = wanted;
            string err;
            if (!McpToolContext.TrySetName(feature, wanted, out err))
            {
                d["seqNameVerified"] = false;
                d["seqNameError"] = err;
                if (ctx != null) ctx.Log("BuildSeq set_Name FAIL " + err);
                return d;
            }
            string back = McpToolContext.SafeNameOf(feature);
            d["seqNameReadBack"] = back;
            d["seqNameVerified"] = (back == wanted);
            return d;
        }

        /// <summary>自动取下一个序号并命名，结果按 keyPrefix 并入调用方 result。</summary>
        public static int ApplyNext(McpToolContext ctx, object feature, string label,
                                    Dictionary<string, object> result, string keyPrefix)
        {
            int seq = Next(ctx);
            var d = Apply(ctx, feature, label, seq);
            Merge(result, keyPrefix, d);
            return seq;
        }

        public static void Merge(Dictionary<string, object> result, string prefix, Dictionary<string, object> d)
        {
            if (result == null || d == null) return;
            foreach (var kv in d) result[prefix + kv.Key.Substring(0, 1).ToUpperInvariant() + kv.Key.Substring(1)] = kv.Value;
        }

        /// <summary>给零件的首特征按建模顺序编号（图素类建模工具共用）。</summary>
        public static void NameFirstFeature(McpToolContext ctx, IZPart part, string label, Dictionary<string, object> result)
        {
            if (part == null || result == null) return;
            IZElement e = McpToolContext.AsElement(part);
            IZPartFeatureMgr fm = FeatureMgr(e);
            if (fm == null) { result["featureSeqNameVerified"] = false; result["featureSeqNameError"] = "QI 不到 IZPartFeatureMgr（第三态）"; return; }
            object first = null;
            try { first = fm.GetFeatureAt(0); } catch (Exception ex) { result["featureSeqNameError"] = ex.Message; }
            if (first == null) { result["featureSeqNameVerified"] = false; return; }
            ApplyNext(ctx, first, label, result, "feature");
        }

        /// <summary>按名称 / id 在顶层元素里找零件（不递归，布尔与激活只作用于顶层零件）。</summary>
        public static IZElement FindTopPart(McpToolContext ctx, string key, out IZPart part, out string err)
        {
            part = null; err = null;
            if (ctx == null || ctx.Scene == null) { err = "当前没有 3D 场景文档"; return null; }
            if (string.IsNullOrEmpty(key)) { err = "请给目标零件名 / id"; return null; }
            foreach (IZElement e in AgentKit.Tops(ctx))
            {
                string n = AgentKit.SafeName(e);
                bool hit = (!string.IsNullOrEmpty(n) && n.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                           || AgentKit.SafeId(e).ToString(CultureInfo.InvariantCulture) == key;
                if (!hit) continue;
                part = e as IZPart;
                if (part == null) { err = "命中的顶层元素不是 IZPart（第三态）"; return null; }
                return e;
            }
            err = "顶层元素里未找到零件：" + key;
            return null;
        }
    }

    // ============================================================ ① 顺序号查询

    [ComVisible(false)]
    internal sealed class ToolGetBuildSeq : McpToolBase
    {
        public override string Name { get { return "get_build_seq"; } }
        public override string Category { get { return "build"; } }
        public override string Description { get { return "只读：扫描全场特征名的建模顺序号（N-名称），返回最大号/下一号/乱序/缺号/重号清单与各零件特征序列。重排前先调它。"; } }
        public override object InputSchema() { return S.Schema(new Dictionary<string, object>()); }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            if (ctx.Scene == null) return AgentKit.Fail("no_scene", AgentKit.NeedSceneMessage);
            var parts = new List<object>();
            int max = 0, numbered = 0, unnumbered = 0, unordered = 0;
            var seen = new Dictionary<int, int>();
            foreach (IZElement e in AgentKit.Tops(ctx))
            {
                IZPartFeatureMgr fm = BuildSeq.FeatureMgr(e);
                if (fm == null) continue;
                var rows = BuildSeq.Rows(fm);
                var items = new List<object>();
                int prevSeq = -1;
                bool partUnordered = false;
                foreach (BuildSeq.Row r in rows)
                {
                    if (r.Seq > 0)
                    {
                        numbered++;
                        if (r.Seq > max) max = r.Seq;
                        if (!seen.ContainsKey(r.Seq)) seen[r.Seq] = 0;
                        seen[r.Seq]++;
                        if (r.Seq < prevSeq) { partUnordered = true; unordered++; }
                        prevSeq = r.Seq;
                    }
                    else unnumbered++;
                    items.Add(new Dictionary<string, object>
                    {
                        ["index"] = r.Index, ["name"] = r.Name, ["seq"] = r.Seq,
                        ["numbered"] = r.Seq > 0
                    });
                }
                parts.Add(new Dictionary<string, object>
                {
                    ["element"] = AgentKit.SafeName(e), ["id"] = AgentKit.SafeId(e),
                    ["featureCount"] = rows.Count, ["inOrder"] = !partUnordered, ["features"] = items
                });
            }
            var gaps = new List<object>();
            var dupes = new List<object>();
            for (int i = 1; i <= max; i++) if (!seen.ContainsKey(i)) gaps.Add(i);
            foreach (var kv in seen) if (kv.Value > 1) dupes.Add(new Dictionary<string, object> { ["seq"] = kv.Key, ["count"] = kv.Value });
            return AgentKit.Ok("format", BuildSeq.FormatHint, "maxSeq", max, "nextSeq", BuildSeq.Peek(ctx),
                               "numberedFeatures", numbered, "unnumberedFeatures", unnumbered,
                               "outOfOrderPairs", unordered, "missingSeq", gaps, "duplicateSeq", dupes,
                               "parts", parts, "level", "implemented", "verified", true,
                               "verifiedBy", "IZPartFeatureMgr.GetFeaturesCount/GetFeatureAt 只读枚举后按名称前缀解析（不消耗序号）",
                               "note", "只对已编号特征判序；未编号特征请用 number_feature / renumber_features 补齐");
        }
    }

    // ============================================================ ② 单个特征编号（含钣金特征）

    [ComVisible(false)]
    internal sealed class ToolNumberFeature : McpToolBase
    {
        public override string Name { get { return "number_feature"; } }
        public override string Category { get { return "build"; } }
        public override string Description { get { return "给零件内指定特征按建模顺序编号命名（N-名称，写后回读）。钣金件的钣金特征、草图轮廓同走本入口（同 IZFeature/IZElement 表面）。"; } }
        public override object InputSchema()
        {
            var p = InspectKit.LocatorSchema();
            p["index"] = S.Num("特征索引；不传时自动取第一个未编号特征", false, -1.0, true);
            p["feature_name"] = S.Str("按特征名定位（优先于 index）");
            p["label"] = S.Str("名称主体，默认沿用原名称去掉旧序号", false, "");
            p["seq"] = S.Num("显式指定序号；不传则自动取下一个", false, 0.0, true);
            return S.Schema(p);
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            IZElement e; IZPart part;
            var bad = InspectKit.Part(ctx, args, out e, out part); if (bad != null) return bad;
            IZPartFeatureMgr fm = BuildSeq.FeatureMgr(e);
            if (fm == null) return AgentKit.Fail("feature_mgr_surface_missing", "目标不支持 IZPartFeatureMgr 表面（第三态）");
            var rows = BuildSeq.Rows(fm);
            if (rows.Count == 0) return AgentKit.Fail("no_features", "零件内枚举不到特征（第三态，不折算成 0 个成功）");
            string byName = args.ParamString("feature_name", "");
            int idx = args.ParamInt("index", -1);
            BuildSeq.Row row = null;
            if (!string.IsNullOrEmpty(byName))
            {
                foreach (BuildSeq.Row r in rows)
                    if (!string.IsNullOrEmpty(r.Name) && r.Name.IndexOf(byName, StringComparison.OrdinalIgnoreCase) >= 0) { row = r; break; }
                if (row == null) return AgentKit.Fail("feature_not_found", "按名称未找到特征：" + byName);
            }
            else if (idx >= 0)
            {
                if (idx >= rows.Count) return AgentKit.Fail("index_out_of_range", "特征索引越界：共 " + rows.Count + " 个");
                row = rows[idx];
            }
            else
            {
                foreach (BuildSeq.Row r in rows) if (r.Seq == 0) { row = r; break; }
                if (row == null) return AgentKit.Fail("all_numbered", "所有特征已编号；请显式给 index 或 feature_name");
            }
            if (row.Feat == null) return AgentKit.Fail("feature_object_missing", "定位到的特征对象为空（第三态）");
            int seq = args.ParamInt("seq", 0);
            if (seq <= 0) seq = BuildSeq.Next(ctx);
            string label = args.ParamString("label", "");
            if (string.IsNullOrEmpty(label)) label = BuildSeq.Strip(row.Name);
            var d = BuildSeq.Apply(ctx, row.Feat, label, seq);
            bool verified = d.ContainsKey("seqNameVerified") && Convert.ToBoolean(d["seqNameVerified"]);
            return AgentKit.Ok("element", AgentKit.SafeName(e), "index", row.Index, "beforeName", row.Name,
                               "seqNumber", seq, "seqName", d.ContainsKey("seqName") ? d["seqName"] : null,
                               "readBack", d.ContainsKey("seqNameReadBack") ? d["seqNameReadBack"] : null,
                               "error", d.ContainsKey("seqNameError") ? d["seqNameError"] : null,
                               "verified", verified, "level", "implemented",
                               "verifiedBy", "IZElement.set_Name 写后立即 get_Name 回读一致");
        }
    }

    // ============================================================ ③ 批量按当前树顺序重编号

    [ComVisible(false)]
    internal sealed class ToolRenumberFeatures : McpToolBase
    {
        public override string Name { get { return "renumber_features"; } }
        public override string Category { get { return "build"; } }
        public override string Description { get { return "把零件内全部特征按设计树当前顺序重编号（1-…、2-…，保留名称主体），逐项写后回读；最终序列回读一致才算 verified。"; } }
        public override object InputSchema()
        {
            var p = InspectKit.LocatorSchema();
            p["start"] = S.Num("起始序号，默认 1", false, 1.0, true);
            return S.Schema(p);
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            IZElement e; IZPart part;
            var bad = InspectKit.Part(ctx, args, out e, out part); if (bad != null) return bad;
            IZPartFeatureMgr fm = BuildSeq.FeatureMgr(e);
            if (fm == null) return AgentKit.Fail("feature_mgr_surface_missing", "目标不支持 IZPartFeatureMgr 表面（第三态）");
            var rows = BuildSeq.Rows(fm);
            int start = args.ParamInt("start", 1); if (start < 1) start = 1;
            var items = new List<object>();
            int okCount = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                BuildSeq.Row r = rows[i];
                var one = new Dictionary<string, object> { ["index"] = r.Index, ["beforeName"] = r.Name };
                if (r.Feat == null) { one["verified"] = false; one["error"] = "特征对象为空（第三态）"; items.Add(one); continue; }
                var d = BuildSeq.Apply(ctx, r.Feat, BuildSeq.Strip(r.Name), start + i);
                foreach (var kv in d) one[kv.Key] = kv.Value;
                if (d.ContainsKey("seqNameVerified") && Convert.ToBoolean(d["seqNameVerified"])) okCount++;
                items.Add(one);
            }
            var after = BuildSeq.Rows(fm);
            bool sequenceOk = after.Count == rows.Count;
            for (int i = 0; i < after.Count && sequenceOk; i++) if (after[i].Seq != start + i) sequenceOk = false;
            try { part.Update(); } catch { }
            return AgentKit.Ok("element", AgentKit.SafeName(e), "start", start, "featureCount", rows.Count,
                               "renamedVerified", okCount, "items", items, "finalSequence", after.ConvertAll(r => (object)r.Name),
                               "verified", okCount == rows.Count && sequenceOk, "level", "implemented",
                               "verifiedBy", "逐项 set_Name 回读 + 重新枚举后序号序列严格等于 start..start+n-1");
        }
    }

    // ============================================================ ④ 按序号重排特征（创新零件模式纠错）

    [ComVisible(false)]
    internal sealed class ToolReorderFeaturesBySeq : McpToolBase
    {
        public override string Name { get { return "reorder_features_by_seq"; } }
        public override string Category { get { return "build"; } }
        public override string Description { get { return "★ 创新零件模式纠错：按特征名里的顺序号调用 IZPartFeatureMgr.ReorderFeature 重排；重排后逐位回读序号序列非递减才算 verified。需 confirm=true。"; } }
        public override object InputSchema()
        {
            var p = InspectKit.LocatorSchema();
            p["confirm"] = S.Bool("必须 true 才执行（会改变建模结果）", false, false, true);
            p["plan_only"] = S.Bool("只返回重排计划，不改模型", false, false, true);
            return S.Schema(p);
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            IZElement e; IZPart part;
            var bad = InspectKit.Part(ctx, args, out e, out part); if (bad != null) return bad;
            IZPartFeatureMgr fm = BuildSeq.FeatureMgr(e);
            if (fm == null) return AgentKit.Fail("feature_mgr_surface_missing", "目标不支持 IZPartFeatureMgr 表面（第三态）");
            var plan = new List<object>();
            var rows = BuildSeq.Rows(fm);
            int numbered = 0;
            foreach (BuildSeq.Row r in rows) if (r.Seq > 0) numbered++;
            // 选择排序式计划：位置 i 应放 [i..n-1] 中序号最小者；未编号特征保持原序排在有号特征之后
            var order = new List<int>();
            for (int i = 0; i < rows.Count; i++) order.Add(i);
            for (int i = 0; i < order.Count; i++)
            {
                int best = i;
                for (int j = i + 1; j < order.Count; j++)
                {
                    int sj = rows[order[j]].Seq, sb = rows[order[best]].Seq;
                    if (sj <= 0) continue;
                    if (sb <= 0 || sj < sb) best = j;
                }
                if (best != i) { int t = order[i]; order[i] = order[best]; order[best] = t; }
            }
            for (int i = 0; i < order.Count; i++)
                plan.Add(new Dictionary<string, object>
                {
                    ["targetIndex"] = i, ["fromIndex"] = order[i],
                    ["name"] = rows[order[i]].Name, ["seq"] = rows[order[i]].Seq
                });
            if (args.ParamBool("plan_only", false))
                return AgentKit.Ok("element", AgentKit.SafeName(e), "plan", plan, "numberedFeatures", numbered,
                                   "executed", false, "verified", false, "level", "attempt",
                                   "note", "plan_only=true：未改动模型");
            if (!args.ParamBool("confirm", false))
                return AgentKit.Fail("confirm_required", "reorder_features_by_seq 会改变建模结果，请复核 plan 后显式传 confirm=true" );
            var moves = new List<object>();
            int okMoves = 0;
            for (int target = 0; target < order.Count; target++)
            {
                // 每次重排后索引会变，重新枚举；找当前序列中应落在 target 位置的特征
                var cur = BuildSeq.Rows(fm);
                string wantedName = rows[order[target]].Name;
                int at = -1;
                for (int k = target; k < cur.Count; k++) if (cur[k].Name == wantedName) { at = k; break; }
                if (at < 0) { moves.Add(new Dictionary<string, object> { ["targetIndex"] = target, ["ok"] = false, ["error"] = "重排中找不到目标特征（第三态）" }); continue; }
                if (at == target) { moves.Add(new Dictionary<string, object> { ["targetIndex"] = target, ["ok"] = true, ["moved"] = false, ["name"] = wantedName }); okMoves++; continue; }
                try
                {
                    fm.ReorderFeature(cur[at].Feat, target);
                    moves.Add(new Dictionary<string, object> { ["targetIndex"] = target, ["fromIndex"] = at, ["ok"] = true, ["moved"] = true, ["name"] = wantedName });
                    okMoves++;
                }
                catch (Exception ex)
                {
                    moves.Add(new Dictionary<string, object> { ["targetIndex"] = target, ["fromIndex"] = at, ["ok"] = false, ["error"] = ex.Message });
                }
            }
            try { part.Update(); } catch { }
            var final = BuildSeq.Rows(fm);
            bool inOrder = true; int prev = -1;
            foreach (BuildSeq.Row r in final)
            {
                if (r.Seq <= 0) continue;
                if (r.Seq < prev) { inOrder = false; break; }
                prev = r.Seq;
            }
            return AgentKit.Ok("element", AgentKit.SafeName(e), "plan", plan, "moves", moves,
                               "movesOk", okMoves, "finalSequence", final.ConvertAll(r => (object)r.Name),
                               "finalInOrder", inOrder, "verified", okMoves == order.Count && inOrder,
                               "level", "attempt",
                               "verifiedBy", "ReorderFeature 后重新枚举特征名，已编号特征的序号序列非递减",
                               "note", "ReorderFeature 的 lNewIndex 语义未实机定案；attempt 级，请用 get_bounding_box / capture_viewport 复核几何");
        }
    }

    // ============================================================ ⑤ 多零件模式：统一布尔

    [ComVisible(false)]
    internal sealed class ToolBooleanParts : McpToolBase
    {
        public override string Name { get { return "boolean_parts"; } }
        public override string Category { get { return "build"; } }
        public override string Description { get { return "★ 多零件模式：搭积木定位后把工具零件布尔进目标零件（IZPart.Boolean(13)）。支持 all_other_parts 一次合并全场；每个新特征自动编号。需 confirm=true。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["target"] = S.Str("目标零件名 / id（必填）"),
                ["tools"] = S.Array("工具零件名 / id 列表；与 all_other_parts 二选一", new Dictionary<string, object>()),
                ["all_other_parts"] = S.Bool("对场景内除目标外的所有顶层零件依次布尔", false, false, true),
                ["operation"] = S.Enum("布尔类型", new[] { "unite", "subtract", "intersect" }, false, "unite"),
                ["confirm"] = S.Bool("必须 true 才执行（会合并零件、改变模型）", false, false, true)
            }, "target");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            if (ctx.Scene == null) return AgentKit.Fail("no_scene", AgentKit.NeedSceneMessage);
            string targetKey = args.ParamString("target", "");
            string err; IZPart targetPart; IZElement targetEl;
            targetEl = BuildSeq.FindTopPart(ctx, targetKey, out targetPart, out err);
            if (targetEl == null) return AgentKit.Fail("target_not_found", err);

            string opName = args.ParamString("operation", "unite").ToLowerInvariant();
            eZOperationType chosen = eZOperationType.Z_UNITE;
            bool matched = false;
            foreach (eZOperationType v in Enum.GetValues(typeof(eZOperationType)))
            {
                string n = v.ToString().ToLowerInvariant();
                if (opName == "unite" && (n.Contains("unite") || n.Contains("add"))) { chosen = v; matched = true; break; }
                if (opName == "subtract" && n.Contains("subtract")) { chosen = v; matched = true; break; }
                if (opName == "intersect" && n.Contains("intersect")) { chosen = v; matched = true; break; }
            }
            if (!matched) return AgentKit.Fail("enum_member_not_found", "eZOperationType 里未找到 " + opName + "（第三态）");

            var toolKeys = new List<string>();
            JValue tv = args["tools"];
            if (tv.Kind == JKind.Array && tv.Arr != null)
                foreach (JValue v in tv.Arr) { string s = v.AsString(); if (!string.IsNullOrEmpty(s)) toolKeys.Add(s); }
            bool allOthers = args.ParamBool("all_other_parts", false);
            if (allOthers)
            {
                toolKeys.Clear();
                foreach (IZElement e in AgentKit.Tops(ctx))
                {
                    if (e == targetEl) continue;
                    if (e as IZPart == null) continue;
                    toolKeys.Add(AgentKit.SafeId(e).ToString(CultureInfo.InvariantCulture));
                }
            }
            if (toolKeys.Count == 0) return AgentKit.Fail("no_tool_parts", "没有可布尔的工具零件；请给 tools 或 all_other_parts=true");

            var plan = new List<object>();
            foreach (string k in toolKeys) plan.Add(new Dictionary<string, object> { ["tool"] = k, ["operation"] = chosen.ToString() });
            if (!args.ParamBool("confirm", false))
                return AgentKit.Fail("confirm_required", "boolean_parts 会合并零件，请复核 plan 后显式传 confirm=true");

            IZPartFeatureMgr fm = BuildSeq.FeatureMgr(targetEl);
            var steps = new List<object>();
            int okCount = 0;
            foreach (string k in toolKeys)
            {
                var one = new Dictionary<string, object> { ["tool"] = k };
                IZPart toolPart; IZElement toolEl;
                toolEl = BuildSeq.FindTopPart(ctx, k, out toolPart, out err);
                if (toolEl == null) { one["ok"] = false; one["error"] = err; steps.Add(one); continue; }
                if (toolEl == targetEl) { one["ok"] = false; one["error"] = "工具零件不能是目标零件本身"; steps.Add(one); continue; }
                int before = -1, after = -1;
                try { before = fm == null ? -1 : fm.GetFeaturesCount(); } catch { }
                object feat = null;
                try
                {
                    feat = targetPart.Boolean(toolPart, chosen);
                    targetPart.Update();
                }
                catch (Exception ex) { one["ok"] = false; one["error"] = "IZPart.Boolean / Update 失败：" + ex.Message; }
                try { after = fm == null ? -1 : fm.GetFeaturesCount(); } catch { }
                one["featuresBefore"] = before; one["featuresAfter"] = after;
                one["featureReturned"] = feat != null;
                if (feat != null)
                {
                    var nd = new Dictionary<string, object>();
                    BuildSeq.ApplyNext(ctx, feat, "布尔" + opName, nd, "newFeature");
                    foreach (var kv in nd) one[kv.Key] = kv.Value;
                }
                bool ok = feat != null && before >= 0 && after == before + 1;
                one["ok"] = ok;
                one["verified"] = ok;
                if (ok) okCount++;
                steps.Add(one);
            }
            return AgentKit.Ok("target", AgentKit.SafeName(targetEl), "operation", chosen.ToString(),
                               "toolCount", toolKeys.Count, "plan", plan, "steps", steps,
                               "verifiedCount", okCount, "verified", okCount == toolKeys.Count,
                               "level", "attempt", "channel", "IZPart.Boolean(13) + IZPart.Update(7) RCW 直调",
                               "verifiedBy", "每次目标零件 GetFeaturesCount +1 且返回 IZFeature 非空 + 新特征名写后回读",
                               "note", "工具零件在布尔后是否仍留在场景、布尔后几何是否正确，本项目未实机定案；请用 list_top_elements / get_bounding_box / capture_viewport 复核");
        }
    }

    // ============================================================ ⑥ 激活零件（命令派发 + 起点已知协议）

    /// <summary>宿主主框架查找与命令投递（复刻 caxa3dtracker\CornerReliefTool.FindCaxaMainFrame 实证口径）。</summary>
    [ComVisible(false)]
    internal static class HostCommand
    {
        public const int CmdActivateToggle = 53111;        // IsEngineAfxLg STRINGTABLE「激活\取消激活」= Ctrl+Shift+A
        public const int CmdDeactivateAll = 53227;         // 资源库旁证「取消所有激活」（tracker 11-14 09:45，未实机定案）
        private const uint WM_COMMAND = 0x0111;
        private const int SW_RESTORE = 9;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);

        public static IntPtr FindMainFrame()
        {
            IntPtr found = IntPtr.Zero;
            uint cur = (uint)Process.GetCurrentProcess().Id;
            EnumWindows(delegate (IntPtr h, IntPtr _)
            {
                if (found != IntPtr.Zero) return true;
                if (!IsWindowVisible(h)) return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid != cur) return true;
                var cls = new StringBuilder(256); GetClassNameW(h, cls, cls.Capacity);
                if (!cls.ToString().StartsWith("Afx:", StringComparison.OrdinalIgnoreCase)) return true;
                var title = new StringBuilder(512); GetWindowTextW(h, title, title.Capacity);
                string t = title.ToString();
                if (t.IndexOf("CAXA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.IndexOf("IronCAD", StringComparison.OrdinalIgnoreCase) >= 0) found = h;
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>把目标零件放回活选择集并回读命中（命令作用于当前选中对象）。</summary>
        public static bool Reselect(McpToolContext ctx, IZElement target, out string detail)
        {
            detail = null;
            try
            {
                ctx.Scene.SelectionMgr.RemoveAllFromSelection();
                ctx.Scene.SelectionMgr.AddElementToSelection(target, true);
                int wantId = AgentKit.SafeId(target);
                int hit = 0, total = 0;
                foreach (object o in McpToolContext.UnpackArray(ctx.Scene.SelectionMgr.GetSelectedElements()))
                {
                    total++;
                    IZElement e = McpToolContext.AsElement(o);
                    if (e != null && AgentKit.SafeId(e) == wantId) hit++;
                }
                detail = "选择集回读总数=" + total + " 命中目标=" + hit;
                return hit > 0;
            }
            catch (Exception ex) { detail = "选择集操作失败（第三态）：" + ex.Message; return false; }
        }

        public static bool Post(IntPtr frame, int cmd)
        {
            return PostMessageW(frame, WM_COMMAND, (IntPtr)cmd, IntPtr.Zero);
        }

        public static bool BringToFront(IntPtr frame)
        {
            try { if (IsIconic(frame)) ShowWindow(frame, SW_RESTORE); } catch { }
            try { return SetForegroundWindow(frame); } catch { return false; }
        }

        public static void Trail(int partId, string mode, string detail)
        {
            lock (BuildSeq.ActivationTrail)
            {
                if (!BuildSeq.ActivationTrail.ContainsKey(partId))
                    BuildSeq.ActivationTrail[partId] = new List<object>();
                BuildSeq.ActivationTrail[partId].Add(new Dictionary<string, object>
                {
                    ["time"] = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    ["mode"] = mode, ["detail"] = detail
                });
            }
        }
    }

    [ComVisible(false)]
    internal sealed class ToolSetPartActivation : McpToolBase
    {
        public override string Name { get { return "set_part_activation"; } }
        public override string Category { get { return "activation"; } }
        public override string Description { get { return "★ 激活/取消激活零件（WM_COMMAND 53111 / 53227）。TLB 无激活状态读 API ⇒ 用「53227 归零 → 53111 定向」让起点已知；posted 只代表入队，verified 恒 false。需 confirm=true。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["target"] = S.Str("目标零件名 / id（必填）"),
                ["mode"] = S.Enum("activate=归零后定向激活 / deactivate=仅归零取消所有激活 / toggle=纯切换", new[] { "activate", "deactivate", "toggle" }, false, "activate"),
                ["confirm"] = S.Bool("必须 true 才投递（会改变宿主编辑状态）", false, false, true)
            }, "target");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            if (ctx.Scene == null) return AgentKit.Fail("no_scene", AgentKit.NeedSceneMessage);
            string key = args.ParamString("target", "");
            string err; IZPart part; IZElement el;
            el = BuildSeq.FindTopPart(ctx, key, out part, out err);
            if (el == null) return AgentKit.Fail("target_not_found", err);
            string mode = args.ParamString("mode", "activate").ToLowerInvariant();
            if (mode != "activate" && mode != "deactivate" && mode != "toggle")
                return AgentKit.Fail("bad_mode", "mode 只能是 activate / deactivate / toggle");

            var plan = new List<object>();
            if (mode == "deactivate") plan.Add(new Dictionary<string, object> { ["cmd"] = HostCommand.CmdDeactivateAll, ["meaning"] = "取消所有激活（归零）" });
            if (mode == "activate")
            {
                plan.Add(new Dictionary<string, object> { ["cmd"] = HostCommand.CmdDeactivateAll, ["meaning"] = "先把全场激活状态归零，使起点确定" });
                plan.Add(new Dictionary<string, object> { ["cmd"] = HostCommand.CmdActivateToggle, ["meaning"] = "对已重新选中的目标零件投切换；起点=未激活 ⇒ 结果=已激活（逻辑推断，未回读验证）" });
            }
            if (mode == "toggle") plan.Add(new Dictionary<string, object> { ["cmd"] = HostCommand.CmdActivateToggle, ["meaning"] = "纯切换；起点未知 ⇒ 结果状态未知" });
            if (!args.ParamBool("confirm", false))
                return AgentKit.Fail("confirm_required", "set_part_activation 会向宿主投命令，请复核 plan 后显式传 confirm=true");

            IntPtr frame = HostCommand.FindMainFrame();
            if (frame == IntPtr.Zero)
                return AgentKit.Fail("main_frame_not_found", "未找到 CAXA 主框架（本进程 / 可见 / class 以 Afx: 开头 / 标题含 CAXA 或 IronCAD）：命令无法投递");
            bool fg = HostCommand.BringToFront(frame);
            string selDetail;
            bool selVerified = HostCommand.Reselect(ctx, el, out selDetail);

            var posts = new List<object>();
            bool allPosted = true;
            if (mode == "deactivate" || mode == "activate")
            {
                bool p = HostCommand.Post(frame, HostCommand.CmdDeactivateAll);
                posts.Add(new Dictionary<string, object> { ["cmd"] = HostCommand.CmdDeactivateAll, ["posted"] = p, ["source"] = "资源库 STRINGTABLE 旁证（tracker 11-14 09:45），未实机定案" });
                if (!p) allPosted = false;
            }
            if (mode == "activate" || mode == "toggle")
            {
                // 归零命令可能清空选择集 ⇒ 定向切换前重新放回目标（同一 STA 回合内重选，不等待）
                if (mode == "activate")
                {
                    string again;
                    bool re = HostCommand.Reselect(ctx, el, out again);
                    selVerified = selVerified && re;
                    selDetail += " | 归零后重选：" + again;
                }
                bool p = HostCommand.Post(frame, HostCommand.CmdActivateToggle);
                posts.Add(new Dictionary<string, object> { ["cmd"] = HostCommand.CmdActivateToggle, ["posted"] = p, ["source"] = "caxa3dtracker PartActivateTool v4 实机已投（仅入队证据）" });
                if (!p) allPosted = false;
            }
            int pid = AgentKit.SafeId(el);
            HostCommand.Trail(pid, mode, "posted=" + allPosted + " selection=" + selDetail);

            string targetState = mode == "deactivate" ? "all_deactivated(inferred)"
                               : mode == "activate" ? "activated(inferred)"
                               : "unknown(toggle)";
            return AgentKit.Ok("target", AgentKit.SafeName(el), "targetId", pid, "mode", mode,
                               "plan", plan, "frame", "0x" + frame.ToInt64().ToString("X"),
                               "foreground", fg, "selectionVerified", selVerified, "selectionDetail", selDetail,
                               "posts", posts, "allPosted", allPosted,
                               "targetState", targetState, "verified", false, "level", "attempt",
                               "stateReadBackAvailable", false,
                               "reason", "TLB 全域无零件激活状态读 API（eZElementState 6 位无 ACTIVE；tracker v3 选中相关性实测不准、v4 用户裁定不做推断）",
                               "protocol", "起点已知协议：53227 归零 → 重选目标 → 53111 定向；结果态为逻辑推断，不是回读验证",
                               "note", "PostMessage 的 OK 只代表入队；是否生效以 CAXA 视口/设计树为准，另调 get_part_activation_state 取第三态证据");
        }
    }

    // ============================================================ ⑦ 激活状态（诚实第三态）

    [ComVisible(false)]
    internal sealed class ToolGetPartActivationState : McpToolBase
    {
        public override string Name { get { return "get_part_activation_state"; } }
        public override string Category { get { return "activation"; } }
        public override string Description { get { return "只读第三态：CAXA 无激活状态 COM 读 API ⇒ state 恒 unknown、verified 恒 false；返回选择集、eZElementState 六状态探针证据与本会话命令轨迹（inferred 不可信）。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object> { ["target"] = S.Str("可选：只看某个零件名 / id") });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            if (ctx.Scene == null) return AgentKit.Fail("no_scene", AgentKit.NeedSceneMessage);
            string filter = args.ParamString("target", "");
            var items = new List<object>();
            int selectedParts = 0;
            try
            {
                foreach (object o in McpToolContext.UnpackArray(ctx.Scene.SelectionMgr.GetSelectedElements()))
                {
                    IZElement e = McpToolContext.AsElement(o);
                    if (e == null) continue;
                    int type; try { type = Convert.ToInt32(e.Type); } catch { continue; }
                    if (type != 1 && type != 2 && type != 20) continue;   // 零件1/装配2/钣金件20
                    string name = AgentKit.SafeName(e);
                    if (!string.IsNullOrEmpty(filter) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                        && AgentKit.SafeId(e).ToString(CultureInfo.InvariantCulture) != filter) continue;
                    selectedParts++;
                    var states = new Dictionary<string, object>();
                    int[] values = { 0, 1, 2, 3, 4, 0x10000000 };
                    string[] names = { "SUPPRESSED", "INVALID", "HIDDEN", "NOFACE", "VALID", "LINKED" };
                    for (int i = 0; i < values.Length; i++)
                    {
                        try { states[names[i]] = e.GetStateStatus((eZElementState)values[i]); }
                        catch (Exception ex) { states[names[i]] = "读不到:" + ex.Message; }
                    }
                    int id = AgentKit.SafeId(e);
                    List<object> trail = null;
                    lock (BuildSeq.ActivationTrail) if (BuildSeq.ActivationTrail.ContainsKey(id)) trail = new List<object>(BuildSeq.ActivationTrail[id]);
                    items.Add(new Dictionary<string, object>
                    {
                        ["name"] = name, ["id"] = id, ["type"] = type,
                        ["inSelection"] = true, ["stateProbe"] = states,
                        ["sessionTrail"] = trail, ["state"] = "unknown"
                    });
                }
            }
            catch (Exception ex) { return AgentKit.Fail("selection_read_failed", "选择集读不到（第三态）：" + ex.Message); }

            // 顶层零件中未被选中的也列出，供人工对照（状态同样 unknown）
            foreach (IZElement e in AgentKit.Tops(ctx))
            {
                if (e as IZPart == null && e as IZAssembly == null) continue;
                int id = AgentKit.SafeId(e);
                bool already = false;
                foreach (object o in items) { var d = o as Dictionary<string, object>; if (d != null && Convert.ToInt32(d["id"]) == id) { already = true; break; } }
                if (already) continue;
                string name = AgentKit.SafeName(e);
                if (!string.IsNullOrEmpty(filter) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && id.ToString(CultureInfo.InvariantCulture) != filter) continue;
                List<object> trail = null;
                lock (BuildSeq.ActivationTrail) if (BuildSeq.ActivationTrail.ContainsKey(id)) trail = new List<object>(BuildSeq.ActivationTrail[id]);
                items.Add(new Dictionary<string, object>
                {
                    ["name"] = name, ["id"] = id, ["inSelection"] = false,
                    ["sessionTrail"] = trail, ["state"] = "unknown"
                });
            }
            return AgentKit.Ok("state", "unknown", "verified", false, "level", "capability",
                               "selectedPartCount", selectedParts, "items", items,
                               "reason", "TLB 全域无零件激活状态读 API；eZElementState 全集 6 位均非激活位，GetStateStatus 只作留档证据不映射状态",
                               "inferredNotVerified", true,
                               "recommendedProtocol", "用 set_part_activation 的 deactivate 归零 + activate 定向，让状态起点确定；不要用选择集/颜色推断（tracker v3 已实证不准）",
                               "evidence", "caxa3dtracker PartActivateTool v1~v4（11-14 09:45~10:28）：SetSuppressState=压缩非激活、选中相关性不准、用户裁定取消状态推断",
                               "note", "第三态不是失败也不是成功；需要权威状态时以 CAXA 视口/设计树人工核对为准");
        }
    }
}
