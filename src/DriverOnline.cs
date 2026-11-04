using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PrinterIpFix
{
    /// <summary>一条可下载的驱动：站点详情页里的跳转链接 + 跟随跳转后的真实直链。</summary>
    internal sealed class DriverDownload
    {
        public string PageUrl = "";   // 驱动站详情页
        public string Title = "";     // 链接文案（如「惠普 HP OfficeJet Pro 9730 驱动」）
        public string LinkUrl = "";   // 站点内 /plus/download.php 跳转
        public string RealUrl = "";   // 跟随 302 后的真实直链（多为厂商官方站点）
        public override string ToString() { return Title.Length > 0 ? Title : RealUrl; }
    }

    /// <summary>
    /// 联网找官方驱动：型号 → 站点详情页 → 下载直链 → 落地 → 解包 → pnputil 装 INF。
    /// 全文没有任何品牌/型号写死规则；厂商名只用来拼站点目录与官方入口。
    /// </summary>
    internal static class DriverOnline
    {
        public static string LastNote = "";

        private const string SiteUrl = "https://www.dyjqd.com";
        private const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PrinterToolbox/1.0";

        // ================= 查找 =================

        /// <summary>按厂商/型号查可下载的驱动。vendor 可为空（此时从 model 里猜）。</summary>
        public static List<DriverDownload> Find(string vendor, string model, int timeoutMs, out string error)
        {
            error = "";
            List<DriverDownload> found = new List<DriverDownload>();
            LastNote = "";

            string v = (vendor == null ? "" : vendor.Trim());
            string m = (model == null ? "" : model.Trim());
            if (m.Length == 0 && v.Length == 0) { error = "没有型号，无法查找。"; return found; }
            if (m.Length == 0) m = v;

            string keyword = (v.Length > 0 && !m.ToLowerInvariant().StartsWith(v.ToLowerInvariant())
                ? v + " " + m : m).Trim();

            // 1) 先按站点目录规则直接猜详情页 —— 猜中就不走搜索
            // 整个查找必须有时限：4 个猜测页 + 搜索页 + 详情页 + 每条下载链接的 302
            // 如果每次都吃满 timeoutMs，总共能跑几分钟，界面看起来就是「卡死」。
            // 所以统一走一个截止时间，每次请求只用剩余的额度。
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            List<string> pages = new List<string>();
            string vs = DriverLookup.VendorSlug(keyword);
            string ms = DriverLookup.ModelSlug(m);
            if (vs.Length > 0 && ms.Length > 0)
            {
                string[] guesses = DriverLookup.GuessPages(vs, ms);
                for (int i = 0; i < guesses.Length; i++)
                {
                    int left = LeftMs(deadline);
                    if (left <= 1000) break;
                    if (DriverLookup.Exists(guesses[i], Math.Min(left, 6000))) { pages.Add(guesses[i]); break; }
                }
            }

            // 2) 猜不中就走站内搜索（取前 3 条）
            if (pages.Count == 0)
            {
                int left = LeftMs(deadline);
                if (left <= 1500) { error = "查找超时（站点响应太慢）。"; LastNote = "查找超时，未拿到结果。"; return found; }
                List<DriverPage> hits = DriverLookup.Search(keyword, Math.Min(left, timeoutMs), out error);
                for (int i = 0; i < hits.Count && i < 3; i++) pages.Add(hits[i].Url);
            }
            if (pages.Count == 0)
            {
                if (error.Length == 0) error = "站内没有「" + keyword + "」对应的驱动页。";
                LastNote = "关键词「" + keyword + "」；没找到详情页。";
                return found;
            }

            // 3) 抓详情页里的下载链接，逐条跟随 302 拿真实直链
            int budget = 10;
            for (int i = 0; i < pages.Count && budget > 0; i++)
            {
                int left = LeftMs(deadline);
                if (left <= 1500) break;

                string html;
                int st;
                string err;
                if (!FetchPage(pages[i], Math.Min(left, timeoutMs), out html, out st, out err)) continue;

                MatchCollection links = Regex.Matches(html,
                    "href=\"(/plus/download\\.php[^\"]+)\"[^>]*>(.*?)</a>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);

                for (int k = 0; k < links.Count && budget > 0; k++)
                {
                    int left2 = LeftMs(deadline);
                    if (left2 <= 1200) { budget = 0; break; }

                    string href = links[k].Groups[1].Value.Replace("&amp;", "&");
                    string title = StripTags(links[k].Groups[2].Value);
                    if (href.Length == 0) continue;

                    DriverDownload d = new DriverDownload();
                    d.PageUrl = pages[i];
                    d.LinkUrl = SiteUrl + href;
                    d.Title = title.Length > 0 ? title : KeywordName(keyword);

                    string rerr;
                    string real = ResolveRedirect(d.LinkUrl, Math.Min(left2, timeoutMs), out rerr);
                    d.RealUrl = real;

                    bool dup = false;
                    for (int q = 0; q < found.Count; q++)
                        if (found[q].LinkUrl == d.LinkUrl) { dup = true; break; }
                    if (dup) continue;

                    found.Add(d);
                    budget--;
                }
            }

            LastNote = "关键词「" + keyword + "」；详情页 " + pages.Count + " 个；下载项 " + found.Count + " 条。";
            if (found.Count == 0 && error.Length == 0)
                error = "页面打开了，但没解析出下载链接（站点结构可能变了）。";
            return found;
        }

        /// <summary>距离截止时间还剩多少毫秒（负数归零）。</summary>
        private static int LeftMs(DateTime deadline)
        {
            long left = (long)(deadline - DateTime.Now).TotalMilliseconds;
            if (left < 0) return 0;
            if (left > int.MaxValue) return int.MaxValue;
            return (int)left;
        }

        private static string KeywordName(string keyword)
        {
            return keyword.Length == 0 ? "驱动下载" : keyword + " 驱动";
        }

        /// <summary>厂商官方驱动入口（拿不到第三方站结果时的兜底）。</summary>
        public static string Portal(string vendor, string model)
        {
            string v = (vendor == null ? "" : vendor).ToUpperInvariant();
            string m = Uri.EscapeDataString(model == null ? "" : model.Trim());

            if (v.IndexOf("HP") >= 0 || v.IndexOf("HEWLETT") >= 0)
                return "https://support.hp.com/cn-zh/drivers/printers?q=" + m;
            if (v.IndexOf("CANON") >= 0)
                return "https://www.canon.com.cn/supports/download/search.html?keyword=" + m;
            if (v.IndexOf("EPSON") >= 0 || v.IndexOf("SEIKO") >= 0)
                return "https://www.epson.com.cn/tech_support/drivers_search.aspx?keyword=" + m;
            if (v.IndexOf("BROTHER") >= 0)
                return "https://www.brother.cn/support/drivers?keyword=" + m;
            if (v.IndexOf("LENOVO") >= 0 || v.IndexOf("联想") >= 0)
                return "https://support.lenovo.com/cn/zh/downloads?keyword=" + m;
            return "";
        }

        // ================= 下载 =================

        public static bool Download(string url, string destFile, int timeoutMs, Action<long, long> progress, out string error)
        {
            error = "";
            if (url == null || url.Length == 0) { error = "没有下载地址。"; return false; }
            try
            {
                string dir = Path.GetDirectoryName(destFile);
                if (dir != null && dir.Length > 0 && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.UserAgent = Ua;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AllowAutoRedirect = true;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)resp.StatusCode != 200)
                    {
                        error = "下载地址返回 HTTP " + (int)resp.StatusCode + "。";
                        return false;
                    }
                    long total = resp.ContentLength;
                    long got = 0;
                    byte[] buf = new byte[65536];
                    using (Stream src = resp.GetResponseStream())
                    using (FileStream dst = File.Create(destFile))
                    {
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            dst.Write(buf, 0, n);
                            got += n;
                            if (progress != null) progress(got, total);
                        }
                    }
                    if (got == 0) { error = "下载到 0 字节。"; return false; }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static string DownloadDir()
        {
            return Path.Combine(DriverStore.DriverDir(), "downloads");
        }

        /// <summary>建议的落地文件名（按 URL 末段，冲突时加时间戳）。</summary>
        public static string SuggestedFile(string url)
        {
            string name = "driver.bin";
            try
            {
                Uri u = new Uri(url);
                name = Path.GetFileName(u.AbsolutePath);
            }
            catch { }
            if (name == null || name.Length == 0 || name.IndexOf('.') < 0) name = "driver.bin";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        /// <summary>按后缀判类型：exe / zip / cab / other。</summary>
        public static string Kind(string urlOrFile)
        {
            if (urlOrFile == null) return "other";
            string p = urlOrFile;
            try { p = new Uri(p).AbsolutePath; } catch { }
            int q = p.IndexOf('?');
            if (q >= 0) p = p.Substring(0, q);
            string ext = Path.GetExtension(p);
            if (ext == null) return "other";
            ext = ext.ToLowerInvariant();
            if (ext == ".exe") return "exe";
            if (ext == ".zip") return "zip";
            if (ext == ".cab") return "cab";
            return "other";
        }

        // ================= 解包 / 安装 =================

        /// <summary>压缩包解到和它同级的同名目录；exe 不是压缩包，返回 false。</summary>
        public static bool Expand(string file, out string outDir, out string error)
        {
            error = "";
            outDir = "";
            if (file == null || !File.Exists(file)) { error = "文件不存在：" + file; return false; }

            string kind = Kind(file);
            if (kind == "exe" || kind == "other") { error = "这是安装程序，不是压缩包，需要直接运行。"; return false; }

            outDir = file + "_x";
            try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); }
            catch { }
            Directory.CreateDirectory(outDir);

            if (kind == "zip")
            {
                string o;
                int rc = Run("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "
                  + "\"Expand-Archive -LiteralPath '" + file.Replace("'", "''") + "' -DestinationPath '"
                  + outDir.Replace("'", "''") + "' -Force\"", 180000, out o);
                if (rc != 0) { error = "解压失败（exit=" + rc + "）：" + FirstLine(o); return false; }
                return true;
            }
            if (kind == "cab")
            {
                string o;
                int rc = Run("expand.exe", "\"" + file + "\" -F:* \"" + outDir + "\"", 180000, out o);
                if (rc != 0) { error = "cab 解包失败（exit=" + rc + "）：" + FirstLine(o); return false; }
                return true;
            }
            error = "不认识的压缩格式。";
            return false;
        }

        /// <summary>在目录里找第一个可安装的 INF（优先带 [Manufacturer] 段的）。</summary>
        public static string FindInf(string dir)
        {
            try
            {
                string[] files = Directory.GetFiles(dir, "*.inf", SearchOption.AllDirectories);
                string fallback = "";
                for (int i = 0; i < files.Length; i++)
                {
                    if (fallback.Length == 0) fallback = files[i];
                    try
                    {
                        string[] lines = File.ReadAllLines(files[i], Encoding.GetEncoding(936));
                        for (int k = 0; k < lines.Length; k++)
                            if (lines[k].Trim().StartsWith("[Manufacturer]", StringComparison.OrdinalIgnoreCase))
                                return files[i];
                    }
                    catch { }
                }
                return fallback;
            }
            catch { return ""; }
        }

        /// <summary>运行厂商安装包（交互式）。多数品牌的一键安装包需要人工点下一步。</summary>
        public static bool Launch(string file, out string error)
        {
            error = "";
            if (file == null || !File.Exists(file)) { error = "文件不存在：" + file; return false; }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file);
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Path.GetDirectoryName(file);
                Process.Start(psi);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // ================= 底层工具 =================

        private static int Run(string exe, string args, int timeoutMs, out string output)
        {
            output = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    string e = p.StandardError.ReadToEnd();
                    p.WaitForExit(timeoutMs);
                    output = (o + (e.Length > 0 ? "\n" + e : "")).Trim();
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { output = ex.Message; return -1; }
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOf('\n');
            string t = i >= 0 ? s.Substring(0, i) : s;
            return t.Trim();
        }

        /// <summary>跟随一次 302 拿真实直链（拿不到就返回空串）。</summary>
        public static string ResolveRedirect(string url, int timeoutMs, out string error)
        {
            error = "";
            if (url == null || url.Length == 0) { error = "没有地址。"; return ""; }
            HttpWebRequest req = null;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.UserAgent = Ua;
                req.AllowAutoRedirect = false;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = null;   // 关掉系统代理自动探测：开着时 .NET 会去问 WPAD，单次能卡十几秒

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    string loc = resp.Headers["Location"];
                    if (loc == null || loc.Length == 0) return url;
                    return Absolutize(url, loc);
                }
            }
            catch (WebException wex)
            {
                // .NET 对 3xx 也可能抛 WebException —— 从 Response 里把 Location 捞出来
                if (wex.Response != null)
                {
                    try
                    {
                        string loc = wex.Response.Headers["Location"];
                        if (loc != null && loc.Length > 0) return Absolutize(url, loc);
                    }
                    catch { }
                }
                error = wex.Message;
                return "";
            }
            catch (Exception ex) { error = ex.Message; return ""; }
        }

        private static string Absolutize(string baseUrl, string loc)
        {
            if (loc.StartsWith("http://") || loc.StartsWith("https://")) return loc;
            try { return new Uri(new Uri(baseUrl), loc).ToString(); }
            catch { return loc; }
        }

        private static bool FetchPage(string url, int timeoutMs, out string html, out int status, out string error)
        {
            html = "";
            status = 0;
            error = "";
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.UserAgent = Ua;
                req.Accept = "text/html,application/xhtml+xml";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = null;   // 同上：不要走自动代理探测
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    status = (int)resp.StatusCode;
                    byte[] raw = ReadAll(resp);
                    html = DecodeHtml(raw, resp.ContentType);
                }
                return status == 200;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static byte[] ReadAll(HttpWebResponse resp)
        {
            using (Stream st = resp.GetResponseStream())
            {
                if (st == null) return new byte[0];
                MemoryStream ms = new MemoryStream();
                byte[] buf = new byte[65536];
                int n;
                while ((n = st.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// 中文驱动站很常见「不声明 charset 的 GBK 页面」。
        /// 先用严格 UTF-8 试解，失败就按 GB18030(936) 解 —— 比看 header 可靠。
        /// </summary>
        private static string DecodeHtml(byte[] raw, string contentType)
        {
            if (raw == null || raw.Length == 0) return "";

            try
            {
                if (contentType != null && contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int i = contentType.ToLowerInvariant().IndexOf("charset=");
                    string cs = contentType.Substring(i + 8).Trim().Trim('"', ';');
                    return Encoding.GetEncoding(cs).GetString(raw);
                }
            }
            catch { }

            try { return new UTF8Encoding(false, true).GetString(raw); }
            catch { }

            try { return Encoding.GetEncoding(936).GetString(raw); }
            catch { return Encoding.UTF8.GetString(raw); }
        }

        private static string StripTags(string s)
        {
            if (s == null) return "";
            string t = Regex.Replace(s, "<[^>]+>", "");
            t = t.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                 .Replace("&quot;", "\"").Replace("&nbsp;", " ");
            return Regex.Replace(t, "\\s+", " ").Trim();
        }
    }
}
