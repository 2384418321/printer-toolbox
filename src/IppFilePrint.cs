using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;

namespace PrinterIpFix
{
    /// <summary>
    /// 把「一个文件」通过 IPP Print-Job 直接发给打印机，绕开 Windows 队列。
    ///
    /// 为什么要这个功能：很多喷墨/一体机的 document-format-supported 里没有 application/pdf，
    /// 而 Windows 的 IPP 类驱动只会发 PDF —— 结果电脑报「打印成功」、机器一张纸都不出。
    /// 这类设备普遍支持 image/jpeg，所以这里先把文件渲染成 JPEG 再逐页直发。
    ///
    /// 支持范围（品牌无关）：PDF / XPS / JPG / PNG / BMP / TIF。
    /// Office 文档请先在 WPS/Office 里「另存为 PDF」再发 —— 工具箱不依赖任何办公软件组件。
    /// </summary>
    internal static class IppFilePrint
    {
        public static string LastNote = "";

        private const int RenderDpi = 204;                       // 常见打印机原生分辨率，兼顾体积
        private const long MaxBodyBytes = 24L * 1024 * 1024;     // 单作业上限，别把几十兆塞进设备内存

        /// <summary>能不能直接把这份文件交给 IPP 直发。</summary>
        public static bool SupportedExt(string file)
        {
            if (file == null) return false;
            string ext = Path.GetExtension(file).ToLowerInvariant();
            return ext == ".pdf" || ext == ".jpg" || ext == ".jpeg" || ext == ".png"
                || ext == ".bmp" || ext == ".tif" || ext == ".tiff" || ext == ".xps";
        }

        /// <summary>
        /// 发送一个文件。mime 传空串则自动挑。
        /// 返回 true = 设备已受理并回了 job-id；最终以纸上有没有字为准。
        /// </summary>
        public static bool Send(DeviceInfo dev, string filePath, string mime, int timeoutMs)
        {
            LastNote = "";
            if (dev == null) { LastNote = "没有选中设备。"; return false; }
            if (string.IsNullOrEmpty(dev.Ip)) { LastNote = "这台设备没有 IP 地址。"; return false; }
            if (!File.Exists(filePath)) { LastNote = "文件不存在：" + filePath; return false; }

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            List<byte[]> pages;

            try
            {
                if (ext == ".jpg" || ext == ".jpeg")
                {
                    pages = new List<byte[]>();
                    pages.Add(File.ReadAllBytes(filePath));
                    if (string.IsNullOrEmpty(mime)) mime = "image/jpeg";
                }
                else if (ext == ".png" || ext == ".bmp" || ext == ".tif" || ext == ".tiff")
                {
                    byte[] j = ToJpeg(filePath, 85);
                    if (j == null) { LastNote = "图片转 JPEG 失败。"; return false; }
                    pages = new List<byte[]>();
                    pages.Add(j);
                    if (string.IsNullOrEmpty(mime)) mime = "image/jpeg";
                }
                else if (ext == ".pdf" || ext == ".xps")
                {
                    string pdfPath = filePath;
                    string tmp = null;
                    if (ext == ".xps")
                    {
                        note0 = "";
                        tmp = ConvertViaPrintToPdf(filePath, out note0);
                        if (tmp == null) { LastNote = note0; return false; }
                        pdfPath = tmp;
                    }
                    pages = RenderPdfToJpegs(pdfPath);
                    if (tmp != null) { try { File.Delete(tmp); } catch { } }
                    if (pages == null || pages.Count == 0) return false;
                    if (string.IsNullOrEmpty(mime)) mime = "image/jpeg";
                }
                else
                {
                    LastNote = "这种文件类型不能直发。请在原程序里「另存为 PDF」或「导出为图片」后再发。";
                    return false;
                }
            }
            catch (Exception ex)
            {
                LastNote = "读取/渲染失败：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }

            long total = 0;
            for (int i = 0; i < pages.Count; i++) total += pages[i].LongLength;
            if (total > MaxBodyBytes)
            {
                LastNote = "渲染后共 " + (total / 1024) + " KB，超过单次上限 "
                         + (MaxBodyBytes / 1024 / 1024) + " MB。请减少页数后重发。";
                return false;
            }

            string name = SafeName(Path.GetFileNameWithoutExtension(filePath));
            LogLine("准备直发：" + name + " · " + pages.Count + " 页 · 共 " + (total / 1024) + " KB · 格式 " + mime);

            int okCount = 0;
            var ids = new StringBuilder();
            for (int i = 0; i < pages.Count; i++)
            {
                string jobName = pages.Count == 1 ? name : (name + "-p" + (i + 1));
                bool ok = Ipp.PrintJob(dev.Ip, "/ipp/print", mime, pages[i], jobName, timeoutMs);
                if (ok)
                {
                    okCount++;
                    if (ids.Length > 0) ids.Append(", ");
                    ids.Append(Ipp.LastNote);
                    LogLine("  第 " + (i + 1) + "/" + pages.Count + " 页 → 设备已受理（" + Ipp.LastNote + "）");
                }
                else
                {
                    LogLine("  第 " + (i + 1) + "/" + pages.Count + " 页 → 被拒：" + Ipp.LastNote);
                    if (okCount == 0)
                    {
                        LastNote = "第一页就被拒绝（" + Ipp.LastNote + "），后面的页没发，不会浪费纸。";
                        return false;
                    }
                    LastNote = "已发出 " + okCount + " 页，第 " + (i + 1) + " 页被拒：" + Ipp.LastNote;
                    return false;
                }
            }

            LastNote = "全部 " + okCount + " 页已被设备受理（" + ids + "）。请看打印机是否真出纸。";
            return true;
        }

        /// <summary>按设备上报的格式表挑一个能构造出来的格式（目前只保证 JPEG）。</summary>
        public static string PickFormat(DeviceInfo dev)
        {
            if (dev != null && dev.FormatsProbed)
            {
                if (Ipp.HasFormat(dev.Formats, "image/jpeg")) return "image/jpeg";
                if (Ipp.HasFormat(dev.Formats, "image/tiff")) return "image/jpeg";   // TIFF 未实现编码，仍走 JPEG
                if (Ipp.HasFormat(dev.Formats, "application/pdf")) return "application/pdf";
            }
            return "image/jpeg";
        }

        // ---------- JPEG 编码 ----------

        private static byte[] ToJpeg(string filePath, int quality)
        {
            using (Image img = Image.FromFile(filePath))
            using (Bitmap src = new Bitmap(img.Width, img.Height))
            {
                using (Graphics g = Graphics.FromImage(src))
                {
                    g.Clear(Color.White);
                    g.DrawImage(img, 0, 0, img.Width, img.Height);
                }
                return SaveJpeg(src, quality);
            }
        }

        private static byte[] SaveJpeg(Bitmap bmp, int quality)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                EncoderParameter ep = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                EncoderParameters ps = new EncoderParameters(1);
                ps.Param[0] = ep;
                ImageCodecInfo codec = FindJpegCodec();
                try
                {
                    if (codec != null) bmp.Save(ms, codec, ps);
                    else bmp.Save(ms, ImageFormat.Jpeg);
                }
                finally { ep.Dispose(); ps.Dispose(); }
                return ms.ToArray();
            }
        }

        private static ImageCodecInfo FindJpegCodec()
        {
            ImageCodecInfo[] cs = ImageCodecInfo.GetImageEncoders();
            for (int i = 0; i < cs.Length; i++)
                if (cs[i].MimeType == "image/jpeg") return cs[i];
            return null;
        }

        // ---------- PDF → 每页 JPEG（用系统自带 Windows.Data.Pdf，不装任何东西） ----------

        private static string note0 = "";

        private static List<byte[]> RenderPdfToJpegs(string pdfPath)
        {
            object doc = PdfOpen(pdfPath);
            if (doc == null)
            {
                if (LastNote.Length == 0)
                    LastNote = "本机没有 Windows.Data.Pdf 组件（Win8.1+ 自带），无法把 PDF 转成图片。"
                             + "可以在浏览器/WPS 里把它导出成图片后再直发那张图片。";
                return null;
            }

            int count = PdfInt(doc, "PageCount");
            if (count <= 0) { LastNote = "这份 PDF 没有可读页面（可能加密）。"; return null; }

            var list = new List<byte[]>();
            double scale = RenderDpi / 96.0;

            for (int i = 0; i < count; i++)
            {
                byte[] jpg = RenderPage(doc, i, scale);
                if (jpg == null)
                {
                    LastNote = "第 " + (i + 1) + " 页渲染失败（文件可能损坏或加密）。";
                    return null;
                }
                list.Add(jpg);
                LogLine("  渲染第 " + (i + 1) + "/" + count + " 页 → " + (jpg.Length / 1024) + " KB");
            }
            return list;
        }

        private static Assembly winRtAsm;
        private static Type pdfDocType;

        private static bool EnsurePdfRuntime()
        {
            if (pdfDocType != null) return true;
            try
            {
                winRtAsm = Assembly.Load(
                    "Windows.Data.Pdf, Version=255.255.255.255, Culture=neutral, PublicKeyToken=null, ContentType=WindowsRuntime");
                if (winRtAsm == null) return false;
                pdfDocType = winRtAsm.GetType("Windows.Data.Pdf.PdfDocument");
                return pdfDocType != null;
            }
            catch { return false; }
        }

        private static object PdfOpen(string path)
        {
            if (!EnsurePdfRuntime()) return null;
            try
            {
                MethodInfo mi = pdfDocType.GetMethod("LoadFromFileAsync", new Type[] { typeof(string) });
                if (mi == null) return null;
                object op = mi.Invoke(null, new object[] { path });
                return AwaitResult(op);
            }
            catch (TargetInvocationException tie)
            {
                Exception inner = tie.InnerException ?? tie;
                LastNote = "打开 PDF 失败：" + inner.GetType().Name + " " + inner.Message;
                return null;
            }
            catch (Exception ex)
            {
                LastNote = "打开 PDF 失败：" + ex.GetType().Name + " " + ex.Message;
                return null;
            }
        }

        private static int PdfInt(object o, string prop)
        {
            object v = GetProp(o, prop);
            if (v == null) return 0;
            try { return Convert.ToInt32(v); } catch { return 0; }
        }

        private static byte[] RenderPage(object doc, int index, double scale)
        {
            object page = PdfPage(doc, index);
            if (page == null) return null;
            Type pt = page.GetType();

            // 页面尺寸（点）→ 目标像素
            float pw = 595f, ph = 842f;   // A4 兜底
            object sizeOp = Invoke(pt, "GetSizeAsync", new object[] { EnumArgb(pt, "SizeOption", 0), (float)RenderDpi });
            object sizeRes = sizeOp == null ? null : AwaitResult(sizeOp);
            if (sizeRes != null)
            {
                object wv = GetProp(sizeRes, "Width");
                object hv = GetProp(sizeRes, "Height");
                if (wv != null) pw = Convert.ToSingle(wv);
                if (hv != null) ph = Convert.ToSingle(hv);
            }

            int w = (int)Math.Round(pw * scale);
            int h = (int)Math.Round(ph * scale);
            if (w < 64 || h < 64) return null;
            if (w > 5000 || h > 7000)
            {
                double shrink = Math.Min(5000.0 / w, 7000.0 / h);
                w = (int)(w * shrink); h = (int)(h * shrink);
            }

            // WinRT 渲染到 InMemoryRandomAccessStream，再拷进 GDI Bitmap
            object stream = CreateWinRt("Windows.Storage.Streams.InMemoryRandomAccessStream",
                                         "Windows.Storage, Version=255.255.255.255, Culture=neutral, PublicKeyToken=null, ContentType=WindowsRuntime");
            if (stream == null) return null;

            Type optType = pt.Assembly.GetType(pt.Namespace + ".PdfPageRenderOptions");
            object opts = optType == null ? null : Activator.CreateInstance(optType);
            if (opts != null)
            {
                SetProp(opts, "Scale", (float)(RenderDpi / 96.0));
                object bg = ArgbStruct(0xFFFFFFFFu);
                if (bg != null) SetProp(opts, "BackgroundColor", bg);
            }

            object renderOp = null;
            try
            {
                MethodInfo rm = FindRender(pt, opts != null);
                if (rm != null)
                    renderOp = opts != null ? rm.Invoke(page, new object[] { stream, opts })
                                             : rm.Invoke(page, new object[] { stream });
            }
            catch (TargetInvocationException tie)
            {
                LastNote = "渲染调用失败：" + (tie.InnerException ?? tie).Message;
                return null;
            }
            if (renderOp == null) { LastNote = "这台系统的 PDF 渲染接口形态不认识。"; return null; }
            if (AwaitResult(renderOp) == null && LastNote.Length == 0) { LastNote = "渲染超时。"; return null; }

            return StreamToJpeg(stream, 82);
        }

        private static MethodInfo FindRender(Type pt, bool withOpts)
        {
            MethodInfo[] ms = pt.GetMethods();
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != "RenderToStreamAsync") continue;
                ParameterInfo[] ps = ms[i].GetParameters();
                if (withOpts && ps.Length == 2) return ms[i];
                if (!withOpts && ps.Length == 1) return ms[i];
            }
            return null;
        }

        private static object PdfPage(object doc, int index)
        {
            object pages = GetProp(doc, "Pages");
            if (pages == null) return null;
            MethodInfo getAt = pages.GetType().GetMethod("get_Item");
            if (getAt == null) return null;
            try { return getAt.Invoke(pages, new object[] { index }); } catch { return null; }
        }

        private static byte[] StreamToJpeg(object inMemStream, int quality)
        {
            try
            {
                // 取长度
                object lenV = GetProp(inMemStream, "Size");
                ulong len = lenV == null ? 0UL : Convert.ToUInt64(lenV);
                if (len == 0) return null;

                // Windows.Storage.Streams.DataReader(stream) → LoadAsync((uint)len) → 等待
                object reader = CreateWinRtDataReader(inMemStream);
                if (reader == null) return null;

                byte[] buf = ReadAllFrom(reader, (uint)len);
                if (buf == null || buf.Length == 0) return null;

                using (MemoryStream bm = new MemoryStream(buf))
                using (Image img = Image.FromStream(bm))
                using (Bitmap bmp = new Bitmap(img.Width, img.Height))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.White);
                        g.DrawImage(img, 0, 0, img.Width, img.Height);
                    }
                    return SaveJpeg(bmp, quality);
                }
            }
            catch (Exception ex)
            {
                LastNote = "读回渲染结果失败：" + ex.GetType().Name + " " + ex.Message;
                return null;
            }
        }

        private static object CreateWinRtDataReader(object stream)
        {
            try
            {
                Assembly asm = Assembly.Load("Windows.Storage, Version=255.255.255.255, Culture=neutral, PublicKeyToken=null, ContentType=WindowsRuntime");
                Type rt = asm.GetType("Windows.Storage.Streams.DataReader");
                if (rt == null) return null;
                // DataReader 的构造参数是 IRandomAccessStream，InMemoryRandomAccessStream 本身就实现它
                return Activator.CreateInstance(rt, new object[] { stream });
            }
            catch { return null; }
        }

        private static byte[] ReadAllFrom(object reader, uint len)
        {
            try
            {
                MethodInfo load = reader.GetType().GetMethod("LoadAsync");
                object op = load.Invoke(reader, new object[] { len });
                object res = AwaitResult(op);
                if (res == null) return null;

                MethodInfo detach = reader.GetType().GetMethod("DetachBuffer");
                object bufObj = detach.Invoke(reader, new object[] { len });
                if (bufObj == null) return null;

                byte[] outBuf = new byte[len];
                MethodInfo copy = bufObj.GetType().GetMethod("CopyTo");
                copy.Invoke(bufObj, new object[] { outBuf });
                return outBuf;
            }
            catch (Exception ex)
            {
                LastNote = "取出渲染字节失败：" + ex.GetType().Name + " " + ex.Message;
                return null;
            }
        }

        // ---------- XPS → PDF（借系统「Microsoft Print to PDF」队列，非虚拟打印机不可用时提示） ----------

        private static string ConvertViaPrintToPdf(string filePath, out string note)
        {
            note = "";
            string dir = TempDir();
            string outPdf = Path.Combine(dir, "conv-" + DateTime.Now.ToString("HHmmss") + ".pdf");
            try { if (File.Exists(outPdf)) File.Delete(outPdf); } catch { }

            string printer = FindPrintToPdfQueue();
            if (printer == null)
            {
                note = "本机没有「Microsoft Print to PDF」队列，XPS 转不了。请在原程序里另存为 PDF 后再发。";
                return null;
            }

            // 让这次转换的输出固定落到我们要的路径：改该虚拟打印机的端口指向文件
            try
            {
                string script = "$ErrorActionPreference='Stop';"
                  + "$p='" + Escape(printer) + "';$d='" + Escape(outPdf) + "';$s='" + Escape(filePath) + "';"
                  + "Set-Printer -Name $p -PortName ('FILE:' + $d) -ErrorAction SilentlyContinue;"
                  + "$sp = Start-Process -FilePath $s -Verb PrintTo -ArgumentList $p -PassThru -ErrorAction Stop;"
                  + "$deadline=(Get-Date).AddSeconds(45);"
                  + "while ((Get-Date) -lt $deadline) {"
                  + "  if (Test-Path -LiteralPath $d) { break };"
                  + "  Start-Sleep -Milliseconds 700;"
                  + "}"
                  + "if (-not (Test-Path -LiteralPath $d)) { exit 2 };";
                string ignored;
                PrinterSystem.RunPowerShell(script, out ignored);
            }
            catch (Exception ex)
            {
                note = "启动转换失败：" + ex.Message;
                return null;
            }

            for (int i = 0; i < 20; i++)
            {
                if (File.Exists(outPdf) && new FileInfo(outPdf).Length > 1024) return outPdf;
                Thread.Sleep(500);
            }
            note = "转换后没拿到 PDF。「Microsoft Print to PDF」可能在等人工选保存位置，"
                 + "请先在原程序里手动另存为 PDF，再用本功能发那个 PDF。";
            return null;
        }

        private static string FindPrintToPdfQueue()
        {
            try
            {
                foreach (PrinterInfo pi in PrinterSystem.GetPrinters())
                {
                    if (pi.Name.IndexOf("Print to PDF", StringComparison.OrdinalIgnoreCase) >= 0) return pi.Name;
                    if (pi.Name.IndexOf("打印为 PDF", StringComparison.OrdinalIgnoreCase) >= 0) return pi.Name;
                }
            }
            catch { }
            return null;
        }

        // ---------- WinRT 反射小工具 ----------

        private static object CreateWinRt(string typeName, string assemblyName)
        {
            try
            {
                Assembly asm = Assembly.Load(assemblyName);
                Type t = asm.GetType(typeName);
                if (t == null) return null;
                return Activator.CreateInstance(t);
            }
            catch { return null; }
        }

        private static object EnumArgb(Type pt, string enumName, int value)
        {
            try
            {
                Type et = pt.Assembly.GetType(pt.Namespace + "." + enumName);
                if (et == null) return 0;
                return Enum.ToObject(et, value);
            }
            catch { return 0; }
        }

        private static object ArgbStruct(uint argb)
        {
            try
            {
                Assembly asm = Assembly.Load("Windows.Foundation, Version=255.255.255.255, Culture=neutral, PublicKeyToken=null, ContentType=WindowsRuntime");
                Type t = asm.GetType("Windows.Foundation.Color");
                if (t == null) return null;
                object c = Activator.CreateInstance(t);
                SetField(c, "A", (byte)((argb >> 24) & 0xFF));
                SetField(c, "R", (byte)((argb >> 16) & 0xFF));
                SetField(c, "G", (byte)((argb >> 8) & 0xFF));
                SetField(c, "B", (byte)(argb & 0xFF));
                return c;
            }
            catch { return null; }
        }

        private static void SetField(object o, string name, object v)
        {
            FieldInfo fi = o.GetType().GetField(name);
            if (fi != null) fi.SetValue(o, v);
        }

        private static object GetProp(object o, string name)
        {
            if (o == null) return null;
            PropertyInfo pi = o.GetType().GetProperty(name);
            return pi == null ? null : pi.GetValue(o, null);
        }

        private static void SetProp(object o, string name, object v)
        {
            PropertyInfo pi = o.GetType().GetProperty(name);
            if (pi == null) return;
            try { pi.SetValue(o, v, null); } catch { }
        }

        private static object Invoke(Type t, string name, object[] args)
        {
            MethodInfo mi = t.GetMethod(name);
            if (mi == null) return null;
            try { return mi.Invoke(null, args); } catch { return null; }
        }

        /// <summary>等 IAsyncOperation&lt;T&gt; / IAsyncAction 完成，返回 Result（IAsyncAction 返回非 null 哨兵表示成功）。</summary>
        private static object AwaitResult(object asyncOp)
        {
            if (asyncOp == null) return null;
            Type iface = null;
            foreach (Type it in asyncOp.GetType().GetInterfaces())
            {
                string n = it.Name;
                if (n.StartsWith("IAsyncOperation`1") || n == "IAsyncAction" || n == "IAsyncOperationWithProgress`2")
                { iface = it; break; }
            }
            if (iface == null) return null;

            PropertyInfo statusP = iface.GetProperty("Status");
            if (statusP == null) return null;
            Type statusEnum = statusP.PropertyType;
            object completed = Enum.Parse(statusEnum, "Completed");

            var sw = Stopwatch.StartNew();
            while (true)
            {
                object st = statusP.GetValue(asyncOp, null);
                if (Equals(st, completed)) break;
                if (sw.ElapsedMilliseconds > 30000) { LastNote = "异步操作超时（30 秒）。"; return null; }
                Thread.Sleep(20);
            }

            PropertyInfo errP = iface.GetProperty("ErrorCode");
            if (errP != null)
            {
                object ec = errP.GetValue(asyncOp, null);
                if (ec != null)
                {
                    int code = Convert.ToInt32(ec);
                    if (code != 0) { LastNote = "异步操作出错，HRESULT=0x" + code.ToString("X8"); return null; }
                }
            }

            PropertyInfo resultP = iface.GetProperty("Result");
            if (resultP == null) return new object();   // IAsyncAction：完成即成功
            return resultP.GetValue(asyncOp, null);
        }

        // ---------- 杂项 ----------

        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PrinterToolbox");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        private static string Escape(string s)
        {
            return (s ?? "").Replace("'", "''");
        }

        private static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "toolbox-job";
            var sb = new StringBuilder();
            foreach (char ch in s)
            {
                if (ch < 128 && !char.IsControl(ch)) sb.Append(ch);
                else sb.Append('?');
            }
            string r = sb.ToString().Replace('"', '\'');
            if (r.Length > 60) r = r.Substring(0, 60);
            return r.Length == 0 ? "toolbox-job" : r;
        }

        public static event Action<string> OnLog;

        private static void LogLine(string s)
        {
            try
            {
                Action<string> h = OnLog;
                if (h != null) h(s);
            }
            catch { }
        }
    }
}
