// CAXA 3D MCP 服务端（独立 stdio 进程）。
//
// 架构（为什么必须独立进程 + 管道，而不是让 MCP 直接驱动 CAXA）：
//   CAXA 的 ICAPI 只能进程内调用（规则 01 架构 §3：跨进程 QI 到的是 IUnknown-only 短代理，函数指针不可调）。
//   因此"真干活"的代码在 IronCAD.exe 内的 caxa3dmcp 插件里（命名管道服务器 McpBridge）。
//   本进程只做 MCP 协议（stdio JSON-RPC）与管道转发：收到 tools/call → 写一行 JSON 到 \\.\pipe\caxa3dmcp
//   → 读回一行 JSON → 包成 MCP content 返回。本进程不引用任何 CAXA COM，可独立编译/运行。
//
// 运行：dotnet build -c Release 后用 run.bat（dotnet <dll>）。日志写入 %TEMP%\caxa3dmcp-server.log，不污染 stdout。

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace caxa3dmcp_server
{
    internal static class Program
    {
        private const string PipeName = "caxa3dmcp";
        private static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "caxa3dmcp-server.log");

        private static void Log(string s)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\r\n"); }
            catch { }
        }

        private static int Main()
        {
            Log("MCP server 启动（stdio 传输）");
            var stdin = Console.OpenStandardInput();
            var stdout = Console.OpenStandardOutput();
            using var reader = new StreamReader(stdin, new UTF8Encoding(false));
            using var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string resp = Handle(line);
                if (resp != null) writer.WriteLine(resp);
            }
            Log("MCP server 退出（stdin 关闭）");
            return 0;
        }

        private static string Handle(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("method", out var methodEl)) return null;
                string method = methodEl.GetString();

                int? id = null;
                if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                    id = idEl.GetInt32();

                // 通知不回复
                if (method != null && method.StartsWith("notifications/", StringComparison.Ordinal)) return null;

                switch (method)
                {
                    case "initialize":
                        return JsonRpc(id, new Dictionary<string, object>
                        {
                            ["protocolVersion"] = "2024-11-05",
                            ["capabilities"] = new Dictionary<string, object> { ["tools"] = new Dictionary<string, object>() },
                            ["serverInfo"] = new Dictionary<string, object> { ["name"] = "caxa3dmcp", ["version"] = "0.2.0" }
                        });

                    case "tools/list":
                        return HandleToolsList(id);

                    case "tools/call":
                        string name = root.GetProperty("params").GetProperty("name").GetString();
                        JsonElement args = root.GetProperty("params").TryGetProperty("arguments", out var a)
                            ? a : default;
                        return HandleToolCall(id, name, args);

                    case "ping":
                        return JsonRpc(id, new Dictionary<string, object>());

                    default:
                        return ErrRpc(id, "unsupported method: " + method);
                }
            }
            catch (Exception ex)
            {
                Log("Handle 异常: " + ex);
                return ErrRpc(null, "handle error: " + ex.Message);
            }
        }

        private static string HandleToolCall(int? id, string name, JsonElement args)
        {
            string resp = CallAddin(name, args);
            if (resp == null)
            {
                return ToolResult(id,
                    "无法连接到 CAXA 3D：请确认 IronCAD.exe 已启动、caxa3dmcp 插件已通过 安装.bat 部署并加载" +
                    "（看 %TEMP%\\caxa3dmcp_trace.log），且当前有打开的文档。", true);
            }
            try
            {
                using var d = JsonDocument.Parse(resp);
                var r = d.RootElement;
                bool ok = r.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                if (!ok)
                {
                    string err = r.TryGetProperty("error", out var eEl) ? eEl.GetString() : "未知错误";
                    return ToolResult(id, err, true);
                }
                string text = r.TryGetProperty("result", out var resEl) ? resEl.GetRawText() : "{}";
                // 视觉闭环：若结果含图片文件（如 get_view），把它作为 image 内容块一并回传
                string imgPath = null;
                if (resEl.ValueKind == JsonValueKind.Object &&
                    resEl.TryGetProperty("filePath", out var fpEl) && fpEl.ValueKind == JsonValueKind.String)
                {
                    string p = fpEl.GetString();
                    if (p != null && File.Exists(p) &&
                        (p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                         p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                         p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                         p.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)))
                        imgPath = p;
                }
                return ToolResult(id, text, imgPath, false);
            }
            catch (Exception ex)
            {
                return ToolResult(id, "解析插件返回失败：" + ex.Message, true);
            }
        }

        // 向进程内插件管道发送一次请求并读回一行响应；连不上返回 null
        private static string CallAddin(string tool, JsonElement args)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
                pipe.Connect(3000);
                using var w = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true);
                // ★ 必须用带 leaveOpen=true 的 5 参数重载：using 逆序释放时若 reader 先关管道，
                //   writer 后续 Dispose/Flush 会抛 "Cannot access a closed pipe"（实机 2026-11-12）。
                using var r = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, true);

                string paramsJson = (args.ValueKind == JsonValueKind.Undefined)
                    ? "{}"
                    : args.GetRawText();
                // ★ tool 必须放第一个键：兼容已部署的旧版插件桥（其 JSON 解析器只取首个键）。
                //   新版桥解析器已修复，键顺序无关；这样新 server 对旧/新插件都能工作。
                string req = "{\"tool\":" + JsonLiteral(tool) + ",\"params\":" + paramsJson + ",\"id\":1}";
                w.WriteLine(req);
                w.Flush();
                return r.ReadLine();
            }
            catch (Exception ex)
            {
                Log("CallAddin 连接失败: " + ex.Message);
                return null;
            }
        }

        // ---------------- 动态工具清单 ----------------
        // 对应 caxa3dmytoolsaddin 的「运行时扫描 plugins 目录」：工具清单不写死在 server，
        // 每次 tools/list 都向进程内桥的 describe_tools 元工具查询。插件端新增工具，server 无需改动。
        // 桥连不上时降级给最小清单，保证 MCP 客户端仍能完成 initialize / tools/list 握手。
        private static string HandleToolsList(int? id)
        {
            string resp = CallAddin("describe_tools", default);
            if (resp != null)
            {
                try
                {
                    using var d = JsonDocument.Parse(resp);
                    var r = d.RootElement;
                    bool ok = r.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                    if (ok && r.TryGetProperty("result", out var resEl) &&
                        resEl.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
                    {
                        Log("tools/list: 动态清单 " + toolsEl.GetArrayLength() + " 个工具");
                        // ★ 不要手工拼 GetRawText()：插件返回的中文是未转义的原始 UTF-8 字符，
                        //   手工拼接会绕过 JsonSerializer 的 \uXXXX 转义。一旦客户端或终端按
                        //   非 UTF-8（GBK/OEM）解码 stdout，字节流里可能撞出引号而破坏 JSON。
                        //   统一走 JsonSerializer 序列化 ⇒ stdout 全 ASCII，任何解码方式都安全。
                        //   （实机 2026-11-12：PowerShell 管道下 tools/list 中文乱码且 ConvertFrom-Json 失败）
                        var tools = new List<object>();
                        foreach (var t in toolsEl.EnumerateArray())
                            tools.Add(JsonSerializer.Deserialize<object>(t.GetRawText()));
                        return JsonRpc(id, new Dictionary<string, object> { ["tools"] = tools });
                    }
                }
                catch (Exception ex) { Log("tools/list 动态清单解析失败: " + ex.Message); }
            }
            Log("tools/list: 桥不可达，降级为最小清单");
            return JsonRpc(id, new Dictionary<string, object> { ["tools"] = FallbackTools() });
        }

        private static List<object> FallbackTools()
        {
            return new List<object>
            {
                ToolDef("ping", "[system] 连通性测试：返回 CAXA 3D 应用版本、API 版本与已注册工具数。"),
                ToolDef("get_capabilities", "[system] 能力协商：CAXA 版本/API 版本/已注册工具与分类/默认单位/活动文档状态。"),
                ToolDef("get_active_document", "[system] 返回当前活动文档的名称与类型。"),
                ToolDef("get_scene_info", "[system] 返回当前 3D 场景的顶层元素数量与文档名。"),
                ToolDef("list_top_elements", "[query] 列出当前场景顶层元素（id/名称/类型）。"),
                ToolDef("get_element_tree", "[query] 递归输出元素树（可限深度与节点数）。"),
                ToolDef("get_element_info", "[query] 读取指定元素的详细属性信息。"),
                ToolDef("create_box", "[model] 在当前 3D 场景生成长方体/方块，默认 10×10×10 mm。"),
                ToolDef("create_cylinder", "[model] 生成圆柱体，默认直径/高 10 mm。"),
                ToolDef("create_sphere", "[model] 生成球体，默认直径 10 mm。"),
                ToolDef("get_bounding_box", "[query] 返回首个零件的局部包围盒（米）。"),
                ToolDef("get_custom_props", "[property] 读取元素共享自定义属性表。"),
                ToolDef("set_custom_prop", "[property] 写入元素共享自定义属性（严格校验共享表）。"),
                ToolDef("get_bom_properties", "[property] 读取零件 BOM 属性。"),
                ToolDef("set_element_name", "[element] 重命名元素。"),
                ToolDef("delete_element", "[element] 删除元素（需 confirm=true）。"),
                ToolDef("save_document", "[system] 保存/另存当前文档。"),
                ToolDef("get_view", "[view] 捕获当前 3D 场景视图为图片，返回路径（视觉闭环）。"),
                ToolDef("batch_exec", "[system] 事务化批量执行多个工具调用（一次 STA 回投）。"),
                ToolDef("regenerate_parts", "[model] 重建场景零件。"),
                ToolDef("create_tube", "[model] 生成圆管（外圆柱＋内孔除料 Z_SUBTRACT），外径/壁厚/高，默认 20/2/30 mm。"),
                ToolDef("create_cone", "[model] 生成圆锥（CreateCone(21)），底面半径/高/半角，默认 10/20 mm、20°。"),
                ToolDef("create_torus", "[model] 生成圆环（CreateTorus(23)），大半径/小半径，默认 20/5 mm。"),
                ToolDef("get_part_type", "[model] 回读零件种类掩码 GetPartType(51)：0x10=创新零件、0x20=工程零件。"),
                ToolDef("set_sizebox_handles", "[model] 包围盒四勾选 + Reset + L/W/H 回读（tracker 基体模型建立共用链）。"),
                ToolDef("get_sizebox", "[model] 只读回读包围盒驱动尺寸 L/W/H(mm) 与四个显示柄状态。"),
                // ---- 26-11-12 20:25 轮子插件映射：caxa3dsetMaterial ----
                ToolDef("get_material_info", "[material] 读取零件材料名/密度/体积/计算与指定质量/表面颜色（COLORREF→#RRGGBB），逐项第三态。"),
                ToolDef("set_material", "[material] 设置材料名（可选密度），先名称后密度，写后回读校验。"),
                ToolDef("set_color", "[material] 设置零件表面颜色 IZSurfaceFinish.Color，支持 #RRGGBB / r,g,b，写后回读。"),
                ToolDef("list_material_library", "[material] 只读扫描 CAXA 安装目录下材料库相关 XML，返回候选路径。"),
                // ---- caxa3dTreeBom / caxa3dButton.BomTool ----
                ToolDef("get_bom_tree", "[bom] 递归构建装配 BOM 树（层级/类型/计入BOM/BOM设置/可选共享属性）。"),
                ToolDef("export_bom_csv", "[bom] 把 BOM 树导出为 UTF-8 BOM CSV 到 %TEMP%，返回路径与行数。"),
                ToolDef("export_self_bom", "[bom] IZSceneElement.ExportSelfBOM 5-out 属性快照（RCW），表头取值兜底通道。"),
                ToolDef("set_included_in_bom", "[bom] 设置零件是否计入 BOM 并回读校验。"),
                // ---- caxa3dButton 调试类 / caxa3dsheetmaster / caxa3dBatchMaster / caxa3dAttributeManageraddin ----
                ToolDef("get_body_info", "[inspect] 内核几何：体类型/流形性/面边顶点数/面 Id/包围盒与紧致盒(mm)/逐面包围盒。"),
                ToolDef("get_feature_info", "[inspect] 枚举零件特征并回读 OperationType（增料/除料）。"),
                ToolDef("set_operation_type", "[inspect] 切换特征增料/除料，Update 后回读校验。"),
                ToolDef("set_kernel_type", "[inspect] 切换零件内核 Parasolid/ACIS，写后回读校验。"),
                ToolDef("get_sheet_metal_info", "[inspect] 钣金件 K 因子/板厚/最小折弯半径/GetStockProperties 7-out。"),
                ToolDef("calc_k_factor", "[inspect] 纯计算 K 因子与折弯补偿 BA，不读写 CAXA。"),
                ToolDef("batch_set_custom_props", "[inspect] 批量写/清空共享自定义属性（空值 RemoveByName、新属性 AddCustomProp 回退、逐项回读）。"),
                // ---- 26-11-13 七层 Agent 工具集（22 个，插件端 41 → 63）----
                ToolDef("list_agent_layers", "[system] 只读：七层 Agent 的工具清单与能力级别（implemented/attempt/capability）。"),
                ToolDef("new_document", "[document] 新建 3D 场景文档 OpenNewDoc(Z_SCENE)，回读活动文档作判据。"),
                ToolDef("open_document", "[document] OpenFile2 打开已有文档，回读活动文档名作判据。"),
                ToolDef("export_step", "[document] IZPart.Export(Z_MODEL_STEP) 导出 STEP，磁盘文件>0 字节才算成功。"),
                ToolDef("export_parasolid", "[document] IZPart.Export(Z_MODEL_X_T) 导出 Parasolid，磁盘文件>0 字节才算成功。"),
                ToolDef("add_scene", "[scene] 新建 3D 场景文档（new_document 的场景层别名）。"),
                ToolDef("add_assembly", "[scene] IZSceneDoc.CreateAssembly，判据=顶层元素+1。"),
                ToolDef("add_part", "[scene] IZSceneDoc.CreatePart，判据=顶层元素+1。"),
                ToolDef("list_catalogs", "[scene] 枚举 IZCatalogMgr 目录与条目，产出 catalog_id / item_id。"),
                ToolDef("get_active_scene_graph", "[scene] 活动场景图（顶层+一层子节点，带节点预算）。"),
                ToolDef("drag_intellishape_from_catalog", "[catalog] ★ CAXA 独有：InsertElement/DropObject 把目录图素放进场景。"),
                ToolDef("set_shape_handle", "[intellishape] IZSizeBox L/W/H 写入 → Update → 三值回读校验。"),
                ToolDef("edit_cross_section", "[intellishape] 能力协商：解析草图命令但关联通道未实证，不改模型、不假成功。"),
                ToolDef("set_triball", "[triball] PositionTransform 矩阵平移/旋转，GetTranslation 回读校验。"),
                ToolDef("position_by_triball", "[triball] 仅平移（支持 relative），写后回读 translation。"),
                ToolDef("extrude_sketch", "[feature] CreateProfile + CreateExtrudeFeature，判据=特征数+1。"),
                ToolDef("revolve", "[feature] 能力协商：旋转特征的轴线关联未实证，不改模型。"),
                ToolDef("sweep", "[feature] 能力协商：扫掠路径构造未实证，不改模型。"),
                ToolDef("shell", "[feature] 能力协商：抽壳开放面 Id 数组形态未实证，不改模型。"),
                ToolDef("mirror", "[feature] MirrorAboutPlane + ApplyTransform，回读 Mirrored 标志。"),
                ToolDef("capture_viewport", "[feedback] 捕获当前 3D 视口为 PNG（get_view 别名），回传 image 内容块。"),
                ToolDef("measure_distance", "[feedback] 两点欧氏距离或两零件局部包围盒中心距离。"),
                // ---- 26-11-13 从 caxa3dtracker 移植的四类能力（6 个，插件端 63 → 69）----
                ToolDef("get_parameter_table", "[probe] 参数表抓取：递归元素树读 ParameterMgr→IZParameter，米/毫米双值，读不到记第三态。"),
                ToolDef("get_feature_bounding_boxes", "[probe] 特征/零件/装配级包围盒，局部盒可换算世界 AABB，附工程/创新零件位掩码。"),
                ToolDef("create_sketch", "[sketch] XY 平面 2D 草图（CreateProfile+CreateLine），rect/polyline，判据=GetCurvesCount 回读；不投 UI 命令。"),
                ToolDef("move_component", "[transform] 按 name/id 移动零部件到 xyz(mm)，三分量写后回读才算成功。"),
                ToolDef("move_selected_components", "[transform] 批量移动当前选中的零部件（四通道选择集去重）。"),
                ToolDef("get_selection", "[transform] 只读列出当前选择集与 SelectionUIContext，供移动/特征工具定位目标。"),
                // ---- 26-11-13 图纸环境唯一桥 caxa3dmcp_（5 个，插件端 69 → 74）----
                ToolDef("draft_ping", "[draft] 唯一桥握手：marker 新鲜度（按 CRX 令牌 pid 判进程启动时间）、PING 令牌、命令表、第五登记点状态。"),
                ToolDef("draft_state", "[draft] 只读 DFT_STATE（文档数/活动文档/Draft 窗口数）+ CMD_STATE（最近结束命令，ended 不等于业务成功）。"),
                ToolDef("draft_command", "[draft] 向唯一桥发具名命令，C# 白名单 + confirm 闸；表外动作不投递，四态回包。"),
                ToolDef("draft_exec_native", "[draft] 投递图纸环境原生命令（CX_DFT_/CX_ 前缀 + 离线 MODAL 索引）；OK 仅代表已投出，完成看 CMD_STATE。"),
                ToolDef("draft_warmup", "[draft] 图纸环境预热：OpenFile2 专用空图纸带起 CRX → PING → CLOSERAW 不保存关载体；失败判死。"),
                // ---- 26-11-13 16:xx 从 caxa3dexbtest 移植：图纸模型空间矩形（2 个，插件端 74 → 76）----
                ToolDef("draft_draw_rect", "[draft] 在图纸环境 *Model_Space 画矩形（默认原点 100×100 mm）；OK 只代表已投出，业务成功须另查 draft_rect_state。"),
                ToolDef("draft_rect_state", "[draft] 矩形权威回读：*Model_Space 块表计数 before/after/delta 与最近一次 append 结果；verified 只由增量判定。"),
                // ---- 26-11-15 学习 caxa3dBatchMaster「工程图附加二维码」移植（7 个，插件端 83 → 90）----
                ToolDef("draft_qr_png", "[draft] QRCoder 生成二维码 PNG（磁盘 >0 字节判据；带时间戳唯一名，96DPI 基准）。"),
                ToolDef("draft_get_scale", "[draft] GETSCALE 只读：当前图纸比例与图幅物理宽高，归一为 1:N 分母。"),
                ToolDef("draft_qr_plan", "[draft] 纯计算：定位规则匹配 + 比例补偿，产出插入点与宿主比例因子，不改模型。"),
                ToolDef("draft_list_ents", "[draft] LISTENTS 只读：模型空间实体类名分布与 imageRef 计数（先删后插取证）。"),
                ToolDef("draft_delete_qr", "[draft] DELETE_QR：按目标点 ±1mm 精确删除旧二维码图片实体；需 confirm=true。"),
                ToolDef("draft_insert_bitmap", "[draft] BITMAP_NATIVE：投宿主 CX_DFT_InsertBitmap 并启动后台对话框代点；OK 只代表已投出。"),
                ToolDef("draft_qr_state", "[draft] QR_STATE 权威回读：模型空间实体 delta>0 才算插图成功，附后台代点状态。"),
                // ---- 26-11-14 建模顺序号 + 普通零件两条建模路线（7 个，插件端 76 → 83）----
                ToolDef("get_build_seq", "[build] 只读扫描全场特征顺序号（N-名称）：最大号/下一号/乱序/缺号/重号，重排前先调。"),
                ToolDef("number_feature", "[build] 给零件内指定特征（含钣金特征、草图轮廓）按建模顺序编号命名，写后回读。"),
                ToolDef("renumber_features", "[build] 把零件内全部特征按设计树当前顺序重编号 1-…、2-…，逐项写后回读。"),
                ToolDef("reorder_features_by_seq", "[build] 创新零件模式纠错：按特征名顺序号调 ReorderFeature 重排，回读序号序列非递减才算 verified；需 confirm=true。"),
                ToolDef("boolean_parts", "[build] 多零件模式：把工具零件布尔进目标零件（IZPart.Boolean(13)），支持 all_other_parts 合并全场；需 confirm=true。"),
                ToolDef("set_part_activation", "[activation] 激活/取消激活零件（WM_COMMAND 53111/53227）：先 53227 归零、再 53111 定向让起点已知；posted 仅入队，verified 恒 false；需 confirm=true。"),
                ToolDef("get_part_activation_state", "[activation] 只读第三态：TLB 无激活状态读 API ⇒ state 恒 unknown；返回选择集、六状态探针与本会话命令轨迹。"),
                // ---- 26-11-21 从 caxa3dtoolboxmaker 移植：标准件库 XML/.cache/xlsx 纯文件工具（7 个，插件端 90 → 97）----
                ToolDef("toolbox_resolve_paths", "[toolbox] 只读：反查 CAXA ToolBox 根目录，按给定 xml/ics/.cache 推断同目录同名 sibling 与上一级 .cache。"),
                ToolDef("toolbox_read_xml", "[toolbox] 只读：保格式解析族 XML，返回族信息/参数表/规格表行键与行数据（可限行数）。"),
                ToolDef("toolbox_edit_xml", "[toolbox] 写：保格式编辑族 XML（改单元格/增删行列/改族信息/改参数），需 confirm=true，写后回读 verified。"),
                ToolDef("toolbox_read_cache", "[toolbox] 只读：解析 ToolBox 类目级 .cache 索引，返回条目与属性并集列。"),
                ToolDef("toolbox_edit_cache", "[toolbox] 写：保格式编辑 .cache（改属性/增删条目），需 confirm=true，写后回读 verified。"),
                ToolDef("toolbox_export_excel", "[toolbox] 写：族 XML 规格表导出 CAXA 模板格式 xlsx，B1/B2 取 .cache 对应行，磁盘>0字节判据。"),
                ToolDef("toolbox_import_excel", "[toolbox] 写：导入 xlsx 覆盖规格表，强校验 B1/B2 与 .cache 一致，按表头同步列/行，需 confirm=true。")
            };
        }

        private static Dictionary<string, object> ToolDef(string name, string desc)
        {
            return new Dictionary<string, object>
            {
                ["name"] = name,
                ["description"] = desc,
                ["inputSchema"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>(),
                    ["required"] = new List<object>()
                }
            };
        }

        private static string JsonRpc(int? id, object result)
        {
            var m = new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = (object)(id ?? 0),
                ["result"] = result
            };
            return JsonSerializer.Serialize(m);
        }

        private static string ErrRpc(int? id, string message)
        {
            var m = new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = (object)(id ?? 0),
                ["error"] = new Dictionary<string, object> { ["code"] = -32601, ["message"] = message }
            };
            return JsonSerializer.Serialize(m);
        }

        private static string ToolResult(int? id, string text, bool isError)
        {
            var content = new List<object>
            {
                new Dictionary<string, object> { ["type"] = "text", ["text"] = text }
            };
            var m = new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = (object)(id ?? 0),
                ["result"] = new Dictionary<string, object>
                {
                    ["content"] = content,
                    ["isError"] = isError
                }
            };
            return JsonSerializer.Serialize(m);
        }

        private static string ToolResult(int? id, string text, string imagePath, bool isError)
        {
            var content = new List<object>
            {
                new Dictionary<string, object> { ["type"] = "text", ["text"] = text }
            };
            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(imagePath);
                    string b64 = Convert.ToBase64String(bytes);
                    string mime = imagePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
                                : imagePath.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ? "image/bmp"
                                : "image/jpeg";
                    content.Add(new Dictionary<string, object> { ["type"] = "image", ["data"] = b64, ["mimeType"] = mime });
                }
                catch (Exception ex) { Log("image 内容块编码失败: " + ex.Message); }
            }
            var m = new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = (object)(id ?? 0),
                ["result"] = new Dictionary<string, object>
                {
                    ["content"] = content,
                    ["isError"] = isError
                }
            };
            return JsonSerializer.Serialize(m);
        }

        private static string JsonLiteral(string s)
        {
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\');
                sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
