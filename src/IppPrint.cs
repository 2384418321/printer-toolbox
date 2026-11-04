using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace PrinterIpFix
{
    /// <summary>
    /// 绕过 Windows 打印队列、直接往设备发作业的通道。
    ///
    /// 为什么需要它：Windows 的「Microsoft IPP Class Driver」配在标准 TCP/IP（RAW 9100）
    /// 端口上时，会把文档渲染成原始 PDF 再裸推到端口。而不少喷墨一体机
    /// （document-format-supported 里没有 application/pdf）收到 PDF 会直接丢弃，
    /// 但 spooler 照样记一条「已通过端口打印、打印页数 1」的事件 —— 于是出现
    /// 「电脑说打印成功、打印机不出纸」。这条通道用 IPP Print-Job 把设备真正
    /// 支持的格式（JPEG）交过去，设备才会受理。
    /// </summary>
    internal static class IppPrint
    {
        /// <summary>最近一次操作的诊断文本，给界面日志用。</summary>
        public static string LastNote = "";

        /// <summary>问设备支持哪些文档格式，结果写进 dev。探不到返回 false。</summary>
        public static bool ProbeFormats(DeviceInfo dev, int timeoutMs)
        {
            LastNote = "";
            if (dev == null || string.IsNullOrEmpty(dev.Ip)) return false;
            string fmts = Ipp.DocumentFormats(dev.Ip, timeoutMs);
            if (fmts.Length == 0)
            {
                LastNote = Ipp.LastNote.Length > 0 ? Ipp.LastNote : "设备没有回应格式列表";
                return false;
            }
            dev.Formats = fmts;
            dev.FormatsProbed = true;
            LastNote = "支持：" + fmts;
            return true;
        }

        /// <summary>
        /// 诊断这台设备能不能被 Windows 的「微软 IPP 类驱动 + RAW 端口」正常打印。
        /// 返回一句人话结论，空串表示信息不足以下结论。
        /// </summary>
        public static string WindowsQueueVerdict(DeviceInfo dev)
        {
            if (dev == null || !dev.FormatsProbed) return "";
            if (dev.SupportsPdf) return "设备支持 PDF，Windows 队列可以直接打印。";
            if (dev.SupportsJpeg || Ipp.HasFormat(dev.Formats, "image/pwg-raster"))
                return "设备不支持 PDF（Windows 的 IPP 类驱动只会发 PDF），"
                     + "所以走 Windows 队列会出现「电脑提示成功、打印机不出纸」。"
                     + "请改用本页的「IPP 直发测试页」，或安装打印机厂商的完整驱动。";
            return "设备上报的格式里既没有 PDF 也没有 JPEG，直发通道也未必可用，请以设备面板提示为准。";
        }

        /// <summary>
        /// 生成一张 A4（300dpi）白底黑字的 JPEG 测试页。
        /// 刻意做得「小而省」：只有顶部几行短字，页面其余留白 —— 一行小字已能证明通道通，
        /// 不必每次打满整页（那样既费纸又费墨）。
        /// </summary>
        public static byte[] BuildTestPageJpeg(string title, string subtitle)
        {
            int w = 2480, h = 3508;   // A4 @300dpi
            using (var bmp = new Bitmap(w, h))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                    using (var f1 = new Font("Microsoft YaHei", 40F, FontStyle.Bold))
                    using (var f2 = new Font("Microsoft YaHei", 20F))
                    using (var f3 = new Font("Microsoft YaHei", 15F))
                    using (var black = new SolidBrush(Color.Black))
                    using (var gray = new SolidBrush(Color.FromArgb(110, 110, 110)))
                    {
                        g.DrawString(title == null ? "IPP 直发测试页" : title, f1, black, 140, 130);
                        g.DrawString(subtitle == null ? "" : subtitle, f2, gray, 140, 235);

                        string[] lines = new string[]
                        {
                            "看到这一页 = 设备已通过 IPP Print-Job 通道收到并打印。",
                            "本页只为验证通道，故意只印这几行，省纸省墨。",
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        };
                        int y = 310;
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (lines[i].Length > 0) g.DrawString(lines[i], f3, black, 140, y);
                            y += 40;
                        }
                    }
                }

                var codec = FindJpegCodec();
                if (codec == null) return null;
                using (var ps = new EncoderParameters(1))
                {
                    ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 65L);
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, codec, ps);
                        return ms.ToArray();
                    }
                }
            }
        }

        /// <summary>把测试页存成本地图片并打开，先看一眼再决定要不要真打（不耗纸）。</summary>
        public static bool PreviewTestPage(DeviceInfo dev, out string savedPath)
        {
            savedPath = "";
            byte[] jpg = BuildTestPageJpeg("IPP 直发测试页（预览）",
                (dev == null || dev.DisplayName.Length == 0 ? "" : dev.DisplayName + "   ")
                + (dev == null ? "" : dev.Ip));
            if (jpg == null) { LastNote = "本机找不到 JPEG 编码器"; return false; }

            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "PrinterToolbox");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                savedPath = Path.Combine(dir, "ipp-test-page-" + DateTime.Now.ToString("HHmmss") + ".jpg");
                File.WriteAllBytes(savedPath, jpg);
                System.Diagnostics.ProcessStartInfo psi =
                    new System.Diagnostics.ProcessStartInfo(savedPath);
                psi.UseShellExecute = true;      // 交给系统默认看图工具，不弹本程序自己的窗
                System.Diagnostics.Process.Start(psi);
                LastNote = "已生成预览：" + savedPath;
                return true;
            }
            catch (Exception ex)
            {
                LastNote = "生成预览失败：" + ex.Message;
                return false;
            }
        }

        private static ImageCodecInfo FindJpegCodec()
        {
            ImageCodecInfo[] all = ImageCodecInfo.GetImageEncoders();
            for (int i = 0; i < all.Length; i++)
                if (string.Compare(all[i].MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase) == 0)
                    return all[i];
            return null;
        }

        /// <summary>
        /// 直发一张 JPEG 测试页。成功返回 true，并把设备回的 job-id 写进 LastNote。
        /// 判据是设备的 IPP 响应（status=0x0000 且回 job-id），不是 spooler 的事件。
        /// </summary>
        public static bool SendTestPage(DeviceInfo dev, int timeoutMs)
        {
            LastNote = "";
            if (dev == null || string.IsNullOrEmpty(dev.Ip))
            {
                LastNote = "没有可用的设备地址";
                return false;
            }

            // 先确认设备吃 JPEG；探不到格式就按 JPEG 试一把，让设备自己回错误码。
            if (!dev.FormatsProbed) ProbeFormats(dev, timeoutMs);

            string fmt = "image/jpeg";
            if (dev.FormatsProbed && !dev.SupportsJpeg)
            {
                if (Ipp.HasFormat(dev.Formats, "image/pwg-raster"))
                {
                    LastNote = "设备只认 PWG-Raster，本工具暂时只能发 JPEG。请用厂商驱动打印。";
                    return false;
                }
                LastNote = "设备上报的格式里没有 image/jpeg：" + dev.Formats;
                return false;
            }

            byte[] jpg = BuildTestPageJpeg(
                "IPP 直发测试页",
                (dev.DisplayName.Length > 0 ? dev.DisplayName : dev.Ip) + "   " + dev.Ip);
            if (jpg == null)
            {
                LastNote = "本机找不到 JPEG 编码器，无法生成测试页";
                return false;
            }

            bool ok = Ipp.PrintJob(dev.Ip, "/ipp/print", fmt, jpg, "toolbox-test-page", timeoutMs);
            LastNote = "格式 " + fmt + "，发送 " + jpg.Length + " 字节 → " + Ipp.LastNote;
            return ok;
        }
    }
}
