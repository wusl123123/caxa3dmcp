// 图纸环境工具集：所有与图纸环境的通讯一律经 DraftBridge（caxa3dmcp_ 唯一桥）。
// 本文件不含任何 COM/窗口/消息循环逻辑——决策与协议都在请求侧与 CRX 侧，工具只是薄封装。
//
// 级别口径：本轮全部 `attempt`（26-11-13 编译通过，CRX 与请求侧均未实机运行）。
// 26-11-13 16:xx 追加 draft_draw_rect / draft_rect_state：移植桌面 caxa3dexbtest 的
// 「图纸环境模型空间画 100x100 矩形」，仍走本文件 → DraftBridge → caxa3dmcp_ 唯一桥。
// 回包 OK 只代表 CRX 收到并执行了这条通讯；业务是否成功须看权威副作用，
// EXEC_NATIVE 的 OK 更是只代表「已投出」，完成判据要另查 CMD_STATE。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace caxa3dmcp
{
    [ComVisible(false)]
    internal static class DraftToolMeta
    {
        public static Dictionary<string, object> Base(McpToolContext ctx)
        {
            var d = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["level"] = "attempt",
                ["verified"] = false,
                ["bridgePrefix"] = DraftBridge.Prefix,
                ["bridge"] = "caxa3dmcp_bridge.crx（桥接型，第五登记点 CxAutorun.cx 自动加载）",
                ["evidence"] = "compiledOnly: 26-11-15 两工程编译通过；drawing 层 14 个工具（含二维码 7 个）与本桥 CRX 均未在本服务实机运行；二维码算法与 UI 代点移植自 caxa3dBatchMaster 2026-11-15 实机整链",
                ["markerFreshness"] = DraftBridge.MarkerFreshness(),
                ["markerState"] = DraftBridge.MarkerState(),
                ["autoWarmState"] = DraftBridge.AutoWarmState
            };
            return d;
        }

        public static Dictionary<string, object> WithReply(Dictionary<string, object> d, DraftReply r)
        {
            foreach (var kv in r.ToMap()) d["reply_" + kv.Key] = kv.Value;
            d["ok"] = r.Ok;
            d["status"] = r.Status;
            return d;
        }

        public static Dictionary<string, object> ParseFields(string message)
        {
            var d = new Dictionary<string, object>();
            if (string.IsNullOrEmpty(message)) return d;
            foreach (string part in message.Split('|'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                d[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
            }
            return d;
        }

        public static string CrxInstalledHere()
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(DraftToolMeta).Assembly.Location) ?? "";
                string p = Path.Combine(dir, "CAXADraft", "bin", DraftBridge.CrxFileName);
                return File.Exists(p) ? "installed: " + p : "missing: " + p + "（第五登记点未装或装到别的版本目录）";
            }
            catch (Exception ex) { return "unknown: " + ex.Message; }
        }
    }

    /// <summary>握手：PING + 会话令牌 + CRX 所在进程 + marker 新鲜度 + 第五登记点状态。冷态只发 PING。</summary>
    [ComVisible(false)]
    internal sealed class ToolDraftPing : McpToolBase
    {
        public override string Name { get { return "draft_ping"; } }
        public override string Category { get { return "draft"; } }
        public override string Description { get { return "图纸环境唯一桥握手：marker 新鲜度、PING 会话令牌、CRX 所在进程 pid、命令表与第五登记点安装状态。冷态不制造请求。"; } }
        public override object InputSchema() { return S.Schema(new Dictionary<string, object>()); }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var d = DraftToolMeta.Base(ctx);
            d["crxInstalled"] = DraftToolMeta.CrxInstalledHere();
            d["verbs"] = new List<object>(DraftBridge.Verbs);
            d["requestPath"] = DraftBridge.RequestPath;
            d["markerPath"] = DraftBridge.MarkerPath;
            d["crxLogPath"] = DraftBridge.LogPath;

            if (!DraftBridge.MarkerPresent())
            {
                d["status"] = "NOCRX";
                d["hint"] = "图纸环境未加载 CRX：可先调 draft_warmup 预热，或让用户打开一张图纸；冷态不会写任何请求文件";
                return d;
            }
            bool tok = DraftBridge.EnsureToken(8000);
            d["pingOk"] = tok;
            d["token"] = tok ? "present" : "unavailable";
            d["crxPid"] = DraftBridge.CrxPid;
            d["ready"] = DraftBridge.IsReady();
            d["note"] = "marker 新鲜度用 PING 令牌里的 pid 所对应的进程启动时间判定，不假设 CRX 与 add-in 同进程；读不到记 unknown 第三态";
            return d;
        }
    }

    /// <summary>只读状态：DFT_STATE + CMD_STATE（可选命令名）。</summary>
    [ComVisible(false)]
    internal sealed class ToolDraftState : McpToolBase
    {
        public override string Name { get { return "draft_state"; } }
        public override string Category { get { return "draft"; } }
        public override string Description { get { return "图纸环境只读状态：文档数/活动文档/Draft 窗口数/pid/lastSeq/token，以及最近结束命令的 CMD_STATE（ended 不等于业务成功）。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["cmd"] = S.Str("可选：要核对是否已结束的命令名（传给 CMD_STATE）；不传则只返回最近一条"),
                ["timeout_ms"] = S.Num("等回包毫秒数", false, 8000, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var d = DraftToolMeta.Base(ctx);
            int timeout = args.ParamInt("timeout_ms", 8000);
            string cmd = args.ParamString("cmd", "");
            DraftReply st = DraftBridge.Request("DFT_STATE", "", false, timeout);
            DraftToolMeta.WithReply(d, st);
            d["fields"] = DraftToolMeta.ParseFields(st.Message);
            d["raw"] = st.Message;

            DraftReply cs = DraftBridge.Request("CMD_STATE", cmd, false, timeout);
            d["cmdState"] = cs.ToMap();
            d["cmdStateFields"] = DraftToolMeta.ParseFields(cs.Message);
            d["note"] = "quiescent 不是完成信号（实机结构性恒 0）；ended=1 只证明命令跑完/取消/失败，业务判据另看权威副作用";
            return d;
        }
    }

    /// <summary>通用具名命令：C# 白名单 + confirm 闸 + 单写者 seq + 四态回包。</summary>
    [ComVisible(false)]
    internal sealed class ToolDraftCommand : McpToolBase
    {
        public override string Name { get { return "draft_command"; } }
        public override string Category { get { return "draft"; } }
        public override string Description { get { return "向图纸环境唯一桥发一条具名命令（C# 白名单 + confirm 闸）。破坏性动作必须显式 confirm=true；表外动作不投递。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["action"] = S.Enum("桥命令名", DraftBridge.Verbs, true),
                ["arg"] = S.Str("命令参数（如文件名 / 命令串 / exb 路径）", false, ""),
                ["confirm"] = S.Bool("破坏性动作必须置 true（CLOSEDOC/CLOSERAW/SAVE/CONVERT_DXF/OPEN_EXB）", false, false, true),
                ["timeout_ms"] = S.Num("等回包毫秒数", false, 15000, true)
            }, "action");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var d = DraftToolMeta.Base(ctx);
            string action = args.ParamString("action", "").Trim().ToUpperInvariant();
            string arg = args.ParamString("arg", "");
            bool confirm = args.ParamBool("confirm", false);
            int timeout = args.ParamInt("timeout_ms", 15000);

            if (!DraftBridge.IsKnownVerb(action))
                return Fail("UNKNOWN_ACTION", "表外命令不投递：" + action + "；可用 " + string.Join("/", DraftBridge.Verbs));
            bool needConfirm = DraftBridge.NeedsConfirm(action);
            if (needConfirm && !confirm)
                return Fail("CONFIRM_REQUIRED", action + " 是破坏性动作，必须显式传 confirm=true；本次未投递");

            DraftReply r = DraftBridge.Request(action, arg, needConfirm, timeout);
            DraftToolMeta.WithReply(d, r);
            d["action"] = action;
            d["needsConfirm"] = needConfirm;
            d["dispatched"] = (r.Status != "NOCRX" && r.Status != "BUSY");
            return d;
        }
    }

    /// <summary>投递图纸环境能识别的原生命令：离线索引校验 + 前缀白名单，OK 只代表已投出。</summary>
    [ComVisible(false)]
    internal sealed class ToolDraftExecNative : McpToolBase
    {
        public override string Name { get { return "draft_exec_native"; } }
        public override string Category { get { return "draft"; } }
        public override string Description { get { return "经唯一桥投递一条图纸环境原生命令（CX_DFT_/CX_ 白名单 + 离线 MODAL 命令索引）。OK 仅代表已投出，完成须另查 CMD_STATE。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["command"] = S.Str("原生命令名，取自 Mdl*Cmd.xml 的 <Identify>（如 CX_DFT_ViewUpdate）", true),
                ["timeout_ms"] = S.Num("等回包毫秒数", false, 15000, true)
            }, "command");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var d = DraftToolMeta.Base(ctx);
            string cmd = args.ParamString("command", "").Trim();
            int timeout = args.ParamInt("timeout_ms", 15000);
            if (cmd.Length == 0) return Fail("EMPTY_COMMAND", "命令名为空，未投递");

            bool prefixOk = cmd.StartsWith("CX_DFT_", StringComparison.OrdinalIgnoreCase) ||
                            cmd.StartsWith("CX_", StringComparison.OrdinalIgnoreCase);
            if (!prefixOk)
                return Fail("PREFIX_REJECTED", "只放行 CX_DFT_ / CX_ 前缀，表外一律不投递：" + cmd);

            bool indexMissing;
            bool inIndex = DraftBridge.CommandInIndex(cmd, out indexMissing);
            d["indexMissing"] = indexMissing;
            d["indexCount"] = DraftBridge.CommandIndexCount;
            if (!inIndex)
                return Fail("NOT_IN_INDEX", "离线 MODAL 命令索引里没有该名字（宿主会回 unknownCommand），未投递：" + cmd);

            DraftReply r = DraftBridge.Request("EXEC_NATIVE", cmd, false, timeout);
            DraftToolMeta.WithReply(d, r);
            d["command"] = cmd;
            d["dispatched"] = r.Ok;
            d["completed"] = "unknown";
            d["check"] = "draft_state 或 draft_command(action=CMD_STATE, arg=" + cmd + ")";
            d["note"] = indexMissing
                ? "随包 draft_commands.txt 不在＝索引缺失第三态，本次只按前缀放行，名字未经离线表验证"
                : "命令名已对离线 MODAL 索引校验；CRX 回 OK 只代表 sendStringToExecute 已投出，不证明执行完成，更不证明业务成功";
            return d;
        }
    }

    /// <summary>手动预热（自动预热之外可重复点）：OpenFile2 专用空图纸 → 等 marker → CLOSERAW 不保存关载体。</summary>
    [ComVisible(false)]
    internal sealed class ToolDraftWarmup : McpToolBase
    {
        public override string Name { get { return "draft_warmup"; } }
        public override string Category { get { return "draft"; } }
        public override string Description { get { return "图纸环境预热（可重复）：打开专用空图纸带起 CRX 桥，PING 握手后不保存关掉载体。失败判死、不重试、不弹框。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["wait_ms"] = S.Num("等 CRX marker 的毫秒数", false, 30000, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var d = DraftToolMeta.Base(ctx);
            int wait = args.ParamInt("wait_ms", 30000);
            d["warmupExb"] = DraftBridge.LocateWarmupExb();
            var lines = new List<object>();
            bool ok = DraftBridge.Warmup(ctx.App, wait, delegate(string m) { lines.Add(m); Diagnostics.Log("draft-warmup: " + m); });
            d["ok"] = ok;
            d["warmupOk"] = ok;
            d["autoWarmState"] = DraftBridge.AutoWarmState;
            d["markerFreshness"] = DraftBridge.MarkerFreshness();
            d["log"] = lines;
            d["note"] = "预热过不等于 CRX 常驻（关载体可能触发 kUnloadAppMsg 摘 marker）；每次图纸环境会话由 CxAutorun.cx 重新加载";
            return d;
        }
    }

    /// <summary>图纸环境 *Model_Space 画矩形：默认原点 (0,0)-(100,100) mm。OK 只代表已投出。</summary>
    [ComVisible(false)]
    internal sealed class ToolDraftDrawRect : McpToolBase
    {
        public override string Name { get { return "draft_draw_rect"; } }
        public override string Category { get { return "draft"; } }
        public override string Description { get { return "在图纸环境的 *Model_Space 画闭合多段线矩形（默认原点 100×100 mm）。CRX 侧首次请求时懒注册内部 MODAL 命令 + sendStringToExecute 进文档命令上下文；OK 只代表已投出，业务成功须另查 draft_rect_state 的权威计数增量。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["x0"] = S.Num("左下角 X（图纸 mm）", false, 0.0, true),
                ["y0"] = S.Num("左下角 Y（图纸 mm）", false, 0.0, true),
                ["x1"] = S.Num("右上角 X（图纸 mm）", false, 100.0, true),
                ["y1"] = S.Num("右上角 Y（图纸 mm）", false, 100.0, true),
                ["timeout_ms"] = S.Num("等回包毫秒数", false, 15000, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var d = DraftToolMeta.Base(ctx);
            double x0 = args.ParamDouble("x0", 0.0);
            double y0 = args.ParamDouble("y0", 0.0);
            double x1 = args.ParamDouble("x1", 100.0);
            double y1 = args.ParamDouble("y1", 100.0);
            int timeout = args.ParamInt("timeout_ms", 15000);
            if (double.IsNaN(x0) || double.IsNaN(y0) || double.IsNaN(x1) || double.IsNaN(y1) ||
                double.IsInfinity(x0) || double.IsInfinity(y0) ||
                double.IsInfinity(x1) || double.IsInfinity(y1))
                return Fail("BAD_RECT", "坐标必须是有限数，未投递");
            if (x1 <= x0 || y1 <= y0)
                return Fail("BAD_RECT", "必须 x1>x0 且 y1>y0（默认 0,0→100,100），未投递");

            string arg = x0.ToString("0.####", CultureInfo.InvariantCulture) + " " +
                         y0.ToString("0.####", CultureInfo.InvariantCulture) + " " +
                         x1.ToString("0.####", CultureInfo.InvariantCulture) + " " +
                         y1.ToString("0.####", CultureInfo.InvariantCulture);
            d["rect"] = new Dictionary<string, object> { ["x0"] = x0, ["y0"] = y0, ["x1"] = x1, ["y1"] = y1 };
            d["unit"] = "mm（图纸坐标系，原样传给 CRX；不做 3D 侧的米/毫米换算）";
            d["source"] = "caxa3dexbtest 2026-10-21 实机：ExbRectBridge.cpp 的 RECT x0 y0 x1 y1";

            DraftReply r = DraftBridge.Request("DRAW_RECT", arg, true, timeout);
            DraftToolMeta.WithReply(d, r);
            d["dispatched"] = r.Ok;
            d["completed"] = "unknown";
            d["verified"] = false;
            d["check"] = "draft_rect_state";
            d["authority"] = "*Model_Space 块表迭代计数 after>before（delta>=1）且 appendEs=0";
            d["note"] = "默认 0,0→100,100 对应桌面工程的 100x100 矩形；OK 只代表 sendStringToExecute 已投出，命令异步执行，必须另查 draft_rect_state 才能判业务成功";
            return d;
        }
    }

    /// <summary>矩形权威回读：模型空间实体计数增量 + 最近一次 append 结果 + CMD_STATE。</summary>
    [ComVisible(false)]
    internal sealed class ToolDraftRectState : McpToolBase
    {
        public override string Name { get { return "draft_rect_state"; } }
        public override string Category { get { return "draft"; } }
        public override string Description { get { return "只读回读图纸环境模型空间矩形结果：*Model_Space 块表迭代计数 count/before/after/delta、最近一次 append 结果、命令是否结束。verified 只由权威计数增量判定，不认 OK 回包。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["timeout_ms"] = S.Num("等回包毫秒数", false, 8000, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var d = DraftToolMeta.Base(ctx);
            int timeout = args.ParamInt("timeout_ms", 8000);
            DraftReply r = DraftBridge.Request("RECT_STATE", "", false, timeout);
            DraftToolMeta.WithReply(d, r);
            var f = DraftToolMeta.ParseFields(r.Message);
            d["fields"] = f;
            d["raw"] = r.Message;
            object v;
            bool verified = r.Ok && f.TryGetValue("verified", out v) && Convert.ToString(v) == "1";
            d["verified"] = verified;
            d["level"] = "attempt";
            d["authority"] = "count/before/after/delta 来自 CRX 内 *Model_Space 块表迭代；读不到记 -1 第三态";
            d["note"] = "verified=1 需 appendEs=0 且 delta>=1 且 verts=4；CMD_STATE 的 ended=1 只证明命令跑完，不证明矩形落库";
            DraftReply cs = DraftBridge.Request("CMD_STATE", "CXMCPDRAWRECT", false, timeout);
            d["cmdState"] = cs.ToMap();
            d["cmdStateFields"] = DraftToolMeta.ParseFields(cs.Message);
            return d;
        }
    }

    // =================================================================================
    // 工程图附加二维码（26-11-15 学习 caxa3dBatchMaster 后移植，7 个工具）
    //
    // 链路（可与 draft_command(SAVE) 组合，全部经 DraftBridge / caxa3dmcp_ 唯一桥）：
    //   draft_qr_png（QRCoder PNG，磁盘 >0 字节）
    //   → draft_get_scale（GETSCALE 读比例/图幅，只读）
    //   → draft_qr_plan（定位规则 × 比例分母 → 插入点；尺寸补偿 → 宿主比例因子，纯计算）
    //   → draft_list_ents（LISTENTS 只读取证，可选）
    //   → draft_delete_qr（DELETE_QR 先删后插，confirm）
    //   → draft_insert_bitmap（BITMAP_NATIVE + 后台代点，confirm；OK 只代表投出）
    //   → draft_qr_state（QR_STATE，delta>0 = 唯一权威成功判据）
    //   → draft_command(action=SAVE, confirm=true) + 盘上 mtime/长度变化（调用方复核）
    //
    // ★ 与 batchMaster 的差别：那边是 WinForms 单行 180 s 同步轮询；本服务是 stdio 同步调用，
    //   故把「投命令」与「等权威信号」拆成两个工具，UI 代点器放后台线程，不阻塞 MCP 主线程、
    //   不改窗口过程（铁律 3）。所有工具一律 attempt，编译通过 ≠ 实机可用。
    // =================================================================================

    /// <summary>二维码链路共用的小工具：回包解析 + 后台代点器状态快照。</summary>
    [ComVisible(false)]
    internal static class DraftQrKit
    {
        public static Dictionary<string, object> Fields(string msg)
        {
            var src = QrKit.ParseFields(msg);   // 同时兼容 | 与空格分隔（QR_STATE 用空格）
            var d = new Dictionary<string, object>();
            foreach (var kv in src) d[kv.Key] = kv.Value;
            return d;
        }

        public static Dictionary<string, object> DialogSnapshot()
        {
            var d = new Dictionary<string, object>
            {
                ["found"] = NativeBitmapInsert.Found,
                ["filled"] = NativeBitmapInsert.Filled,
                ["opened"] = NativeBitmapInsert.Opened,
                ["imageDlgFound"] = NativeBitmapInsert.ImageDlgFound,
                ["pointFilled"] = NativeBitmapInsert.PointFilled,
                ["screenPickUnchecked"] = NativeBitmapInsert.ScreenPickUnchecked,
                ["scaleFilled"] = NativeBitmapInsert.ScaleFilled,
                ["scaleUnchecked"] = NativeBitmapInsert.ScaleUnchecked,
                ["okClicked"] = NativeBitmapInsert.OkClicked,
                ["done"] = NativeBitmapInsert.Done,
                ["lastError"] = NativeBitmapInsert.LastError,
                ["followUpDialogCount"] = NativeBitmapInsert.FollowUpDialogs.Count,
                ["viewPromptsHandled"] = NativeQrDialog.ViewPromptsHandled
            };
            return d;
        }

        public static Action<string> Log = delegate(string m)
        {
            Diagnostics.Log("draft-qr: " + m, 0);
        };
    }

    /// <summary>① QRCoder 生成二维码 PNG。唯一判据 = 磁盘文件存在且 > 0 字节。</summary>
    [ComVisible(false)]
    internal sealed class ToolDraftQrPng : McpToolBase
    {
        public override string Name { get { return "draft_qr_png"; } }
        public override string Category { get { return "draft"; } }
        public override string Description { get { return "QRCoder(ECC L) 生成二维码 PNG：1 模块静区、8px/模块、96DPI、文件名带时间戳。verified 只由磁盘文件 > 0 字节判定；不碰图纸。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["payload"] = S.Str("二维码内容（必填）", true),
                ["tag"] = S.Str("可选：文件名标签（一般传零件名，仅影响临时文件名）")
            }, "payload");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var d = DraftToolMeta.Base(ctx);
            string payload = args.ParamString("payload", "");
            string tag = args.ParamString("tag", "qr");
            if (string.IsNullOrEmpty(payload)) return Fail("EMPTY_PAYLOAD", "二维码内容为空，未生成");
            string png; int side, px;
            bool ok = QrKit.BuildQr(payload, tag, DraftQrKit.Log, out png, out side, out px);
            d["payloadLength"] = payload.Length;
            d["pngPath"] = png;
            d["matrixSide"] = side;
            d["pngPx"] = px;
            d["pngMm"] = px > 0 ? px / QrKit.Dpi * QrKit.MmPerInch : 0.0;
            d["verified"] = ok;
            d["level"] = "attempt";
            d["authority"] = "PNG 磁盘文件存在且 > 0 字节（本地纯计算，当场可验）；图纸侧链路仍 attempt";
            d["note"] = "文件名带 yyyyMMdd-HHmmss-fff：batchMaster 11-15 实机坑——固定名重跑会让宿主「图像」框滞留旧路径、插图命令 pending=1 挂死；96DPI 是宿主比例因子的换算基准";
            return d;
        }
    }

    /// <summary>② GETSCALE：读当前图纸比例 + 图幅物理宽高，归一为分母 1:N（只读，不改模型）。</summary>
    [ComVisible(false)]
    internal sealed class ToolDraftGetScale : McpToolBase
    {
        public override string Name { get { return "draft_get_scale"; } }
        public override string Category { get { return "draft"; } }
        public override string Description { get { return "GETSCALE 只读：PaperTable.CurrentPaper 的纸张比例与图幅物理宽高 mm；把 2.5 / 0.4 两种存储形态归一为分母 1:N。读不到记第三态，绝不瞎折算。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["timeout_ms"] = S.Num("等回包毫秒数", false, 8000, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var d = DraftToolMeta.Base(ctx);
            DraftReply r = DraftBridge.Request("GETSCALE", "", false, args.ParamInt("timeout_ms", 8000));
            DraftToolMeta.WithReply(d, r);
            var raw = QrKit.ParseFields(r.Message);
            d["fields"] = DraftQrKit.Fields(r.Message);
            d["raw"] = r.Message;
            double denom = 1.0, scaleRaw = 0;
            bool identified = r.Ok && QrKit.TryNormalizeDenom(raw, out denom, out scaleRaw);
            double w = 0, h = 0;
            QrKit.TryDouble(raw, "w", out w);
            QrKit.TryDouble(raw, "h", out h);
            d["scaleRaw"] = scaleRaw;
            d["denom"] = identified ? denom : 1.0;
            d["denomSource"] = identified ? "GETSCALE identified" : "fallback 1:1（第三态，不折算）";
            d["paperW"] = w; d["paperH"] = h;
            d["verified"] = identified;
            d["level"] = "attempt";
            d["authority"] = "CRX PaperTable.CurrentPaper.getPaperScale/getPaperSize（ICAPI 无该读取入口）";
            d["note"] = "verified 只表示比例读到且能归一，不代表图纸已插图；读不到一律回落 1:1 并标第三态";
            return d;
        }
    }

    /// <summary>③ 纯计算：定位规则匹配 + 比例补偿，产出插入点与宿主比例因子。不改模型、不发桥请求。</summary>
    [ComVisible(false)]
    internal sealed class ToolDraftQrPlan : McpToolBase
    {
        public override string Name { get { return "draft_qr_plan"; } }
        public override string Category { get { return "draft"; } }
        public override string Description { get { return "纯计算：按图幅匹配 1:1 定位规则，插入点 = 规则坐标 × 比例分母；成品 mm × 分母 = 落图 mm，宿主比例因子 = 落图 ÷ PNG@96DPI。不改模型、不发桥请求。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["size_mm"] = S.Num("需求成品边长 mm（二维码为正方，按宽度算）", false, 20.0, true),
                ["denom"] = S.Num("图纸比例分母（draft_get_scale 的 denom）", false, 1.0, true),
                ["paper_w"] = S.Num("图幅物理宽 mm（GETSCALE 的 w）", false, 0.0, true),
                ["paper_h"] = S.Num("图幅物理高 mm（GETSCALE 的 h）", false, 0.0, true),
                ["fallback_x"] = S.Num("规则未命中时的兜底插入点 X（图纸坐标）", false, 0.0, true),
                ["fallback_y"] = S.Num("规则未命中时的兜底插入点 Y（图纸坐标）", false, 0.0, true),
                ["png_px"] = S.Num("PNG 边长像素（draft_qr_png 的 pngPx）", false, 0.0, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var d = DraftToolMeta.Base(ctx);
            double size = args.ParamDouble("size_mm", 20.0);
            double denom = args.ParamDouble("denom", 1.0);
            double w = args.ParamDouble("paper_w", 0.0), h = args.ParamDouble("paper_h", 0.0);
            double fx = args.ParamDouble("fallback_x", 0.0), fy = args.ParamDouble("fallback_y", 0.0);
            int px = args.ParamInt("png_px", 0);
            if (size <= 0 || size > 1000 || denom <= 0 || px <= 0)
                return Fail("BAD_PLAN_INPUT", "size_mm/denom 必须 >0、png_px 必须 >0（先调 draft_qr_png / draft_get_scale）；未计算");
            var plan = QrKit.Plan(size, denom, w, h, fx, fy, px);
            foreach (var kv in plan) d[kv.Key] = kv.Value;
            d["level"] = "attempt";
            d["verified"] = false;   // 纯计算不产生模型副作用，绝不假成功
            d["computed"] = true;
            d["unit"] = "图纸坐标系 mm（与 3D 侧的米/毫米换算无关）";
            d["note"] = "规则命中时插入点 = 1:1 基准 × 分母；未命中回落兜底值并标第三态。computed=true 只表示算式走完，插图成败只认 draft_qr_state 的 delta>0";
            return d;
        }
    }

    /// <summary>④ LISTENTS：只读枚举模型空间实体类名分布 + imageRef 计数（先删后插的取证）。</summary>
    [ComVisible(false)]
    internal sealed class ToolDraftListEnts : McpToolBase
    {
        public override string Name { get { return "draft_list_ents"; } }
        public override string Category { get { return "draft"; } }
        public override string Description { get { return "LISTENTS 只读：枚举 *Model_Space 每实体 isA()->name() 统计类名分布，并按 CRxDbImageRef::desc() 复核图片实体数。绝不写库、不 erase。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["timeout_ms"] = S.Num("等回包毫秒数", false, 8000, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var d = DraftToolMeta.Base(ctx);
            DraftReply r = DraftBridge.Request("LISTENTS", "", false, args.ParamInt("timeout_ms", 8000));
            DraftToolMeta.WithReply(d, r);
            d["fields"] = DraftQrKit.Fields(r.Message);
            d["raw"] = r.Message;
            d["verified"] = false;
            d["level"] = "attempt";
            d["note"] = "只读取证：imageRef>0 说明图上已有图片实体，重跑前应先按插入点调 draft_delete_qr；descReady=0 表示 rxInit 未跑，类名判据降级";
            return d;
        }
    }

    /// <summary>⑤ DELETE_QR：按目标点 ±1mm 命中图片实体包围盒后删除旧二维码（confirm 闸）。</summary>
    [ComVisible(false)]
    internal sealed class ToolDraftDeleteQr : McpToolBase
    {
        public override string Name { get { return "draft_delete_qr"; } }
        public override string Category { get { return "draft"; } }
        public override string Description { get { return "DELETE_QR：arg=x,y，按 CRxDbImageRef 包围盒命中目标点 ±1mm 精确删除旧二维码（两趟：只读收集 id → kForWrite erase）。deleted/eraseFail 只作取证，不作成败判据；需 confirm=true。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["x"] = S.Num("目标插入点 X（图纸 mm）", true),
                ["y"] = S.Num("目标插入点 Y（图纸 mm）", true),
                ["confirm"] = S.Bool("破坏性动作必须置 true", false, false, true),
                ["timeout_ms"] = S.Num("等回包毫秒数", false, 10000, true)
            }, "x", "y");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var d = DraftToolMeta.Base(ctx);
            if (!args.ParamBool("confirm", false))
                return Fail("CONFIRM_REQUIRED", "DELETE_QR 是破坏性动作，必须显式传 confirm=true；本次未投递");
            double x = args.ParamDouble("x", 0.0), y = args.ParamDouble("y", 0.0);
            string arg = QrKit.Fmt(x) + "," + QrKit.Fmt(y);
            DraftReply r = DraftBridge.Request("DELETE_QR", arg, true, args.ParamInt("timeout_ms", 10000));
            DraftToolMeta.WithReply(d, r);
            d["target"] = arg;
            d["fields"] = DraftQrKit.Fields(r.Message);
            d["raw"] = r.Message;
            d["verified"] = false;
            d["level"] = "attempt";
            d["note"] = "batchMaster 实机坑：erase() 返回值不可信（可能已删却回 err），deleted=0 是正常首插；成败一律由 draft_qr_state 的 delta 裁定，本工具不假成功";
            return d;
        }
    }

    /// <summary>⑥ BITMAP_NATIVE：投宿主插图命令 + 启动后台代点器。OK 只代表已投出。</summary>
    [ComVisible(false)]
    internal sealed class ToolDraftInsertBitmap : McpToolBase
    {
        public override string Name { get { return "draft_insert_bitmap"; } }
        public override string Category { get { return "draft"; } }
        public override string Description { get { return "BITMAP_NATIVE：投宿主 CX_DFT_InsertBitmap 让 CAXA 自己插图（实体由宿主命令收养），并启动后台线程代填文件框/图像参数框。OK 只代表已投出；业务成功须另查 draft_qr_state 的 delta>0。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["png_path"] = S.Str("draft_qr_png 产出的 PNG 全路径（必填）", true),
                ["x"] = S.Num("插入点 X（图纸 mm，draft_qr_plan 的 insertX）", true),
                ["y"] = S.Num("插入点 Y（图纸 mm，draft_qr_plan 的 insertY）", true),
                ["scale"] = S.Num("宿主比例因子（draft_qr_plan 的 imgScale）", true),
                ["auto_dialog"] = S.Bool("是否启动后台对话框代点器（默认 true）", false, true, true),
                ["confirm"] = S.Bool("破坏性动作必须置 true", false, false, true),
                ["timeout_ms"] = S.Num("等回包毫秒数", false, 15000, true)
            }, "png_path", "x", "y", "scale");
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var d = DraftToolMeta.Base(ctx);
            if (!args.ParamBool("confirm", false))
                return Fail("CONFIRM_REQUIRED", "BITMAP_NATIVE 是破坏性动作，必须显式传 confirm=true；本次未投递");
            string png = args.ParamString("png_path", "");
            if (string.IsNullOrEmpty(png) || !File.Exists(png))
                return Fail("PNG_MISSING", "PNG 不存在（先调 draft_qr_png），未投递：" + png);
            double x = args.ParamDouble("x", 0.0), y = args.ParamDouble("y", 0.0);
            double scale = args.ParamDouble("scale", 1.0);
            bool autoDialog = args.ParamBool("auto_dialog", true);
            string point = QrKit.Fmt(x) + "," + QrKit.Fmt(y);

            d["pngPath"] = png;
            d["insertPoint"] = point;
            d["imgScale"] = scale;
            d["autoDialog"] = autoDialog;

            if (autoDialog)
            {
                // 守护与代点器都是后台线程，绝不阻塞 MCP 主线程、不改窗口过程（铁律 3）。
                NativeQrDialog.ArmViewGuard(150, DraftQrKit.Log);
                NativeBitmapInsert.Start(png, point, scale, DraftQrKit.Log, delegate() { return false; });
                d["dialogStarted"] = true;
                d["noteDialog"] = "视图更新提示守护 150s + 文件框/图像框后台代点已启动；代点状态在 draft_qr_state 里回读";
            }
            else d["dialogStarted"] = false;

            // CRX 侧先落回包再 sendStringToExecute，所以这里能拿到 OK 而不会被模态框堵住。
            DraftReply r = DraftBridge.Request("BITMAP_NATIVE", "", true, args.ParamInt("timeout_ms", 15000));
            DraftToolMeta.WithReply(d, r);
            d["dispatched"] = r.Ok;
            d["completed"] = "unknown";
            d["verified"] = false;
            d["level"] = "attempt";
            d["check"] = "draft_qr_state";
            d["authority"] = "*Model_Space 实体计数增量 delta>0（宿主命令异步生成，必须实时重数）";
            d["dialog"] = DraftQrKit.DialogSnapshot();
            d["note"] = "OK 只代表 sendStringToExecute 已投出，不证明执行完成、更不证明插图落库；命令滞留时 pending 会恒 1（batchMaster 实机需 60s ESC 救援），本工具不轮询、不吊死 stdio 调用";
            return d;
        }
    }

    /// <summary>⑦ QR_STATE：权威回读。delta>0 才是插图成功的唯一判据，附带后台代点状态。</summary>
    [ComVisible(false)]
    internal sealed class ToolDraftQrState : McpToolBase
    {
        public override string Name { get { return "draft_qr_state"; } }
        public override string Category { get { return "draft"; } }
        public override string Description { get { return "QR_STATE 只读权威回读：实时重数 *Model_Space 实体得 before/after/delta 与 pending；verified 只由 delta>0 判定，不认 OK 回包 / commandEnded / 代点器 Found。附后台对话框代点状态。"; } }
        public override object InputSchema()
        {
            return S.Schema(new Dictionary<string, object>
            {
                ["timeout_ms"] = S.Num("等回包毫秒数", false, 8000, true)
            });
        }
        public override object Execute(McpToolContext ctx, JValue args)
        {
            var d = DraftToolMeta.Base(ctx);
            DraftReply r = DraftBridge.Request("QR_STATE", "", false, args.ParamInt("timeout_ms", 8000));
            DraftToolMeta.WithReply(d, r);
            var raw = QrKit.ParseFields(r.Message);
            d["fields"] = DraftQrKit.Fields(r.Message);
            d["raw"] = r.Message;
            double delta = -999, pending = -999;
            QrKit.TryDouble(raw, "delta", out delta);
            QrKit.TryDouble(raw, "pending", out pending);
            d["delta"] = delta;
            d["pending"] = pending;
            d["verified"] = r.Ok && delta > 0;
            d["level"] = "attempt";
            d["authority"] = "CRX 内 *Model_Space 块表迭代实时重数；delta<=0 且 pending=0 = 真失败，delta>1 = 可能重复插入";
            NativeBitmapInsert.DrainLogs(DraftQrKit.Log);
            d["dialog"] = DraftQrKit.DialogSnapshot();
            d["note"] = "verified=true 仍只是「实体已落库」；保存须另调 draft_command(action=SAVE, confirm=true)，并复核 exb 的 mtime/长度变化。代点器状态只是取证，不作成败判据";
            return d;
        }
    }
}
