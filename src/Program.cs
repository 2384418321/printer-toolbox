using System;
using System.Windows.Forms;

namespace PrinterIpFix
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 布局自检：把窗口在屏幕外建一次，导坐标（不需要可视桌面）
            if (args.Length > 0 && args[0] == "--dump-ui")
            {
                Environment.ExitCode = UiDump.Run(args);
                return;
            }

            // 无头自检：--ipp-test <ip> 走 IPP 直发通道（不问桌面、不弹窗），便于自动化验证
            // 注意：默认只探测能力，不打印。要真打一张必须再加 --send（打印耗纸，不能默认做）。
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--ipp-test")
                {
                    string ip = (i + 1 < args.Length) ? args[i + 1] : "";
                    bool send = false;
                    for (int k = 0; k < args.Length; k++) if (args[k] == "--send") send = true;
                    Environment.ExitCode = IppSelfTest.Run(ip, send);
                    return;
                }
            }            // 无头校准：--calibrate [范围] [--out 路径]，不弹窗，适合开机计划任务
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--calibrate")
                {
                    Environment.ExitCode = IpRebindSelfTest.Run(args);
                    return;
                }
            }

            // 无头驱动自检：--drvcheck <ip> [--capture]
            // 默认只问设备支持哪些语言、列本机驱动、打印判定矩阵，绝不打印；
            // 加 --capture 才真建临时队列打测试页抓驱动输出语言（费纸）。
            // --drvsearch 也必须走无头分支：单独给 --drvsearch（不带 --drvcheck）时
            // 之前会落到 Application.Run(new ToolboxForm())，命令行看不到任何输出、只能等 GUI，
            // 从外面看就是「卡死两分钟」（实测 exit 124）。
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--drvcheck" || args[i] == "--drvsearch")
                {
                    Environment.ExitCode = DriverSelfTest.Run(args);
                    return;
                }
            }

            // 提权重开自己时走无界面路径：--apply-shared <ids> / --revert-shared / --diagnose-shared
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--apply-shared" || a == "--revert-shared" || a == "--diagnose-shared")
                {
                    Environment.ExitCode = SharedFixes.RunHeadless(args);
                    return;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.ToString(), "未处理的错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                MessageBox.Show(ex == null ? "(未知错误)" : ex.ToString(), "严重错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Application.Run(new ToolboxForm());
        }
    }
}
