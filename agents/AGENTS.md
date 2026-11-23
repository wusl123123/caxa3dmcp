# AGENTS.md — caxa3dmcp / agents 统一入口

> **本文件是给所有 AI 工具读的入口，不是新规则。** 权威口径仍在 `C:\code\ICAPI\AGENTS.md` 与 `00-caxa3d-csharp-二次开发规则\README.md`；本文件只做索引与本项目落地摘要，冲突一律以规则文件为准。
> 26-11-13 08:22 复核定案：**本文件夹 `C:\code\ICAPI\caxa3dmcp\agents\` 是后续所有 agents 定义的唯一权威源**（08:05 曾用长名 `agents---项目铁律\`，现已改回），其他位置不得再放副本。

---

## 1. 给谁读 / 怎么读

| 读者 | 入口 | 说明 |
|---|---|---|
| 任何进入本工程根目录的 AI 工具 | `C:\code\ICAPI\caxa3dmcp\AGENTS.md` | **遵循 AGENTS.md 开放标准的根入口**，20+ 工具原生自动读取；内嵌核心约束与跨工具识别矩阵，不依赖是否展开指针 |
| Claude Code（全版本） | `CLAUDE.md` → `@AGENTS.md` | 薄入口，一行导入，无内容副本；覆盖 v2.1.277 以下或无法直读 AGENTS.md 的会话 |
| 直接浏览本文件夹的 AI 工具 | 本文件 `AGENTS.md` | ⚠️ **子目录 AGENTS.md 只在工具进入 `agents\` 编辑/读取文件时才自动加载**（就近优先）；从工程根进来靠根 `AGENTS.md` 的明确路径指路 |
| WorkBuddy 子代理发现 | `.workbuddy\agents\` | **目录联接（Junction）指向本文件夹**，不是副本；目标改名后必须重建，否则断链 |
| 人类 / 规则核查 | `00-项目铁律.md`、`CAPABILITY-MAP.md` | 铁律摘录、能力映射 |

**读取顺序（不可跳）**：根 `AGENTS.md` → 本文件 → `00-项目铁律.md` → `CAPABILITY-MAP.md` → 对应层 `caxa3d-*.md` → `caxa3dmcp-项目总结\` 对应章节 → 上级规则目录。
上级权威：`C:\code\ICAPI\AGENTS.md`（总索引）→ `C:\code\ICAPI\00-caxa3d-csharp-二次开发规则\README.md`（v003.058，唯一权威）→ `C:\code\ICAPI\caxa3dmcp\caxa3dmcp-项目总结\`（本项目唯一写入面）→ `C:\code\ICAPI\caxa3d-csharp-addin\SKILL.md`。

## 2. 文件清单（12 个）

| 文件 | 作用 |
|---|---|
| `AGENTS.md` | 本文件，子目录统一入口 |
| `00-项目铁律.md` | 本项目必须遵守的硬约束摘录（含来源指向） |
| `CAPABILITY-MAP.md` | 理想 API → 工具名 → COM 通道 → 级别（`implemented`/`attempt`/`capability`） |
| `caxa3d-orchestrator.md` | 总控编排：开场四步、分层派发、判据收口、失败收口 |
| `caxa3d-document.md` | 文档层：新建/打开/保存/STEP/Parasolid |
| `caxa3d-scene.md` | 场景层：装配/零件/设计树/目录 |
| `caxa3d-catalog.md` | Catalog 拖放（本服务最独特的能力） |
| `caxa3d-intellishape.md` | 尺寸手柄 / 截面编辑 |
| `caxa3d-triball.md` | 定位与旋转 |
| `caxa3d-feature.md` | 拉伸/旋转/扫掠/抽壳/镜像 |
| `caxa3d-feedback.md` | 截图、包围盒、测距、视觉确认 |
| `caxa3d-drawing.md` | **图纸环境层（26-11-13 新增）**：`caxa3dmcp_` 唯一桥、第五登记点、自动预热、四态回包 |

> 每个子代理文件带 front matter（`name` / `description` / `tools` 白名单）。宿主若不识别 `tools` 字段，按正文「工具白名单」执行。新增层必须同时更新 `CAPABILITY-MAP.md` 与 `AgentKit.cs` 的 `list_agent_layers` 数组，**避免文档级别与代码行为漂移**。

## 3. 跨工具识别的真实边界（08:22 复核，别把两件事混为一谈）

| 机制 | 结论 |
|---|---|
| **读规则 / 铁律** | ✅ 成立。根 `AGENTS.md` 是开放标准，Codex / Cursor / GitHub Copilot / Gemini CLI / Aider / Zed / Warp / Devin / Junie 等原生读取；Claude Code v2.1.277+ 无 `CLAUDE.md` 时直读，旧版经 `CLAUDE.md` 的 `@AGENTS.md` 导入。当前目录树无其它 `CLAUDE.md`/`CLAUDE.local.md` 干扰（除新建的薄入口）。 |
| **自动发现全部子代理** | ❌ 不成立。**没有任何工具把工程根 `agents\` 当子代理目录**。各工具私有目录：WorkBuddy `.workbuddy\agents\`、Claude Code `.claude\agents\*.md`、Cursor `.cursor\agents\*.md`、Copilot `.github\agents\*.agent.md`、Codex `.codex\agents\*.toml`（用户级）、Gemini `.gemini\agents\*.md`；格式也不同（toml / `.agent.md`），不能直接用同名 md。目前只有 WorkBuddy 经 Junction 自动发现；其它工具按根 `AGENTS.md` 指针把 `caxa3d-*.md` **读作规则文档**。 |
| **嵌套 AGENTS.md** | ⚠️ 只在工具进入该子目录时加载，仅靠目录名不会被扫描 —— 所以根文件必须自带核心约束，不能只写「请去读另一个文件」。 |

若要让某个工具也自动注册子代理，按它的私有目录格式单独接入，并用 Junction / 导入保持零副本；不要复制多份 md。

## 4. 当前状态（读之前先认这个数）

- 架构：`McpServer`（net8.0 stdio）→ 命名管道 → `IronCAD.exe` 进程内插件（net48/x64），全部工具在宿主线程执行。
- 工具总数 **97**（插件 `IMcpTool` 实现类 97 = stdio server `ToolDef` 降级清单 97），分 **9 层**（`drawing` 层 14 个工具）+ 第 10 个分类 `toolbox`；`list_agent_layers` 可回读。26-11-14 新增 7 个工具（`ToolsBuildMode.cs`）落在两个新 category `build` / `activation`，分层清单仍挂 feature 层；26-11-15 新增 7 个工程图二维码工具（`ToolsDraft.cs` + `QrKit.cs` + `NativeBitmapInsert.cs` + `NativeQrDialog.cs` + CRX 5 verbs），仍挂 `drawing` 层，层数不变；26-11-21 新增 7 个 `toolbox_*` 纯文件工具（`ToolsToolbox.cs` + 4 个 `Tb*.cs` 内核），category `toolbox` 不计入 9 层，写类须 `confirm=true`。
- 服务侧新增工具：`ToolsDocument.cs` / `ToolsCatalog.cs` / `ToolsFeature.cs` / `ToolsTrackerPort.cs` / `AgentKit.cs` / `DraftBridge.cs` + `ToolsDraft.cs` / **`ToolsBuildMode.cs`**；CRX 桥 `Bridge\caxa3dmcp_bridge.cpp` → `caxa3dmcp_bridge.crx`。
- ⚠️ **97 个工具中，多轮新增的工具均未在 CAXA 内实机运行过**（含本组 14 个图纸工具与整座 CRX 桥、两个矩形工具、7 个二维码工具、7 个 build/activation 工具，以及 26-11-21 新增的 7 个 toolbox 纯文件工具）。二维码在 `caxa3dBatchMaster`、toolbox 文件内核在 `caxa3dtoolboxmaker` 均有实机/离屏证据，但**移植来源的实机 ≠ 本服务实机**，一律仍按 `attempt`。编译通过 ≠ 运行时可调；`attempt` 级一律靠写后回读与 `attempts` 判定，`capability` 级只返回能力协商第三态，不改模型、不假成功。
- 交付产物：`bin\x64\Release\net48\caxa3dmcp.dll`、`McpServer\bin\Release\net8.0\caxa3dmcpserver.exe`、`bin\x64\Release\net48\安装.bat`（**由用户自己运行，绝不自动安装**）。

## 5. 三条最容易被忽略的铁律

1. **只认权威副作用信号**：写类工具判成功只看写后回读 `verified=true`（导出看磁盘文件 > 0 字节、拖放看顶层元素 +1 且名称可回读），不看 API 是否抛异常、不看返回 S_OK。
2. **单位是米**：参数 Value、包围盒 min/max、`PositionTransformComponentValue` 三分量全部是米；界面与入参是 mm（×0.001 换算）。tracker 实机曾把 20 当米写，UI 显示 20000mm。
3. **不做能力之外的活**：UI 交互（智能标注 50731 / 草图编辑态 50283、50123 / 窗口子类化 / `DoEvents` 观察）依赖宿主消息泵，本 stdio 同步服务不阻塞主线程、不改窗口过程、COM 层也无「创建草图尺寸」写接口 —— 明确列为未移植，禁止编造几何结果。**26-11-15 边界澄清**：二维码轮引入的 `NativeBitmapInsert` / `NativeQrDialog` 是**后台线程上的白名单对话框代点**，不改窗口过程、不在 MCP 主线程 `DoEvents`、不做 180 s 同步轮询；与本条「不挂窗口过程 / 不阻塞主线程」不冲突，但同样一律 `attempt`，完成只认 `draft_qr_state delta>0`。

## 6. 变更纪律

- 新增/修改 agents 定义 → **只改本文件夹**；`.workbuddy\agents\` 是联接，会自动同步，不要在那儿改副本。**目标目录改名后必须重建 Junction**（08:22 曾因指向已不存在的 `agents---项目铁律\` 断链）。
- 实机每定案一个工具 → 同步 `CAPABILITY-MAP.md` 的 `level` 与 `AgentKit.cs` 的 `levels` 数组，并在返回值 `evidence` 里把「tracker 侧证据」与「本服务侧证据」分开写。
- 子项目功能轮 / 总结轮**不改**上级规则目录、不改技能两份副本、不改上级 `AGENTS.md`；新坑写本项目总结第 4 节，实证 3/6、否决 9、探针 7、待实测 10、里程碑 5。
- 编译后不自动安装；改了 `AgentKit.cs` 等 `.cs` 必须两工程 Release 重编并做强签名 / SxS 清单 / 工具计数三项硬校验。


## 7. 图纸环境唯一桥（26-11-13 09:30 落地，`attempt`）

| 项 | 口径 |
|---|---|
| 唯一前缀 | `caxa3dmcp_`（`%TEMP%` 五个文件 + `caxa3dmcp_bridge.crx`）；本机另有 7 座桥，**不得旁听或复用** |
| 桥形态 | 桥接型（零 UI / 零命令注册 / 不 pin / `CxAutorun.cx` 自动加载）；CRX 只按命令表执行并必落一条回包，决策全在 C# |
| 第五登记点 | `.crx` 落 `CAXADraft\bin` + `CxAutorun.cx` 在 `</Modules>` 之前插行；`deploy.ps1` / `restore.ps1` 自带写前 XML 校验、写后回读、失败回滚、幂等、双向清旧行 |
| 自动预热 | 进入插件后空闲 tick（3 s WinForms Timer）一次性：`auto-warm` 四态进 trace；已热 SKIP、安全模式不跑、失败判死 |
| 新鲜度判据 | 用 `PING` 令牌里的 pid 所对应的进程启动时间判 marker，**不假设 CRX 与 add-in 同进程**（上轮拓扑仍 UNKNOWN） |
| 命令完成 | `EXEC_NATIVE` 的 `OK` = 已投出；`draft_state` / `CMD_STATE` 的 `ended=1` 只证明命令结束，业务成功另看权威副作用 |
| 离线验收 | `probe\crx-registration-selftest.ps1`（从部署原文切函数块、6 段演练 + 坏样本反证，已在 %TEMP% 副本全绿） |
