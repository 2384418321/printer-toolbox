using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PrinterIpFix
{
    /// <summary>一条网上的驱动页搜索结果。</summary>
    internal sealed class DriverPage
    {
        public string Url;
        public string Title;
        public int Score;
        public override string ToString()
        {
            return Score > 0 ? (Title + "   [" + Score + "]") : Title;
        }
    }

    /// <summary>
    /// 驱动查询：把设备自报的厂商/型号变成 printer 驱动站上的具体页面。
    /// 只用公开的搜索页 + 公开的型号页，不下载、不执行任何第三方程序。
    /// </summary>
    internal static class DriverLookup
    {
        public const string Site = "https://www.dyjqd.com";

        // 老 .NET 默认可能还是 TLS1.0，访问 https 站会直接失败
        public static void EnableTls()
        {
            try { ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072; }
            catch { }
        }

        /// <summary>厂商名/设备 ID 里的 MFG 归一成站点的目录名。</summary>
        public static string VendorSlug(string vendor)
        {
            if (vendor == null) return "";
            string v = vendor.ToUpperInvariant();

            if (v.IndexOf("HP") >= 0 || v.IndexOf("HEWLETT") >= 0) return "hp";
            if (v.IndexOf("CANON") >= 0) return "canon";
            if (v.IndexOf("EPSON") >= 0 || v.IndexOf("SEIKO") >= 0) return "epson";
            if (v.IndexOf("BROTHER") >= 0) return "brother";
            if (v.IndexOf("LENOVO") >= 0) return "lenovo";
            if (v.IndexOf("PANTUM") >= 0) return "pantum";
            if (v.IndexOf("RICOH") >= 0) return "ricoh";
            if (v.IndexOf("KONICA") >= 0 || v.IndexOf("MINOLTA") >= 0) return "driver/konicaminolta";
            if (v.IndexOf("XEROX") >= 0 || v.IndexOf("FUJI") >= 0) return "fujixerox";
            if (v.IndexOf("KYOCERA") >= 0) return "kyocera";
            if (v.IndexOf("SAMSUNG") >= 0) return "samsung";
            if (v.IndexOf("DELL") >= 0) return "dell";
            if (v.IndexOf("DELI") >= 0 || v.IndexOf("得力") >= 0) return "driver/deli";
            if (v.IndexOf("HPRT") >= 0 || v.IndexOf("汉印") >= 0) return "driver/hprt";
            if (v.IndexOf("GAINSCHA") >= 0 || v.IndexOf("佳博") >= 0) return "gainscha";
            if (v.IndexOf("ZEBRA") >= 0) return "zebra";
            if (v.IndexOf("TSC") >= 0) return "driver/tsc";
            if (v.IndexOf("JOLIMARK") >= 0 || v.IndexOf("映美") >= 0) return "driver/jolimark";

            // 兜底：取第一段字母数字
            string s = Regex.Replace(vendor.ToLowerInvariant(), "[^a-z0-9]+", "");
            return s;
        }

        public static string ModelSlug(string model)
        {
            if (model == null) return "";
            string s = model.ToLowerInvariant();
            s = s.Replace("series", "");
            s = Regex.Replace(s, "[^a-z0-9]+", "_");
            s = s.Trim('_');
            return s;
        }

        /// <summary>直接拼几种可能的详情页 URL（用于不搜也能命中的情况）。</summary>
        public static string[] GuessPages(string vendorSlug, string modelSlug)
        {
            if (vendorSlug.Length == 0 || modelSlug.Length == 0)
                return new string[0];
            return new string[]
            {
                Site + "/" + vendorSlug + "/" + vendorSlug + "_" + modelSlug + ".html",
                Site + "/" + vendorSlug + "/" + modelSlug + ".html",
                Site + "/" + "driver/" + vendorSlug + "/" + modelSlug + ".html",
                Site + "/" + "driver/" + vendorSlug + "/" + vendorSlug + "_" + modelSlug + ".html"
            };
        }

        /// <summary>站内搜索地址（用户点开自己看结果，不代下载）。</summary>
        public static string SearchUrl(string keyword)
        {
            string k = keyword == null ? "" : keyword.Trim();
            if (k.Length == 0) return Site + "/";
            return Site + "/plus/search.php?searchtype=titlekeyword&keyword=" + UrlEncode(k);
        }

        /// <summary>把关键词送进站点搜索，解析出候选驱动页并按型号相似度打分。</summary>
        public static List<DriverPage> Search(string keyword, int timeoutMs, out string error)
        {
            error = "";
            List<DriverPage> list = new List<DriverPage>();
            if (keyword == null || keyword.Trim().Length == 0)
            {
                error = "关键词为空。";
                return list;
            }

            string url = Site + "/plus/search.php?searchtype=titlekeyword&keyword=" + UrlEncode(keyword.Trim());
            string html;
            int status;
            if (!HttpGet(url, timeoutMs, out html, out status, out error))
                return list;
            if (status != 200)
            {
                error = "站点返回 HTTP " + status + "。";
                return list;
            }

            string norm = Normalize(keyword);
            MatchCollection ms = Regex.Matches(html,
                "<a[^>]+href=\"(/[^\"]+?\\.html)\"[^>]*>(.*?)</a>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            Dictionary<string, DriverPage> seen = new Dictionary<string, DriverPage>();
            for (int i = 0; i < ms.Count; i++)
            {
                string href = ms[i].Groups[1].Value;
                string text = StripTags(ms[i].Groups[2].Value);
                if (text.Length == 0) continue;
                if (href.IndexOf("sitemap", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (href.IndexOf("/article/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (href.IndexOf("/plus/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (seen.ContainsKey(href)) continue;

                int score = ScoreOf(Normalize(text), norm);
                if (score <= 0) continue;

                DriverPage p = new DriverPage();
                p.Url = Site + href;
                p.Title = text;
                p.Score = score;
                seen[href] = p;
                list.Add(p);
            }

            list.Sort(delegate(DriverPage a, DriverPage b) { return b.Score.CompareTo(a.Score); });
            if (list.Count == 0 && error.Length == 0)
                error = "站内没搜到匹配「" + keyword + "」的驱动页。";
            return list;
        }

        private static int ScoreOf(string text, string normKeyword)
        {
            if (text.Length == 0 || normKeyword.Length == 0) return 0;
            int score = 0;

            if (text.IndexOf(normKeyword, StringComparison.Ordinal) >= 0) score += 100;

            // 型号里最特殊的那个片段（带数字的）命中才算强相关
            string[] parts = normKeyword.Split(' ');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.Length < 2) continue;
                if (!Regex.IsMatch(p, "[0-9]")) continue;
                if (text.IndexOf(p, StringComparison.Ordinal) >= 0) score += 40;
            }

            if (text.IndexOf("驱动", StringComparison.Ordinal) >= 0) score += 5;
            if (text.IndexOf("一体机", StringComparison.Ordinal) >= 0) score += 1;
            return score;
        }

        private static string Normalize(string s)
        {
            if (s == null) return "";
            string t = s.ToUpperInvariant();
            t = t.Replace("打印机", " ").Replace("驱动", " ").Replace("下载", " ");
            t = Regex.Replace(t, "[^A-Z0-9\u4e00-\u9fa5]+", " ");
            return t.Trim();
        }

        private static string StripTags(string s)
        {
            if (s == null) return "";
            string t = Regex.Replace(s, "<[^>]+>", "");
            t = t.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                 .Replace("&quot;", "\"").Replace("&nbsp;", " ");
            return Regex.Replace(t, "\\s+", " ").Trim();
        }

        private static string UrlEncode(string s)
        {
            byte[] bytes;
            bool ascii = true;
            for (int i = 0; i < s.Length; i++) { if (s[i] > 127) { ascii = false; break; } }

            if (ascii) bytes = Encoding.UTF8.GetBytes(s);
            else
            {
                try { bytes = Encoding.GetEncoding("gb2312").GetBytes(s); }
                catch { bytes = Encoding.UTF8.GetBytes(s); }
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                if ((b >= '0' && b <= '9') || (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z')
                    || b == '-' || b == '_' || b == '.' || b == '~')
                    sb.Append((char)b);
                else
                    sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        private static bool HttpGet(string url, int timeoutMs, out string body, out int status, out string error)
        {
            body = "";
            status = 0;
            error = "";
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PrinterToolbox/1.0";
                req.Accept = "text/html,application/xhtml+xml";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AllowAutoRedirect = true;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    status = (int)resp.StatusCode;
                    using (Stream st = resp.GetResponseStream())
                    using (StreamReader sr = new StreamReader(st, Encoding.UTF8, true))
                        body = sr.ReadToEnd();
                }
                return true;
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    try { status = (int)((HttpWebResponse)wex.Response).StatusCode; }
                    catch { }
                    error = "HTTP " + status;
                }
                else error = wex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>探测某个 URL 是否真的存在（200 才算数）。</summary>
        public static bool Exists(string url, int timeoutMs)
        {
            string body;
            int status;
            string err;
            if (!HttpGet(url, timeoutMs, out body, out status, out err)) return false;
            return status == 200;
        }

        public static void OpenUrl(string url)
        {
            if (url == null || url.Length == 0) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }
    }
}
