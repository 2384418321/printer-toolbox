using System;
using System.IO;
using System.Text;

namespace PrinterIpFix
{
    /// <summary>
    /// 无头 IPP 自检：问设备能力、（可选）发一张 JPEG 测试页、把全过程写进日志文件。
    /// 用法：PrinterToolbox.exe --ipp-test &lt;打印机IP&gt; [--send] [--out 路径]
    /// **默认只探测、不打印** —— 打印会真耗纸，必须显式加 --send 才发。
    /// 退出码：0 = 探测成功（带 --send 时还要求设备受理作业）；1 = 失败。
    /// 这条路径不需要桌面，也不需要管理员权限。
    /// </summary>
    internal static class IppSelfTest
    {
        public static int Run(string ip, bool send)
        {
            if (ip == null || ip.Trim().Length == 0)
            {
                Console.Error.WriteLine("用法：--ipp-test <打印机IP> [--send] [--out <日志文件>]");
                Console.Error.WriteLine("  不加 --send 只做能力探测，不会打印任何纸。");
                return 2;
            }
            ip = ip.Trim();

            string outPath = "";
            string[] cands = new string[]
            {
                Path.Combine(Path.GetTempPath(), "PrinterToolbox"),
                AppDomain.CurrentDomain.BaseDirectory,
            };
            for (int i = 0; i < cands.Length; i++)
            {
                try
                {
                    Directory.CreateDirectory(cands[i]);
                    outPath = Path.Combine(cands[i],
                        "ipp-test-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                    break;
                }
                catch { outPath = ""; }
            }

            var sb = new StringBuilder();
            W(sb, "=== IPP 直发自检 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
            W(sb, "目标：" + ip);
            W(sb, "模式：" + (send ? "探测 + 发一张测试页（会耗 1 张纸）" : "只探测（不打印）"));

            int code = 1;
            try
            {
                var dev = new DeviceInfo();
                dev.Ip = ip;

                // 这一步验证 Get-Printer-Attributes 的解析（型号/序列号/状态/状态原因/格式表）
                bool probeOk = Ipp.Probe(dev, 8000);
                W(sb, "属性探测：" + (probeOk ? "成功" : "失败 —— " + dev.Error));
                W(sb, "  名称：" + dev.IppName);
                W(sb, "  型号：" + dev.Model);
                W(sb, "  设备ID：" + dev.DeviceId);
                W(sb, "  序列号：" + dev.Serial);
                W(sb, "  UUID：" + dev.Uuid);
                W(sb, "  IPP 状态：" + dev.IppState + "（3=空闲 4=打印中 5=停止）");
                W(sb, "  状态原因：" + dev.StateReasons);
                W(sb, "  支持格式：" + dev.FormatsText);

                bool probed = IppPrint.ProbeFormats(dev, 8000);
                W(sb, "格式探测：" + (probed ? "成功" : "失败") + " —— " + IppPrint.LastNote);
                W(sb, "支持 PDF：" + (dev.SupportsPdf ? "是" : "否")
                    + "；支持 JPEG：" + (dev.SupportsJpeg ? "是" : "否"));
                W(sb, "Windows 队列结论：" + IppPrint.WindowsQueueVerdict(dev));

                if (!send)
                {
                    W(sb, "直发：未执行（默认只探测）。确认要真打一张再加 --send。");
                    code = probeOk ? 0 : 1;
                }
                else
                {
                    bool ok = IppPrint.SendTestPage(dev, 10000);
                    W(sb, "直发结果：" + (ok ? "设备已受理" : "失败") + " —— " + IppPrint.LastNote);
                    code = ok ? 0 : 1;
                }
            }
            catch (Exception ex)
            {
                W(sb, "异常：" + ex.GetType().Name + " " + ex.Message);
                code = 1;
            }

            try
            {
                if (outPath.Length > 0)
                {
                    File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
                    Console.WriteLine("written " + outPath);
                }
                else Console.WriteLine("(日志目录不可写，只输出到控制台)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("写日志失败：" + ex.Message);
            }
            Console.WriteLine(sb.ToString());
            return code;
        }

        private static void W(StringBuilder sb, string s)
        {
            sb.AppendLine(s == null ? "" : s);
        }
    }
}
