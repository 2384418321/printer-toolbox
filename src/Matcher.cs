using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PrinterIpFix
{
    internal sealed class MatchCandidate
    {
        public DeviceInfo Device;
        public int Score;
        public string Reason = "";
        public bool SameAddress;    // 端口地址 == 设备 IP（说明本来就指对了）
    }

    /// <summary>
    /// 通用身份匹配：不依赖任何设备清单，只比较
    ///   打印机队列自身（名称 / 驱动 / 端口 / 端口里记的地址）
    /// 与网络上发现的设备（IPP 名称 / 型号 / 设备ID / 序列号 / IP）。
    /// </summary>
    internal static class Matcher
    {
        private static readonly string[] Stop = new string[] {
            "the","and","printer","print","printing","driver","series","universal","class",
            "microsoft","windows","tcp","tcpip","port","ipp","wsd","usb","local","network",
            "copy","scan","fax","pcl","ps","ps3","ps6","xl","plus","pro","ltd","inc",
            "auto","default","direct","standard","generic","built","in"
        };

        public static List<string> Tokens(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            string lower = s.ToLowerInvariant();
            foreach (Match m in Regex.Matches(lower, "[a-z0-9]+"))
            {
                string t = m.Value;
                if (t.Length < 3) continue;
                bool skip = false;
                for (int i = 0; i < Stop.Length; i++)
                    if (Stop[i] == t) { skip = true; break; }
                if (skip) continue;
                if (!list.Contains(t)) list.Add(t);
            }
            return list;
        }

        private static bool TokenMatch(string a, string b)
        {
            if (a == b) return true;
            if (a.Length >= 4 && b.Length >= 4)
                return a.IndexOf(b, StringComparison.Ordinal) >= 0
                    || b.IndexOf(a, StringComparison.Ordinal) >= 0;
            return false;
        }

        private static int CountHits(List<string> left, List<string> right, out List<string> hits)
        {
            hits = new List<string>();
            int n = 0;
            for (int i = 0; i < left.Count; i++)
            {
                for (int j = 0; j < right.Count; j++)
                {
                    if (TokenMatch(left[i], right[j]))
                    {
                        n++;
                        if (!hits.Contains(left[i])) hits.Add(left[i]);
                        break;
                    }
                }
            }
            return n;
        }

        public static List<MatchCandidate> Rank(PrinterInfo p, List<DeviceInfo> devices)
        {
            var result = new List<MatchCandidate>();
            if (devices == null || devices.Count == 0) return result;

            string nameText = StripParen(p.Name);
            List<string> pName = Tokens(nameText);
            List<string> pDriver = Tokens(p.DriverName);
            List<string> dModel = new List<string>();
            List<string> dName = new List<string>();

            foreach (DeviceInfo d in devices)
            {
                var c = new MatchCandidate();
                c.Device = d;
                int score = 0;
                var reasons = new List<string>();

                dModel = Tokens(d.Model);
                // 型号串里常带品牌与描述，补上 device-id 里的 MDL 段
                dModel.AddRange(Tokens(ExtractDeviceIdField(d.DeviceId, "MDL")));
                dName = Tokens(d.IppName);

                if (!string.IsNullOrEmpty(p.ResolvedIp) && p.ResolvedIp == d.Ip)
                {
                    score += 100;
                    c.SameAddress = true;
                    reasons.Add("端口地址就是这台设备");
                    c.Score = 100;
                    c.Reason = string.Join("；", reasons.ToArray());
                    result.Add(c);
                    continue;
                }

                // 序列号出现在队列名或端口名里 —— 最强证据
                if (d.Serial.Length >= 4)
                {
                    string hay = (p.Name + " " + p.PortName + " " + p.RawAddress).ToLowerInvariant();
                    if (hay.IndexOf(d.Serial.ToLowerInvariant(), StringComparison.Ordinal) >= 0)
                    {
                        score += 60;
                        reasons.Add("序列号 " + d.Serial);
                    }
                }

                // 端口里记的主机名 == 设备名称/IP
                if (!string.IsNullOrEmpty(p.RawAddress))
                {
                    string raw = p.RawAddress.ToLowerInvariant();
                    string dn = (d.IppName == null ? "" : d.IppName.ToLowerInvariant());
                    if (!c.SameAddress && raw.Length >= 3 &&
                        (dn.Length >= 3 && (dn.IndexOf(raw, StringComparison.Ordinal) >= 0
                                         || raw.IndexOf(dn, StringComparison.Ordinal) >= 0)))
                    {
                        score += 45;
                        reasons.Add("端口主机名 " + p.RawAddress);
                    }
                }

                // 型号词命中队列名
                List<string> hits;
                int modelHits = CountHits(pName, dModel, out hits);
                if (modelHits > 0)
                {
                    score += Math.Min(55, 35 + 10 * (modelHits - 1));
                    reasons.Add("型号 " + string.Join("/", hits.ToArray()));
                }

                // 设备名称命中队列名
                int nameHits = CountHits(pName, dName, out hits);
                if (nameHits > 0)
                {
                    score += Math.Min(40, 25 + 8 * (nameHits - 1));
                    reasons.Add("设备名 " + string.Join("/", hits.ToArray()));
                }

                // 驱动词命中设备型号
                int drvHits = CountHits(pDriver, dModel, out hits);
                if (drvHits > 0)
                {
                    score += Math.Min(20, 10 * drvHits);
                    reasons.Add("驱动 " + string.Join("/", hits.ToArray()));
                }

                c.Score = Math.Min(100, score);
                c.Reason = reasons.Count == 0 ? "" : string.Join("；", reasons.ToArray());
                if (c.Score > 0) result.Add(c);
            }

            result.Sort(delegate(MatchCandidate a, MatchCandidate b)
            {
                return b.Score.CompareTo(a.Score);
            });
            return result;
        }

        private static string StripParen(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return Regex.Replace(s, @"\([^)]*\)", " ");
        }

        private static string ExtractDeviceIdField(string deviceId, string key)
        {
            if (string.IsNullOrEmpty(deviceId)) return "";
            Match m = Regex.Match(deviceId, key + ":([^;]*)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Replace("_", " ") : "";
        }
    }
}
