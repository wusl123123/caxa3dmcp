# caxa3dmcp 能力映射表（九层 Agent + caxa3dtracker 移植 + 建模顺序号/激活协议）

> 取证日期 2026-11-13。取证来源：`_probe-tools/out/tlb/2027/members.csv` + `enums.csv` + `Lib/interop.ICApiIronCAD.dll` 字节扫描 + 本项目既有 41 工具实现 + `caxa3dtracker` 实机探针。
> 26-11-13 追加：两工程 `Lib/interop.ICApiIronCAD.dll` **md5 完全一致（29a23396…）**，tracker 已实机验证的读链可直接在本工程用 RCW 直调复刻（见文末移植表）。
> **级别口径**：`implemented` = 有既有实证或纯 RCW 只读/写后回读链路；`attempt` = TLB + interop 均声明、但**未实机定案**，必须靠 `verified`/`attempts` 判定；`capability` = 无实证通道，只返回能力协商第三态，**绝不假成功**。

| 层 | 提示中的 API | 本版工具名 | 状态 | 真实通道 | 级别 |
|---|---|---|---|---|---|
| 文档 | new_document | `new_document` | 新增 | `IZBaseApp.OpenNewDoc(Z_SCENE,…)` → `CreateNewDoc` 兜底 | attempt |
| 文档 | open_document | `open_document` | 新增 | `IZBaseApp.OpenFile2` | attempt |
| 文档 | save_document | `save_document` | **已有** | `IZDoc.Save` / `SaveAs(path)` | implemented |
| 文档 | export_step | `export_step` | 新增 | `IZPart.Export(path, eZModelType.Z_MODEL_STEP=3)` + 磁盘回读 | attempt |
| 文档 | export_parasolid | `export_parasolid` | 新增 | `IZPart.Export(path, eZModelType.Z_MODEL_X_T=0)` + 磁盘回读 | attempt |
| 场景 | add_scene | `add_scene` | 新增 | 新建 `Z_SCENE` 文档（同 new_document） | attempt |
| 场景 | add_assembly | `add_assembly` | 新增 | `IZSceneDoc.CreateAssembly()` + 顶层计数回读 | implemented |
| 场景 | add_part | `add_part` | 新增 | `IZSceneDoc.CreatePart()` + 顶层计数回读 | implemented |
| 场景 | list_catalogs | `list_catalogs` | 新增 | `IZBaseApp.CatalogMgr` → `IZCatalogMgr.Count/get_Catalog(i)` → `IZCatalog.EntryCount/get_Entry(j)` | attempt |
| 场景 | get_active_scene_graph | `get_active_scene_graph` | 新增 | `IZSceneDoc.GetChildElements` + `IZElement.GetChildren` | implemented |
| 场景 | （既有对照） | `get_element_tree` / `list_top_elements` / `get_scene_info` | 已有 | 同上 | implemented |
| Catalog | drag_intellishape_from_catalog | `drag_intellishape_from_catalog` | 新增 ★ | `IZCatalogEntry.InsertElement()` 主通道 / `DropObject(screenX,screenY)` 备通道 → 顶层计数 + 名称回读 | attempt |
| IntelliShape | set_shape_handle | `set_shape_handle` | 新增 | `IZSizeBox.Length/Width/Height` 写入 → `IZPart.Update()` → 三值回读 | implemented |
| IntelliShape | edit_cross_section | `edit_cross_section` | 新增 | `IZSceneDoc.CreateProfile()` + `IZProfile.CreateLine/CreateCircle/CreateArc`；**截面与既有特征的关联通道未实证** | capability |
| TriBall | set_triball | `set_triball` | 新增 | `IZEDOSiteElement.GetPositionTransform` → `IZMathMatrix.SetTranslation/SetRotation` → `SetPositionTransform` → 回读 | attempt |
| TriBall | position_by_triball | `position_by_triball` | 新增 | 同上（仅平移，支持 relative） | attempt |
| 特征 | extrude_sketch | `extrude_sketch` | 新增 | `IZPartFeatureMgr.CreateExtrudeFeature(11)` + `IZProfile` | attempt |
| 特征 | revolve | `revolve` | 新增 | `CreateSpinFeature(17)` / `CreateSpinFeatureWithAxis(29)` | capability |
| 特征 | sweep | `sweep` | 新增 | `CreateSweepFeature(16)`（需路径元素） | capability |
| 特征 | shell | `shell` | 新增 | `CreateShellFeature(12)`（需开放面 Id 数组） | capability |
| 特征 | mirror | `mirror` | 新增 | `IZMathMatrix.MirrorAboutPlane(14)` + `IZEDOSiteElement.ApplyTransform(10)` | attempt |
| 反馈 | capture_viewport | `capture_viewport` | 新增（别名） | `IZSceneDoc.ExportImage` → PNG + image 内容块；既有同名能力 = `get_view` | implemented |
| 反馈 | get_bounding_box | `get_bounding_box` | **已有** | `IZPart.GetBody(true)` → `IZBody.GetBoundingBox` | implemented |
| 反馈 | measure_distance | `measure_distance` | 新增 | 两元素包围盒中心/角点距离，纯数学，不读写模型 | implemented |
| 元 | — | `list_agent_layers` | 新增 | 只读元数据，回显本表分级 | implemented |

## 26-11-14 追加：建模顺序号 + 普通零件两条建模路线（76 → 83 工具，`ToolsBuildMode.cs`）

> 用户 11-14 10:50 裁定。新增两个 category：`build`（建模顺序号 / 多零件布尔 / 按号重排）与 `activation`（激活协议）；`list_agent_layers` 分层仍挂 **feature 层**，保持 9 层不变。
> 取证：`_probe-tools/out/tlb/2027/members.csv` → `IZPart.Boolean(13)`、`IZPart.CombineFeatures(32)`、`IZPartFeatureMgr.ReorderFeature(24)`、`IZFeature.get_FeatureIndex(8)`；`Lib/interop.ICApiIronCAD.dll` 字节扫描确认 `Boolean` / `CombineFeatures` / `ReorderFeature` / `GetFeatureAt` 在 **v26 interop PRESENT** ⇒ 全部 RCW 直调（通道阶梯顶层），未下沉槽位。

| 工具 | 真实通道 | 级别 / 判据 |
|---|---|---|
| `get_build_seq` | `IZPartFeatureMgr.GetFeaturesCount/GetFeatureAt` 只读枚举 + 名称前缀 `N-` 解析 | **implemented**；只读、不消耗序号；输出乱序/缺号/重号 |
| `number_feature` | `IZElement.set_Name(10)` + `get_Name(9)` | **implemented**；写后回读一致才 `verified`。**钣金特征、草图轮廓同入口**（同为 `IZFeature`/`IZElement` 表面） |
| `renumber_features` | 同上 + `IZPart.Update(7)` | **implemented**；逐项回读 + 重新枚举后序号严格 `start..start+n-1` |
| `reorder_features_by_seq` | `IZPartFeatureMgr.ReorderFeature(24, IZFeature, int)` | **attempt**；选择排序式归位，每步重新枚举；最终已编号特征序号非递减才 `verified`；`lNewIndex` 语义未实机定案 |
| `boolean_parts` | `IZPart.Boolean(13, IZPart, eZOperationType)` → `IZFeature` + `IZPart.Update(7)` | **attempt**；每个工具零件判据 = 目标 `GetFeaturesCount +1` 且返回特征非空 + 新特征名写后回读；支持 `all_other_parts` 一次合并全场 |
| `set_part_activation` | 设计环境命令派发 `WM_COMMAND 53111`（Ctrl+Shift+A 等价）/ `53227`（取消所有激活） | **attempt**；`PostMessage` 只报 `posted`（入队证据）与选择集回读命中，`verified` **恒 false** |
| `get_part_activation_state` | `IZSelectionMgr.GetSelectedElements(10)` + `IZElement.GetStateStatus(17)` 六状态探针 | **capability**；`state` 恒 `unknown`、`verified` 恒 false，只作留档证据 |

**★ 激活状态的能力边界（本轮最重要的结论）**：TLB 全域穷举**没有「零件激活状态」读 API** —— `eZElementState` 全集只有 6 位（SUPPRESSED/INVALID/HIDDEN/NOFACE/VALID/LINKED），`Activate`/`IsActivated` 只落在目录/窗口/配置/投影参考等无关接口。`caxa3dtracker\PartActivateTool.cs` 四轮实证：v1 `SetSuppressState(16)` 语义是「压缩」不是激活；v3 按「选中⇄激活」相关性换色实测不准；v4（用户 10:26 裁定）取消一切状态推断。
⇒ 本工程**不假造状态读口**，改用「**起点已知协议**」：`53227` 先把全场激活归零 → 重新选中目标 → `53111` 定向切换；起点已知则结果态在逻辑上可推，但仍标 `inferred`、`verified=false`。`53227` 目前只有资源库 STRINGTABLE 旁证（tracker 11-14 09:45），**未实机定案**，已写进返回值 `source`。

**★ 建模特征命名纪律**：创新零件模式下改动特征顺序会改变建模结果甚至报错 ⇒ 所有零件内建模产生的特征名一律 `N-名称`（如 `1-长方体`）。已接入：`create_box`/`create_cylinder`/`create_sphere`（首特征）、`create_tube`/`create_cone`/`create_torus`（共用链 `PrimitiveKit.NameFeature`）、`create_tube` 内孔第二特征、`extrude_sketch`、`create_sketch`、`boolean_parts` 新特征。序号由 `BuildSeq` 分配（进程内递增 + 场景扫描续号取大，重启/open 文档后可续），出错后 `get_build_seq` → `reorder_features_by_seq` 按号恢复。

## 关键能力边界（为什么不是全部 implemented）
1. **ICAPI 只能进程内调用**：stdio server 只是管道转发，所有工具都在 `IronCAD.exe` 的 STA 主线程执行（桥 `Dispatch` 回投）。
2. **TLB 有 ≠ 运行时可调 ≠ 语义正确**：Catalog / TriBall / 特征层成员在 2027 TLB 与本工程 interop 里都声明了，但除 `CreatePart` / `CreateCylinder` / `IZSizeBox` 三处外**没有本项目实机日志**；因此一律 `attempt`，靠写后回读与 `attempts` 判成败。
3. **TriBall 与 IntelliShape 是 UI 概念**：ICAPI 暴露的是 `SetPositionTransform` / `IZSizeBox`，不是 TriBall 手柄对象本身；本版把它映射为「变换矩阵 + 尺寸盒」，语义近似但不等价。
4. **草图命令 → 截面几何**：`IZProfile` 能画线/圆，但把新 Profile 关联回既有 IntelliShape 特征的通道未取证，故 `edit_cross_section` 只返回能力协商，不改几何。
5. **Parasolid** 走 `Z_MODEL_X_T`（含版本族 `Z_MODEL_X_T_*`），STEP 走 `Z_MODEL_STEP` / `Z_MODEL_STEP214`；成功判据是**导出文件存在且长度 > 0**，不是 `Export` 没抛异常。

## 26-11-13 追加：从 caxa3dtracker 移植的四类能力（63 → 69 工具）

> 全部实现在 `ToolsTrackerPort.cs`（共享底座 `TrackerKit` + 6 个 `IMcpTool`）。**级别一律 attempt**：tracker 侧有实机日志，本 MCP 服务侧尚未实机跑过。

| 能力 | 工具名 | tracker 来源 | 真实通道 | 判据 / 第三态 |
|---|---|---|---|---|
| 参数表抓取 | `get_parameter_table` | `ParameterTableProbe.cs` | `GetChildElements(7)` → `GetChildren(13)` 递归 → `IZSceneElement.ParameterMgr(19)` → `Count(7)/GetParameter(i)(8)` → `IZParameter` getters | Value 单位=米、`valueMm`=×1000；`TextParameterValue` 对 LENGTH 抛 E_FAIL 记 `N/A`；空集必须区分「真空」与「读不到（有分支失败/截断）」 |
| 特征包围盒 | `get_feature_bounding_boxes` | `FeatureBoxProbe.cs` | `IZFeature.GetBoundingBox(13)` / `IZPart.GetBody(true)`→`IZBody.GetBoundingBox(13)`、`GetTightBoundingBox(21)` / `IZAssembly.GetBoundingBox(false)(53)` | 按 **eZElementType 名称**分诊（QI 顺序会串位）；局部盒经 `GetTransformToGlobal` 八角点换算世界 AABB，失败保留「局部」不冒充世界；读不到 `status=no_box` 且不给 0 |
| 草图建立 | `create_sketch` | `RectSketchTool.cs`（仅建模段） | `CreateProfile(22)` → `CreateLine(29)` 2D 双元素优先 / 3D 三元素兜底 → `IZSceneDoc.Update()` | `GetCurvesCount()` 写后回读 ≥ 期望线数；起始 `SelectionUIContext==2` 直接拒绝；**不拉伸、不投 WM_COMMAND** |
| 移动零部件 | `move_component` / `move_selected_components` | `MoveComponentTool.cs` | `IZSceneElement.get/set_PositionTransformComponentValue(25)`，枚举 `Z_TRANS_L=0/W=1/H=2` | 分量单位=米，输入 mm×0.001；三分量回读 ±0.01mm 才算 `verified`，`GetPositionTransform/GetTranslation` 仅作旁证；顶层=全局、嵌套件=相对父级 |
| 选择集只读 | `get_selection` | `ProbeCommon.ReadSelectionRaw` | `SelectionMgr` 四通道（19/10/28/30）按 IUnknown 去重 | 回显 id/name/type/可移动判定/UIContext；`=2` 提示草图编辑态 |
| UI 交互能力 | **未移植** | `RectSketchTool.cs` 标注段 / `SketchLockTool.cs` | WM_COMMAND 50283/50123/50731、窗口子类化、`Application.DoEvents` 10s 观察 | **能力边界**：COM 层无「创建草图尺寸」写接口；std io 同步服务不阻塞主线程、不挂窗口过程、不假成功 |

## 目录（26-11-13 收敛为唯一权威源）

**本文件夹 `C:\code\ICAPI\caxa3dmcp\agents\` 是后续所有 agents 定义的唯一权威源**（08:22 复核定案，08:05 长名 `agents---项目铁律\` 已改回）。`.workbuddy\agents\` 是指向本文件夹的目录联接（Junction，非副本，改名后须重建），别在别处改副本；工程根 `AGENTS.md` 是跨工具指令入口，`CLAUDE.md` 是其一行导入薄入口。

| 文件 | 作用 |
|---|---|
| `AGENTS.md` | 所有 AI 工具的统一入口（读取顺序、文件清单、当前状态、变更纪律） |
| `00-项目铁律.md` | 本项目硬约束摘录（取证通道 / 写后回读 / 单位米 / 编译交付 / 禁忌） |
| 本文件 `CAPABILITY-MAP.md` | 理想 API → 工具 → 通道 → 级别 |
| `caxa3d-orchestrator.md` | 总控编排 |
| `caxa3d-{document,scene,catalog,intellishape,triball,feature,feedback,drawing}.md` | 八层子代理（含 26-11-13 新增图纸环境层） |

工程根 `caxa3dmcp\AGENTS.md` 是索引指针，把任何进入本工程的 AI 工具导向本文件夹；上级权威仍是 `C:\code\ICAPI\AGENTS.md`。
移植层工具直接在 `list_agent_layers` 的 `tracker` 分组里回显；若宿主不识别 front matter 的 `tools` 字段，按各文件正文的「工具白名单」执行。新增/改名工具必须同步 `AgentKit.cs` 的 `list_agent_layers` 与本表，避免文档级别与代码行为漂移。


## 图纸环境（CRX 桥）能力边界 — 26-11-13 08:55 探索取证 → **09:30 已落地（attempt）**

| 诉求 | 结论 | 依据 / 边界 |
|---|---|---|
| MCP 服务作为**文件桥请求侧**读写 `%TEMP%` 文本、传 CRX 命令表内具名命令 | ✅ **协议层可行**（零 COM / 零反射 / 零消息循环依赖） | `ExbBridge.cs:19-31,330-406` 请求/回包全为文本文件；正合 CRX 桥铁律「桥只通讯、决策在外」（06 第 13 节） |
| 把 CRX 桥改造成命名管道 + 工作线程**直连** MCP 服务 | ❌ **禁止** | 否决 85：桥里挂管道/线程/DB 探针 = 禁止的第三种形态 |
| 传**任意**图纸界面命令名当万能通道 | ❌ **不成立** | `EXEC_NATIVE` 只放行 `CX_DFT_`/`CX_`；名字必须取 `Mdl*Cmd.xml` 的 `<Identify>` 且 `CMDPRIORITY_MODAL`；表外名字恒 `runs=0` / `unknownCommand` |
| 旁听式接入既有桥（不新建、不接管） | ❌ **会互吃请求** | `seq <= g_lastSeq` 直接丢；本机 7 座桥前缀各异、各自独立 seq（坑 185 同族） |
| 冷态（marker 不在）投动手命令 | ❌ **陈旧请求** | 坑 146：用户一开图就补做一次；只 `PING` 例外 |

**命令面**（离线索引 `cmd_index_20261111_124939.csv`）：1758 条 / `CMDPRIORITY_MODAL` 1565 条 / `CX_DFT_` 前缀 1484 条；`CX_DFT_ViewUpdate`、`CX_DFT_ChangeLinks`、`CX_DFT_TITLEBLOCK_3DSET`、`CX_DFT_Update3DBom`、`CX_save`、`CX_DFT_ViewClose` 全部在表。命令表 15 条具名命令见 `ExbTrackerBridge.cpp:2202-2219`。

**本轮零写入探针**：`probe/crx-bridge-probe.ps1`（扫本机桥日志推 pid/token/冷热态、残留请求，读离线命令索引；`-SelfTest` 坏样本反证；不发 PING、不改 seq）。新增/落地任何 CRX 工具前先跑它，并把 `list_agent_layers` 与本表同步。


### 09:30 落地结果（用户指令：新建 `caxa3dmcp_` 唯一前缀 + 自带 CRX 第五登记点）

| 层 / 工具 | 通道 | 级别 / 判据 |
|---|---|---|
| `drawing` 层（第 9 层） | `DraftBridge.cs` 唯一请求侧 → `%TEMP%\caxa3dmcp_request.txt`（`seq` + `confirm` + `token`，写 `.tmp` 原子替换）→ CRX 100 ms `WM_TIMER` 取件 → 一条回包 | `attempt`（编译通过，未实机） |
| `draft_ping` | `PING` + marker 四态 + 令牌 pid 对应进程启动时间 | `ready = fresh && token`；冷态不写请求 |
| `draft_state` | `DFT_STATE` + `CMD_STATE`（CRX 侧新增两个只读命令） | `quiescent` 不是完成信号；`ended` ≠ 业务成功 |
| `draft_command` | 具名命令表 11 条（C# 白名单 + confirm 闸） | 表外不投递；四态 `OK/ERR/NOCRX/TIMEOUT/BUSY` |
| `draft_exec_native` | `EXEC_NATIVE` + 离线 `draft_commands.txt`（783 条 MODAL + `CX_DFT_`/`CX_`） | `OK` = 已投出；完成看 `CMD_STATE`；索引缺失记 `indexMissing` |
| `draft_warmup` | `OpenFile2` 专用空图纸 → 等 marker → `CLOSERAW` | 失败判死；自动预热四态进 trace |
| `draft_draw_rect` | `DRAW_RECT` → CRX 懒注册内部 `CXMCPDRAWRECT` + `sendStringToExecute` | 默认 `(0,0)-(100,100)` mm；`OK` = 已投出，`verified=false`；完成看 `CMD_STATE`，业务看 `RECT_STATE`。attempt（来源 `caxa3dexbtest` 2026-10-21 实机，本桥未实机） |
| `draft_rect_state` | `RECT_STATE`（块表迭代计数）+ `CMD_STATE` | `verified` 只由 `delta>=1 && appendEs=0 && verts=4` 判定；`count=-1` 记第三态。attempt |

**自带 CRX**：`Bridge\caxa3dmcp_bridge.cpp` → `caxa3dmcp_bridge.crx`（459264 B / SHA256 `266D4C51…1816`）。以 `caxa3dBatchMasterView` 桥接型为蓝本全量改前缀/模块名/GUID，补 `DFT_STATE` / `CMD_STATE` 与 reactor 的 `commandEnded/Cancelled/Failed` 留痕；三条静默丢请求路径已堵，卸载顺序 `StopBridge → WriteMarker(false) → 关日志 → 基类`。
**命令表（26-11-15 已 18 = 18，与 `DraftBridge.Verbs` 逐项/顺序一致）**：`PING / DFT_STATE / CMD_STATE / CLOSEDOC / CLOSERAW / SAVE / EXEC_NATIVE / TITLEBLOCK_3DSET / TB_ABORT / CONVERT_DXF / OPEN_EXB / DRAW_RECT / RECT_STATE / GETSCALE / LISTENTS / DELETE_QR / BITMAP_NATIVE / QR_STATE`；`EXEC_NATIVE` 仅放行 `CX_DFT_` / `CX_`。
**第五登记点**：`deploy.ps1 Register-CrxBridge` / `restore.ps1 Unregister-CrxBridge`，随包资源 `caxa3dmcp_bridge.crx`、`caxa3d图纸环境预热.exb`、`draft_commands.txt`、`caxa3dmcp二维码定位规则.txt`、`QRCoder.dll`；共享预热 exb 与 `QRCoder.dll` 卸载时保留不删（多插件共用），只有本插件独占的规则文件随卸载删除。
**自动预热**：`Addin.InitSelf` 排 `_host.ScheduleAutoWarmup`（3 s 空闲 tick），`DraftBridge.AutoWarmup` 静态一次、已热 SKIP、安全模式不跑；参考 `caxa3dBatchMaster\ViewBridge.cs` 的 `WarmUpDrawingEnv` 一条直线。
**离线验收**：`probe\crx-registration-selftest.ps1 -SelfTest` 六段全绿（首装 / 重复装逐字节幂等 / 清旧行 / 卸载回原文 / 重复卸载 / 坏样本拒绝），宿主真实 `CxAutorun.cx` 814 B 未被写入。
**仍未实机**：CRX 加载、`PING→PONG` 握手、`DFT_STATE` 回包、`EXEC_NATIVE` 完成判据、自动预热四态；安装后按第 10 节待实测 55~ 逐项验。

---

## 26-11-15 追加：工程图附加二维码（83 → 90 工具，`ToolsDraft.cs` + `QrKit.cs` + CRX 5 verbs）

> 移植自兄弟项目 `caxa3dBatchMaster`（其 `QrBatchTool.cs` 整链有实机 verified 证据）。**移植来源的实机 ≠ 本服务实机**：以下 7 个工具在本项目一律 `attempt`。
> 设计取舍：`caxa3dBatchMaster` 是 WinForms 单行 180 s 同步轮询；本服务是 stdio 同步管道，不能阻塞 MCP 主线程，故拆成 7 个两步式可组合工具，UI 代点器放后台线程。

| 工具 | 真实通道 | 级别 / 判据 |
|---|---|---|
| `draft_qr_png` | 本地 `QrKit.BuildQr` → `QRCoder.dll` 生成 PNG（`%TEMP%`），纯算法不碰 CAXA | **attempt**；回 `pngPath` / `side` / `pngPx`，失败第三态 |
| `draft_get_scale` | `GETSCALE`（CRX 只读）→ `QrKit.TryNormalizeDenom` | **attempt**；`scale` 2.5 / 0.4 两种口径归一为 `denom ≥ 1` |
| `draft_qr_plan` | 本地 `QrKit.LoadRules` + `TryMatch` 图幅匹配 + `QrKit.Plan` | **attempt**；插入点 = 1:1 规则坐标 × `denom`；`imgScale = (sizeMm × denom) ÷ (pngPx ÷ 96 × 25.4)` |
| `draft_list_ents` | `LISTENTS`（CRX 只读取证模型空间实体类名/计数） | **attempt**；不改模型；`erase` 返回值不可信 |
| `draft_delete_qr` | `DELETE_QR`（`confirm` 闸） | **attempt**；只在目标点先删旧码；`deleted` ≠ 插图成功 |
| `draft_insert_bitmap` | `BITMAP_NATIVE`（`confirm` 闸）+ `NativeBitmapInsert.Start` + `NativeQrDialog.ArmViewGuard(150s)` | **attempt**；回包**先落盘再投** `sendStringToExecute`；`OK` = 已投出，`verified=false`；不轮询、不阻塞主线程 |
| `draft_qr_state` | `QR_STATE`（CRX 实时重数 `*Model_Space` 实体计数） | **attempt**；`verified = r.Ok && delta > 0`，**唯一权威判据** |

**CRX 侧新增 5 verbs**：`GETSCALE` / `LISTENTS` / `DELETE_QR` / `BITMAP_NATIVE` / `QR_STATE`；`kCmdTable` 18 条与 `DraftBridge.Verbs` 18 条逐项、顺序一致、差集为空。`DELETE_QR` / `BITMAP_NATIVE` 进 `ConfirmVerbs`。`BITMAP_NATIVE` 命令名由 `PickHostQrCmd` 在宿主命令面里择优（目标 `CX_DFT_InsertBitmap`）。QR 状态用 `g_qrBefore/g_qrAfter/g_qrDelta` + `g_qrPending` 全局量，`delta>0` 才清 pending。
**随包资源**：`Lib\QRCoder.dll`（QRCoder 1.6.0 net40，随 csproj `Reference Private=True` 输出）、`caxa3dmcp二维码定位规则.txt`（A4 竖/横、A3 横、A2/A1/A0 的 1:1 基准坐标）；未带规则文件时 `QrKit` 内置播种兜底，不假成功。
**四件套硬校验**：`clrClass=1` / `progid=caxa3dmcp.Addin` / `token=56647e1eee90f6d4` / `sn -vf=0`；`deploy.ps1` / `restore.ps1` / `strip-manifest.ps1` 均 BOM + `parseErrors=0`；插件 90 = server `ToolDef` 90。
**仍未实机**：`GETSCALE` 回包解析、`BITMAP_NATIVE` 宿主命令可用性、UI 代点器在本服务进程内的时序、`QR_STATE delta>0`、`SAVE` 后盘上文件变化、重跑覆盖（固定名 PNG / 先删后插）。安装后按第 10 节待实测 93~ 逐项验。

---

## 26-11-21 追加：标准件库 XML/.cache/xlsx 纯文件工具（90 → 97 工具，`ToolsToolbox.cs` + 4 内核）

> 用户 @ 引用 `C:\code\ICAPI\caxa3dtoolboxmaker`，要求「更新 MCP、参考其增加工具」。该兄弟项目是独立 EXE（不进 CAXA 进程），内核已在 toolboxmaker 离屏自检验证（内核 implemented）；**作为本服务工具整链未实机 ⇒ 7 个一律 `attempt`**。
> 这些工具不碰 ICAPI/COM，纯文件处理；经桥 STA 回投执行只是复用统一通道。新增 category `toolbox`（第 10 个分类），不改 9 层 Agent 分层。

| 工具 | 真实通道 | 级别 / 判据 |
|---|---|---|
| `toolbox_resolve_paths` | `ToolboxPath.Resolve` 反查 + 三栏同目录/上一级推断 | **attempt**；只读；只认确实存在的同名文件，缺失即留空（第三态） |
| `toolbox_read_xml` | `XmlCore.Load` 字符串级保格式解析 | **attempt**；只读；格式不符/读不到返回第三态；`max_rows` 可限体积 |
| `toolbox_edit_xml` | `XmlCore.Save`（写前时间戳备份 + UTF-8 无 BOM + CRLF） | **attempt**；`confirm` 闸；`verified` = 写后回读目标值一致 + 字节变化；op=set_cell/add_row/del_row/add_col/del_col/set_cur/set_param；增删列联动参数表 |
| `toolbox_read_cache` | `CacheCore.Load`（XmlDocument 校验 + 保序 + 结构片段捕获） | **attempt**；只读；坏根/缺文件第三态 |
| `toolbox_edit_cache` | `CacheCore.Save`（保格式 round-trip + 时间戳备份） | **attempt**；`confirm` 闸；`verified` = 写后回读一致；不伪造 GUID（改它须自行同步族 XML） |
| `toolbox_export_excel` | `XlsxIO.WriteTemplate`（零依赖 OOXML） | **attempt**；B1/B2 取 `.cache` 对应行，定位不到/歧义拒绝；判据 = 磁盘 > 0 字节且回读行数一致 |
| `toolbox_import_excel` | `XlsxIO.ReadSheet` + `.cache` 强校验 + 列/行结构同步 | **attempt**；`confirm` 闸；B1/B2 与 `.cache` 不一致/无行/歧义拒绝；写后回读行数/列数一致才 verified |

**移植方式**：4 内核 `XmlCore.cs/CacheCore.cs/XlsxIO.cs/ToolboxPath.cs` 复制为 `Tb*.cs` 并改命名空间 `caxa3dmcp.toolbox`（字节级保留、仅改 namespace）；`caxa3dmcp.csproj` 加 `<Reference Include="WindowsBase" />`（`System.IO.Packaging` 所需，与 toolboxmaker 同款）。判成功口径 = 写后回读 `verified` + 磁盘副作用，不看「没抛异常」；第三态 = 定位不到/歧义/不一致明确拒绝并点名。

**硬校验**：两工程 0 错误（net48 仅 `mt.exe` 810100b3 预期重签提示）；`clrClass=1` / `progid=caxa3dmcp.Addin` / `token=56647e1eee90f6d4` / `sn -vf` 有效；插件工具 **97**（Tools*.cs 96 + AgentKit `list_agent_layers` 1）= server `ToolDef` **97**；7 个工具名 UTF-16LE 在交付 DLL 可查。net8 server 被 PID 4008 `.NET Host` 锁（MSB3027）⇒ 旁路编译 + 换出，未杀进程。

**仍未实机**：7 个工具经 MCP 整链调用、真机文件副作用、CAXA 重启后对编辑后 XML/.cache 的消费行为均未验；按铁律未自动安装、未重启 CAXA。安装后按第 10 节待实测 100~ 逐项验。
