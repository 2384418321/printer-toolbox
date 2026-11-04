using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PrinterIpFix
{
    /// <summary>
    /// 驱动 ↔ 设备 的「语言兼容性」判定与自动校准。
    ///
    /// 背景（本机实证）：Windows 队列报「已打印」不代表出纸。真正决定成败的是
    /// **驱动输出的是什么页面描述语言**，以及**设备认不认这门语言**。
    /// 实测两种驱动 + RAW 9100 端口：
    ///   · Microsoft IPP Class Driver  → 原样推 %PDF-1.7  → 设备格式表里没有 application/pdf → 静默丢弃
    ///   · HP Smart Universal Printing → @PJL + ENTER LANGUAGE=PCLXL → 设备 LANGUAGES 里没有 PCLXL → 同样丢弃
    /// 而设备认的是 PWGRASTER / URF / JPEG / TIFF / PCL3GUI / PCLm。
    ///
    /// 所以「自动校准驱动」不能靠驱动名硬编码猜，必须两条都实测：
    ///   ① 设备自报支持的语言（@PJL INFO CONFIG 的 LANGUAGES）
    ///   ② 驱动实际吐出来的语言（建临时队列指向本机 910x 端口，真打一份，抓字节头判定）
    /// 两者求交，有交集就换驱动／留用，没交集就明确告知只能走 IPP 直发。
    /// 这套流程不含任何品牌/型号常量，换任何打印机都成立。
    /// </summary>
    internal static class DriverCompat
    {
        /// <summary>最近一次判定的说明，界面日志直接刷这个。</summary>
        public static string LastNote = "";

        // ================= 一、设备语言白名单 =================

        /// <summary>
        /// 走 9100 发 @PJL INFO CONFIG，抠出 LANGUAGES 段的枚举值。
        /// 设备不认 PJL 或超时都返回空列表（调用方按「不知道」处理，不要当成「不支持」）。
        /// </summary>
        public static List<string> DeviceLanguages(string ip, int timeoutMs, out string raw)
        {
            var list = new List<string>();
            raw = "";
            if (string.IsNullOrEmpty(ip)) return list;

            string text = SendPjl(ip, "INFO CONFIG", timeoutMs);
            raw = text;
            if (text.Length == 0) return list;

            int at = text.IndexOf("LANGUAGES", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return list;

            // LANGUAGES 段头形如「LANGUAGES [6 ENUMERATED]」。
            // 设备在方括号里就把条数报出来了，必须按这个条数截断 —— 实测 PJL 的
            // LANGUAGES 段后面紧跟 USTATUS / DEVICE / JOB / PAGE / SERIAL / MODEL / FIRMWARE
            // 这些段名，若只靠「遇空行就停」会把它们全当成语言收进来。
            string tail = text.Substring(at);
            int want = -1;
            int lb = tail.IndexOf('[');
            if (lb >= 0)
            {
                int rb = tail.IndexOf(']', lb);
                if (rb > lb)
                {
                    string inside = tail.Substring(lb + 1, rb - lb - 1).Trim();
                    int sp = inside.IndexOf(' ');
                    string num = sp > 0 ? inside.Substring(0, sp) : inside;
                    int n;
                    if (int.TryParse(num, out n) && n > 0 && n < 64) want = n;
                }
            }

            string[] lines = tail.Replace("\r", "").Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                string s = lines[i].Trim();
                if (s.Length == 0) break;
                // 段名行形如「USTATUS [2 ENUMERATED]」，带方括号的一律不是语言值
                if (s.IndexOf('[') >= 0) break;
                // 属性行形如「SERIAL NUMBER = xxx」，带等号也不是语言值
                if (s.IndexOf('=') >= 0) break;
                // 有些机型语言和属性同行、用空格分隔，取第一段大写字母数字串。
                int cut = s.IndexOf(' ');
                if (cut > 0) s = s.Substring(0, cut);
                s = s.Trim();
                if (s.Length == 0) continue;
                bool ok = true;
                for (int k = 0; k < s.Length; k++)
                {
                    char c = s[k];
                    if (!(char.IsLetterOrDigit(c) || c == '/' || c == '-' || c == '_')) { ok = false; break; }
                }
                if (!ok) break;
                list.Add(s.ToUpperInvariant());
                if (want > 0 && list.Count >= want) break;
            }
            return list;
        }

        /// <summary>@PJL INFO STATUS，用来确认设备在线（可选，失败不影响主流程）。</summary>
        public static string DeviceStatusLine(string ip, int timeoutMs)
        {
            string t = SendPjl(ip, "INFO STATUS", timeoutMs);
            if (t.Length == 0) return "";
            return t.Replace("\r", "").Replace("\n", " ").Trim();
        }

        private static string SendPjl(string ip, string command, int timeoutMs)
        {
            TcpClient client = null;
            try
            {
                client = new TcpClient();
                client.SendTimeout = timeoutMs;
                client.ReceiveTimeout = timeoutMs;
                if (!client.ConnectAsync(ip, 9100).Wait(timeoutMs)) return "";
                NetworkStream ns = client.GetStream();
                byte[] payload = Encoding.ASCII.GetBytes(
                    (char)27 + "%-12345X@PJL " + command + "\r\n" + (char)27 + "%-12345X");
                ns.Write(payload, 0, payload.Length);
                ns.Flush();

                var sb = new StringBuilder();
                var buf = new byte[4096];
                DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
                while (DateTime.Now < deadline)
                {
                    int n = 0;
                    try { n = ns.Read(buf, 0, buf.Length); }
                    catch { break; }
                    if (n <= 0) break;
                    sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                    // 一页回显足够，别等设备那边超时
                    if (sb.Length > 2048) break;
                }
                return sb.ToString();
            }
            catch { return ""; }
            finally { if (client != null) { try { client.Close(); } catch { } } }
        }

        // ================= 二、驱动实际输出语言 =================

        /// <summary>
        /// 建一条临时队列指向本机监听端口，真打一份测试页，抓首包字节判定语言。
        /// 判完立即删队列、删端口，不留痕迹。任何品牌驱动都适用 —— 这是唯一不靠猜的办法。
        /// </summary>
        public static string CaptureDriverLanguage(string driverName, int timeoutMs, out string detail)
        {
            detail = "";
            if (string.IsNullOrEmpty(driverName)) { detail = "驱动名为空"; return ""; }

            TcpListener listener = null;
            string portName = "";
            string queueName = "";
            int portNum = 0;

            try
            {
                // 1) 找个能监听的本地端口（9101~9120）
                for (int n = 9101; n <= 9120; n++)
                {
                    try
                    {
                        listener = new TcpListener(IPAddress.Loopback, n);
                        listener.Start();
                        portNum = n;
                        break;
                    }
                    catch { listener = null; }
                }
                if (listener == null) { detail = "没有可用的本地监听端口（9101~9120 都被占用）"; return ""; }

                // 2) 建端口 + 临时队列
                portName = "PRNTOOLBOX_CAP_" + portNum;
                if (!PrinterSystem.CreateTcpPort(portName, "127.0.0.1", portNum))
                {
                    detail = "建临时端口失败：" + string.Join(" / ", PrinterSystem.LastPortLog.ToArray());
                    return "";
                }
                queueName = "PRNTOOLBOX_CAPQ_" + portNum;
                string err;
                if (!PrinterSystem.CreateQueue(queueName, driverName, portName, out err))
                {
                    detail = "建临时队列失败：" + err + " " + string.Join(" / ", PrinterSystem.LastAddLog.ToArray());
                    return "";
                }

                // 3) 真打一份（走渲染管线，才会产生驱动的真实输出）
                PrintTestPage(queueName);
                Thread.Sleep(1500);

                // 4) 收首包
                byte[] data = AcceptBytes(listener, timeoutMs);
                if (data == null || data.Length == 0)
                {
                    detail = "打印作业没有到达本机监听端口（驱动没吐数据，可能被策略拦下）";
                    return "";
                }
                string lang = Classify(data, out detail);
                detail = string.Format("{0} 字节 · {1}", data.Length, detail);
                return lang;
            }
            catch (Exception ex)
            {
                detail = "抓包异常：" + ex.GetType().Name + " " + ex.Message;
                return "";
            }
            finally
            {
                if (queueName.Length > 0) { try { PrinterSystem.DeletePrinter(queueName, out _dummy); } catch { } }
                if (portName.Length > 0) { try { PrinterSystem.DeleteTcpPort(portName, out _dummy); } catch { } }
                if (listener != null) { try { listener.Stop(); } catch { } }
            }
        }

        private static string _dummy = "";

        private static byte[] AcceptBytes(TcpListener listener, int timeoutMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                if (!listener.Pending()) { Thread.Sleep(150); continue; }
                TcpClient c = null;
                try
                {
                    c = listener.AcceptTcpClient();
                    c.ReceiveTimeout = timeoutMs;
                    NetworkStream ns = c.GetStream();
                    // 等首块数据攒够再读，spooler 是一次性灌完的
                    Thread.Sleep(300);
                    var arr = new List<byte>();
                    var buf = new byte[8192];
                    DateTime readDeadline = DateTime.Now.AddMilliseconds(Math.Max(2000, timeoutMs / 2));
                    while (DateTime.Now < readDeadline && arr.Count < 24 * 1024 * 1024)
                    {
                        int n = 0;
                        try { n = ns.Read(buf, 0, buf.Length); }
                        catch { break; }
                        if (n <= 0) break;
                        for (int i = 0; i < n; i++) arr.Add(buf[i]);
                        if (arr.Count > 65536) break;   // 头部信息够了，不必收全
                    }
                    return arr.Count == 0 ? null : arr.ToArray();
                }
                catch { return null; }
                finally { if (c != null) { try { c.Close(); } catch { } } }
            }
            return null;
        }

        /// <summary>按首若干字节判定页面描述语言（不依赖任何厂商常量）。</summary>
        public static string Classify(byte[] data, out string detail)
        {
            detail = "";
            if (data == null || data.Length < 4) { detail = "数据太短，判不了"; return "未知"; }

            int head = Math.Min(data.Length, 4096);
            string ascii = Encoding.ASCII.GetString(data, 0, head);

            // PDF：前 5 字节 %PDF-
            if (ascii.StartsWith("%PDF")) { detail = "首包是 %PDF —— Adobe PDF"; return "PDF"; }

            // PostScript：%!PS
            if (ascii.StartsWith("%!PS")) { detail = "首包是 %!PS —— PostScript"; return "PostScript"; }

            // PJL 头：ESC%-12345X 开头，里面带 ENTER LANGUAGE=<名称>
            int enter = ascii.IndexOf("ENTER LANGUAGE", StringComparison.OrdinalIgnoreCase);
            if (enter >= 0)
            {
                string rest = ascii.Substring(enter + "ENTER LANGUAGE".Length).TrimStart();
                if (rest.StartsWith("=")) rest = rest.Substring(1).TrimStart();
                int end = 0;
                while (end < rest.Length && (char.IsLetterOrDigit(rest[end]) || rest[end] == '/' || rest[end] == '-')) end++;
                string lang = rest.Substring(0, end).Trim().ToUpperInvariant();
                if (lang.Length == 0) lang = "PCL";
                detail = "PJL 头声明 ENTER LANGUAGE=" + lang;
                return lang;
            }

            // 裸 ESC E：PCL5
            if (data[0] == 27 && data[1] == (byte)'E') { detail = "裸 ESC E —— PCL5"; return "PCL5"; }

            // ESC%-12345X 但没 ENTER LANGUAGE：多数喷墨机按 PCL 处理
            if (ascii.StartsWith("\u001b%-12345X")) { detail = "PJL 头但没有 ENTER LANGUAGE"; return "PCL"; }

            // 其它二进制：可能是 PWG Raster / URF
            if (ascii.IndexOf("RaS2", StringComparison.Ordinal) >= 0 || ascii.IndexOf("UNIRAST", StringComparison.Ordinal) >= 0)
            { detail = "含 PWG/URF 魔数"; return "RASTER"; }

            detail = "首包 " + Printable(data, 24) + "（未能识别）";
            return "未知";
        }

        private static string Printable(byte[] b, int n)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < b.Length && i < n; i++)
            {
                byte v = b[i];
                if (v >= 32 && v < 127) sb.Append((char)v);
                else sb.Append('.');
            }
            return sb.ToString();
        }

        // ================= 三、兼容性判定 =================

        /// <summary>设备语言表里有没有这门语言（宽松匹配：PCLXL ⊂ PCL6、PCL3GUI ⊂ PCL）。</summary>
        public static bool Compatible(List<string> deviceLangs, string driverLang)
        {
            if (deviceLangs == null || deviceLangs.Count == 0) return false;
            if (string.IsNullOrEmpty(driverLang)) return false;

            string d = driverLang.ToUpperInvariant();

            // PDF 只能靠 IPP 通道送，RAW 队列一律不兼容（设备格式表里几乎都没有 application/pdf）
            if (d == "PDF" || d == "POSTSCRIPT") return false;

            for (int i = 0; i < deviceLangs.Count; i++)
            {
                string s = deviceLangs[i].ToUpperInvariant();

                if (s == d) return true;
                if (d == "PCLXL" || d == "PCL6")
                {
                    if (s == "PCLXL" || s == "PCL6" || s == "PCLXL6") return true;
                }
                if (d == "PCL3GUI" || d == "PCL3")
                {
                    // PCL3GUI 是喷墨专有光栅语言；只有当设备明确列出 PCL3GUI 才算兼容。
                    // 退到通用「PCL」是常见误判源（黑白激光机的 PCL 并不吃 PCL3GUI 的字形），
                    // 所以只放行显式写 PCL3GUI / PCL3 的机型。
                    if (s == "PCL3GUI" || s == "PCL3") return true;
                }
                if (d == "PCL5")
                {
                    // PCL5 只能落到真认 PCL5 的设备上。别把 PCL3GUI 算进来 ——
                    // 两者是不同的解释器（PCL3GUI 是喷墨机的光栅语言），算进来会误判成「能打」。
                    if (s == "PCL5" || s == "PCL5E" || s == "PCL") return true;
                }
                if (d == "RASTER")
                {
                    if (s == "PWGRASTER" || s == "URF" || s == "PWG" || s == "JPEG") return true;
                }
                // 裸 PJL 头（没有 ENTER LANGUAGE）只能笼统算 PCL —— 这一类放行两种 PCL 家族
                if (d == "PCL")
                {
                    if (s == "PCL3GUI" || s == "PCL5" || s == "PCL5E" || s == "PCL") return true;
                }
                // PCLm（移动打印用的 PDF 兼容子集）
                if (d == "PCLM" && s == "PCLM") return true;
            }
            return false;
        }

        /// <summary>一句人话结论：这条队列能不能真出纸。</summary>
        public static string VerdictText(List<string> deviceLangs, string driverLang)
        {
            string langs = deviceLangs == null || deviceLangs.Count == 0
                ? "（设备没有上报语言表）"
                : string.Join("/", deviceLangs.ToArray());
            if (string.IsNullOrEmpty(driverLang))
                return "设备认：" + langs + "。驱动输出的语言没测出来，判不了 —— 建议用「IPP 直发」直接送。";
            if (Compatible(deviceLangs, driverLang))
                return "设备认：" + langs + "。这个驱动输出 " + driverLang + "，在名单里 —— 这条队列能真出纸。";
            return "设备认：" + langs + "。这个驱动输出 " + driverLang
                 + "，不在名单里 —— Windows 会报「已打印」，机器不出纸。";
        }

        // ================= 四、自动校准 =================

        /// <summary>可安装的本地驱动名列表（去重排序），用来找替代驱动。</summary>
        public static List<string> InstalledDrivers()
        {
            try { return PrinterSystem.GetPrinterDrivers(); }
            catch { return new List<string>(); }
        }

        /// <summary>原地把队列换成另一个驱动（实测 Set-Printer -DriverName 免管理员可用）。</summary>
        public static bool SetDriver(string queueName, string driverName, out string message)
        {
            message = "";
            if (string.IsNullOrEmpty(queueName) || string.IsNullOrEmpty(driverName)) { message = "参数为空"; return false; }
            string output;
            int rc;
            try
            {
                rc = PrinterSystem.RunPowerShell(
                    "Set-Printer -Name " + PrinterSystem.Q(queueName)
                    + " -DriverName " + PrinterSystem.Q(driverName) + " -ErrorAction Stop", out output);
            }
            catch (Exception ex)
            {
                message = ex.GetType().Name + " " + ex.Message;
                return false;
            }
            if (rc != 0)
            {
                message = "exit=" + rc + " " + Shorten(output, 200);
                return false;
            }
            // 回读确认真的换了
            try
            {
                foreach (PrinterInfo p in PrinterSystem.GetPrinters())
                {
                    if (string.Equals(p.Name, queueName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(p.DriverName, driverName, StringComparison.OrdinalIgnoreCase))
                        {
                            message = "已换成 " + driverName;
                            return true;
                        }
                        message = "命令返回成功，但队列驱动仍是 " + p.DriverName;
                        return false;
                    }
                }
            }
            catch { }
            message = "命令返回成功，但回读时队列不见了";
            return false;
        }

        /// <summary>
        /// 完整流程：探设备语言 → 实测当前驱动输出语言 → 不兼容则在本机驱动库里找能用的。
        /// 找到就原地换驱动并回读；找不到就明确说只能走 IPP 直发。不删队列、不建 RAW 端口。
        /// </summary>
        public static bool AutoCalibrate(PrinterInfo p, DeviceInfo d, Action<string> log)
        {
            Action<string> noop = delegate(string s) { };
            if (log == null) log = noop;

            if (p == null) { LastNote = "没有选中队列。"; return false; }
            if (d == null) { LastNote = "没有匹配到设备，先点「扫描网络设备」再试。"; log(LastNote); return false; }

            log("开始判定 " + p.Name + "（驱动 " + p.DriverName + "）");

            // ① 设备语言表
            string rawCfg;
            List<string> langs = DeviceLanguages(d.Ip, 6000, out rawCfg);
            if (langs.Count == 0)
            {
                LastNote = "设备没有回应语言表（9100 上的 @PJL INFO CONFIG 无输出）。"
                         + "这台设备可能不认 PJL —— 请直接用「IPP 直发」，别指望 RAW 队列。";
                log(LastNote);
                return false;
            }
            log("设备自报语言：" + string.Join("/", langs.ToArray()));

            // ② 当前驱动实际输出语言（抓包实测，不猜）
            string detail;
            string curLang = CaptureDriverLanguage(p.DriverName, 20000, out detail);
            log("驱动实测输出：" + (curLang.Length == 0 ? "（没抓到）" : curLang) + " · " + detail);

            if (curLang.Length > 0 && Compatible(langs, curLang))
            {
                LastNote = "不用改：" + p.DriverName + " 输出 " + curLang + "，设备认。";
                log(LastNote);
                return true;
            }

            // ③ 在本机驱动库里找输出语言在设备白名单内的驱动
            log("当前驱动不兼容，在本机驱动库里找能用的…");
            List<string> drivers = InstalledDrivers();
            string picked = "";
            var tried = new List<string>();
            for (int i = 0; i < drivers.Count; i++)
            {
                string dn = drivers[i];
                if (string.Equals(dn, p.DriverName, StringComparison.OrdinalIgnoreCase)) continue;
                // 虚拟打印机（PDF/XPS/传真/OneNote）不参与
                string up = dn.ToUpperInvariant();
                if (up.IndexOf("PRINT TO PDF") >= 0 || up.IndexOf("XPS") >= 0
                    || up.IndexOf("FAX") >= 0 || up.IndexOf("ONENOTE") >= 0
                    || up.IndexOf("ADOBE PDF") >= 0 || up.IndexOf("ONEPDF") >= 0) continue;

                tried.Add(dn);
                string why;
                string l = CaptureDriverLanguage(dn, 20000, out why);
                log("  试 " + dn + " → " + (l.Length == 0 ? "（没抓到）" : l) + " · " + why);
                if (l.Length > 0 && Compatible(langs, l)) { picked = dn; break; }
            }

            if (picked.Length > 0)
            {
                string msg;
                if (SetDriver(p.Name, picked, out msg))
                {
                    LastNote = "已把 " + p.Name + " 换成 " + picked + "（输出语言在设备白名单内）。" + msg;
                    log(LastNote);
                    return true;
                }
                LastNote = "找到可用驱动 " + picked + "，但换不上：" + msg
                         + "。请正常双击打开工具箱（受限权限换不了驱动），或手动到打印机属性里换驱动。";
                log(LastNote);
                return false;
            }

            // ④ 都不行 —— 说清楚为什么、以及唯一可行的路
            LastNote = "本机 " + drivers.Count + " 个驱动里没有一个能输出设备认的语言（"
                     + string.Join("/", langs.ToArray()) + "）。"
                     + "这不是配置能修好的：设备根本不接受 PDF/PCLXL，而 Windows 队列只会发这两种。"
                     + "唯一能真出纸的路是「IPP 直发」（按 image/jpeg 送图像），或用厂商原厂安装包重装驱动后再试。";
            log(LastNote);
            return false;
        }

        private static string Shorten(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length <= n) return s;
            return s.Substring(0, n) + "...";
        }

        private static void PrintTestPage(string queueName)
        {
            try
            {
                string q = PrinterSystem.Q(queueName);
                PrinterSystem.RunPowerShell(
                    "$p = Get-CimInstance Win32_Printer -Filter ('Name=' + " + q + "); "
                    + "if ($p) { Invoke-CimMethod -InputObject $p -MethodName PrintTestPage | Out-Null }", out _ptOut);
            }
            catch { }
        }

        private static string _ptOut = "";
    }
}
