// 隐藏窗体：仅作为 CAXA 主线程(STA)的消息泵载体，供后台命名管道线程把 ICAPI 调用封送回主线程执行。
// 不显示任何 UI（本插件是后台 MCP 桥，无界面入口）。句柄在构造函数内预创建（CreateHandle 是 protected，只能在此处调）。
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dmcp
{
    [ComVisible(false)]
    public class MainForm : Form
    {
        private readonly IZBaseApp _app;

        public MainForm(IZBaseApp app)
        {
            _app = app;

            Visible = false;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new System.Drawing.Point(-32000, -32000);   // 移出可见区

            // 句柄必须在 InitSelf 线程预创建，否则工作线程第一次 Invoke 时跨线程建窗口会与 CAXA 主线程互等死锁
            if (!IsHandleCreated) CreateHandle();
        }

        /// <summary>把 ICAPI 调用封送回 CAXA 主线程(STA)执行（规则第 8 节 STA 铁律：禁止在工作线程碰 COM）。</summary>
        public T InvokeOnMainThread<T>(Func<T> fn)
        {
            if (InvokeRequired) return (T)Invoke(fn);
            return fn();
        }

        public void InvokeOnMainThread(Action fn)
        {
            if (InvokeRequired) Invoke(fn);
            else fn();
        }

        /// <summary>规则 06 第 16 节：带预热能力的插件「进入即自动预热一次」。
        /// 本插件无可见窗口 ⇒ 触发点 = 入口注册完成后的空闲消息泵 tick（3 秒 WinForms Timer，
        /// 避开 CAXA 启动高峰），禁止塞进 InitSelf；只跑一次，静态闸门在 DraftBridge.AutoWarmup 内。</summary>
        public void ScheduleAutoWarmup(IZBaseApp app)
        {
            var timer = new Timer { Interval = 3000 };
            timer.Tick += delegate(object s, EventArgs e)
            {
                timer.Stop();
                timer.Dispose();
                try { DraftBridge.AutoWarmup(app); }
                catch (Exception ex) { Diagnostics.Log("auto-warm | 调度异常 " + ex); }
            };
            timer.Start();
        }
    }
}
