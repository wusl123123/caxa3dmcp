// CAXA 3D MCP 桥 —— 进程内入口（headless）。
// 本插件不带 UI 入口，加载即启动命名管道服务器 McpBridge，供外部 MCP 服务端进程（McpServer\）转发工具调用。
// 铁律：入口类 [ComVisible(true)]；程序集内其余类型一律 [ComVisible(false)]（见 AssemblyInfo）。
// 能力边界：设计环境建不了页签（规则第 36 节 A），本插件也不需要——它是后台服务。
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    [ComVisible(true)]
    [Guid("5B82F332-9F34-456D-ACB6-8CC02F616340")]   // 与 deploy/restore 的 $clsid 一致（new-addin.ps1 已生成）；禁止复用其它工程
    [ClassInterface(ClassInterfaceType.None)]
    [ProgId("caxa3dmcp.Addin")]
    public class Addin : IZAddinServer
    {
        private ZAddinSite _site;
        private MainForm _host;   // 隐藏窗体：CAXA 主线程(STA)消息泵载体，供管道线程回投 ICAPI 调用

        public IZBaseApp IronCADApp
        {
            get { return _site != null ? _site.Application : null; }
        }

        public void InitSelf(ZAddinSite piAddinSite)
        {
            Diagnostics.Reset();

            // 1) 缓存站点
            _site = piAddinSite;
            if (_site == null || _site.Application == null)
            {
                Diagnostics.Log("entry: site or Application is null，不启动 MCP 桥");
                return;
            }

            // 2) 安全模式二分开关（规则第 36 节 E）：同名空文件存在即本次不注册任何后台服务
            if (IsSafeMode())
            {
                Diagnostics.Log("safe mode: MCP 桥未启动（删除 caxa3dmcp.safe 恢复）");
                return;
            }

            // 3) 预创建隐藏主线程窗体（句柄在构造函数内 CreateHandle），用作 STA 封送载体；随后启动管道服务器
            try
            {
                _host = new MainForm(IronCADApp);
                McpBridge.Start(IronCADApp, _host);
                // 第五登记点：随包自带 caxa3dmcp_bridge.crx；进入插件后空闲 tick 自动预热一次图纸环境。
                // 安全模式已在上面 return，故此处不会排预热；失败判死、只记 trace，不弹框、不重试。
                _host.ScheduleAutoWarmup(IronCADApp);
                Diagnostics.Log("entry: OK MCP 桥已启动（命名管道 caxa3dmcp）；图纸环境唯一桥前缀 caxa3dmcp_，自动预热已排程");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("entry: MCP 桥启动失败 " + ex);
            }
        }

        /// <summary>DLL 同目录放空文件 caxa3dmcp.safe ⇒ 本次启动不注册任何后台服务；删文件即恢复。</summary>
        private static bool IsSafeMode()
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(Addin).Assembly.Location);
                if (dir == null) return false;
                return File.Exists(Path.Combine(dir, "caxa3dmcp.safe"));
            }
            catch { return false; }
        }

        public void DeInitSelf()
        {
            try { McpBridge.Stop(); } catch { }
            try { if (_host != null && !_host.IsDisposed) { _host.Close(); _host.Dispose(); } } catch { }
            _host = null;
            _site = null;
        }
    }
}
