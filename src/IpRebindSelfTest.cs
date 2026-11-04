using System;
using System.IO;
using System.Text;

namespace PrinterIpFix
{
    /// <summary>
    /// 无头校准：PrinterToolbox.exe --calibrate [范围] [--out 路径]
    /// 不弹窗、不问桌面，适合做成快捷方式/计划任务，开机跑一次就把换过 IP 的打印机指回去。
    /// </summary>
    internal static class IpRebindSelfTest
    {
        public static int Run(string[] args)
        {
            string range = "";
            string outPath = "";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--calibrate")
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-")) range = args[i + 1];
                }
                else if (args[i] == "--out")
                {
                    if (i + 1 < args.Length) outPath = args[i + 1];
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("打印机工具箱 · 无头 IP 校准");
            sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("范围：" + (range.Length == 0 ? "(空，只确认原地址)" : range));
            sb.AppendLine("绑定表：" + IpRebind.StatePath());
            sb.AppendLine("绑定表目录：" + IpRebind.StateDir());
            sb.AppendLine("");

            IpRebind.RebindResult r = null;
            int code = 0;
            try
            {
                r = IpRebind.Rebind(range, delegate(string m) { sb.AppendLine("[进度] " + m); });
                sb.AppendLine("");
                for (int i = 0; i < r.Steps.Count; i++) sb.AppendLine(r.Steps[i]);
                sb.AppendLine("");
                sb.AppendLine(IpRebind.LastNote);
                if (IpRebind.LastStorageError.Length > 0)
                    sb.AppendLine("落盘错误：" + IpRebind.LastStorageError);
                sb.AppendLine("落盘校验：" + (System.IO.File.Exists(IpRebind.StatePath()) ? "文件已生成" : "文件仍不存在"));
                if (r.Moved > 0) code = 0;
                else if (r.Failed > 0) code = 2;
            }
            catch (Exception ex)
            {
                sb.AppendLine("校准异常：" + ex.GetType().Name + " " + ex.Message);
                code = 3;
            }

            if (outPath.Length == 0) outPath = DefaultOut();
            try
            {
                string dir = Path.GetDirectoryName(outPath);
                if (dir != null && dir.Length > 0 && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
                Console.WriteLine(sb.ToString());
                Console.WriteLine("结果已写入：" + outPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine(sb.ToString());
                Console.WriteLine("(写文件失败：" + ex.Message + ")");
            }
            return code;
        }

        private static string DefaultOut()
        {
            string name = "calibrate-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
            string[] cands = new string[] {
                Path.Combine(Path.GetTempPath(), "PrinterToolbox"),
                Path.GetDirectoryName(Environment.GetCommandLineArgs()[0])
            };
            for (int i = 0; i < cands.Length; i++)
            {
                try
                {
                    if (cands[i] == null || cands[i].Length == 0) continue;
                    if (!Directory.Exists(cands[i])) Directory.CreateDirectory(cands[i]);
                    return Path.Combine(cands[i], name);
                }
                catch { }
            }
            return name;
        }
    }
}
