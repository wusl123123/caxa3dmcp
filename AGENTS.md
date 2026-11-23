# AGENTS.md — caxa3dmcp 项目指令入口

> 本文件遵循 **AGENTS.md 开放标准**（Linux Foundation / Agentic AI Foundation），位于工程根，供 Codex、Cursor、GitHub Copilot、Gemini CLI/Jules、Aider、Zed、Warp、Devin/Windsurf、Junie、Claude Code v2.1.277+ 等自动读取。
> **不覆盖上级权威**：`C:\code\ICAPI\AGENTS.md` 仍是工作区总索引，优先级 `00-caxa3d-csharp-二次开发规则\`（唯一权威）> `caxa3dmcp-项目总结\` > `caxa3d-csharp-addin` 技能 > 本文件。冲突以规则文件为准。
> 就近原则：嵌套 `AGENTS.md` 只在工具进入该子目录时加载；本根文件是**所有工具无条件会读到的那一份**，因此这里直接放核心约束，不依赖对方是否展开指针。

## 1. 先读什么（按顺序）

1. `C:\code\ICAPI\AGENTS.md` —— 工作区总索引（TLB 优先、通道阶梯、证据优先级、共享资源、禁忌）。
2. `agents\AGENTS.md` —— agents 统一入口（读取顺序、12 个文件清单、9 层清单、变更纪律）。
3. `agents\00-项目铁律.md` —— 本项目硬约束摘录（21 条，逐条指向上级规则目录）。
4. `agents\CAPABILITY-MAP.md` —— 理想 API → 工具名 → COM 通道 → `implemented`/`attempt`/`capability` 级别。
5. `caxa3dmcp-项目总结\` —— 本项目唯一写入面（新坑第 4 节、实证 3/6、否决 9、探针 7、待实测 10、里程碑 5）。

## 2. 核心约束（即使不展开上面的文件也必须看到）

1. **判成功只认写后回读 `verified` 与权威副作用**，不看 API 返回值 / S_OK；导出看磁盘文件 > 0 字节、拖放看顶层元素 +1 且名称可回读。三级口径 `implemented`（有实证）/ `attempt`（未实机定案）/ `capability`（只返回能力协商第三态，**绝不假成功、不改模型**）。
2. **单位是米**：`IZParameter.Value`、包围盒 min/max、`PositionTransformComponentValue` 三分量都是米；界面与入参 mm（×0.001）。
3. **编译后绝不自动安装**：由用户自行运行 `bin\x64\Release\net48\安装.bat` 并重启 CAXA；未实机的工具不得宣称可用。
4. **写侧边界**：子项目轮不改上级规则目录、不改技能两份副本、不改上级 `AGENTS.md`；新经验只写 `caxa3dmcp-项目总结\`。
5. **agents 单一权威源 = `agents\`**；`.workbuddy\agents\` 是指向它的目录联接（Junction，非副本），不要在别处放第二份定义。
6. **图纸环境唯一桥 = `caxa3dmcp_`**：`caxa3dmcp_bridge.crx`（桥接型，第五登记点 `CxAutorun.cx`）是本项目与图纸环境通讯的唯一通道；所有 MCP 工具经 `DraftBridge.cs` 走它，禁止旁听/复用本机其它 7 座桥（单写者 `seq` 会互吃请求），禁止把桥改成命名管道（否决 85）。冷态只准 `PING`；`EXEC_NATIVE` 的 `OK` 只代表已投出，完成须另查 `CMD_STATE`。

## 3. 目录约定

| 路径 | 角色 |
|---|---|
| `agents\` | **后续所有 agents 定义的唯一权威源**（12 个 md：总控 + 8 层（含图纸环境 `drawing`）+ `AGENTS.md` + `00-项目铁律.md` + `CAPABILITY-MAP.md`） |
| `agents\AGENTS.md` | 子目录内的统一入口；工具进入 `agents\` 时才自动加载，进入工程根时靠本文件指路 |
| `.workbuddy\agents\` | Junction → `agents\`，供 WorkBuddy 子代理发现；非副本，勿在此处单独改 |
| `CLAUDE.md` | 薄入口，仅 `@AGENTS.md` 一行导入（Claude Code 旧版 / 存在 CLAUDE.md 时的兼容），**无内容副本** |
| `caxa3dmcp-项目总结\` | 十节总结，本项目唯一写入面 |

## 4. 跨工具识别矩阵（26-11-13 复核）

| 机制 | 谁自动识别 | 现状 |
|---|---|---|
| **项目根 `AGENTS.md`**（规则/铁律） | Codex、Cursor、GitHub Copilot、Gemini CLI/Jules、Aider、Zed、Warp、Devin/Windsurf、Junie、opencode 等 20+ 工具原生读取；Claude Code **v2.1.277+** 在无 `CLAUDE.md` 时直读 | ✅ 本文件已就位；当前目录树无 `CLAUDE.md`/`CLAUDE.local.md` 干扰 |
| **`CLAUDE.md` 导入** | Claude Code 全版本（`@AGENTS.md` 展开） | ✅ 已建薄入口，覆盖旧版与「将来新增 CLAUDE.md」的情形 |
| **上级 `C:\code\ICAPI\AGENTS.md`** | 支持层级合并的工具（Codex 从 Git 根向下合并、就近优先；Claude Code 读工作目录及以上所有 `AGENTS.md`） | ✅ 存在；本文件明确声明不覆盖它 |
| **子代理自动发现** | 各工具**私有目录**：WorkBuddy `.workbuddy\agents\`、Claude Code `.claude\agents\*.md`、Cursor `.cursor\agents\*.md`、Copilot `.github\agents\*.agent.md`、Codex `.codex\agents\*.toml`（仅用户级）、Gemini `.gemini\agents\*.md` | ⚠️ **没有任何工具把工程根 `agents\` 当子代理目录**；`agents\caxa3d-*.md` 目前只有 WorkBuddy 经 Junction 自动发现，其它工具按本文件指针**读取为规则文档**，不会自动注册成子代理 |
| **子目录嵌套 `AGENTS.md`** | 工具进入该目录编辑/读取文件时才加载（就近优先） | ⚠️ 仅靠目录名不会被扫描；靠本文件的明确路径指路 |

> 结论：**「所有 AI 工具都能读到项目规则与铁律」= 成立；「所有 AI 工具都自动把 `caxa3d-*.md` 注册成子代理」= 不成立**。若要后者，需按各工具私有目录格式另行接入（格式不同，Codex 用 toml、Copilot 用 `.agent.md`，不能直接用同名 md）。

## 5. 一句话状态

双层 MCP 服务（net8 stdio → 命名管道 → IronCAD.exe 进程内 net48 插件），**97 个工具 / 9 层 + toolbox 分类**（`drawing` 层 14 个含 26-11-15 的 7 个二维码工具；26-11-21 新增 7 个 `toolbox_*` 纯文件工具，category `toolbox` 不计入 9 层）；多轮新增工具**未在 CAXA 内实机运行**，一律按 `attempt` / `capability` 第三态口径使用；toolbox 文件工具写类须 `confirm=true`、写后回读 `verified` 才算成功，Excel B1/B2 与 .cache 不一致一律拒绝；图纸环境模型空间矩形走 `draft_draw_rect`（投递）→ `draft_rect_state`（权威计数回读）两步；26-11-14 起零件内建模特征名一律按建模顺序编号 `N-名称`（如 `1-长方体`，含钣金特征），出错用 `get_build_seq` → `reorder_features_by_seq` 按号恢复；多零件模式用 `boolean_parts`，激活零件用 `set_part_activation` 的「53227 归零 → 53111 定向」协议（TLB 无激活状态读 API，`get_part_activation_state` 恒回第三态）；二维码链路唯一权威判据 = `draft_qr_state` 的 `delta>0`（插图实体增量）+ `SAVE` 后盘上文件变化，`OK` / `commandEnded` / 代点器 `Found` / `DELETE_QR.deleted` 都不是成功信号；编译产物由用户自行运行 `安装.bat` 部署。
