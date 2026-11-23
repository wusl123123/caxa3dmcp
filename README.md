# caxa3dmcp

CAXA 3D（IronCAD 内核）二次开发的 MCP（Model Context Protocol）服务：让 AI 工具通过标准 MCP 协议直接操控 CAXA 3D 的 **3D 设计环境**与**图纸环境**。

## 架构

双层 MCP 服务，两条通道互不混用：

```
MCP client
  └─ McpServer (net8.0 stdio, caxa3dmcpserver.exe)
       └─ 命名管道 \\.\pipe\caxa3dmcp
            └─ IronCAD.exe 进程内 net48 插件 caxa3dmcp.dll
                 ├─ 3D 设计环境：进程内 ICAPI COM（document / scene / catalog /
                 │   intellishape / triball / feature / feedback / tracker 共 8 层）
                 └─ 图纸环境：DraftBridge.cs → %TEMP%\caxa3dmcp_* 文件桥
                     → caxa3dmcp_bridge.crx（第五登记点，drawing 层 14 个 draft_* 工具，含 7 个二维码工具）
```

共 **97 个工具 / 9 层 agents + toolbox 分类**（插件 `IMcpTool` 实现类 97 = stdio server `ToolDef` 降级清单 97；`agents/` 目录含能力映射 `CAPABILITY-MAP.md` 与各层子代理定义）。其中 7 个 `toolbox_*` 为纯文件工具（标准件库 XML/.cache/xlsx 保格式编辑，移植自 `caxa3dtoolboxmaker`），写类须 `confirm=true`。

26-11-15 追加「工程图附加二维码」链路（移植自 `caxa3dBatchMaster`，**attempt / 未实机**）：`draft_qr_png` 生成 PNG → `draft_get_scale` 读图纸比例 → `draft_qr_plan` 定位规则 × 比例分母 → `draft_list_ents` 只读取证 → `draft_delete_qr` 先删旧码 → `draft_insert_bitmap` 投宿主 `CX_DFT_InsertBitmap` + 后台 UI 代点 → `draft_qr_state` 查模型空间实体 `delta>0`（**唯一权威判据**）。随包资源 `QRCoder.dll` / `caxa3dmcp二维码定位规则.txt`。

## 编译

- C# 插件：net48 / x64（`caxa3dmcp.csproj`），需要 `Lib/ICApiAddin.snk` 强名称密钥（**不入库，请自备并放到 Lib/ 下**）。
- stdio server：net8.0（`McpServer/caxa3dmcpserver.csproj`）。
- CRX 桥：VS v143 / x64（`Bridge/caxa3dmcp_bridge.vcxproj`，或直接跑 `Bridge/build.ps1`）。

## 安装

编译产物目录下运行 `安装.bat`（含第五登记点：拷贝 `caxa3dmcp_bridge.crx` 到 `<CAXA bin>\CAXADraft\bin\` 并在 `CxAutorun.cx` 登记），然后**重启 CAXA**。卸载运行 `卸载.bat`（先摘登记行再删 `.crx`）。

## 能力口径

工具按三级标注：`implemented`（有实机实证）/ `attempt`（编译通过、未实机定案）/ `capability`（能力协商第三态）。判成功只认写后回读与权威副作用，不以 API 返回值为准。

## License

[Apache License 2.0](LICENSE)
