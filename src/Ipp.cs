using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PrinterIpFix
{
    /// <summary>
    /// 极简 IPP 客户端：只做 Get-Printer-Attributes。
    /// IPP 全部多字节字段是大端序，这里所有读写都按大端手工处理。
    /// </summary>
    internal static class Ipp
    {
        private const int OpGetPrinterAttributes = 0x000B;

        private static readonly string[] Paths = new string[] { "/ipp/print", "/ipp", "/" };

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
                    Attrs a = Query(dev.Ip, Paths[i], timeoutMs);
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

        private static Attrs Query(string ip, string path, int timeoutMs)
        {
            byte[] req = BuildRequest(ip, path);
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
                var ms = new MemoryStream();
                var buf = new byte[16384];
                int n;
                int guard = 0;
                while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (++guard > 512) break;   // 8MB 上限，防呆
                }
                return Parse(ms.ToArray());
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
            byte[] vb = Encoding.UTF8.GetBytes(value);
            b.Add(tag);
            AddBe16(b, nb.Length);
            b.AddRange(nb);
            AddBe16(b, vb.Length);
            b.AddRange(vb);
        }

        private static byte[] BuildRequest(string ip, string path)
        {
            var b = new List<byte>(256);
            b.Add(0x01); b.Add(0x01);                    // IPP 1.1
            AddBe16(b, OpGetPrinterAttributes);
            AddBe32(b, 1);                               // request-id
            b.Add(0x01);                                 // operation-attributes-tag
            AddAttr(b, 0x47, "attributes-charset", "utf-8");
            AddAttr(b, 0x48, "attributes-natural-language", "en");
            AddAttr(b, 0x45, "printer-uri", "ipp://" + ip + ":631" + path);
            b.Add(0x03);                                 // end-of-attributes-tag
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
                    if (valLen == 4 && name.Length > 0)
                    {
                        List<int> list;
                        if (!a.Ints.TryGetValue(name, out list)) { list = new List<int>(); a.Ints[name] = list; }
                        list.Add(Be32(b, p));
                    }
                }
                else if (tag == 0x31 || tag == 0x32 || tag == 0x33 || tag == 0x44)
                {
                    // dateTime / resolution / rangeOfInteger：跳过二进制值
                }
                else if (name.Length > 0)
                {
                    string v = Encoding.UTF8.GetString(b, p, valLen);
                    string exist;
                    if (a.Text.TryGetValue(name, out exist) && nameLen == 0)
                        a.Text[name] = exist + "," + v;
                    else if (nameLen > 0)
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
