using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PrinterIpFix
{
    /// <summary>
    /// 极简 IPP 客户端：Get-Printer-Attributes（读能力）+ Print-Job（直发文档）。
    /// IPP 全部多字节字段是大端序，这里所有读写都按大端手工处理。
    /// </summary>
    internal static class Ipp
    {
        /// <summary>Get-Printer-Attributes</summary>
        private const int OpGetPrinterAttributes = 0x000B;
        /// <summary>Print-Job：把文档字节直接交给设备，由设备自己解释格式。</summary>
        private const int OpPrintJob = 0x0002;

        private static readonly string[] Paths = new string[] { "/ipp/print", "/ipp", "/" };

        /// <summary>探测时显式要的属性。少要点，响应小、快。</summary>
        private static readonly string[] ProbeAttrs = new string[]
        {
            "printer-name",
            "printer-info",
            "printer-make-and-model",
            "printer-device-id",
            "printer-uuid",
            "printer-state",
            "printer-state-reasons",
            "printer-is-accepting-jobs",
            "queued-job-count",
            "document-format-supported",
            "document-format-default",
        };

        /// <summary>最近一次 IPP 调用的诊断信息（状态码 / 错误原因），给界面日志用。</summary>
        public static string LastNote = "";

        private sealed class Attrs
        {
            public readonly Dictionary<string, string> Text =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<int>> Ints =
                new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>探测一个 IP 上的 IPP 设备，成功则填充 dev。</summary>
        public static bool Probe(DeviceInfo dev, int timeoutMs)
        {
            string lastErr = "";
            for (int i = 0; i < Paths.Length; i++)
            {
                try
                {
                    Attrs a = Query(dev.Ip, Paths[i], timeoutMs, ProbeAttrs);
                    if (a == null) continue;
                    Fill(dev, a);
                    dev.HasIpp = true;
                    dev.Error = "";
                    return true;
                }
                catch (WebException wex)
                {
                    lastErr = DescribeWebError(wex);
                    var resp = wex.Response as HttpWebResponse;
                    if (resp != null)
                    {
                        int code = (int)resp.StatusCode;
                        resp.Close();
                        // 401/403 说明这台是打印机但要认证；其余 4xx 换路径再试
                        if (code == 401 || code == 403) break;
                    }
                }
                catch (Exception ex)
                {
                    lastErr = ex.Message;
                }
            }
            if (lastErr.Length > 0) dev.Error = lastErr;
            return false;
        }

        /// <summary>
        /// 问设备支持哪些文档格式（document-format-supported），返回逗号分隔串。
        /// 探不到返回空串 —— 空串表示「不知道」，不能据此判定不支持 PDF。
        /// </summary>
        public static string DocumentFormats(string ip, int timeoutMs)
        {
            LastNote = "";
            string[] want = new string[] { "document-format-supported", "document-format-default" };
            for (int i = 0; i < Paths.Length; i++)
            {
                try
                {
                    Attrs a = Query(ip, Paths[i], timeoutMs, want);
                    if (a == null) continue;
                    string v = Get(a, "document-format-supported");
                    if (v.Length > 0) return v;
                    return "";
                }
                catch (WebException wex)
                {
                    LastNote = DescribeWebError(wex);
                    var resp = wex.Response as HttpWebResponse;
                    if (resp != null)
                    {
                        int code = (int)resp.StatusCode;
                        resp.Close();
                        if (code == 401 || code == 403) break;
                    }
                }
                catch (Exception ex)
                {
                    LastNote = ex.Message;
                }
            }
            return "";
        }

        /// <summary>在逗号分隔的格式串里找某个 mime 类型（忽略大小写）。</summary>
        public static bool HasFormat(string formats, string mime)
        {
            if (formats == null || formats.Length == 0 || mime == null) return false;
            string[] parts = formats.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                // document-format 可能带 charset 后缀，如 "text/plain; charset=utf-8"
                string t = parts[i].Trim();
                int semi = t.IndexOf(';');
                if (semi >= 0) t = t.Substring(0, semi).Trim();
                if (string.Compare(t, mime, StringComparison.OrdinalIgnoreCase) == 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 用 IPP Print-Job 把文档直接发给设备。
        /// 成功返回 true，并把 job-id 写进 LastNote；失败返回 false，LastNote 里是状态码与原因。
        /// 注意：这是绕过 Windows 打印队列的直发通道 —— 只有设备真正受理才算成功，
        /// 和 spooler 的「已发送到端口」是两码事。
        /// </summary>
        public static bool PrintJob(string ip, string path, string documentFormat, byte[] body,
            string jobName, int timeoutMs)
        {
            LastNote = "";
            if (path == null || path.Length == 0) path = "/ipp/print";
            if (body == null) body = new byte[0];
            if (documentFormat == null) documentFormat = "application/octet-stream";

            byte[] req = BuildPrintJobRequest(ip, path, documentFormat, jobName, body.Length);

            try
            {
                var uri = new Uri("http://" + ip + ":631" + path);
                var hr = (HttpWebRequest)WebRequest.Create(uri);
                hr.Method = "POST";
                hr.ContentType = "application/ipp";
                hr.Accept = "application/ipp";
                hr.Timeout = timeoutMs;
                hr.ReadWriteTimeout = timeoutMs;
                hr.Proxy = null;
                hr.KeepAlive = false;
                hr.UserAgent = "PrinterIpFix/1.0";
                hr.ContentLength = req.Length + body.Length;
                try { hr.ServicePoint.Expect100Continue = false; } catch { }

                using (Stream s = hr.GetRequestStream())
                {
                    s.Write(req, 0, req.Length);
                    if (body.Length > 0) s.Write(body, 0, body.Length);
                }

                using (var resp = (HttpWebResponse)hr.GetResponse())
                using (Stream rs = resp.GetResponseStream())
                {
                    byte[] raw = ReadAll(rs);
                    int status = raw.Length >= 4 ? Be16(raw, 2) : -1;
                    Attrs a = null;
                    try { a = Parse(raw); } catch { }
                    if (status == 0)
                    {
                        string jid = "";
                        if (a != null)
                        {
                            List<int> ids;
                            if (a.Ints.TryGetValue("job-id", out ids) && ids.Count > 0)
                                jid = ids[0].ToString();
                            string st = Get(a, "job-state");
                            if (st.Length == 0 && a.Ints.ContainsKey("job-state"))
                            {
                                List<int> sv = a.Ints["job-state"];
                                if (sv.Count > 0) st = sv[0].ToString();
                            }
                            LastNote = "job-id=" + jid + " job-state=" + st;
                        }
                        else LastNote = "状态 0x0000";
                        return true;
                    }
                    LastNote = "IPP status=0x" + status.ToString("X4") + " " + StatusText(status);
                    return false;
                }
            }
            catch (WebException wex)
            {
                LastNote = DescribeWebError(wex);
                return false;
            }
            catch (Exception ex)
            {
                LastNote = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>把 IPP 状态码翻成人话。常见：0x040A 不支持该文档格式。</summary>
        public static string StatusText(int status)
        {
            switch (status)
            {
                case 0x0000: return "成功";
                case 0x0400: return "请求格式不对（bad-request）";
                case 0x0401: return "需要认证（not-authorized）";
                case 0x0403: return "设备拒绝（forbidden）";
                case 0x0405: return "不接受作业（not-accepting-jobs）";
                case 0x0407: return "作业已取消（job-canceled）";
                case 0x040A: return "设备不支持这种文档格式（document-format-not-supported）";
                case 0x040B: return "属性值不支持（attributes-or-values-not-supported）";
                case 0x0409: return "没这个属性（not-found）";
                case 0x0500: return "设备内部错误（internal-error）";
                case 0x0501: return "操作不支持（operation-not-supported）";
                case 0x0502: return "服务不可用（service-unavailable）";
                case 0x0507: return "设备忙（server-busy）";
                default: return "未收录的状态码";
            }
        }

        private static string DescribeWebError(WebException wex)
        {
            var resp = wex.Response as HttpWebResponse;
            if (resp != null)
            {
                string s = "HTTP " + (int)resp.StatusCode + " " + resp.StatusDescription;
                resp.Close();
                return s;
            }
            if (wex.Status == WebExceptionStatus.Timeout) return "超时";
            return wex.Message;
        }

        private static byte[] ReadAll(Stream rs)
        {
            var ms = new MemoryStream();
            var buf = new byte[16384];
            int n;
            int guard = 0;
            while ((n = rs.Read(buf, 0, buf.Length)) > 0)
            {
                ms.Write(buf, 0, n);
                if (++guard > 512) break;   // 8MB 上限，防呆
            }
            return ms.ToArray();
        }

        private static Attrs Query(string ip, string path, int timeoutMs, string[] requestedAttributes)
        {
            byte[] req = BuildRequest(ip, path, requestedAttributes);
            var uri = new Uri("http://" + ip + ":631" + path);
            var hr = (HttpWebRequest)WebRequest.Create(uri);
            hr.Method = "POST";
            hr.ContentType = "application/ipp";
            hr.Accept = "application/ipp";
            hr.Timeout = timeoutMs;
            hr.ReadWriteTimeout = timeoutMs;
            hr.Proxy = null;
            hr.KeepAlive = false;
            hr.UserAgent = "PrinterIpFix/1.0";
            hr.ContentLength = req.Length;
            try { hr.ServicePoint.Expect100Continue = false; } catch { }

            using (Stream s = hr.GetRequestStream())
            {
                s.Write(req, 0, req.Length);
            }
            using (var resp = (HttpWebResponse)hr.GetResponse())
            using (Stream rs = resp.GetResponseStream())
            {
                return Parse(ReadAll(rs));
            }
        }

        // ---------- 编码 ----------

        private static void AddBe16(List<byte> b, int v)
        {
            b.Add((byte)((v >> 8) & 0xFF));
            b.Add((byte)(v & 0xFF));
        }

        private static void AddBe32(List<byte> b, int v)
        {
            b.Add((byte)((v >> 24) & 0xFF));
            b.Add((byte)((v >> 16) & 0xFF));
            b.Add((byte)((v >> 8) & 0xFF));
            b.Add((byte)(v & 0xFF));
        }

        private static void AddAttr(List<byte> b, byte tag, string name, string value)
        {
            byte[] nb = Encoding.UTF8.GetBytes(name);
            byte[] vb = Encoding.UTF8.GetBytes(value == null ? "" : value);
            b.Add(tag);
            AddBe16(b, nb.Length);
            b.AddRange(nb);
            AddBe16(b, vb.Length);
            b.AddRange(vb);
        }

        /// <summary>
        /// 写一个 1setOf keyword 属性：第一个值带属性名，后续值名长写 0。
        /// 这一点写错设备会回 0x0409（not-found），而不是少拿一个值。
        /// </summary>
        private static void AddKeywordSet(List<byte> b, string name, string[] values)
        {
            if (values == null || values.Length == 0) return;
            for (int i = 0; i < values.Length; i++)
            {
                byte[] nb = i == 0 ? Encoding.UTF8.GetBytes(name) : new byte[0];
                byte[] vb = Encoding.UTF8.GetBytes(values[i]);
                b.Add(0x44);                     // keyword
                AddBe16(b, nb.Length);
                b.AddRange(nb);
                AddBe16(b, vb.Length);
                b.AddRange(vb);
            }
        }

        private static byte[] BuildRequest(string ip, string path, string[] requestedAttributes)
        {
            var b = new List<byte>(256);
            b.Add(0x01); b.Add(0x01);                    // IPP 1.1
            AddBe16(b, OpGetPrinterAttributes);
            AddBe32(b, 1);                               // request-id
            b.Add(0x01);                                 // operation-attributes-tag
            AddAttr(b, 0x47, "attributes-charset", "utf-8");
            AddAttr(b, 0x48, "attributes-natural-language", "en");
            AddAttr(b, 0x45, "printer-uri", "ipp://" + ip + ":631" + path);
            AddKeywordSet(b, "requested-attributes", requestedAttributes);
            b.Add(0x03);                                 // end-of-attributes-tag
            return b.ToArray();
        }

        private static byte[] BuildPrintJobRequest(string ip, string path, string documentFormat,
            string jobName, int documentLength)
        {
            var b = new List<byte>(256);
            b.Add(0x01); b.Add(0x01);                    // IPP 1.1
            AddBe16(b, OpPrintJob);
            AddBe32(b, 9);                               // request-id（随便，只要非 0）
            b.Add(0x01);                                 // operation-attributes-tag
            AddAttr(b, 0x47, "attributes-charset", "utf-8");
            AddAttr(b, 0x48, "attributes-natural-language", "zh-cn");
            AddAttr(b, 0x45, "printer-uri", "ipp://" + ip + ":631" + path);
            AddAttr(b, 0x42, "requesting-user-name", "PrinterToolbox");
            AddAttr(b, 0x42, "job-name", jobName == null ? "toolbox-test-page" : jobName);
            AddAttr(b, 0x49, "document-format", documentFormat);
            b.Add(0x03);                                 // end-of-attributes-tag，后面直接跟文档字节
            return b.ToArray();
        }

        private static int Be16(byte[] b, int off)
        {
            return (b[off] << 8) | b[off + 1];
        }

        private static int Be32(byte[] b, int off)
        {
            return (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];
        }

        // ---------- 解析 ----------

        private static Attrs Parse(byte[] b)
        {
            var a = new Attrs();
            int len = b.Length;
            if (len < 8) throw new InvalidDataException("响应太短（" + len + " 字节）");
            int major = b[0], minor = b[1];
            if (major != 1 && major != 2)
                throw new InvalidDataException("不是 IPP 响应（版本字节 " + major + "." + minor + "）");

            int status = Be16(b, 2);
            if (status != 0) LastNote = "IPP status=0x" + status.ToString("X4") + " " + StatusText(status);

            int p = 8;
            string lastName = "";
            while (p < len)
            {
                // 布局：tag(1) + name-length(2) + name + value-length(2) + value
                // 注意 tag 自己先吃掉，否则后面每个长度都错一位
                byte tag = b[p];
                p++;
                if (tag == 0x03) break;                       // end-of-attributes
                if (tag <= 0x05) continue;                    // 分组标签，无负载

                if (p + 2 > len) break;
                int nameLen = Be16(b, p); p += 2;
                if (nameLen < 0 || p + nameLen > len) break;
                string name = Encoding.UTF8.GetString(b, p, nameLen);
                p += nameLen;

                if (p + 2 > len) break;
                int valLen = Be16(b, p); p += 2;
                if (valLen < 0 || p + valLen > len) break;

                if (nameLen > 0) lastName = name;
                else name = lastName;                         // 多值：后续值名字为空

                if (tag == 0x21 || tag == 0x22 || tag == 0x23)
                {
                    // integer / boolean / enum。boolean 只占 1 字节，不能按 4 字节读。
                    if (name.Length > 0)
                    {
                        List<int> list;
                        if (!a.Ints.TryGetValue(name, out list)) { list = new List<int>(); a.Ints[name] = list; }
                        if (valLen == 4) list.Add(Be32(b, p));
                        else if (valLen == 1) list.Add((int)b[p]);
                    }
                }
                else if (tag == 0x31 || tag == 0x32 || tag == 0x33)
                {
                    // dateTime / resolution / rangeOfInteger：跳过二进制值
                }
                else if (name.Length > 0)
                {
                    // 其余一律按 UTF-8 文本收：keyword(0x44) / uri(0x45) / charset(0x47)
                    // naturalLanguage(0x48) / mimeMediaType(0x49) / nameWithoutLanguage(0x42)
                    // textWithoutLanguage(0x41) / octetString(0x30)。
                    // 早先这里把 0x44 keyword 整个跳过，导致 printer-state-reasons 永远读成空。
                    string v = Encoding.UTF8.GetString(b, p, valLen);
                    string exist;
                    if (a.Text.TryGetValue(name, out exist) && nameLen == 0)
                        a.Text[name] = exist + "," + v;
                    else if (nameLen > 0 || a.Text.ContainsKey(name))
                        a.Text[name] = v;
                }
                p += valLen;
            }
            return a;
        }

        private static void Fill(DeviceInfo dev, Attrs a)
        {
            dev.IppName = Get(a, "printer-name");
            if (dev.IppName.Length == 0) dev.IppName = Get(a, "printer-info");
            dev.Model = Get(a, "printer-make-and-model");
            dev.Uuid = Get(a, "printer-uuid");
            dev.DeviceId = Get(a, "printer-device-id");

            List<int> st;
            if (a.Ints.TryGetValue("printer-state", out st) && st.Count > 0) dev.IppState = st[0];

            dev.StateReasons = Get(a, "printer-state-reasons");
            if (dev.StateReasons.Length == 0) dev.StateReasons = "none";

            string fmts = Get(a, "document-format-supported");
            if (fmts.Length > 0)
            {
                dev.Formats = fmts;
                dev.FormatsProbed = true;
            }

            dev.Serial = ExtractSerial(dev.DeviceId);
            if (dev.Serial.Length == 0) dev.Serial = ExtractSerial(dev.IppName + " " + dev.Model);
        }

        private static string Get(Attrs a, string key)
        {
            string v;
            return a.Text.TryGetValue(key, out v) ? (v == null ? "" : v.Trim()) : "";
        }

        private static string ExtractSerial(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            Match m = Regex.Match(s, @"\b(?:SN|SER|SERIAL|S/N)\s*[:=]\s*([A-Za-z0-9\-]{4,})",
                RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
            return "";
        }
    }
}
