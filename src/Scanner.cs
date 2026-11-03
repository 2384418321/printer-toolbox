using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace PrinterIpFix
{
    internal static class Scanner
    {
        public const int MaxHosts = 4096;

        // ---------- 扫描范围 ----------

        public static uint Ip2U(IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | (uint)b[3];
        }

        public static IPAddress U2Ip(uint v)
        {
            return new IPAddress(new byte[] {
                (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        }

        /// <summary>本机所有活动 IPv4 网段（去掉网络地址与广播地址）</summary>
        public static List<uint[]> LocalRanges()
        {
            var res = new List<uint[]>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties props = ni.GetIPProperties();
                    if (props == null) continue;
                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        byte[] ab = ua.Address.GetAddressBytes();
                        if (ab[0] == 127) continue;
                        if (ab[0] == 169 && ab[1] == 254) continue;   // APIPA
                        if (ab[0] == 0) continue;
                        IPAddress mask = ua.IPv4Mask;
                        if (mask == null) continue;
                        uint m = Ip2U(mask);
                        if (m == 0) continue;
                        uint net = Ip2U(ua.Address) & m;
                        uint bc = net | (~m);
                        uint lo = net + 1;
                        uint hi = bc - 1;
                        if (hi < lo) continue;
                        res.Add(new uint[] { lo, hi });
                    }
                }
            }
            catch { }
            return res;
        }

        public static string DescribeLocalRanges()
        {
            var parts = new List<string>();
            foreach (uint[] r in LocalRanges())
            {
                string a = U2Ip(r[0]).ToString();
                string b = U2Ip(r[1]).ToString();
                // 同一 /24 内才缩写成「起-末段」；跨网段（如 /23）必须写全，否则会把范围截断
                int cut = a.LastIndexOf('.');
                string head = cut > 0 ? a.Substring(0, cut) : a;
                if (b.StartsWith(head + ".", StringComparison.Ordinal)) b = b.Split('.')[3];
                parts.Add(a + "-" + b);
            }
            return parts.Count == 0 ? "(未检测到网卡)" : string.Join(" ; ", parts.ToArray());
        }

        public static List<IPAddress> ParseTargets(string text, out string error)
        {
            error = "";
            var result = new List<IPAddress>();
            var seen = new HashSet<string>();
            string spec = (text == null) ? "" : text.Trim();

            if (spec.Length == 0)
            {
                foreach (uint[] r in LocalRanges()) AddRange(result, seen, r[0], r[1]);
                if (result.Count > MaxHosts)
                {
                    error = "本机网段共有 " + result.Count + " 个地址，超过上限 " + MaxHosts
                        + "。请手动指定更小的范围（例：192.168.1.1-254）。";
                    return new List<IPAddress>();
                }
                return result;
            }

            string[] parts = spec.Split(new char[] { ';', ',', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in parts)
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                uint lo, hi;
                if (!ParseOne(p, out lo, out hi, out error)) return new List<IPAddress>();
                AddRange(result, seen, lo, hi);
                if (result.Count > MaxHosts)
                {
                    error = "范围展开后有 " + result.Count + " 个地址，超过上限 " + MaxHosts + "。";
                    return new List<IPAddress>();
                }
            }
            return result;
        }

        private static bool ParseOne(string s, out uint lo, out uint hi, out string error)
        {
            lo = 0; hi = 0; error = "";
            try
            {
                if (s.IndexOf('/') > 0)
                {
                    string[] p = s.Split('/');
                    IPAddress a = IPAddress.Parse(p[0].Trim());
                    int bits = int.Parse(p[1].Trim());
                    if (bits < 8 || bits > 32) { error = "CIDR 前缀必须在 8-32 之间：" + s; return false; }
                    uint m = bits == 0 ? 0u : (0xFFFFFFFFu << (32 - bits));
                    uint net = Ip2U(a) & m;
                    uint bc = net | (~m);
                    lo = net + 1;
                    hi = (bits >= 31) ? bc : bc - 1;
                    return true;
                }
                if (s.IndexOf('-') > 0)
                {
                    string[] p = s.Split('-');
                    string left = p[0].Trim();
                    string right = p[1].Trim();
                    IPAddress start = IPAddress.Parse(left);
                    IPAddress end;
                    if (right.IndexOf('.') > 0)
                        end = IPAddress.Parse(right);
                    else
                    {
                        byte[] sb = start.GetAddressBytes();
                        int last = int.Parse(right);
                        if (last < 0 || last > 255) { error = "末段必须在 0-255：" + s; return false; }
                        sb[3] = (byte)last;
                        end = new IPAddress(sb);
                    }
                    lo = Ip2U(start); hi = Ip2U(end);
                    if (hi < lo) { error = "范围起止反了：" + s; return false; }
                    return true;
                }
                // a.b.c.d
                int dots = 0;
                foreach (char c in s) if (c == '.') dots++;
                if (dots == 3)
                {
                    IPAddress a = IPAddress.Parse(s);
                    lo = hi = Ip2U(a);
                    return true;
                }
                if (dots == 2)
                {
                    IPAddress a = IPAddress.Parse(s + ".0");
                    uint net = Ip2U(a);
                    lo = net + 1; hi = net + 254;
                    return true;
                }
                error = "看不懂这个范围写法：" + s + "（支持 192.168.1.0/24、192.168.1.10-60、192.168.1.20）";
                return false;
            }
            catch (Exception ex)
            {
                error = "范围解析失败：" + s + " —— " + ex.Message;
                return false;
            }
        }

        private static void AddRange(List<IPAddress> list, HashSet<string> seen, uint lo, uint hi)
        {
            for (uint v = lo; v <= hi; v++)
            {
                IPAddress a = U2Ip(v);
                string k = a.ToString();
                if (seen.Add(k)) list.Add(a);
                if (v == uint.MaxValue) break;
            }
        }

        // ---------- 探测 ----------

        public static bool TcpAlive(string host, int port, int timeoutMs)
        {
            var client = new TcpClient();
            try
            {
                IAsyncResult ar = client.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false)) return false;
                try { client.EndConnect(ar); } catch { return false; }
                return client.Connected;
            }
            catch { return false; }
            finally { try { client.Close(); } catch { } }
        }

        /// <summary>
        /// ping 通性。只用来判别"地址上到底有没有设备"：
        /// 很多打印机深睡时会关掉 631/9100，但仍然应答 ping。
        /// 任何异常都按"不通"处理，不让它影响主流程。
        /// </summary>
        public static bool PingAlive(string host, int timeoutMs)
        {
            if (string.IsNullOrEmpty(host)) return false;
            try
            {
                using (var ping = new System.Net.NetworkInformation.Ping())
                {
                    PingReply r = ping.Send(host, timeoutMs);
                    return r != null && r.Status == IPStatus.Success;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// 端口地址上有没有设备。带重试：打印机深睡时第一次连接常被丢，
        /// 隔 800 ms 再试往往就通了（实测 HP 9000 首次超时、重试即通）。
        /// 返回 true 表示可达；同时通过 pingAlive 回传"地址本身在不在"。
        /// </summary>
        public static bool EnsureAlive(string ip, out bool pingAlive)
        {
            pingAlive = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) Thread.Sleep(800);

                if (TcpAlive(ip, 631, 400) || TcpAlive(ip, 9100, 400) || TcpAlive(ip, 515, 400))
                {
                    // 打印端口能连上，说明地址本身就是可达的（不必再 ping，省 700 ms）
                    pingAlive = true;
                    return true;
                }

                // 端口都不应答时用 ping 判别：ping 通说明设备在、只是不应答打印端口
                if (PingAlive(ip, 700)) pingAlive = true;

                // 最后一次尝试再用完整的 IPP 探测兜底（有些设备 TCP 起来很慢）
                if (attempt == 2)
                {
                    DeviceInfo d = ProbeHost(ip, 300, 1200);
                    if (d != null) return true;
                }
            }
            return false;
        }

        /// <summary>探一个 IP，返回 null 表示不是打印设备。</summary>
        public static DeviceInfo ProbeHost(string ip, int tcpTimeoutMs, int ippTimeoutMs)
        {
            bool ipp = TcpAlive(ip, 631, tcpTimeoutMs);
            bool raw = TcpAlive(ip, 9100, tcpTimeoutMs);
            bool lpd = false;
            if (!ipp && !raw) lpd = TcpAlive(ip, 515, tcpTimeoutMs);
            if (!ipp && !raw && !lpd) return null;

            var d = new DeviceInfo();
            d.Ip = ip;
            d.Has9100 = raw;
            d.HasLpd = lpd;

            var sw = Stopwatch.StartNew();
            if (ipp)
            {
                Ipp.Probe(d, ippTimeoutMs);
                d.HasIpp = true;
            }
            sw.Stop();
            d.RttMs = (int)sw.ElapsedMilliseconds;

            if (string.IsNullOrEmpty(d.IppName))
            {
                string host = TryReverseDns(ip);
                if (host.Length > 0) d.IppName = host;
            }
            return d;
        }

        public static string TryReverseDns(string ip)
        {
            try
            {
                IPHostEntry h = Dns.GetHostEntry(ip);
                return string.IsNullOrEmpty(h.HostName) ? "" : h.HostName;
            }
            catch { return ""; }
        }

        // ---------- 并发扫描 ----------

        public static void Sweep(List<IPAddress> targets, Action<DeviceInfo> onDevice,
            Action<int, int> onProgress, Func<bool> cancelled, int tcpTimeoutMs, int ippTimeoutMs)
        {
            if (targets == null || targets.Count == 0) return;
            var queue = new Queue<IPAddress>(targets);
            var gate = new object();
            int total = targets.Count;
            int done = 0;
            int workers = Math.Min(96, Math.Max(8, Environment.ProcessorCount * 8));
            if (workers > total) workers = total;

            var threads = new List<Thread>();
            for (int i = 0; i < workers; i++)
            {
                var t = new Thread(delegate()
                {
                    while (true)
                    {
                        if (cancelled != null && cancelled()) return;
                        IPAddress ip;
                        lock (gate)
                        {
                            if (queue.Count == 0) return;
                            ip = queue.Dequeue();
                        }
                        try
                        {
                            DeviceInfo d = ProbeHost(ip.ToString(), tcpTimeoutMs, ippTimeoutMs);
                            if (d != null && onDevice != null) onDevice(d);
                        }
                        catch { }
                        int n = Interlocked.Increment(ref done);
                        if (onProgress != null && (n % 4 == 0 || n == total)) onProgress(n, total);
                    }
                });
                t.IsBackground = true;
                t.Name = "sweep" + i;
                t.Start();
                threads.Add(t);
            }
            foreach (Thread t in threads) t.Join();
        }
    }
}
