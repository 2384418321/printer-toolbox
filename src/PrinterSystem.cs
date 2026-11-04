using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace PrinterIpFix
{
    /// <summary>
    /// 打印子系统操作。全部走 WMI -> spooler RPC，实测非管理员即可完成：
    /// 建端口、改打印机端口指针、删打印机、删端口（prnport.vbs）。
    /// </summary>
    internal static class PrinterSystem
    {
        private static string _prnport;

        /// <summary>最近一次设默认打印机的逐路线结果，给界面日志用。</summary>
        public static List<string> LastDefaultLog = new List<string>();

        // ---------- 读取 ----------

        public static List<PrinterInfo> GetPrinters()
        {
            var list = new List<PrinterInfo>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Name,PortName,DriverName,WorkOffline,PrinterStatus,Default FROM Win32_Printer"))
            using (ManagementObjectCollection col = searcher.Get())
            {
                foreach (ManagementObject mo in col)
                {
                    using (mo)
                    {
                        var p = new PrinterInfo();
                        p.Name = Str(mo["Name"]);
                        p.PortName = Str(mo["PortName"]);
                        p.DriverName = Str(mo["DriverName"]);
                        p.WorkOffline = Bool(mo["WorkOffline"]);
                        p.IsDefault = Bool(mo["Default"]);
                        object st = mo["PrinterStatus"];
                        p.StatusCode = (st == null) ? 0 : Convert.ToInt32(st);
                        list.Add(p);
                    }
                }
            }
            list.Sort(delegate(PrinterInfo a, PrinterInfo b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return list;
        }

        public static Dictionary<string, PortInfo> GetTcpPorts()
        {
            var d = new Dictionary<string, PortInfo>(StringComparer.OrdinalIgnoreCase);
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Name,HostAddress,PortNumber,Protocol,SNMPEnabled FROM Win32_TCPIPPrinterPort"))
            using (ManagementObjectCollection col = searcher.Get())
            {
                foreach (ManagementObject mo in col)
                {
                    using (mo)
                    {
                        var pi = new PortInfo();
                        pi.Name = Str(mo["Name"]);
                        pi.HostAddress = Str(mo["HostAddress"]).Trim();
                        // WMI 的 HostAddress 在 Win11 上常为 null（实测本机 7 条 TCP/IP 端口
                        // 全是 null，但 Get-WmiObject 单条查又有值）。真正权威的地址在注册表
                        // Standard TCP/IP Port 的 HostName 里，且 WMI 写入也是落到那里。
                        // 顺序：WMI → 注册表 HostName → 从端口名猜（最后兜底，猜出来可能是旧的）。
                        if (pi.HostAddress.Length == 0)
                            pi.HostAddress = StandardPortHost(pi.Name);
                        if (pi.HostAddress.Length == 0)
                            pi.HostAddress = GuessHostFromPortName(pi.Name);
                        object pn = mo["PortNumber"];
                        pi.PortNumber = (pn == null) ? 9100 : Convert.ToInt32(pn);
                        object pr = mo["Protocol"];
                        pi.Protocol = (pr == null) ? 1 : Convert.ToInt32(pr);
                        pi.SnmpEnabled = Bool(mo["SNMPEnabled"]);
                        pi.IsTcp = true;
                        if (pi.Name.Length > 0)
                        {
                            // 端口的 Name 并不唯一：一个 URL 形态的端口会同时存在
                            // 「标准 TCP/IP 端口」（HostAddress 有值）与「Internet Port」（HostAddress 为空）两条同名记录。
                            // 直接 d[name] = pi 会被后枚举到的那条覆盖 —— 实测覆盖掉的正是有地址的那条，
                            // 于是队列地址解析成空串，界面把 IPP Fresh 判成「地址不通」。
                            // 所以同名时一律保留「有地址」的那条。
                            PortInfo exist;
                            if (d.TryGetValue(pi.Name, out exist))
                            {
                                if (exist.HostAddress.Length > 0) continue;
                                if (pi.HostAddress.Length == 0) continue;
                            }
                            d[pi.Name] = pi;
                        }
                    }
                }
            }
            return d;
        }

        /// <summary>
        /// 读注册表里标准 TCP/IP 端口的真实地址（HostName / IPAddress）。
        /// 这是改地址后立刻能读到的权威值：WMI 的 HostAddress 会返回 null，
        /// 而 Put() 写入的正是这里，所以校准必须读这一处，否则永远看不出地址被改过。
        /// 读不到返回空串。
        /// </summary>
        public static string StandardPortHost(string portName)
        {
            if (portName == null || portName.Length == 0) return "";
            string key = @"SYSTEM\CurrentControlSet\Control\Print\Monitors\Standard TCP/IP Port\Ports\" + portName;
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key))
                {
                    if (k == null) return "";
                    object h = k.GetValue("HostName");
                    string host = (h == null) ? "" : h.ToString().Trim();
                    if (host.Length > 0) return host;
                    object ip = k.GetValue("IPAddress");
                    return (ip == null) ? "" : ip.ToString().Trim();
                }
            }
            catch { return ""; }
        }

        /// <summary>
        /// WMI 的 HostAddress 为空时，从端口名里猜设备地址。
        /// 支持的端口名形态：
        ///   IP_192.168.1.100            （本工具自己建的）
        ///   http://192.168.1.100/ipp/print
        ///   http://192.168.1.100:631/ipp/print
        ///   https://host/ipp/print
        ///   IP_hostname / 其它含主机名的名字
        /// 猜不到返回空串（调用方按「地址不通」处理，不会误报可达）。
        /// </summary>
        private static string GuessHostFromPortName(string portName)
        {
            if (portName == null) return "";
            string n = portName.Trim();
            if (n.Length == 0) return "";

            // 形态一：IP_<addr>
            if (n.StartsWith("IP_", StringComparison.OrdinalIgnoreCase))
                return FirstIpv4OrEmpty(n.Substring(3));

            // 形态二：URL（http/https 开头）——取主机名部分
            string lower = n.ToLowerInvariant();
            if (lower.StartsWith("http://") || lower.StartsWith("https://"))
            {
                int from = n.IndexOf("://", StringComparison.Ordinal) + 3;
                int to = n.Length;
                int slash = n.IndexOf('/', from);
                if (slash >= 0) to = slash;
                string hostPart = n.Substring(from, to - from);
                int colon = hostPart.IndexOf(':');
                if (colon >= 0) hostPart = hostPart.Substring(0, colon);   // 剥端口
                return FirstIpv4OrEmpty(hostPart);
            }

            // 形态三：名字里嵌着 IPv4（如 CAP_127.0.0.1_9101）
            string embedded = FirstIpv4OrEmpty(n);
            if (embedded.Length > 0) return embedded;

            return "";
        }

        /// <summary>
        /// 从一段文本里抠出第一个 IPv4 字面量；文本本身是合法 IPv4 就原样返回。
        /// 用于端口名/URL 里取地址，避免把 hostname 当 IP 用。
        /// </summary>
        private static string FirstIpv4OrEmpty(string s)
        {
            if (s == null || s.Length == 0) return "";
            string t = s.Trim();
            IPAddress direct;
            if (IPAddress.TryParse(t, out direct) && direct.AddressFamily == AddressFamily.InterNetwork)
                return t;

            // 允许 "127.0.0.1_9101" 这种被符号包住的写法
            int start = -1;
            for (int i = 0; i < t.Length; i++)
            {
                if (char.IsDigit(t[i]))
                {
                    if (start < 0) start = i;
                }
                else if (t[i] == '.' && start >= 0) { /* 继续 */ }
                else
                {
                    if (start >= 0)
                    {
                        string cand = t.Substring(start, i - start);
                        IPAddress a;
                        if (IPAddress.TryParse(cand, out a) && a.AddressFamily == AddressFamily.InterNetwork)
                            return cand;
                        start = -1;
                    }
                }
            }
            if (start >= 0)
            {
                string cand2 = t.Substring(start);
                IPAddress a2;
                if (IPAddress.TryParse(cand2, out a2) && a2.AddressFamily == AddressFamily.InterNetwork)
                    return cand2;
            }
            return "";
        }

        /// <summary>判断端口类别并解析地址</summary>
        public static void Classify(PrinterInfo p, Dictionary<string, PortInfo> ports)
        {
            string pn = p.PortName == null ? "" : p.PortName;
            PortInfo pc;
            if (ports.TryGetValue(pn, out pc))
            {
                p.Kind = PortKind.TcpIp;
                p.RawAddress = pc.HostAddress;
                p.Fixable = true;
                return;
            }
            if (pn.StartsWith("WSD-", StringComparison.OrdinalIgnoreCase) ||
                pn.StartsWith("IPP-", StringComparison.OrdinalIgnoreCase))
            {
                p.Kind = PortKind.Wsd;
                p.RawAddress = "";
                p.Fixable = true;   // WSD 可以改造成 TCP/IP
                return;
            }
            if (pn.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
            {
                p.Kind = PortKind.Usb; p.Fixable = false; return;
            }
            if (pn.StartsWith("\\\\", StringComparison.Ordinal))
            {
                p.Kind = PortKind.Smb; p.Fixable = false; return;
            }
            p.Kind = PortKind.Local;
            p.Fixable = false;
        }

        /// <summary>把端口里记的地址（可能是主机名）解析成 IPv4</summary>
        public static string ResolveAddress(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            IPAddress ip;
            if (IPAddress.TryParse(raw, out ip)) return ip.AddressFamily == AddressFamily.InterNetwork ? ip.ToString() : "";
            try
            {
                IPAddress[] all = Dns.GetHostAddresses(raw);
                foreach (IPAddress a in all)
                    if (a.AddressFamily == AddressFamily.InterNetwork) return a.ToString();
            }
            catch { }
            return "";
        }

        // ---------- 写入 ----------

        /// <summary>最近一次建端口的逐路线结果，给界面日志用。</summary>
        public static List<string> LastPortLog = new List<string>();

        public static int RunPowerShell(string script, out string output)
        {
            string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return RunHidden("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + b64,
                40000, out output);
        }

        private static string Q(string s)
        {
            return "'" + (s == null ? "" : s.Replace("'", "''")) + "'";
        }

        /// <summary>创建标准 TCP/IP 端口（RAW）。已存在则返回 false。</summary>
        public static bool CreateTcpPort(string portName, string host, int portNumber)
        {
            LastPortLog.Clear();
            if (PortExists(portName)) return false;

            // 路线一：PrintManagement 模块（本机实测普通权限可用）
            string out1;
            int rc1 = RunPowerShell(
                "Add-PrinterPort -Name " + Q(portName) + " -PrinterHostAddress " + Q(host)
                + " -ErrorAction Stop", out out1);
            LastPortLog.Add("Add-PrinterPort: exit=" + rc1 + " " + Short(out1));
            if (PortExists(portName)) return true;

            // 路线二：系统自带 prnport.vbs
            string vbs = FindPrnPort();
            if (vbs != null)
            {
                string out2;
                int rc2 = RunHidden("cscript.exe",
                    "//nologo \"" + vbs + "\" -a -r \"" + portName + "\" -h \"" + host
                    + "\" -o raw -n " + portNumber, 20000, out out2);
                LastPortLog.Add("prnport.vbs: exit=" + rc2 + " " + Short(out2));
                if (PortExists(portName)) return true;
            }

            // 路线三：WMI 兜底（.NET 的 Put() 在部分机器上会抛 pUnk，只能当备用）
            try
            {
                using (var cls = new ManagementClass("Win32_TCPIPPrinterPort"))
                using (ManagementObject inst = cls.CreateInstance())
                {
                    inst["Name"] = portName;
                    inst["HostAddress"] = host;
                    inst["PortNumber"] = (uint)portNumber;
                    inst["Protocol"] = (uint)1;      // 1 = RAW
                    inst["SNMPEnabled"] = false;
                    inst.Put();
                }
                LastPortLog.Add("WMI: ok");
            }
            catch (Exception ex)
            {
                LastPortLog.Add("WMI: " + ex.GetType().Name + " " + Short(ex.Message));
            }

            return PortExists(portName);
        }

        private static string Short(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 120 ? s.Substring(0, 120) + "..." : s;
        }

        public static bool PortExists(string portName)
        {
            return GetTcpPorts().ContainsKey(portName);
        }

        /// <summary>把打印机指向另一个端口。走 WMI -> spooler SetPrinter。</summary>
        public static void SetPrinterPort(string printerName, string portName)
        {
            Exception last = null;
            // 最多重试：spooler 偶发繁忙
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher(
                        "SELECT Name,PortName FROM Win32_Printer"))
                    using (ManagementObjectCollection col = searcher.Get())
                    {
                        foreach (ManagementObject mo in col)
                        {
                            string n = Str(mo["Name"]);
                            if (string.Compare(n, printerName, StringComparison.CurrentCultureIgnoreCase) == 0)
                            {
                                using (mo)
                                {
                                    mo["PortName"] = portName;
                                    mo.Put();
                                }
                                // 回读验证
                                string back = GetPrinterPort(printerName);
                                if (string.Compare(back, portName, StringComparison.OrdinalIgnoreCase) == 0) return;
                                throw new InvalidOperationException(
                                    "修改后回读仍是 \"" + back + "\"，改动未生效。");
                            }
                        }
                    }
                    throw new InvalidOperationException("找不到打印机：" + printerName);
                }
                catch (Exception ex)
                {
                    last = ex;
                    System.Threading.Thread.Sleep(300);
                }
            }

            // WMI 写不通时改用 PrintManagement 模块，并回读确认
            string outPs;
            RunPowerShell("Set-Printer -Name " + Q(printerName) + " -PortName " + Q(portName)
                + " -ErrorAction Stop", out outPs);
            if (string.Compare(GetPrinterPort(printerName), portName, StringComparison.OrdinalIgnoreCase) == 0)
                return;

            throw new InvalidOperationException("修改端口失败：" + (last == null ? "未知" : last.Message)
                + " / Set-Printer: " + Short(outPs), last);
        }

        public static string GetPrinterPort(string printerName)
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name,PortName FROM Win32_Printer"))
            using (ManagementObjectCollection col = searcher.Get())
            {
                foreach (ManagementObject mo in col)
                {
                    using (mo)
                    {
                        if (string.Compare(Str(mo["Name"]), printerName, StringComparison.CurrentCultureIgnoreCase) == 0)
                            return Str(mo["PortName"]);
                    }
                }
            }
            return "";
        }

        /// <summary>设置默认打印机，并回读确认。三条路线依次尝试。</summary>
        public static bool SetDefaultPrinter(string printerName, out string error)
        {
            error = "";
            LastDefaultLog.Clear();
            if (string.IsNullOrEmpty(printerName))
            {
                error = "队列名为空。";
                return false;
            }

            // 路线一：按显式 DeviceID 路径绑定 WMI 对象再调方法。
            // 不能用 searcher 投影出来的对象调 InvokeMethod：SELECT 里没有键属性 DeviceID，
            // 对象没有 key，InvokeMethod 会抛 InvalidOperationException「对象的当前状态使该操作无效」。
            try
            {
                string devId = printerName.Replace("\\", "\\\\").Replace("\"", "\\\"");
                using (var mo = new ManagementObject("Win32_Printer.DeviceID=\"" + devId + "\""))
                {
                    mo.Get();
                    mo.InvokeMethod("SetDefaultPrinter", null);
                }
                LastDefaultLog.Add("WMI SetDefaultPrinter：已调用");
            }
            catch (Exception ex)
            {
                LastDefaultLog.Add("WMI SetDefaultPrinter：" + Short(ex.Message));
            }
            if (WaitDefault(printerName, 2500)) return true;

            // 路线二：WScript.Network COM，写的是当前用户的默认打印机。
            try
            {
                Type comType = Type.GetTypeFromProgID("WScript.Network");
                if (comType == null)
                {
                    LastDefaultLog.Add("WScript.Network：这台机器没有该 COM 组件");
                }
                else
                {
                    object net = Activator.CreateInstance(comType);
                    comType.InvokeMember("SetDefaultPrinter",
                        System.Reflection.BindingFlags.InvokeMethod, null, net,
                        new object[] { printerName });
                    LastDefaultLog.Add("WScript.Network：已调用");
                }
            }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException != null ? ex.InnerException : ex;
                LastDefaultLog.Add("WScript.Network：" + Short(inner.Message));
            }
            if (WaitDefault(printerName, 2500)) return true;

            // 路线三：rundll32 printui.dll,PrintUIEntry /y /n "<name>"
            try
            {
                string output;
                int rc = RunHidden("rundll32.exe",
                    "printui.dll,PrintUIEntry /y /n \"" + printerName + "\"", 12000, out output);
                string tail = Short(output);
                LastDefaultLog.Add("printui /y：exit=" + rc + (tail.Length > 0 ? " " + tail : ""));
            }
            catch (Exception ex)
            {
                LastDefaultLog.Add("printui /y：" + Short(ex.Message));
            }
            if (WaitDefault(printerName, 3000)) return true;

            string now = GetDefaultPrinterName();
            error = "三条路线都没成功，当前默认打印机仍是 \"" + now + "\"。";
            string allRoutes = string.Join(" | ", LastDefaultLog.ToArray());
            if (allRoutes.IndexOf("拒绝访问") >= 0 || allRoutes.IndexOf("权限") >= 0)
                error += "\r\n日志里出现「拒绝访问」说明当前进程权限不够：请在正常桌面上双击 exe，别在受限/沙箱环境里跑。";
            else
                error += "\r\n如果系统开着「让 Windows 管理默认打印机」，它可能随后自己改回去：设置 → 蓝牙和其他设备 → 打印机和扫描仪，把那一项关掉再试。";
            return false;
        }

        /// <summary>轮询等待默认打印机变成指定名字。</summary>
        private static bool WaitDefault(string printerName, int timeoutMs)
        {
            int waited = 0;
            while (true)
            {
                if (string.Compare(GetDefaultPrinterName(), printerName,
                        StringComparison.CurrentCultureIgnoreCase) == 0) return true;
                if (waited >= timeoutMs) return false;
                System.Threading.Thread.Sleep(300);
                waited += 300;
            }
        }
        public static string GetDefaultPrinterName()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name,Default FROM Win32_Printer"))
                using (ManagementObjectCollection col = searcher.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        using (mo)
                        {
                            if (Bool(mo["Default"])) return Str(mo["Name"]);
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>删除 TCP/IP 端口（WMI 该类没有 Delete 方法，用系统自带 prnport.vbs）。</summary>
        public static bool DeleteTcpPort(string portName, out string message)
        {
            message = "";
            string vbs = FindPrnPort();
            if (vbs == null)
            {
                message = "找不到系统自带的 prnport.vbs，无法删除端口。";
                return false;
            }
            string output;
            int code = RunHidden("cscript.exe",
                "//nologo \"" + vbs + "\" -d -r \"" + portName + "\"", 20000, out output);
            if (code != 0)
            {
                message = "删除端口失败（exit " + code + "）：" + output.Trim();
                return false;
            }
            return !PortExists(portName);
        }

        public static string FindPrnPort()
        {
            if (_prnport != null) return _prnport.Length == 0 ? null : _prnport;
            _prnport = "";
            try
            {
                string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string root = Path.Combine(sys, "Printing_Admin_Scripts");
                if (Directory.Exists(root))
                {
                    string[] files = Directory.GetFiles(root, "prnport.vbs", SearchOption.AllDirectories);
                    if (files.Length > 0) _prnport = files[0];
                }
            }
            catch { }
            return _prnport.Length == 0 ? null : _prnport;
        }

        // ---------- 删除队列 ----------

        /// <summary>最近一次删队列的逐路线结果，给界面日志用。</summary>
        public static List<string> LastDeleteLog = new List<string>();

        private static string _prnmngr;

        /// <summary>按名字精确判断队列是否还在。</summary>
        public static bool PrinterExists(string printerName)
        {
            if (string.IsNullOrEmpty(printerName)) return false;
            try
            {
                foreach (PrinterInfo p in GetPrinters())
                    if (string.Equals(p.Name, printerName, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch { }
            return false;
        }

        /// <summary>队列里还有几个作业（排队 + 打印中）。</summary>
        public static int CountJobs(string printerName)
        {
            int n = 0;
            if (string.IsNullOrEmpty(printerName)) return 0;
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PrintJob"))
                using (ManagementObjectCollection col = searcher.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        using (mo)
                        {
                            // Win32_PrintJob 的 Name 形如「队列名, 作业号」
                            string nm = Str(mo["Name"]);
                            if (nm.StartsWith(printerName + ",", StringComparison.OrdinalIgnoreCase)) n++;
                        }
                    }
                }
            }
            catch { }
            return n;
        }

        /// <summary>端口上还挂着哪些队列。</summary>
        public static List<string> PrintersOnPort(string portName)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(portName)) return list;
            try
            {
                foreach (PrinterInfo p in GetPrinters())
                    if (string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase))
                        list.Add(p.Name);
            }
            catch { }
            return list;
        }

        /// <summary>
        /// 删除一个打印队列。四条路线依次试，每条都用回读确认，任一成功即返回。
        /// 路线一 WMI Delete() 最干净；printui 立刻返回所以要等；Remove-Printer 放最后，
        /// 因为队列名里的 [ ] 会被它当通配符（本项目踩过这个坑）。
        /// </summary>
        public static bool DeletePrinter(string printerName, out string message)
        {
            LastDeleteLog.Clear();
            message = "";
            if (string.IsNullOrEmpty(printerName))
            {
                message = "队列名为空。";
                return false;
            }
            if (!PrinterExists(printerName))
            {
                message = "队列不存在（可能已经被删掉了）。";
                return false;
            }

            // 路线一：WMI（走 spooler RPC）
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_Printer WHERE Name = " + WqlStr(printerName)))
                using (ManagementObjectCollection col = searcher.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        using (mo) { mo.Delete(); }
                        break;
                    }
                }
                LastDeleteLog.Add("WMI Delete: 已调用");
            }
            catch (Exception ex) { LastDeleteLog.Add("WMI Delete: " + ex.Message); }
            if (WaitGone(printerName, 3000))
            {
                message = "已删除（WMI）。";
                return true;
            }

            // 路线二：rundll32 printui.dll,PrintUIEntry /dl
            try
            {
                string o;
                int rc = RunHidden("rundll32.exe",
                    "printui.dll,PrintUIEntry /dl /n \"" + printerName + "\" /q", 20000, out o);
                LastDeleteLog.Add("printui /dl: exit=" + rc + " " + Short(o));
            }
            catch (Exception ex) { LastDeleteLog.Add("printui /dl: " + ex.Message); }
            if (WaitGone(printerName, 4000))
            {
                message = "已删除（printui）。";
                return true;
            }

            // 路线三：系统自带 prnmngr.vbs -d -p
            try
            {
                string vbs = FindPrnMngr();
                if (vbs == null) LastDeleteLog.Add("prnmngr -d: 找不到 prnmngr.vbs");
                else
                {
                    string o;
                    int rc = RunHidden("cscript.exe",
                        "//nologo \"" + vbs + "\" -d -p \"" + printerName + "\"", 20000, out o);
                    LastDeleteLog.Add("prnmngr -d: exit=" + rc + " " + Short(o));
                }
            }
            catch (Exception ex) { LastDeleteLog.Add("prnmngr -d: " + ex.Message); }
            if (WaitGone(printerName, 3000))
            {
                message = "已删除（prnmngr）。";
                return true;
            }

            // 路线四：PrintManagement（Where-Object 精确比名字，避开 [ ] 通配）
            try
            {
                string o;
                int rc = RunPowerShell(
                    "$n = " + Q(printerName) + "; "
                    + "Get-Printer -ErrorAction Stop | Where-Object { $_.Name -eq $n } | "
                    + "Remove-Printer -ErrorAction Stop; 'ok'", out o);
                LastDeleteLog.Add("Remove-Printer: exit=" + rc + " " + Short(o));
            }
            catch (Exception ex) { LastDeleteLog.Add("Remove-Printer: " + ex.Message); }
            if (WaitGone(printerName, 3000))
            {
                message = "已删除（Remove-Printer）。";
                return true;
            }

            message = IsAdmin()
                ? "四条路线都没删掉它"
                : "四条路线都没删掉它；当前不是管理员，用『以管理员身份运行』再试一次";
            return false;
        }

        public static string FindPrnMngr()
        {
            if (_prnmngr != null) return _prnmngr.Length == 0 ? null : _prnmngr;
            _prnmngr = "";
            try
            {
                string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string root = Path.Combine(sys, "Printing_Admin_Scripts");
                if (Directory.Exists(root))
                {
                    string[] files = Directory.GetFiles(root, "prnmngr.vbs", SearchOption.AllDirectories);
                    if (files.Length > 0) _prnmngr = files[0];
                }
            }
            catch { }
            return _prnmngr.Length == 0 ? null : _prnmngr;
        }

        private static bool WaitGone(string printerName, int timeoutMs)
        {
            int waited = 0;
            while (true)
            {
                if (!PrinterExists(printerName)) return true;
                if (waited >= timeoutMs) return false;
                System.Threading.Thread.Sleep(300);
                waited += 300;
            }
        }

        private static string WqlStr(string s)
        {
            return "'" + (s == null ? "" : s.Replace("\\", "\\\\").Replace("'", "\\'")) + "'";
        }

        // ---------- 添加队列 ----------

        /// <summary>最近一次「添加打印机」的逐路线结果，界面日志直接刷这个。</summary>
        public static List<string> LastAddLog = new List<string>();

        /// <summary>本机已安装的打印驱动名（去重、忽略大小写、排序）。</summary>
        public static List<string> GetPrinterDrivers()
        {
            var list = new List<string>();
            string output;
            try
            {
                RunPowerShell("$ProgressPreference='SilentlyContinue'; "
                    + "Get-PrinterDriver | Sort-Object Name | ForEach-Object { $_.Name }", out output);
                AddOutputLines(list, output);
            }
            catch { }

            if (list.Count == 0)
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PrinterDriver"))
                    {
                        foreach (ManagementBaseObject row in searcher.Get())
                        {
                            string name = Str(row["Name"]);
                            int comma = name.IndexOf(',');
                            if (comma > 0) name = name.Substring(0, comma);
                            AddUnique(list, name.Trim());
                        }
                    }
                }
                catch { }
                SortIgnoreCase(list);
            }
            return list;
        }

        /// <summary>USB 打印端口（USB Monitor 下注册的端口，如 USB001）。</summary>
        public static List<string> GetUsbPorts()
        {
            var list = new List<string>();
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Print\Monitors\USB Monitor\Ports"))
                {
                    if (key != null)
                    {
                        foreach (string name in key.GetSubKeyNames()) list.Add(name);
                    }
                }
            }
            catch { }
            SortIgnoreCase(list);
            return list;
        }

        /// <summary>插在 USB 上的打印设备（PnP 视角），带它占用的端口后缀。</summary>
        public static List<UsbDevice> GetUsbPrintDevices()
        {
            var list = new List<UsbDevice>();
            string output;
            try
            {
                RunPowerShell(
                    "$ProgressPreference='SilentlyContinue'; "
                    + "Get-PnpDevice -Class Printer -ErrorAction SilentlyContinue | "
                    + "ForEach-Object { $_.FriendlyName + \"`t\" + $_.InstanceId }", out output);
            }
            catch { return list; }

            if (output == null) return list;
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#<") || line.StartsWith("<Objs") || line.StartsWith("<?xml")) continue;
                string[] parts = line.Split('\t');
                if (parts.Length < 2) continue;

                var d = new UsbDevice();
                d.FriendlyName = parts[0].Trim();
                d.InstanceId = parts[1].Trim();
                d.Port = PortFromInstanceId(d.InstanceId);
                if (d.Port.Length > 0 && FindItem(list, d.Port) < 0) list.Add(d);
            }
            return list;
        }

        private static int FindItem(List<UsbDevice> list, string port)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Port, port, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private static string PortFromInstanceId(string instanceId)
        {
            if (instanceId == null) return "";
            int amp = instanceId.LastIndexOf('&');
            string tail = amp >= 0 ? instanceId.Substring(amp + 1) : "";
            tail = tail.Trim();
            if (tail.Length > 3 && tail.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) return tail;
            return "";
        }

        /// <summary>找指向该地址的已有 TCP 端口名；没有返回空串。</summary>
        public static string FindTcpPortByHost(string host)
        {
            if (host == null) return "";
            foreach (KeyValuePair<string, PortInfo> kv in GetTcpPorts())
            {
                if (string.Equals(kv.Value.HostAddress, host, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(kv.Key, host, StringComparison.OrdinalIgnoreCase))
                    return kv.Key;
            }
            return "";
        }

        /// <summary>新建打印队列。三条路线依次试，成功后回读端口校验。</summary>
        public static bool CreateQueue(string printerName, string driverName, string portName, out string message)
        {
            LastAddLog.Clear();
            message = "";
            if (printerName == null || printerName.Trim().Length == 0) { message = "队列名不能为空"; return false; }
            printerName = printerName.Trim();
            if (PrinterExists(printerName)) { message = "本机已经有同名队列了：" + printerName; return false; }

            // 路线一：PrintManagement 模块（实测普通权限可用）
            string out1;
            int rc1 = RunPowerShell("Add-Printer -Name " + Q(printerName) + " -DriverName " + Q(driverName)
                + " -PortName " + Q(portName) + " -ErrorAction Stop", out out1);
            LastAddLog.Add("Add-Printer: exit=" + rc1 + " " + Short(out1));
            if (WaitAppear(printerName, 20000)) return Confirm(printerName, portName, out message);

            // 路线二：prnmngr.vbs -a
            string vbs = FindPrnMngr();
            if (vbs != null)
            {
                string out2;
                int rc2 = RunHidden("cscript.exe", "//nologo " + Q(vbs) + " -a -p " + Q(printerName)
                    + " -m " + Q(driverName) + " -r " + Q(portName), 40000, out out2);
                LastAddLog.Add("prnmngr.vbs -a: exit=" + rc2 + " " + Short(out2));
                if (WaitAppear(printerName, 20000)) return Confirm(printerName, portName, out message);
            }
            else
            {
                LastAddLog.Add("prnmngr.vbs -a: 没找到脚本");
            }

            // 路线三：WMI（本机实测会报 Invalid parameter，留作兜底）
            try
            {
                using (var cls = new ManagementClass("Win32_Printer"))
                using (ManagementObject mo = cls.CreateInstance())
                {
                    mo["Name"] = printerName;
                    mo["DriverName"] = driverName;
                    mo["PortName"] = portName;
                    mo["Shared"] = false;
                    mo.Put();
                }
                LastAddLog.Add("WMI CreateInstance+Put: 已提交");
                if (WaitAppear(printerName, 20000)) return Confirm(printerName, portName, out message);
            }
            catch (Exception ex)
            {
                LastAddLog.Add("WMI CreateInstance+Put: " + ex.GetType().Name + " " + Short(ex.Message));
            }

            message = "三条路线都没建出队列 " + printerName + "。日志里若是拒绝访问，用『以管理员身份运行』再试一次。";
            return false;
        }

        /// <summary>连接共享打印机（\\主机\共享名）。</summary>
        public static bool ConnectSharedPrinter(string uncPath, out string message)
        {
            LastAddLog.Clear();
            message = "";
            string unc = (uncPath == null ? "" : uncPath.Trim());
            if (unc.Length < 5 || !unc.StartsWith("\\\\"))
            {
                message = "共享打印机要写成 \\\\主机\\共享名";
                return false;
            }

            string out1;
            int rc1 = RunPowerShell("Add-Printer -ConnectionName " + Q(unc) + " -ErrorAction Stop", out out1);
            LastAddLog.Add("Add-Printer -ConnectionName: exit=" + rc1 + " " + Short(out1));

            string trimmed = unc.TrimEnd('\\');
            int slash = trimmed.LastIndexOf('\\');
            string shareName = slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
            if (WaitAppear(shareName, 20000))
            {
                message = "已连接共享打印机：" + unc;
                return true;
            }

            message = "没能连上共享打印机 " + unc + "。确认目标机器开着、共享名对、当前账号有权限。";
            return false;
        }

        private static bool Confirm(string printerName, string portName, out string message)
        {
            string actual = GetPrinterPort(printerName);
            if (actual != null && actual.Length > 0)
            {
                if (string.Equals(actual, portName, StringComparison.OrdinalIgnoreCase))
                {
                    message = "已添加：" + printerName + "（端口 " + portName + "）";
                    return true;
                }
                message = "已添加，但端口是 " + actual + "（期望 " + portName + "），请刷新后核对。";
                return false;
            }
            message = "队列已出现，但读不到端口，刷新队列列表确认。";
            return true;
        }

        private static bool WaitAppear(string printerName, int timeoutMs)
        {
            int waited = 0;
            while (true)
            {
                if (PrinterExists(printerName)) return true;
                if (waited >= timeoutMs) return false;
                System.Threading.Thread.Sleep(400);
                waited += 400;
            }
        }

        private static void AddOutputLines(List<string> list, string text)
        {
            if (text == null) return;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#<")) continue;
                if (line.StartsWith("<Objs") || line.StartsWith("<?xml")) continue;
                if (line.IndexOf("schemas.microsoft.com/powershell", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                AddUnique(list, line);
            }
            SortIgnoreCase(list);
        }

        private static void AddUnique(List<string> list, string value)
        {
            if (value == null || value.Length == 0) return;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return;
            }
            list.Add(value);
        }

        private static void SortIgnoreCase(List<string> list)
        {
            list.Sort(delegate(string a, string b) { return string.Compare(a, b, StringComparison.OrdinalIgnoreCase); });
        }

        // ---------- 杂项 ----------

        private static int _integrity = -2;

        /// <summary>
        /// 当前进程的完整性级别：0x1000 低、0x2000 中、0x3000 高、0x4000 系统。
        /// 低完整性（受限令牌）下改打印配置一律「拒绝访问」——从聊天窗口/沙箱里点开 exe 就会这样，
        /// 而右键资源管理器双击是「中」，同样能建端口。所以这个值决定要不要提示用户换个方式启动。
        /// </summary>
        public static int GetIntegrityLevel()
        {
            if (_integrity != -2) return _integrity;

            string output = "";
            try { RunHidden("whoami.exe", "/groups", 8000, out output); }
            catch { }

            string[] sids = { "S-1-16-16384", "S-1-16-12288", "S-1-16-8192", "S-1-16-4096" };
            int[] values = { 0x4000, 0x3000, 0x2000, 0x1000 };
            _integrity = -1;
            for (int i = 0; i < sids.Length; i++)
            {
                if (output.IndexOf(sids[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _integrity = values[i];
                    break;
                }
            }
            return _integrity;
        }

        public static string IntegrityText()
        {
            int lv = GetIntegrityLevel();
            if (lv == 0x1000) return "低（Low，受限）";
            if (lv == 0x2000) return "中（Medium，正常）";
            if (lv == 0x3000) return "高（High，已提权）";
            if (lv == 0x4000) return "系统（System）";
            return "未知";
        }

        /// <summary>当前是否被限制在低完整性沙箱里（改打印配置会失败）。</summary>
        public static bool IsRestricted()
        {
            return GetIntegrityLevel() == 0x1000;
        }
        public static bool IsAdmin()
        {
            try
            {
                using (WindowsIdentity wi = WindowsIdentity.GetCurrent())
                {
                    var wp = new WindowsPrincipal(wi);
                    return wp.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        public static int RunHidden(string exe, string args, int timeoutMs, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    var sbOut = new StringBuilder();
                    var sbErr = new StringBuilder();
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) sbOut.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) sbErr.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        output = "命令超时（" + timeoutMs + "ms）";
                        return -1;
                    }
                    p.WaitForExit();
                    output = (sbOut.ToString() + sbErr.ToString()).Trim();
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -2;
            }
        }

        private static string Str(object o)
        {
            return o == null ? "" : Convert.ToString(o);
        }

        private static bool Bool(object o)
        {
            if (o == null) return false;
            try { return Convert.ToBoolean(o); } catch { return false; }
        }
    }
}
