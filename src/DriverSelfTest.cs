using System;
using System.Collections.Generic;
using System.Text;

namespace PrinterIpFix
{
    /// <summary>
    /// 无头驱动自检：--drvcheck &lt;ip&gt; [--capture]
    ///
    /// 不加 --capture 时**绝不出纸**：只问设备自己支持哪些页面语言（@PJL INFO CONFIG），
    /// 再把本机已装驱动列出来，并用已知的两种典型输出（PDF / PCLXL）演示判定矩阵。
    /// 加 --capture 才会真建临时队列、真打测试页去抓驱动实际输出语言（费纸，得用户自己决定）。
    /// </summary>
    internal static class DriverSelfTest
    {
        public static int Run(string[] args)
        {
            string ip = "";
            bool capture = false;
            string search = "";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--drvcheck")
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ip = args[i + 1];
                }
                if (args[i] == "--capture") capture = true;
                if (args[i] == "--drvsearch")
                {
                    // --drvsearch 后面的整串都当关键词（型号里常带空格）
                    StringBuilder kb = new StringBuilder();
                    for (int k = i + 1; k < args.Length; k++)
                    {
                        if (args[k].StartsWith("--")) break;
                        if (kb.Length > 0) kb.Append(' ');
                        kb.Append(args[k]);
                    }
                    search = kb.ToString();
                }
            }

            if (search.Length > 0) return SearchOnly(search);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("设备：" + (ip.Length > 0 ? ip : "（未指定，只做离线判定）"));
            sb.AppendLine("模式：" + (capture ? "抓包（会真打测试页）" : "只探测（不打印）"));
            sb.AppendLine();

            int code = 0;

            // ---- 1) 设备自报语言 ----
            if (ip.Length > 0)
            {
                List<string> langs = new List<string>();
                string raw = "";
                try { langs = DriverCompat.DeviceLanguages(ip, 6000, out raw); }
                catch (Exception ex) { sb.AppendLine("设备语言探测异常：" + ex.Message); }

                if (langs.Count == 0)
                {
                    sb.AppendLine("设备语言：没有回应（@PJL INFO CONFIG 无输出）。");
                    if (DriverCompat.LastNote.Length > 0) sb.AppendLine("  " + DriverCompat.LastNote);
                    sb.AppendLine("  => 这台机器不认 PJL，RAW 队列没有意义，只能走 IPP 直发。");
                }
                else
                {
                    sb.AppendLine("设备语言（" + langs.Count + " 种）：" + string.Join(" / ", langs.ToArray()));
                    bool pdf = DriverCompat.Compatible(langs, "PDF");
                    sb.AppendLine("  设备接受 PDF = " + (pdf ? "是" : "否")
                        + "；接受 PCLXL = " + (DriverCompat.Compatible(langs, "PCLXL") ? "是" : "否")
                        + "；接受 PCL3GUI = " + (DriverCompat.Compatible(langs, "PCL3GUI") ? "是" : "否"));
                    if (!pdf)
                        sb.AppendLine("  => 这台设备不收 PDF。微软 IPP 类驱动经标准 TCP/IP 端口只会发 PDF，"
                            + "所以那条队列会「Windows 报已打印、机器不出纸」。");
                }

                string st = "";
                try { st = DriverCompat.DeviceStatusLine(ip, 4000); } catch { }
                if (st.Length > 0) sb.AppendLine("设备状态行：" + st.Replace("\r", " ").Replace("\n", " ").Trim());
                sb.AppendLine();

                // ---- 2) 可选抓包 ----
                if (capture && langs.Count > 0)
                {
                    List<string> drv = DriverCompat.InstalledDrivers();
                    sb.AppendLine("抓包模式：逐个用本机驱动打一张测试页抓输出语言（会出纸）。");
                    for (int i = 0; i < drv.Count; i++)
                    {
                        string up = drv[i].ToUpperInvariant();
                        if (up.IndexOf("PRINT TO PDF") >= 0 || up.IndexOf("XPS") >= 0 || up.IndexOf("FAX") >= 0
                            || up.IndexOf("ONENOTE") >= 0 || up.IndexOf("ADOBE PDF") >= 0 || up.IndexOf("ONEPDF") >= 0)
                        {
                            sb.AppendLine("  跳过虚拟驱动：" + drv[i]);
                            continue;
                        }
                        string lang = "";
                        string detail = "";
                        try { lang = DriverCompat.CaptureDriverLanguage(drv[i], 20000, out detail); }
                        catch (Exception ex) { detail = ex.Message; }
                        bool ok = lang.Length > 0 && DriverCompat.Compatible(langs, lang);
                        sb.AppendLine("  " + (ok ? "[可用] " : "[不行] ") + drv[i]
                            + " → 输出 " + (lang.Length == 0 ? "（没抓到）" : lang)
                            + (ok ? "" : "  " + detail));
                    }
                    sb.AppendLine();
                }
                else if (langs.Count > 0)
                {
                    sb.AppendLine("未加 --capture：不抓驱动输出（避免出纸）。");
                    sb.AppendLine("要看某个驱动到底吐什么语言，再带 --capture 跑一次。");
                    sb.AppendLine();
                }
            }

            // ---- 3) 本机驱动清单 ----
            List<string> all = DriverCompat.InstalledDrivers();
            sb.AppendLine("本机已装驱动（" + all.Count + " 条）：");
            for (int i = 0; i < all.Count; i++) sb.AppendLine("  - " + all[i]);
            sb.AppendLine();

            // ---- 4) 判定矩阵（用两种真实抓到的典型输出做演示，不打印） ----
            string[] samples = new string[] { "PDF", "PCLXL", "PCL6", "PCL5", "PCL3GUI", "PCLM", "RASTER", "PostScript", "未知" };
            List<string> devLangs = new List<string>();
            if (ip.Length > 0)
            {
                string raw2 = "";
                try { devLangs = DriverCompat.DeviceLanguages(ip, 6000, out raw2); } catch { }
            }
            if (devLangs.Count == 0)
                devLangs = new List<string>(new string[] { "PWGRASTER", "URF", "JPEG", "TIFF", "PCL3GUI", "PCLM" });

            sb.AppendLine("判定矩阵（设备语言 = " + string.Join("/", devLangs.ToArray()) + "）：");
            for (int i = 0; i < samples.Length; i++)
                sb.AppendLine("  " + samples[i].PadRight(12) + " → 兼容 = " + (DriverCompat.Compatible(devLangs, samples[i]) ? "是" : "否"));
            sb.AppendLine();

            // ---- 5) 结论 ----
            sb.AppendLine("结论：");
            sb.AppendLine("  能真出纸的 Windows 通道是「WSD/IPP 发现型端口 + 微软 IPP 类驱动」；");
            sb.AppendLine("  走标准 TCP/IP 端口（RAW 9100）时，微软 IPP 驱动吐 PDF、HP UPD 吐 PCLXL，");
            sb.AppendLine("  而这类喷墨一体机的语言表里既没有 PDF 也没有 PCLXL ⇒ Windows 报成功、机器不出纸。");
            sb.AppendLine("  修不了的情形就用主界面的「IPP 直发」绕过队列。");

            string text = sb.ToString();
            Console.WriteLine(text);

            // 落盘（低完整性下写不进 %TEMP% 时回退 exe 目录，与其它自检一致）
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrinterToolbox");
                string file = System.IO.Path.Combine(dir, "drvcheck-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(file, text, new UTF8Encoding(false));
                Console.WriteLine();
                Console.WriteLine("已写入：" + file);
            }
            catch (Exception ex)
            {
                try
                {
                    string dir2 = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PrinterToolbox-Data");
                    System.IO.Directory.CreateDirectory(dir2);
                    string file2 = System.IO.Path.Combine(dir2, "drvcheck-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                    System.IO.File.WriteAllText(file2, text, new UTF8Encoding(false));
                    Console.WriteLine();
                    Console.WriteLine("已写入（回退到程序目录）：" + file2);
                }
                catch
                {
                    Console.WriteLine();
                    Console.WriteLine("写日志失败：" + ex.Message);
                }
            }

            if (ip.Length > 0)
            {
                List<string> langs3 = new List<string>();
                string raw3 = "";
                try { langs3 = DriverCompat.DeviceLanguages(ip, 6000, out raw3); } catch { }
                if (langs3.Count == 0) code = 1;
            }

            return code;
        }

        /// <summary>
        /// --drvsearch &lt;关键词&gt;：只跑联网搜索，把结果打到控制台（不下载、不安装）。
        /// 目的：证明「手动搜索驱动」这条链路在没有界面的情况下也能走通，方便自动化核对。
        /// </summary>
        private static int SearchOnly(string keyword)
        {
            Console.WriteLine("关键词：" + keyword);

            List<DriverDownload> list = new List<DriverDownload>();
            string error = "";
            try
            {
                // 关键词里的厂商交给 DriverOnline 自己拆（它会把厂商+型号拼成站点搜索词）
                list = DriverOnline.Find("", keyword, 20000, out error);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + " " + ex.Message;
            }

            Console.WriteLine("结果：" + list.Count + " 条");
            for (int i = 0; i < list.Count; i++)
            {
                Console.WriteLine("  [" + (i + 1) + "] " + list[i].Title);
                Console.WriteLine("      页面：" + list[i].PageUrl);
                Console.WriteLine("      直链：" + (list[i].RealUrl.Length > 0 ? list[i].RealUrl : "(没解析出)"));
            }
            if (list.Count == 0)
            {
                Console.WriteLine("没搜到。" + error);
                if (DriverOnline.LastNote.Length > 0) Console.WriteLine("  " + DriverOnline.LastNote);
                return 2;
            }
            Console.WriteLine();
            Console.WriteLine("厂商官方入口：" + DriverOnline.Portal(
                keyword.ToUpperInvariant().StartsWith("HP") || keyword.IndexOf("惠普") >= 0 ? "HP" : "", keyword));
            return 0;
        }
    }
}
