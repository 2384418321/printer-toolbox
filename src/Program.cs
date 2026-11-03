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
