using System;
using System.Collections.Generic;
using System.Text;

namespace PrinterIpFix
{
    /// <summary>
    /// 环境自检：验证「建端口 / 改队列端口 / 删临时对象」这三件事在当前机器上真的能跑通。
    /// 只在命令行加 --self-test 时触发，结果写文件（GUI 版没有控制台）。
    /// </summary>
    internal static class SelfTest
    {
        private const string TempPrinter = "ZZT_SelfTest";
        private const string TestIp = "203.0.113.77";
        private const string Port1 = "IP_203.0.113.77";
        private const string Port2 = "IP_203.0.113.78";

        public static int Run(string[] args)
        {
            string logPath = args.Length > 1
                ? args[1]
                : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "printerfix-selftest.log");

            var sb = new StringBuilder();
            int rc = 0;
            string o;

            sb.AppendLine("=== PrinterIpFix 环境自检 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
            sb.AppendLine("admin=" + PrinterSystem.IsAdmin());
            string who;
            PrinterSystem.RunHidden("cmd.exe", "/c whoami /priv & whoami /groups", 15000, out who);
            sb.AppendLine("--- 本进程看到的令牌 ---");
            sb.AppendLine(who);

            try
            {
                Cleanup();

                sb.AppendLine();
                sb.AppendLine("[1] 建端口 " + Port1 + " -> 203.0.113.77:9100");
                bool c1 = PrinterSystem.CreateTcpPort(Port1, TestIp, 9100);
                sb.AppendLine("    结果=" + c1 + "  回读存在=" + PrinterSystem.PortExists(Port1));
                foreach (string line in PrinterSystem.LastPortLog) sb.AppendLine("    " + line);
                if (!PrinterSystem.PortExists(Port1)) { rc = 1; }

                sb.AppendLine();
                sb.AppendLine("[2] 建临时队列 " + TempPrinter);
                int rcAdd = PrinterSystem.RunPowerShell(
                    "Add-Printer -Name '" + TempPrinter + "' -DriverName 'Microsoft IPP Class Driver' "
                    + "-PortName '" + Port1 + "' -ErrorAction Stop", out o);
                sb.AppendLine("    Add-Printer exit=" + rcAdd + " " + Shorten(o));
                string cur = PrinterSystem.GetPrinterPort(TempPrinter);
                sb.AppendLine("    队列端口回读=" + (cur.Length == 0 ? "(没有这个队列)" : cur));
                if (cur.Length == 0) { rc = 2; }

                if (cur.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("[3] 换端口到 " + Port2);
                    bool c2 = PrinterSystem.CreateTcpPort(Port2, "203.0.113.78", 9100);
                    sb.AppendLine("    建第二个端口=" + c2 + " 回读存在=" + PrinterSystem.PortExists(Port2));
                    try
                    {
                        PrinterSystem.SetPrinterPort(TempPrinter, Port2);
                        string back = PrinterSystem.GetPrinterPort(TempPrinter);
                        bool ok = string.Compare(back, Port2, StringComparison.OrdinalIgnoreCase) == 0;
                        sb.AppendLine("    SetPrinterPort 回读=" + back + (ok ? "  OK" : "  <<< FAIL"));
                        if (!ok) rc = 3;
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("    SetPrinterPort 抛异常: " + ex.Message);
                        rc = 3;
                    }
                }

                sb.AppendLine();
                sb.AppendLine("[4] 本机真实队列（只读）");
                foreach (PrinterInfo p in PrinterSystem.GetPrinters())
                    sb.AppendLine("    " + p.Name + "  port=" + p.PortName + (p.IsDefault ? "  [默认]" : ""));
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION " + ex.GetType().Name + ": " + ex.Message);
                sb.AppendLine(ex.StackTrace);
                rc = 9;
            }
            finally
            {
                Cleanup();
                sb.AppendLine();
                sb.AppendLine("清理后: 队列存在=" + (PrinterSystem.GetPrinterPort(TempPrinter).Length > 0)
                    + " 端口存在=" + PrinterSystem.PortExists(Port1) + "/" + PrinterSystem.PortExists(Port2));
                sb.AppendLine(rc == 0 ? "RESULT: PASS" : "RESULT: FAIL(" + rc + ")");
                try { System.IO.File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8); } catch { }
            }

            return rc;
        }

        private static void Cleanup()
        {
            string o;
            PrinterSystem.RunPowerShell("Remove-Printer -Name '" + TempPrinter
                + "' -ErrorAction SilentlyContinue", out o);
            PrinterSystem.RunPowerShell("Remove-PrinterPort -Name '" + Port1
                + "' -ErrorAction SilentlyContinue; Remove-PrinterPort -Name '" + Port2
                + "' -ErrorAction SilentlyContinue", out o);
        }

        private static string Shorten(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 160 ? s.Substring(0, 160) + "..." : s;
        }
    }
}
