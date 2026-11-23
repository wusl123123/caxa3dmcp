// CAXA 3D MCP 桥：进程内（IronCAD.exe 内）命名管道服务器 —— 可插拔工具集版。
//
// 架构（参考 caxa3dmytoolsaddin 的 Host + IPlugin + PluginManager）：
//   Host  = 本桥（只做管道、协议、STA 封送，不含任何业务逻辑）
//   契约  = IMcpTool（McpTool.cs）
//   插件  = 各 Tools*.cs 里的工具类，由 McpToolRegistry 运行时反射扫描注册
//   ★ 新增工具只需新增一个 IMcpTool 实现类；本文件与 stdio server 都不用改。
//
// 设计要点（规则硬约束）：
//   1) ICAPI 只能进程内调用（架构规则 01 §3：跨进程 QI 到 IUnknown-only 短代理，函数指针不可调），
//      所以 MCP 服务端是独立 stdio 进程（McpServer\），经本管道把请求转发到本进程内的 ICAPI。
//   2) 所有 ICAPI 调用都经 _host.Invoke 封送回 CAXA 主线程(STA)执行（规则第 8 节 STA 铁律）。
//   3) 管道协议：每行一条 JSON = {"id":N,"tool":"...","params":{...}}；
//      响应 {"id":N,"ok":true,"result":{...}} 或 {"id":N,"ok":false,"error":"..."}。
//   4) describe_tools 是纯元数据元工具，不走 STA（tools/list 高频，避免被 CAXA 主线程阻塞）。
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    [ComVisible(false)]
    internal static class McpBridge
    {
        private const string PipeName = "caxa3dmcp";
        private static IZBaseApp _app;
        private static Control _host;
        private static Thread _listener;
        private static volatile bool _running;

        public static void Start(IZBaseApp app, Control host)
        {
            _app = app;
            _host = host;
            _running = true;
            try { Diagnostics.Log("McpBridge: 预注册工具集，共 " + McpToolRegistry.Count + " 个工具"); } catch { }
            _listener = new Thread(ListenLoop) { IsBackground = true, Name = "McpPipeListener" };
            _listener.Start();
            Diagnostics.Log("McpBridge: 命名管道服务器已启动 \\\\.\\pipe\\" + PipeName);
        }

        public static void Stop()
        {
            _running = false;
            Diagnostics.Log("McpBridge: 已请求停止监听");
        }

        private static void ListenLoop()
        {
            while (_running)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(
                        PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096);
                    pipe.WaitForConnection();
                    Diagnostics.Log("McpBridge: MCP 服务端已连接");
                    HandleClient(pipe);
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("McpBridge 监听异常: " + ex.Message);
                    try { Thread.Sleep(300); } catch { }
                }
                finally
                {
                    try { if (pipe != null) { pipe.Close(); pipe.Dispose(); } } catch { }
                }
            }
        }

        private static void HandleClient(NamedPipeServerStream pipe)
        {
            using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, true))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true))
            {
                string line;
                while (_running && (line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string resp = Dispatch(line);
                    writer.WriteLine(resp);
                    writer.Flush();
                }
            }
        }

        private static string Dispatch(string requestLine)
        {
            object idObj = 0;
            try
            {
                JValue req = JsonRpc.Parse(requestLine);
                JValue idv = req["id"];
                idObj = idv.Kind == JKind.Number ? (object)idv.AsInt()
                        : (idv.Kind == JKind.String ? idv.Str : 0);
                string tool = req["tool"].AsString() ?? "";
                JValue @params = req["params"];
                if (@params == null || @params.IsNull) @params = new JValue { Kind = JKind.Object, Obj = new Dictionary<string, JValue>() };

                // 元工具：纯元数据，不碰 COM，不需要 STA 封送
                if (tool == "describe_tools")
                {
                    return Reply(idObj, true, new Dictionary<string, object>
                    {
                        ["tools"] = McpToolRegistry.Describe(),
                        ["toolCount"] = McpToolRegistry.Count,
                        ["applicationVersion"] = Convert.ToString(_app == null ? "" : _app.ApplicationVersion)
                    });
                }

                IMcpTool t = McpToolRegistry.Find(tool);
                if (t == null)
                {
                    return Reply(idObj, false, null,
                        "unknown tool: " + tool + "（可用工具请用 describe_tools 查询）");
                }

                // 回投 CAXA 主线程(STA)执行 ICAPI（规则第 8 节）
                object result = _host.Invoke(new Func<object>(() =>
                {
                    var ctx = new McpToolContext { App = _app };
                    try { return t.Execute(ctx, @params); }
                    catch (Exception ex)
                    {
                        Diagnostics.Log("工具 " + t.Name + " 执行异常: " + ex);
                        return new Dictionary<string, object> { ["ok"] = false, ["error"] = t.Name + " 执行异常: " + ex.Message };
                    }
                }));

                return Reply(idObj, true, result);
            }
            catch (Exception ex)
            {
                string msg = (ex.InnerException != null) ? ex.InnerException.Message : ex.Message;
                Diagnostics.Log("McpBridge Dispatch 异常: " + msg);
                return Reply(idObj, false, null, msg);
            }
        }

        private static string Reply(object id, bool ok, object result, string error = null)
        {
            var d = new Dictionary<string, object> { ["id"] = id, ["ok"] = ok };
            if (ok) d["result"] = result;
            else d["error"] = error ?? "unknown error";
            return JsonRpc.Serialize(d);
        }
    }
}
