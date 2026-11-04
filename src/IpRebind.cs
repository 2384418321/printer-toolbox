using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PrinterIpFix
{
    /// <summary>
    /// 一条「队列 ↔ 设备」绑定：记住队列原来对应的那台设备是谁（UUID / 序列号 / 型号），
    /// 这样打印机重启换了 IP 之后还能靠身份找回来，而不是靠那个会变的地址。
    /// </summary>
    internal sealed class Binding
    {
        public string Queue = "";
        public string Uuid = "";
        public string Serial = "";
        public string Model = "";
        public string LastIp = "";
        public string LastPort = "";
        public long LastSeen;

        public string Key
        {
            get { return Queue; }
        }

        /// <summary>有没有足够 identity 去找回这台设备（型号不算，太容易重名）。</summary>
        public bool HasIdentity
        {
            get { return Uuid.Length > 0 || Serial.Length > 0; }
        }
    }

    /// <summary>
    /// 打印机换 IP 之后自动把队列端口指回正确地址。
    ///
    /// 判据顺序（从硬到软）：
    ///   ① 设备自报的 printer-uuid —— 出厂唯一，最硬；
    ///   ② printer-device-id 里的 SN —— 同型号多台时靠它区分；
    ///   ③ 都拿不到就不动，并且明说「认不出是哪台」，绝不瞎猜。
    ///
    /// 地址变了以后怎么改：
    ///   · 标准 TCP/IP 端口：直接改端口的 HostAddress（WMI Win32_TCPIPPrinterPort），
    ///     不新建端口、不影响队列上其它设置；
    ///   · WSD / IPP 发现端口：这类端口没有可写的地址字段，改不了；
    ///     若是同一台设备（UUID 相同），优先找本机已有的、指向该 UUID 的端口直接切过去。
    /// </summary>
    internal static class IpRebind
    {
        public static string LastNote = "";
        /// <summary>绑定表落盘时的真实错误（LastNote 会被后续流程覆盖，这里单独留一份用于排查）。</summary>
        public static string LastStorageError = "";

        private const string FileName = "bindings.tsv";
        private static List<Binding> _cache;

        // ================= 绑定表持久化 =================

        public static string StatePath()
        {
            return Path.Combine(StateDir(), FileName);
        }

        private static string _stateDir;

        /// <summary>
        /// 绑定表放哪：真实探测可写性，不靠猜。
        /// 本机实测从聊天窗口/沙箱里点开的 exe 是 Low 完整性，连 %APPDATA% 都
        /// 报 UnauthorizedAccessException，所以按用户目录 → 临时目录 → exe 同目录
        /// 依次试写，第一个真能落盘的才用。全失败就退回临时目录并记错误。
        /// </summary>
        public static string StateDir()
        {
            if (_stateDir != null && _stateDir.Length > 0) return _stateDir;

            string[] cands = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrinterToolbox"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrinterToolbox"),
                Path.Combine(Path.GetTempPath(), "PrinterToolbox"),
                Path.Combine(ExeDir(), "PrinterToolbox-Data"),
            };

            for (int i = 0; i < cands.Length; i++)
            {
                string dir = cands[i];
                if (Probe(dir))
                {
                    _stateDir = dir;
                    return dir;
                }
            }

            _stateDir = cands[2];
            return _stateDir;
        }

        private static string ExeDir()
        {
            try
            {
                string p = Application.StartupPath;
                if (p != null && p.Length > 0) return p;
            }
            catch { }
            try
            {
                string p = Process.GetCurrentProcess().MainModule.FileName;
                if (p != null && p.Length > 0) return Path.GetDirectoryName(p);
            }
            catch { }
            return Path.GetTempPath();
        }

        /// <summary>真写一次再删掉，确认这个目录可写。这是唯一可靠的判据。</summary>
        private static bool Probe(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-test-" + DateTime.Now.Ticks);
                File.WriteAllText(probe, "ok", new UTF8Encoding(false));
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static List<Binding> Load()
        {
            if (_cache != null) return _cache;
            var list = new List<Binding>();
            try
            {
                string path = StatePath();
                if (!File.Exists(path)) { _cache = list; return list; }
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string ln = lines[i];
                    if (ln.Length == 0 || ln.StartsWith("#")) continue;
                    string[] p = ln.Split('\t');
                    if (p.Length < 7) continue;
                    var b = new Binding();
                    b.Queue = Unescape(p[0]);
                    b.Uuid = Unescape(p[1]);
                    b.Serial = Unescape(p[2]);
                    b.Model = Unescape(p[3]);
                    b.LastIp = Unescape(p[4]);
                    b.LastPort = Unescape(p[5]);
                    try { b.LastSeen = long.Parse(p[6]); }
                    catch { b.LastSeen = 0; }
                    if (b.Queue.Length > 0) list.Add(b);
                }
            }
            catch (Exception ex) { LastNote = "读绑定表失败：" + ex.Message; }
            _cache = list;
            return list;
        }

        public static void Save(List<Binding> list)
        {
            try
            {
                string path = StatePath();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 打印机工具箱 · 队列与设备身份绑定表（换 IP 后靠这张表找回设备）");
                sb.AppendLine("# queue\tuuid\tserial\tmodel\tlastIp\tlastPort\tlastSeen");
                for (int i = 0; i < list.Count; i++)
                {
                    Binding b = list[i];
                    sb.AppendLine(
                        Escape(b.Queue) + "\t" + Escape(b.Uuid) + "\t" + Escape(b.Serial) + "\t"
                        + Escape(b.Model) + "\t" + Escape(b.LastIp) + "\t" + Escape(b.LastPort) + "\t"
                        + b.LastSeen.ToString());
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                _cache = list;
                LastStorageError = "";
            }
            catch (Exception ex)
            {
                LastStorageError = "写绑定表失败（" + StatePath() + "）：" + ex.GetType().Name + " " + ex.Message;
                LastNote = LastStorageError;
            }
        }

        /// <summary>记住某个队列当前锁定的那台设备。</summary>
        public static void Remember(string queue, DeviceInfo d, string portName)
        {
            if (queue == null || queue.Length == 0) return;
            if (d == null) return;
            List<Binding> list = Load();
            Binding b = null;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Compare(list[i].Queue, queue, StringComparison.OrdinalIgnoreCase) == 0) { b = list[i]; break; }
            }
            if (b == null) { b = new Binding(); b.Queue = queue; list.Add(b); }
            if (NormalizeUuid(d.Uuid).Length > 0) b.Uuid = NormalizeUuid(d.Uuid);
            if (d.Serial.Length > 0) b.Serial = d.Serial;
            if (d.Model.Length > 0) b.Model = d.Model;
            b.LastIp = d.Ip;
            if (portName != null && portName.Length > 0) b.LastPort = portName;
            b.LastSeen = DateTime.Now.Ticks / TimeSpan.TicksPerSecond;
            Save(list);
        }

        /// <summary>删掉某个队列的绑定（队列被删时同步清掉，免得留下幽灵）。</summary>
        public static void Forget(string queue)
        {
            List<Binding> list = Load();
            int n = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (string.Compare(list[i].Queue, queue, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    list.RemoveAt(i);
                    n++;
                }
            }
            if (n > 0) Save(list);
        }

        // ================= 端口操作 =================

        /// <summary>把标准 TCP/IP 端口的指向地址改掉。成功返回 true。</summary>
        public static bool SetPortHost(string portName, string host, out string message)
        {
            message = "";
            string lastErr = "";

            // 路线一：WMI 直接改（普通权限即可，实测非管理员也能成）
            try
            {
                string wql = "Win32_TCPIPPrinterPort.Name=" + Quote(portName);
                using (ManagementObject mo = new ManagementObject(wql))
                {
                    mo.Get();
                    mo["HostAddress"] = host;
                    mo.Put();
                }
                if (ReadBack(portName, host, out message)) return true;
                lastErr = message;
            }
            catch (Exception ex)
            {
                lastErr = "WMI " + ex.GetType().Name + " " + ex.Message;
            }

            // 路线二：注册表直接写（需要管理员；HostName 才是权威值）
            try
            {
                string key = @"SYSTEM\CurrentControlSet\Control\Print\Monitors\Standard TCP/IP Port\Ports\" + portName;
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(key, true))
                {
                    if (k != null) k.SetValue("HostName", host, RegistryValueKind.String);
                }
                if (ReadBack(portName, host, out message)) return true;
                if (lastErr.Length == 0) lastErr = message;
            }
            catch (Exception ex)
            {
                if (lastErr.Length == 0) lastErr = "注册表 " + ex.GetType().Name + " " + ex.Message;
            }

            // 失败时说清楚原因：受限（低完整性）权限下 WMI 写会抛 ArgumentNullException(pUnk)，
            // 那是从别的程序/沙箱里派生启动 exe 才有的现象，正常双击运行不会。
            if (PrinterSystem.IsRestricted())
                message = "改端口地址失败：当前进程是受限（低完整性）权限，写不了打印端口。"
                    + "请在资源管理器/开始菜单里正常双击打开工具箱，再点「校准IP」。"
                    + "（底层错误：" + lastErr + "）";
            else
                message = "改端口地址失败：" + lastErr;
            return false;
        }

        private static bool ReadBack(string portName, string host, out string message)
        {
            message = "";
            string actual = PrinterSystem.StandardPortHost(portName);
            if (actual.Length == 0)
            {
                Dictionary<string, PortInfo> ports = PrinterSystem.GetTcpPorts();
                PortInfo pi;
                if (ports.TryGetValue(portName, out pi)) actual = pi.HostAddress;
            }
            if (string.Compare(actual, host, StringComparison.OrdinalIgnoreCase) == 0)
            {
                message = "端口 " + portName + " 已改指 " + host;
                return true;
            }
            message = "端口写回后读到的地址是 " + (actual.Length == 0 ? "(空)" : actual) + "，不是 " + host;
            return false;
        }

        /// <summary>
        /// 从注册表里找出指向指定设备 UUID 的 WSD / IPP 端口名。
        /// 这类端口是 Windows 发现设备时自己建的，走的是真 IPP 会话，格式能协商 ——
        /// 实测这是「电脑说成功、机器不出纸」之外唯一能正常出纸的 Windows 通道。
        /// </summary>
        public static string FindWsdPort(string uuid)
        {
            string want = NormalizeUuid(uuid);
            if (want.Length == 0) return "";
            try
            {
                using (RegistryKey mon = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Print\Monitors"))
                {
                    if (mon == null) return "";
                    string[] subs = mon.GetSubKeyNames();
                    for (int i = 0; i < subs.Length; i++)
                    {
                        using (RegistryKey ports = mon.OpenSubKey(subs[i] + @"\Ports"))
                        {
                            if (ports == null) continue;
                            string[] names = ports.GetSubKeyNames();
                            for (int j = 0; j < names.Length; j++)
                            {
                                using (RegistryKey one = ports.OpenSubKey(names[j]))
                                {
                                    if (one == null) continue;
                                    object v = one.GetValue("Printer UUID");
                                    string got = NormalizeUuid(v == null ? "" : v.ToString());
                                    if (got.Length > 0 && string.Compare(got, want, StringComparison.OrdinalIgnoreCase) == 0)
                                        return names[j];
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>urn:uuid:xxxx → xxxx，统一大小写比较口径。</summary>
        public static string NormalizeUuid(string s)
        {
            if (s == null) return "";
            string t = s.Trim();
            if (t.Length == 0) return "";
            int k = t.IndexOf("uuid:", StringComparison.OrdinalIgnoreCase);
            if (k >= 0) t = t.Substring(k + 5);
            t = t.Trim('{', '}', ' ', '\t');
            return t;
        }

        private static string Quote(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "").Replace("\n", " ");
        }

        private static string Unescape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\t", "\t").Replace("\\\\", "\\");
        }

        // ================= 首次自动建立绑定表 =================

        /// <summary>
        /// 首次自动建立绑定表：不改任何队列设置，只「认一下每台队列现在锁的是哪台设备」。
        /// 两条识别路：TCP/IP 端口 → 探端口里记的地址；发现型端口 → 从注册表取该端口的 Printer UUID。
        /// 都拿不到就不写，之后那台队列会显示「认不出是哪台」，绝不瞎猜。
        /// </summary>
        public static int EnsureBindings(Action<string> log)
        {
            List<Binding> list = Load();
            List<PrinterInfo> queues = PrinterSystem.GetPrinters();
            Dictionary<string, PortInfo> ports = PrinterSystem.GetTcpPorts();
            int made = 0;

            for (int i = 0; i < queues.Count; i++)
            {
                PrinterInfo p = queues[i];
                if (!p.Fixable) continue;
                if (HasBinding(list, p.Name)) continue;

                DeviceInfo d = null;
                PortInfo pi;
                if (ports.TryGetValue(p.PortName, out pi) && pi.HostAddress.Length > 0)
                {
                    try { d = Scanner.ProbeHost(pi.HostAddress, 500, 1600); }
                    catch { d = null; }
                }
                if (d != null && (NormalizeUuid(d.Uuid).Length > 0 || d.Serial.Length > 0))
                {
                    Remember(p.Name, d, p.PortName);
                    list = Load();
                    made++;
                    if (log != null) log("已认出 " + p.Name + " → " + d.Ip + "（" + d.DisplayName + "）");
                    continue;
                }

                // 发现型端口：地址不在端口里，靠注册表里的 Printer UUID 记身份
                string uu = PortUuid(p.PortName);
                if (uu.Length > 0)
                {
                    var b = new Binding();
                    b.Queue = p.Name;
                    b.Uuid = uu;
                    b.LastPort = p.PortName;
                    b.LastSeen = DateTime.Now.Ticks / TimeSpan.TicksPerSecond;
                    list.Add(b);
                    Save(list);
                    made++;
                    if (log != null) log("已认出 " + p.Name + " → UUID " + uu + "（发现型端口，地址待扫描补全）");
                }
            }

            LastNote = "绑定表共 " + list.Count + " 条记录。";
            return made;
        }

        /// <summary>把扫到的设备按 UUID/SN 填进「有身份但还没地址」的绑定记录。</summary>
        public static int FillFromScan(List<DeviceInfo> found, Action<string> log)
        {
            List<Binding> list = Load();
            int filled = 0;
            for (int i = 0; i < list.Count; i++)
            {
                Binding b = list[i];
                if (b.LastIp.Length > 0) continue;
                DeviceInfo hit = MatchOne(b, found);
                if (hit == null) continue;
                b.LastIp = hit.Ip;
                if (b.Serial.Length == 0 && hit.Serial.Length > 0) b.Serial = hit.Serial;
                if (b.Model.Length == 0 && hit.Model.Length > 0) b.Model = hit.Model;
                b.LastSeen = DateTime.Now.Ticks / TimeSpan.TicksPerSecond;
                filled++;
                if (log != null) log("补全绑定 " + b.Queue + " → " + hit.Ip);
            }
            if (filled > 0) Save(list);
            return filled;
        }

        /// <summary>从端口注册表里读发现型端口的 Printer UUID。</summary>
        public static string PortUuid(string portName)
        {
            if (portName == null || portName.Length == 0) return "";
            try
            {
                using (RegistryKey mon = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Print\Monitors"))
                {
                    if (mon == null) return "";
                    string[] subs = mon.GetSubKeyNames();
                    for (int i = 0; i < subs.Length; i++)
                    {
                        using (RegistryKey one = mon.OpenSubKey(subs[i] + @"\Ports\" + portName))
                        {
                            if (one == null) continue;
                            object v = one.GetValue("Printer UUID");
                            if (v == null) v = one.GetValue("PrinterUUID");
                            if (v == null) continue;
                            string s = NormalizeUuid(v.ToString());
                            if (s.Length > 0) return s;
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        private static bool HasBinding(List<Binding> list, string queue)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Compare(list[i].Queue, queue, StringComparison.OrdinalIgnoreCase) == 0) return true;
            return false;
        }

        // ================= 校准主流程 =================

        public sealed class RebindResult
        {
            public int Checked;
            public int Moved;       // 地址变了并已改回来
            public int Ok;
            public int Failed;
            public int Unknown;     // 认不出是哪台
            public List<string> Steps = new List<string>();
        }

        /// <summary>
        /// 检查所有已绑定的队列，设备换地址的把它们指回正确 IP。
        /// rangeText 为空表示只需要确认「原来那个地址还通不通」，不扫全网。
        /// </summary>
        public static RebindResult Rebind(string rangeText, Action<string> log)
        {
            RebindResult r = new RebindResult();
            List<Binding> list = Load();
            if (list.Count == 0)
            {
                // 首次运行：先自动认一遍，认完再校准，用户不用手动点任何东西
                if (log != null) log("还没有绑定记录，先自动认一遍本机队列对应的设备…");
                EnsureBindings(log);
                list = Load();
            }
            if (list.Count == 0)
            {
                LastNote = "本机没有可识别的打印队列（或都认不出是哪台设备）。";
                return r;
            }

            // 第一步：先看「端口现在真正指向的地址」还通不通、还是不是同一台设备。
            // 注意不能只看绑定表里记的 LastIp —— 用户/系统可能已经把端口地址改错了，
            // 那样绑定表看起来一切正常，实际队列指向死地址（实测踩过一次）。
            List<Binding> needFind = new List<Binding>();
            for (int i = 0; i < list.Count; i++)
            {
                Binding b = list[i];
                r.Checked++;
                if (!b.HasIdentity) { r.Unknown++; r.Steps.Add(Skip(b, "没有 UUID / 序列号，认不出是哪台")); continue; }

                // 端口现在的真实地址（标准 TCP/IP 端口可读；发现型端口读不到，返回空）
                string liveIp = LivePortHost(b);
                if (liveIp.Length > 0)
                {
                    // 端口的地址和绑定记录不一致 → 先把绑定记录对齐到现实，再判断设备
                    if (!string.Equals(liveIp, b.LastIp, StringComparison.OrdinalIgnoreCase))
                    {
                        string oldIp = b.LastIp;
                        b.LastIp = liveIp;
                        if (log != null) log("注意：" + b.Queue + " 的端口地址现在是 " + liveIp + "（记录里是 " + oldIp + "），按现实走。");
                    }
                }

                DeviceInfo cur = ProbeBound(b);
                if (cur != null && SameDevice(b, cur))
                {
                    r.Ok++;
                    r.Steps.Add("· " + b.Queue + "：" + b.LastIp + " 仍是同一台设备（"
                        + (cur.DisplayName.Length > 0 ? cur.DisplayName : cur.Ip) + "），无需改动");
                    b.LastSeen = DateTime.Now.Ticks / TimeSpan.TicksPerSecond;
                    continue;
                }
                if (cur == null)
                {
                    b.LastSeen = b.LastSeen; // 不通，留待扫描
                }
                needFind.Add(b);
            }

            if (needFind.Count == 0)
            {
                Save(list);
                LastNote = "全部 " + r.Checked + " 台队列的地址都还指向原来的设备，不需要改动。";
                return r;
            }

            // 第二步：扫网找设备（只有地址不通时才扫，避免每次开机都扫全网）
            List<DeviceInfo> found = new List<DeviceInfo>();
            string err = "";
            List<System.Net.IPAddress> targets = Scanner.ParseTargets(rangeText == null ? "" : rangeText, out err);
            if (targets == null || targets.Count == 0)
            {
                r.Steps.Add("范围为空，跳过扫描：" + err);
            }
            else
            {
                if (log != null) log("有 " + needFind.Count + " 台队列的地址已经失效，开始扫 " + targets.Count + " 个地址找回设备…");
                Scanner.Sweep(targets,
                    delegate(DeviceInfo d) { lock (found) { found.Add(d); } },
                    null, null, 700, 1200);
                FillFromScan(found, log);
                list = Load();
            }

            // 第三步：逐个按身份匹配并改地址
            for (int i = 0; i < needFind.Count; i++)
            {
                Binding b = needFind[i];
                DeviceInfo hit = MatchOne(b, found);
                if (hit == null)
                {
                    r.Failed++;
                    r.Steps.Add("× " + b.Queue + "：扫到 " + found.Count + " 台设备，但没有一台的 UUID / 序列号对得上（"
                        + Describe(b) + "）。设备可能没开机或不在这个网段。");
                    continue;
                }

                string port = b.LastPort;
                if (port.Length == 0) port = PrinterSystem.GetPrinterPort(b.Queue);
                string msg;

                // 端口是标准 TCP/IP：直接改地址，队列本身不动
                Dictionary<string, PortInfo> ports = PrinterSystem.GetTcpPorts();
                if (ports.ContainsKey(port))
                {
                    if (SetPortHost(port, hit.Ip, out msg))
                    {
                        b.LastIp = hit.Ip;
                        b.LastSeen = DateTime.Now.Ticks / TimeSpan.TicksPerSecond;
                        r.Moved++;
                        r.Steps.Add("√ " + b.Queue + "：" + port + " 已从 " + (b.LastIp) + " 改指 " + hit.Ip + "（按 UUID/SN 认回同一台设备）");
                        if (log != null) log("自动校准：" + b.Queue + " → " + hit.Ip);
                        continue;
                    }
                    r.Failed++;
                    r.Steps.Add("× " + b.Queue + "：" + msg);
                    continue;
                }

                // 端口是 WSD / IPP 发现口：没有可写地址，靠 UUID 认设备，不是靠地址。
                // 如果队列当前这条端口本来就指向这台设备，那就什么都不用改 ——
                // 发现型端口是按设备身份绑定的，设备换 IP 它自己会跟着走。
                string portUuid = PortUuid(port);
                if (portUuid.Length > 0 && string.Compare(NormalizeUuid(portUuid), NormalizeUuid(hit.Uuid),
                    StringComparison.OrdinalIgnoreCase) == 0)
                {
                    b.LastIp = hit.Ip;
                    b.LastSeen = DateTime.Now.Ticks / TimeSpan.TicksPerSecond;
                    r.Ok++;
                    r.Steps.Add("· " + b.Queue + "：" + port + " 是按设备身份绑定的发现型端口，已指向 "
                        + hit.Ip + "（这类端口不用改地址，设备换 IP 会自动跟随）");
                    continue;
                }

                // 本机还有另一条指向该设备的发现型端口 → 切过去
                string wsd = FindWsdPort(hit.Uuid);
                if (wsd.Length > 0 && string.Compare(wsd, port, StringComparison.OrdinalIgnoreCase) != 0)
                {
                    PrinterSystem.SetPrinterPort(b.Queue, wsd);
                    string back = PrinterSystem.GetPrinterPort(b.Queue);
                    if (string.Compare(back, wsd, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        b.LastIp = hit.Ip; b.LastPort = wsd;
                        b.LastSeen = DateTime.Now.Ticks / TimeSpan.TicksPerSecond;
                        r.Moved++;
                        r.Steps.Add("√ " + b.Queue + "：换成本机指向该设备的发现型端口 " + wsd + "（能协商文档格式的那条通道）");
                        continue;
                    }
                    r.Failed++;
                    r.Steps.Add("× " + b.Queue + "：切到端口 " + wsd + " 后回读是 " + back);
                    continue;
                }

                // 端口不可改、也没有可复用的发现型端口 → 老老实实说改不了
                r.Failed++;
                r.Steps.Add("× " + b.Queue + "：当前端口 " + port + " 是发现型端口（没有可改的地址），"
                    + "本机也找不到别的指向这台设备的端口 —— 请在系统「打印机和扫描仪」里重新添加这台设备。");
            }

            Save(list);
            LastNote = "检查 " + r.Checked + " 台：不用改 " + r.Ok + "，已校准 " + r.Moved
                + "，失败 " + r.Failed + "，认不出 " + r.Unknown + "。";
            return r;
        }

        private static string Skip(Binding b, string why)
        {
            return "· " + b.Queue + "：" + why;
        }

        private static string Describe(Binding b)
        {
            string s = "";
            if (b.Uuid.Length > 0) s += "UUID " + b.Uuid + " ";
            if (b.Serial.Length > 0) s += "SN " + b.Serial + " ";
            if (s.Length == 0) s = "无身份信息";
            return s.Trim();
        }

        /// <summary>探一下绑定里记的那个地址，现在已经是什么设备了（不通返回 null）。</summary>
        /// <summary>某条绑定对应的端口，此刻真正指向的地址（读不到返回空）。</summary>
        private static string LivePortHost(Binding b)
        {
            try
            {
                string port = b.LastPort;
                if (port == null || port.Length == 0) port = PrinterSystem.GetPrinterPort(b.Queue);
                if (port == null || port.Length == 0) return "";
                Dictionary<string, PortInfo> ports = PrinterSystem.GetTcpPorts();
                if (!ports.ContainsKey(port)) return "";
                string host = ports[port].HostAddress;
                return host == null ? "" : host;
            }
            catch { return ""; }
        }

        private static DeviceInfo ProbeBound(Binding b)
        {
            if (b.LastIp == null || b.LastIp.Length == 0) return null;
            try
            {
                bool ping = false;
                if (!Scanner.EnsureAlive(b.LastIp, out ping)) return null;
            }
            catch { return null; }
            try
            {
                DeviceInfo d = Scanner.ProbeHost(b.LastIp, 500, 1600);
                return d;
            }
            catch { return null; }
        }

        /// <summary>UUID 优先、序列号兜底；都没有就恒 false（宁可认不出也不乱认）。</summary>
        private static bool SameDevice(Binding b, DeviceInfo d)
        {
            string a = NormalizeUuid(b.Uuid);
            string c = NormalizeUuid(d.Uuid);
            if (a.Length > 0 && c.Length > 0) return string.Compare(a, c, StringComparison.OrdinalIgnoreCase) == 0;
            if (b.Serial.Length > 0 && d.Serial.Length > 0)
                return string.Compare(b.Serial, d.Serial, StringComparison.OrdinalIgnoreCase) == 0;
            return false;
        }

        private static DeviceInfo MatchOne(Binding b, List<DeviceInfo> found)
        {
            // 先按 UUID
            string a = NormalizeUuid(b.Uuid);
            if (a.Length > 0)
            {
                for (int i = 0; i < found.Count; i++)
                {
                    if (string.Compare(NormalizeUuid(found[i].Uuid), a, StringComparison.OrdinalIgnoreCase) == 0)
                        return found[i];
                }
            }
            // 再按序列号（必须同时型号相近，避免不同品牌撞号）
            if (b.Serial.Length > 0)
            {
                for (int i = 0; i < found.Count; i++)
                {
                    if (string.Compare(found[i].Serial, b.Serial, StringComparison.OrdinalIgnoreCase) != 0) continue;
                    if (b.Model.Length > 0 && found[i].Model.Length > 0)
                    {
                        if (!WordOverlap(b.Model, found[i].Model)) continue;
                    }
                    return found[i];
                }
            }
            return null;
        }

        private static bool WordOverlap(string a, string b)
        {
            string[] x = Split(a);
            string[] y = Split(b);
            for (int i = 0; i < x.Length; i++)
                for (int j = 0; j < y.Length; j++)
                    if (x[i].Length >= 3 && x[i] == y[j]) return true;
            return false;
        }

        private static string[] Split(string s)
        {
            if (s == null || s.Length == 0) return new string[0];
            var list = new List<string>();
            StringBuilder cur = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsLetterOrDigit(c)) cur.Append(char.ToLowerInvariant(c));
                else { if (cur.Length > 0) { list.Add(cur.ToString()); cur.Length = 0; } }
            }
            if (cur.Length > 0) list.Add(cur.ToString());
            return list.ToArray();
        }
    }
}
