// caxa3dmcp 图纸环境唯一桥（请求侧，C# / 进程内 add-in）。
//
// ★ 定位（用户 26-11-13 指令）：`caxa3dmcp_` 是本项目与图纸环境通讯的**唯一前缀/唯一桥**，
//   所有 MCP 工具要碰图纸环境，一律经本文件；不许再旁听或另建第二座桥。
//   配套 CRX = Bridge\caxa3dmcp_bridge.crx（桥接型，第五登记点随 CxAutorun.cx 自动加载）。
//
// 协议（与 CRX 侧 caxa3dmcp_bridge.cpp 同一份原文，两边不许各自演化）：
//   请求 %TEMP%\caxa3dmcp_request.txt   : seq=<int> / action=<命令名> / confirm=yes|no / token=<会话令牌> / arg=<参数>
//   回包 %TEMP%\caxa3dmcp_response.txt  : seq / action / status=OK|ERR / token / message（CRX 只写 OK/ERR）
//   marker %TEMP%\caxa3dmcp_crx.marker  内容含 `RUNNING token=<pid>-<tick> pid=<pid>`
//   日志 %TEMP%\caxa3dmcp_crx.log       UTF-8+BOM、随 CRX 加载截断重建
//   安全开关 %TEMP%\caxa3dmcp_crx.kill  在场＝CRX 停止取件（运维）
//
// 四个判词互不顶替：OK / ERR（CRX 动了手但业务失败）/ NOCRX（marker 不在或运维开关）/ TIMEOUT（通道无响应）。
//
// ★ 与兄弟项目 ViewBridge 的关键差别（上一轮取证「进程拓扑 UNKNOWN」的落地）：
//   marker 新鲜度**不拿本进程 StartTime 判**。PING 回包 token 里的 pid 就是 CRX 所在进程，
//   用 Process.GetProcessById(pid).StartTime 判；因此无论图纸环境内嵌 IronCAD.exe 还是独立
//   CAXADraft.exe，判据都成立，取不到进程时一律记 unknown 第三态并拒绝破坏性命令。
//
// ★ 自动预热（规则 06 第 16 节 / 总纲第 30 条，参考 caxa3dBatchMaster\ViewBridge.cs）：
//   进入插件后由空闲消息泵 tick 一次性触发：已热 SKIP、安全模式不跑、专用空图纸 OpenFile2
//   → 等 marker ≤30s → PING 取令牌 → CLOSERAW 不保存关载体；失败即判死，不重试、不换入口、不弹框。
//   四态 `auto-warm | START/OK/FAIL/SKIP` 进 trace.log；「预热过」≠ CRX 常驻。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    /// <summary>一次桥内往来的结论（四态互不顶替）。</summary>
    [ComVisible(false)]
    internal sealed class DraftReply
    {
        public string Status = "NOCRX";      // OK / ERR / NOCRX / TIMEOUT / BUSY
        public int Seq;
        public string Action = "";
        public string Message = "";
        public string Token = "";
        public bool Ok { get { return string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase); } }

        public Dictionary<string, object> ToMap()
        {
            return new Dictionary<string, object>
            {
                ["status"] = Status,
                ["ok"] = Ok,
                ["seq"] = Seq,
                ["action"] = Action,
                ["message"] = Message,
                ["token"] = Token
            };
        }
    }

    [ComVisible(false)]
    internal static class DraftBridge
    {
        public const string Prefix = "caxa3dmcp_";
        public const string CrxFileName = "caxa3dmcp_bridge.crx";
        public const string WarmupExbName = "caxa3d图纸环境预热.exb";
        public const string CommandIndexName = "draft_commands.txt";

        public static readonly string RequestPath = Path.Combine(Path.GetTempPath(), Prefix + "request.txt");
        public static readonly string ResponsePath = Path.Combine(Path.GetTempPath(), Prefix + "response.txt");
        public static readonly string MarkerPath = Path.Combine(Path.GetTempPath(), Prefix + "crx.marker");
        public static readonly string LogPath = Path.Combine(Path.GetTempPath(), Prefix + "crx.log");
        public static readonly string KillPath = Path.Combine(Path.GetTempPath(), Prefix + "crx.kill");
        public static readonly string TracePath = Path.Combine(Path.GetTempPath(), Prefix + "trace.log");

        /// <summary>CRX 命令表（与 Bridge\caxa3dmcp_bridge.cpp 的 kCmdTable 必须逐字一致）。</summary>
        public static readonly string[] Verbs = {
            "PING", "DFT_STATE", "CMD_STATE", "CLOSEDOC", "CLOSERAW", "SAVE",
            "EXEC_NATIVE", "TITLEBLOCK_3DSET", "TB_ABORT", "CONVERT_DXF", "OPEN_EXB",
            "DRAW_RECT", "RECT_STATE",
            // 26-11-15 工程图附加二维码（移植自 caxa3dBatchMaster 11-15 实机整链，仍走本唯一桥）
            "GETSCALE", "LISTENTS", "DELETE_QR", "BITMAP_NATIVE", "QR_STATE"
        };

        /// <summary>需要 confirm=yes 的动作（与 CRX 的 needsConfirm 列一致）。</summary>
        private static readonly string[] ConfirmVerbs = {
            "CLOSEDOC", "CLOSERAW", "SAVE", "CONVERT_DXF", "OPEN_EXB", "DRAW_RECT",
            "DELETE_QR", "BITMAP_NATIVE"
        };

        private static readonly object Gate = new object();
        private static int _seq;
        private static string _token = "";
        private static DateTime _tokenAt = DateTime.MinValue;
        private static int _crxPid;
        private static DateTime _crxProcStart = DateTime.MinValue;
        private static int _busy;                 // 0=空闲 / 1=有请求在等回包（禁止重入，避免 DoEvents 嵌套派发）
        private static bool _autoWarmTried;      // 每进程一次
        private static string _autoWarmState = "NOT_RUN";

        private static HashSet<string> _commandIndex;
        private static bool _commandIndexLoaded;

        public static string AutoWarmState { get { return _autoWarmState; } }
        public static bool AutoWarmTried { get { return _autoWarmTried; } }
        public static int CrxPid { get { return _crxPid; } }
        public static bool Busy { get { return Volatile.Read(ref _busy) != 0; } }

        // ---------------------------------------------------------------- 就绪判据

        public static bool MarkerPresent()
        {
            try { return File.Exists(MarkerPath); } catch { return false; }
        }

        public static bool KillSwitchPresent()
        {
            try { return File.Exists(KillPath); } catch { return false; }
        }

        /// <summary>marker 新鲜度三态：absent / fresh / stale / unknown（不假设 CRX 与 add-in 同进程）。</summary>
        public static string MarkerFreshness()
        {
            try
            {
                if (!File.Exists(MarkerPath)) return "absent";
                if (_crxProcStart == DateTime.MinValue) return "unknown";
                return new FileInfo(MarkerPath).LastWriteTime >= _crxProcStart ? "fresh" : "stale";
            }
            catch { return "unknown"; }
        }

        public static string MarkerState()
        {
            try
            {
                if (!File.Exists(MarkerPath)) return "marker 不存在＝CRX 未加载（冷态只准 PING）";
                DateTime w = new FileInfo(MarkerPath).LastWriteTime;
                return "marker " + w.ToString("yyyy-MM-dd HH:mm:ss") + " freshness=" + MarkerFreshness() +
                       " crxPid=" + (_crxPid > 0 ? _crxPid.ToString() : "unknown");
            }
            catch (Exception ex) { return "marker 读不到（第三态）：" + ex.Message; }
        }

        /// <summary>真就绪 = marker 在 + 新鲜度 fresh + 令牌可用。破坏性命令的唯一前置。</summary>
        public static bool IsReady()
        {
            return MarkerFreshness() == "fresh" && !string.IsNullOrEmpty(_token);
        }

        // ---------------------------------------------------------------- 投递与等回包

        /// <summary>投递一条命令并按 seq 认领自己的回包。marker 不在＝NOCRX（压根不写请求文件）。</summary>
        public static DraftReply Request(string action, string arg, bool needConfirm, int timeoutMs)
        {
            var rep = new DraftReply { Action = action ?? "" };

            if (!MarkerPresent())
            {
                rep.Status = "NOCRX";
                rep.Message = MarkerState() + "；未投递任何请求（冷态只准 PING，也不制造陈旧请求）";
                Diagnostics.Log("draft-bridge: " + action + " NOCRX " + rep.Message);
                return rep;
            }
            if (KillSwitchPresent())
            {
                rep.Status = "NOCRX";
                rep.Message = "运维安全开关在场（" + KillPath + "）＝CRX 停止取件；删掉并重启图纸环境后再试";
                Diagnostics.Log("draft-bridge: " + action + " NOCRX kill-switch");
                return rep;
            }
            // ★ 26-11-13 16:22 修复：令牌握手必须先于 _busy 占用。EnsureToken 内部要再发一次
            //   PING（同样走 Request），若这时 _busy 已被本条占住，PING 会被判 BUSY ⇒
            //   破坏性命令首调恒 ERR「没有可用令牌」（DRAW_RECT / SAVE / CLOSERAW 同族）。
            //   握手放在闸外，PING 自己占放互斥；失败即不投递，不占 seq。
            if (needConfirm && !TokenIsFresh())
            {
                if (!EnsureToken(timeoutMs))
                {
                    rep.Status = "ERR";
                    rep.Message = "没有可用的会话令牌（PING 握手失败或 marker 新鲜度 unknown）＝不发破坏性命令";
                    Diagnostics.Log("draft-bridge: " + action + " 未投递 —— " + rep.Message);
                    return rep;
                }
            }

            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                rep.Status = "BUSY";
                rep.Message = "本桥已有请求在等回包（单写者 seq 约束），本次未投递；稍后重试";
                Diagnostics.Log("draft-bridge: " + action + " BUSY");
                return rep;
            }

            try
            {
                int seq;
                lock (Gate) { seq = ++_seq; }
                rep.Seq = seq;

                try { if (File.Exists(ResponsePath)) File.Delete(ResponsePath); }
                catch (Exception ex) { Diagnostics.Log("draft-bridge: 旧回包没清掉（继续按 seq 认领）：" + ex.Message); }

                var sb = new StringBuilder();
                sb.Append("seq=").Append(seq).Append("\r\n");
                sb.Append("action=").Append(action).Append("\r\n");
                sb.Append("confirm=").Append(needConfirm ? "yes" : "no").Append("\r\n");
                sb.Append("token=").Append(_token ?? "").Append("\r\n");
                sb.Append("arg=").Append(arg ?? "").Append("\r\n");
                if (!AtomicWrite(RequestPath, sb.ToString()))
                {
                    rep.Status = "TIMEOUT";
                    rep.Message = "请求文件写不进去（原子替换失败）";
                    Diagnostics.Log("draft-bridge: " + rep.Message);
                    return rep;
                }
                Diagnostics.Log("draft-bridge: 已投递 seq=" + seq + " " + action +
                                (string.IsNullOrEmpty(arg) ? "" : " arg=" + arg));

                string body = "";
                try
                {
                    CaxaAutomation.WaitUntil(delegate()
                    {
                        if (!File.Exists(ResponsePath)) return false;
                        try { body = File.ReadAllText(ResponsePath, Encoding.UTF8); }
                        catch { return false; }              // CRX 正在写，读到半个文件算没到
                        return ParseInt(GetField(body, "seq"), -1) == seq;   // 绝不认领错号
                    }, timeoutMs, "等 seq=" + seq + " 的 CRX 回包");
                }
                catch (OperationStoppedException) { TryPurgeResponse(); throw; }
                catch (TimeoutException)
                {
                    rep.Status = "TIMEOUT";
                    rep.Message = "投递后 " + timeoutMs + "ms 内没有 seq=" + seq +
                                  " 的回包＝通道无响应（不代表命令没生效，去 CRX 日志核对：" + LogPath + "）";
                    Diagnostics.Log("draft-bridge: " + rep.Message);
                    return rep;
                }

                TryPurgeResponse();
                rep.Status = GetField(body, "status");
                rep.Message = GetField(body, "message");
                rep.Token = GetField(body, "token");
                if (string.IsNullOrEmpty(rep.Status)) rep.Status = "ERR";
                Diagnostics.Log("draft-bridge: " + rep.Status + " seq=" + seq + " " + rep.Message);
                return rep;
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        /// <summary>写 .tmp 再原子替换，避免 CRX 100ms 节拍读到半截请求（蓝本的直接写会触发读失败重试）。</summary>
        private static bool AtomicWrite(string path, string text)
        {
            string tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(tmp, path, null);
                }
                else
                {
                    File.Move(tmp, path);
                }
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("draft-bridge: AtomicWrite 失败 " + ex.Message);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }

        /// <summary>PING 一次并取回会话令牌；同时从 token 的 pid 读 CRX 所在进程启动时间，
        /// 使 marker 新鲜度判据与「图纸环境内嵌还是独立」这个未定案问题解耦。</summary>
        public static bool EnsureToken(int timeoutMs)
        {
            DraftReply r = Request("PING", "", false, timeoutMs);
            if (!r.Ok) { Diagnostics.Log("draft-bridge: PING 不可用（" + r.Status + "）"); return false; }
            string tok = ExtractToken(r.Message);
            if (string.IsNullOrEmpty(tok))
            {
                Diagnostics.Log("draft-bridge: PING 回 OK 但 message 里没有 token=（协议不符），不能发破坏性命令");
                return false;
            }
            _token = tok;
            _tokenAt = DateTime.Now;
            _crxPid = ParsePid(tok);
            _crxProcStart = DateTime.MinValue;
            if (_crxPid > 0)
            {
                try { _crxProcStart = Process.GetProcessById(_crxPid).StartTime; }
                catch (Exception ex) { Diagnostics.Log("draft-bridge: CRX 进程启动时间读不到＝第三态 " + ex.Message); }
            }
            Diagnostics.Log("draft-bridge: 会话令牌=" + tok + " crxPid=" + _crxPid +
                            " freshness=" + MarkerFreshness());
            return true;
        }

        private static bool TokenIsFresh()
        {
            if (string.IsNullOrEmpty(_token)) return false;
            if (_crxProcStart == DateTime.MinValue) return false;   // 拿不到进程启动时间＝不冒充已判
            try
            {
                if (!File.Exists(MarkerPath)) return false;
                return new FileInfo(MarkerPath).LastWriteTime <= _tokenAt;   // marker 在取令牌后被重写＝CRX 换过实例
            }
            catch { return false; }
        }

        private static string ExtractToken(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            int i = message.IndexOf("token=", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            string rest = message.Substring(i + 6);
            int sp = rest.IndexOfAny(new[] { ' ', ';', ',', '\r', '\n' });
            return (sp < 0 ? rest : rest.Substring(0, sp)).Trim();
        }

        private static int ParsePid(string token)
        {
            int dash = token.IndexOf('-');
            int pid;
            return (dash > 0 && int.TryParse(token.Substring(0, dash), out pid)) ? pid : 0;
        }

        private static void TryPurgeResponse()
        {
            try { if (File.Exists(ResponsePath)) File.Delete(ResponsePath); } catch { }
        }

        public static string GetField(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return "";
            string p = key + "=";
            foreach (string line in body.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return t.Substring(p.Length).Trim();
            }
            return "";
        }

        private static int ParseInt(string s, int def)
        {
            int v;
            return int.TryParse((s ?? "").Trim(), out v) ? v : def;
        }

        public static bool NeedsConfirm(string action)
        {
            foreach (string v in ConfirmVerbs)
                if (string.Equals(v, action, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool IsKnownVerb(string action)
        {
            foreach (string v in Verbs)
                if (string.Equals(v, action, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---------------------------------------------------------------- 离线命令索引

        /// <summary>随包 draft_commands.txt：从 cmd_index 离线表导出的 MODAL + CX_DFT_/CX_ 具名命令。
        /// 文件不在＝indexMissing 第三态，只按前缀放行并如实标注，绝不假装「名字已验证」。</summary>
        public static bool CommandInIndex(string cmd, out bool indexMissing)
        {
            indexMissing = false;
            if (!_commandIndexLoaded)
            {
                _commandIndexLoaded = true;
                try
                {
                    string p = Path.Combine(AssemblyDir(), CommandIndexName);
                    if (File.Exists(p))
                    {
                        _commandIndex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                        {
                            string n = line.Trim();
                            if (n.Length > 0 && !n.StartsWith("#")) _commandIndex.Add(n);
                        }
                    }
                }
                catch (Exception ex) { Diagnostics.Log("draft-bridge: 命令索引读取失败 " + ex.Message); }
            }
            if (_commandIndex == null) { indexMissing = true; return true; }
            return _commandIndex.Contains(cmd);
        }

        public static int CommandIndexCount { get { return _commandIndex == null ? -1 : _commandIndex.Count; } }

        private static string AssemblyDir()
        {
            try { return Path.GetDirectoryName(typeof(DraftBridge).Assembly.Location) ?? ""; }
            catch { return ""; }
        }

        // ---------------------------------------------------------------- 图纸环境预热

        public static string LocateWarmupExb()
        {
            try
            {
                string dir = AssemblyDir();
                for (int up = 0; up < 4 && !string.IsNullOrEmpty(dir); up++)
                {
                    string p = Path.Combine(dir, WarmupExbName);
                    if (File.Exists(p)) return p;
                    dir = Path.GetDirectoryName(dir) ?? "";
                }
            }
            catch { }
            string baseline = Path.Combine(@"C:\code\ICAPI\caxa3dmytoolsaddin", WarmupExbName);
            return File.Exists(baseline) ? baseline : "";
        }

        /// <summary>预热一条直线：OpenFile2 专用空图纸 → 等 marker ≤30s → PING 取令牌 →
        /// CLOSERAW 立即不保存关载体。失败判死，不重试、不换入口、不回落业务 exb、不弹框。</summary>
        public static bool Warmup(IZBaseApp app, int waitMs, Action<string> log)
        {
            if (log == null) log = delegate { };
            if (app == null) { log("预热失败：没有 IZBaseApp"); return false; }
            string exb = LocateWarmupExb();
            if (string.IsNullOrEmpty(exb))
            {
                log("预热失败：找不到专用空图纸 " + WarmupExbName + "（程序目录逐级向上＋全仓基准都没找到）＝不猜别的载体");
                return false;
            }
            log("预热开始：OpenFile2 打开专用空图纸 " + exb);
            try
            {
                bool already;
                app.OpenFile2(exb, false, true, out already);   // 返回值/异常都是噪音，判据看 marker
                log("OpenFile2 返回 alreadyOpen=" + already + "（按已实测坑：不据返回值判成败）");
            }
            catch (Exception ex) { log("OpenFile2 抛异常（可能假失败，图纸也许已开）：" + ex.Message + " → 继续等 marker"); }

            try
            {
                CaxaAutomation.WaitUntil(delegate() { return MarkerPresent(); }, waitMs, "等 CRX marker＝图纸环境就绪");
            }
            catch (OperationStoppedException) { throw; }
            catch (TimeoutException)
            {
                log("预热失败：等 " + waitMs + "ms marker 仍不在（" + MarkerState() +
                    "）＝检查第五登记点：CAXADraft\\bin\\" + CrxFileName + " 与 CxAutorun.cx 那一行。判死，不换入口");
                return false;
            }

            if (!EnsureToken(10000))
            {
                log("预热失败：PING 握手不通（桥活着但回不了包）＝判死");
                return false;
            }
            if (MarkerFreshness() != "fresh")
            {
                log("预热失败：marker 新鲜度=" + MarkerFreshness() +
                    "（取不到 CRX 所在进程启动时间或 marker 早于它）＝第三态，不冒充就绪");
                return false;
            }

            DraftReply close = Request("CLOSERAW", Path.GetFileName(exb), true, 30000);
            if (!close.Ok)
                log("预热图纸没关成（" + close.Status + " " + close.Message +
                    "）＝界面可能多出一张预热图纸，请手工关掉；预热本身成功，不影响后续");
            return true;
        }

        /// <summary>进入插件后一次性自动预热（由 MainForm 空闲 tick 调用）。
        /// 已热 SKIP；每进程一次；失败判死并记 FAIL；预热过 ≠ CRX 常驻。</summary>
        public static string AutoWarmup(IZBaseApp app)
        {
            if (_autoWarmTried) return _autoWarmState;
            _autoWarmTried = true;
            try
            {
                Diagnostics.Log("auto-warm | START 每进程一次");
                // 已热只记 SKIP，不开图：先 PING 取进程时间，再判 marker 新鲜度
                if (MarkerPresent())
                {
                    EnsureToken(10000);
                    if (IsReady())
                    {
                        _autoWarmState = "SKIP";
                        Diagnostics.Log("auto-warm | SKIP 图纸环境已热（marker fresh + token 可用），不开图");
                        return _autoWarmState;
                    }
                }
                bool ok = false;
                string reason = "";
                try
                {
                    ok = Warmup(app, 30000, delegate(string m) { Diagnostics.Log("auto-warm | " + m); });
                    if (!ok) reason = "Warmup 返回 false（详见上方 auto-warm 行）";
                }
                catch (Exception ex) { reason = ex.GetType().Name + ": " + ex.Message; }
                _autoWarmState = ok ? "OK" : "FAIL";
                Diagnostics.Log(ok ? "auto-warm | OK 载体已不保存关闭；注意：预热过不等于 CRX 常驻"
                                   : "auto-warm | FAIL " + reason + "（判死，不重试、不换入口）");
            }
            catch (Exception ex)
            {
                _autoWarmState = "FAIL";
                Diagnostics.Log("auto-warm | FAIL 外层异常 " + ex);
            }
            return _autoWarmState;
        }
    }
}
