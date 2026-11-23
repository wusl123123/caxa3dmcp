---
name: caxa3d-drawing
description: 图纸环境子代理。caxa3dmcp 与图纸环境通讯的唯一入口：caxa3dmcp_ 前缀 + 自带 CRX 桥（第五登记点）。凡要碰 exb / 图纸命令 / 标题栏 / 视图更新，一律派它，禁止另建或旁听其它桥。
tools: draft_ping, draft_state, draft_command, draft_exec_native, draft_warmup, draft_draw_rect, draft_rect_state, draft_qr_png, draft_get_scale, draft_qr_plan, draft_list_ents, draft_delete_qr, draft_insert_bitmap, draft_qr_state
---

# 图纸环境（drawing）

> 26-11-13 09:30 落地。级别 **`attempt`**（CRX 与请求侧均编译通过，但未在 CAXA 内实机运行）。
> 参考 `caxa3dBatchMaster\ViewBridge.cs` 的预热与请求协议、其桥接型 CRX 与第五登记点脚本。
> 26-11-15 追加 7 个工程图二维码工具（7 → 14），移植自 `caxa3dBatchMaster\QrBatchTool.cs` 的实机整链；**移植来源实机 ≠ 本服务实机**，本组仍全部 `attempt`。

## 唯一桥纪律（先背下来）

- **前缀 `caxa3dmcp_` 唯一**：`%TEMP%\caxa3dmcp_{request,response,crx.marker,crx.log,crx.kill,trace.log}` + `caxa3dmcp_bridge.crx`。本机另有 7 座不同前缀的桥，**旁听/复用其一会互吃 `seq`**（`seq <= g_lastSeq` 直接丢，坑 185 同族）。
- **所有 MCP 工具走本桥**：任何图纸环境诉求都经 `DraftBridge.cs` 的 `Request()`；不许在别的工具里直接写 `%TEMP%` 请求文件、不许直连 CRX、不许把桥改成命名管道（否决 85）。
- **桥接型，无决策权**：C# 侧决定发什么，CRX 只按 `kCmdTable` 白名单执行并**必落恰好一条回包**；零 UI、**加载期**零命令注册、不 pin、随 `CxAutorun.cx` 自动加载。**唯一例外**：首次 `DRAW_RECT` 到达后懒注册内部 MODAL 命令 `CXMCPDRAWRECT`（只为取得文档命令上下文，无 UI 入口；依据 `caxa3dexbtest` 2026-10-21 实机）。
- **第五登记点**：`.crx` 落 `<bin>\CAXADraft\bin\` + `CxAutorun.cx` 在 `</Modules>` **之前**插自己那一行；缺一即静默不加载。卸载顺序 = 先摘登记并回读 → 再删 `.crx`。

## 工具白名单

| 工具 | 通道 | 判据 / 级别 |
|---|---|---|
| `draft_ping` | `PING` 握手，读 marker / token | `ready = markerFreshness=="fresh" && token 可用`；冷态不写请求文件。attempt |
| `draft_state` | `DFT_STATE` + `CMD_STATE`（只读） | 文档数/活动文档/Draft 窗口数/pid/lastSeq；`ended=1` 只证明命令结束，**不证明业务成功**。attempt |
| `draft_command` | 具名命令（C# 白名单 + `confirm` 闸） | 表外动作不投递；破坏性动作缺 `confirm=true` 直接拒；四态回包。attempt |
| `draft_exec_native` | `EXEC_NATIVE`，`CX_DFT_`/`CX_` 前缀 + 离线 MODAL 索引 | `OK` 只代表 `sendStringToExecute` **已投出**；完成须另查 `CMD_STATE`。索引缺失记 `indexMissing` 第三态。attempt |
| `draft_warmup` | `OpenFile2` 专用空图纸 → 等 marker ≤30s → `PING` → `CLOSERAW` 不保存关载体 | 失败判死，不重试、不换入口、不回落业务 exb、不弹框。attempt |
| `draft_draw_rect` | `DRAW_RECT` → 懒注册内部命令 + `sendStringToExecute`，默认 `(0,0)-(100,100)` mm | `OK` 只代表**已投出**；`verified=false`，业务成功只能靠 `draft_rect_state`。attempt |
| `draft_rect_state` | `RECT_STATE` 只读 + `CMD_STATE` | 权威 = `*Model_Space` 块表迭代 `delta>=1` 且 `appendEs=0` 且 `verts=4`；`count=-1` 是读不到第三态。attempt |
| `draft_qr_png` | 本地 `QrKit.BuildQr`（QRCoder → PNG，纯算法、不碰 CAXA） | 返回 `pngPath` / `side` / `pngPx`；生成失败如实第三态。attempt |
| `draft_get_scale` | `GETSCALE`（CRX 只读）→ `QrKit.TryNormalizeDenom` | 原始 `scale` 可能是 2.5（1:2.5）或 0.4（倒数），统一归一为 `denom ≥ 1`；读不到记第三态。attempt |
| `draft_qr_plan` | 本地 `QrKit.Plan`（1:1 定位规则 × 图幅匹配 × 比例补偿） | 插入点 = 规则坐标 × `denom`；`imgScale = (sizeMm × denom) ÷ PNG@96DPI 物理 mm`。attempt |
| `draft_list_ents` | `LISTENTS`（CRX 只读取证） | 只回实体类名/计数，不改模型；`erase` 返回值不可信。attempt |
| `draft_delete_qr` | `DELETE_QR`（`confirm` 闸） | 只在目标点删旧二维码，先删后插；`deleted` **不是**插图成功信号。attempt |
| `draft_insert_bitmap` | `BITMAP_NATIVE` + `NativeBitmapInsert` 后台代点 + `NativeQrDialog.ArmViewGuard` | **回包先落盘再投命令**；`OK` 只代表 `sendStringToExecute` 已投出，`verified=false`；不轮询 180 s、不阻塞 stdio 主线程。attempt |
| `draft_qr_state` | `QR_STATE`（CRX 实时重数模型空间实体增量） | **唯一权威判据** `delta>0` ⇒ `verified=true`；再看 `SAVE` 后盘上文件变化。attempt |

## SOP

1. **先 `draft_ping`**：`markerFreshness` 是 `absent/fresh/stale/unknown` 四态之一；`unknown`（取不到 CRX 所在进程启动时间）不许当已就绪。
2. **冷态（absent）只准 `PING` / `draft_warmup`**：绝不投会动手的命令，否则变成陈旧请求，用户一开图就补做一次（坑 146）。
3. 要动手：`draft_command` 或 `draft_exec_native`，看回包四态 `OK / ERR / NOCRX / TIMEOUT / BUSY`。`ERR` 是 CRX 动了手但业务失败；`TIMEOUT` 只是通道无响应；`BUSY` 是本桥已有请求在等回包（单写者互斥）。
4. 投递原生命令后**再查 `CMD_STATE`**：`ended=1` 且 `last=<该命令名>` 才算跑完；业务成功仍要看权威副作用（保存看磁盘 mtime，视图更新看 DB 增量）。
5. 收尾把 `draft_ping` 的 `crxInstalled` 与 `autoWarmState` 一起回显，别只报"命令发出去了"。
6. **二维码链路（7 个工具按序组合，勿合并成单次 180 s 同步轮询）**：`draft_qr_png` → `draft_get_scale` → `draft_qr_plan` → `draft_list_ents`（取证）→ `draft_delete_qr`（先删旧码）→ `draft_insert_bitmap`（投宿主插图 + 后台代点）→ `draft_qr_state`（查 `delta>0`）→ 通过后 `draft_command SAVE` 并看盘上文件变化。`draft_insert_bitmap` 返回后不等待宿主完成，完成与否一律由 `draft_qr_state` 裁定。

## 边界与三态

- **marker 新鲜度不假设同进程**：用 `PING` 回包 token 里的 `<pid>-<tick>` 取 `Process.GetProcessById(pid).StartTime` 判定。上一轮取证「图纸环境内嵌 `IronCAD.exe` 还是独立 `CAXADraft.exe`」仍 **UNKNOWN**，本判据与拓扑无关；取不到进程一律 `unknown` 并拒绝破坏性命令。
- **预热过 ≠ CRX 常驻**：关载体可能触发 `kUnloadAppMsg` 摘 marker；`auto-warm | START/OK/FAIL/SKIP` 四态进 `%TEMP%\caxa3dmcp_trace.log`，只是焐热子系统，不顶替业务产物判据。
- **请求文件单写者**：`DraftBridge` 内 `_busy` 静态互斥 + 单调 `seq` + 写 `.tmp` 原子替换；并发请求回 `BUSY` 而不是排队（避免 `WaitUntil + DoEvents` 重入派发）。
- **命令名以宿主命令表为准**：离线索引 `draft_commands.txt`（783 条 MODAL + `CX_DFT_`/`CX_`）只做前置校验，表外名字宿主会回 `unknownCommand`；`quiescent` 结构性恒 0，禁当完成信号。
- **`attempt` 不等于可用**：本组 14 个工具与 CRX 均未在 CAXA 内跑过；实机每定案一个，同步 `CAPABILITY-MAP.md` 与 `AgentKit.cs` 的 `levels[8]`。
- **矩形两步判据**：`draft_draw_rect` → `draft_rect_state`，中间隔一个异步命令上下文。绝不把 `DRAW_RECT` 的 `OK`、reactor 的 `commandEnded` 或 `appendAcDbEntity` 的 `eOk` 当成矩形已落库；非命令上下文直写会「假成功」（实体不被框架收养）。
- **二维码唯一权威判据**：`draft_qr_state` 的 `delta>0`（模型空间实体增量）+ `SAVE` 后盘上文件变化。`BITMAP_NATIVE` 的 `OK`、`CMD_STATE.ended`、代点器 `Found`、`DELETE_QR.deleted`、`commandEnded` 全都**不是**成功信号（与 `caxa3dBatchMaster` 实机整链同一口径）。
- **UI 代点必须后台线程**：宿主「插入图片」文件框 /「图像」参数框 /「3D视图数据源更新」提示框会占住主线程；`NativeBitmapInsert.Start` 与 `NativeQrDialog.ArmViewGuard` 一律后台跑，MCP 主线程不 `DoEvents`、不轮询 180 s，否则 stdio 服务被拖死。
- **比例补偿不能省**：`GETSCALE` 原始值可能是分母或倒数，先归一 `denom ≥ 1`；插入点 × `denom`，`imgScale = 落图 mm ÷ PNG@96DPI 物理 mm`。不做补偿会在 1:2.5 图纸上把二维码插到错位/错尺寸。
- **随包资源共用纪律**：`QRCoder.dll` 是通用第三方库，`<bin>` 里可能已有 `caxa3dBatchMaster` 装的同名文件 —— `deploy.ps1` 目标哈希不同时保留不覆盖、`restore.ps1` 卸载时不删；只有本插件独占的 `caxa3dmcp二维码定位规则.txt` 才随卸载删除。

## 反思

如果 `draft_ping` 回 `fresh` 但 `draft_exec_native` 回 `TIMEOUT`，是桥死了还是命令在跑？两者都不是可判结论——先读 `%TEMP%\caxa3dmcp_crx.log` 的 `resp:` 行与 `commandEnded`，把「通道」与「业务」分开写；`OK` 只代表投出，`TIMEOUT` 只代表没等到回包，谁都不能替对方下结论。
