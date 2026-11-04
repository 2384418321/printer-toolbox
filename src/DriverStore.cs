using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;

namespace PrinterIpFix
{
    /// <summary>驱动库里的一个条目。</summary>
    internal sealed class DriverEntry
    {
        public string Name;        // 驱动名 / 型号
        public string Provider;    // 厂商
        public string Version;
        public string Inf;         // INF 文件名或发布名
        public string InfPath;     // 本地可用的 INF 绝对路径（没有则为空）
        public string Source;      // 来源：队列驱动 / 驱动库数据库 / 本地驱动文件夹
        public List<string> Models = new List<string>();   // INF 里声明的型号列表（匹配的最硬证据）

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>驱动库的枚举、匹配与安装。只读本机已装驱动与本地目录，联网下载由 DriverOnline 负责。</summary>
    internal static class DriverStore
    {
        // ---------------- 目录 ----------------

        public static string DriverDir()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PrinterToolbox\\drivers");
            if (Writable(dir)) return dir;

            string alt = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PrinterToolbox\\drivers");
            if (Writable(alt)) return alt;
            return dir;
        }

        private static bool Writable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                if (!Directory.Exists(dir)) return false;   // 低完整性下会被静默虚拟化，别当真
                string probe = Path.Combine(dir, ".w");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return Directory.Exists(dir);
            }
            catch { return false; }
        }

        // ---------------- 枚举 ----------------

        public static List<DriverEntry> Scan(List<string> note)
        {
            List<DriverEntry> all = new List<DriverEntry>();
            if (note == null) note = new List<string>();

            // 1) 本机打印队列在用的驱动（最可靠：装过就一定在）
            ScanQueueDrivers(all, note);

            // 2) 系统驱动库里所有签名驱动（含从没被队列用过的）
            ScanSignedDrivers(all, note);

            // 3) 我们自己的驱动文件夹（用户从官网下的包解压到这里）
            ScanLocalFolder(all, note);

            // 去重：同 INF + 同名只留一条
            List<DriverEntry> uniq = new List<DriverEntry>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < all.Count; i++)
            {
                string key = all[i].Name + "|" + all[i].Inf;
                if (seen.ContainsKey(key)) continue;
                seen[key] = true;
                uniq.Add(all[i]);
            }

            uniq.Sort(delegate(DriverEntry a, DriverEntry b)
            {
                int c = string.Compare(a.Provider, b.Provider, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return uniq;
        }

        private static void ScanQueueDrivers(List<DriverEntry> all, List<string> note)
        {
            // Get-PrinterDriver 是唯一能同时拿到「驱动名 + 厂商 + 版本 + INF 路径」的来源。
            // Win32_Printer 根本没有 Manufacturer / DriverVersion 这两个属性（选它会报「无效查询」）。
            string csv;
            int code = Run("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command "
              + "\"$ProgressPreference='SilentlyContinue'; "
              + "$ErrorActionPreference='SilentlyContinue'; "
              + "Get-PrinterDriver | Select-Object Name,Manufacturer,MajorVersion,MinorVersion,InfPath | ConvertTo-Csv -NoTypeInformation\"",
                60000, out csv);

            int got = 0;
            if (code == 0 && csv != null && csv.Length > 0)
            {
                string[] lines = csv.Replace("\r", "").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("\"Name\"") || line.StartsWith("Name,")) continue;
                    string[] f = SplitCsv(line);
                    if (f.Length < 2) continue;
                    DriverEntry e = new DriverEntry();
                    e.Name = f[0];
                    if (e.Name.Length == 0) continue;
                    e.Provider = f.Length > 1 ? f[1] : "";
                    string maj = f.Length > 2 ? f[2] : "";
                    string min = f.Length > 3 ? f[3] : "";
                    if (maj.Length > 0 || min.Length > 0)
                        e.Version = (maj.Length > 0 ? maj : "0") + "." + (min.Length > 0 ? min : "0");
                    e.InfPath = f.Length > 4 ? f[4] : "";
                    if (e.InfPath != null && e.InfPath.Length > 0)
                    {
                        e.Inf = Path.GetFileName(e.InfPath);
                        // 关键：通用驱动的名字里没有型号，型号只在 INF 里。
                        // 不读这一步，Match 就永远匹配不到 HP Smart Universal Printing。
                        try { e.Models = InfModels(e.InfPath); } catch { }
                    }
                    e.Source = "队列在用";
                    AddOrMerge(all, e);
                    got++;
                }
            }
            if (got == 0)
                note.Add("Get-PrinterDriver 没返回条目（exit=" + code + " " + FirstLine(csv) + "）。");

            // 兜底：只拿驱动名，至少让匹配有东西可比
            try
            {
                ManagementObjectSearcher es = new ManagementObjectSearcher(
                    "SELECT Name, DriverName FROM Win32_Printer");
                foreach (ManagementObject mo in es.Get())
                {
                    string drv = S(mo["DriverName"]);
                    if (drv.Length == 0) continue;
                    DriverEntry e = new DriverEntry();
                    e.Name = drv;
                    e.Source = "队列在用";
                    AddOrMerge(all, e);
                }
            }
            catch { }
        }

        // ---------------- 进程 / CSV ----------------

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
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
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

        /// <summary>按双引号规则切 CSV 行（驱动名里常有逗号，比如 "Xxx,3,Windows x64"）。</summary>
        private static string[] SplitCsv(string line)
        {
            List<string> fields = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool inQuote = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuote)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                        else inQuote = false;
                    }
                    else cur.Append(c);
                }
                else
                {
                    if (c == '"') inQuote = true;
                    else if (c == ',') { fields.Add(cur.ToString().Trim()); cur.Length = 0; }
                    else cur.Append(c);
                }
            }
            fields.Add(cur.ToString().Trim());
            return fields.ToArray();
        }

        private static string FirstLine(string s)
        {
            if (s == null || s.Length == 0) return "";
            string[] lines = s.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.Length > 0) return t.Length > 120 ? t.Substring(0, 120) : t;
            }
            return "";
        }

        /// <summary>"Xxx,3,Windows x64" → "Xxx"</summary>
        private static string StripDriverSuffix(string name)
        {
            int i = name.IndexOf(',');
            return i > 0 ? name.Substring(0, i).Trim() : name.Trim();
        }

        private static void ScanSignedDrivers(List<DriverEntry> all, List<string> note)
        {
            try
            {
                ManagementObjectSearcher es = new ManagementObjectSearcher(
                    "SELECT DeviceName, DriverProviderName, DriverVersion, InfName, DeviceID, DeviceClass "
                  + "FROM Win32_PnPSignedDriver WHERE DeviceClass='PRINTER'");
                foreach (ManagementObject mo in es.Get())
                {
                    string name = S(mo["DeviceName"]);
                    if (name.Length == 0) continue;
                    DriverEntry e = new DriverEntry();
                    e.Name = name;
                    e.Provider = S(mo["DriverProviderName"]);
                    e.Version = S(mo["DriverVersion"]);
                    e.Inf = S(mo["InfName"]);
                    e.InfPath = ResolveInfPath(e.Inf);
                    if (e.InfPath.Length > 0)
                    {
                        try { e.Models = InfModels(e.InfPath); } catch { }
                    }
                    e.Source = "系统驱动库";
                    AddOrMerge(all, e);
                }
            }
            catch (Exception ex) { note.Add("读系统驱动库失败：" + ex.Message); }
        }

        /// <summary>oem42.inf → DriverStore\FileRepository 下的真实路径。</summary>
        private static string ResolveInfPath(string infName)
        {
            if (infName == null || infName.Length == 0) return "";
            try
            {
                string repo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    @"System32\DriverStore\FileRepository");
                if (!Directory.Exists(repo)) return "";

                // oemNN.inf 的映射写在注册表里，按目录找太慢；只做有限探测
                string[] dirs = Directory.GetDirectories(repo);
                for (int i = 0; i < dirs.Length; i++)
                {
                    string guess = Path.Combine(dirs[i], infName);
                    if (File.Exists(guess)) return guess;
                }
            }
            catch { }
            return "";
        }

        private static void ScanLocalFolder(List<DriverEntry> all, List<string> note)
        {
            string root = DriverDir();
            string[] files;
            if (!Directory.Exists(root)) { note.Add("驱动文件夹还不存在：" + root); return; }
            try { files = Directory.GetFiles(root, "*.inf", SearchOption.AllDirectories); }
            catch (Exception ex) { note.Add("扫本地驱动文件夹失败：" + ex.Message); return; }

            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    List<string> models = InfModels(files[i]);
                    string provider = InfProvider(files[i]);
                    string ver = InfVersion(files[i]);
                    if (models.Count == 0)
                    {
                        DriverEntry e0 = new DriverEntry();
                        e0.Name = Path.GetFileNameWithoutExtension(files[i]);
                        e0.Provider = provider;
                        e0.Version = ver;
                        e0.Inf = Path.GetFileName(files[i]);
                        e0.InfPath = files[i];
                        e0.Models = models;
                        e0.Source = "本地驱动包";
                        AddOrMerge(all, e0);
                        continue;
                    }
                    for (int j = 0; j < models.Count; j++)
                    {
                        DriverEntry e = new DriverEntry();
                        e.Name = models[j];
                        e.Provider = provider;
                        e.Version = ver;
                        e.Inf = Path.GetFileName(files[i]);
                        e.InfPath = files[i];
                        e.Models = models;
                        e.Source = "本地驱动包";
                        AddOrMerge(all, e);
                    }
                }
                catch { }
            }
            if (files.Length > 0) note.Add("本地驱动包：" + files.Length + " 个 INF（来自 " + root + "）");
        }

        private static void AddOrMerge(List<DriverEntry> all, DriverEntry e)
        {
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Compare(all[i].Name, e.Name, StringComparison.OrdinalIgnoreCase) != 0) continue;
                if (all[i].InfPath.Length == 0 && e.InfPath.Length > 0) all[i].InfPath = e.InfPath;
                if (all[i].Provider.Length == 0 && e.Provider.Length > 0) all[i].Provider = e.Provider;
                if (all[i].Version.Length == 0 && e.Version.Length > 0) all[i].Version = e.Version;
                // 型号表必须合并：同一条驱动会从「队列在用」「系统驱动库」两个源各来一次，
                // 先到的可能没有 INF 路径因而没有型号表；只补 InfPath 不补 Models 会让匹配永远失败。
                if (e.Models != null && e.Models.Count > 0)
                {
                    if (all[i].Models == null) all[i].Models = new List<string>();
                    for (int k = 0; k < e.Models.Count; k++)
                    {
                        string m = e.Models[k];
                        bool dup = false;
                        for (int j = 0; j < all[i].Models.Count; j++)
                            if (string.Compare(all[i].Models[j], m, StringComparison.OrdinalIgnoreCase) == 0) { dup = true; break; }
                        if (!dup) all[i].Models.Add(m);
                    }
                }
                return;
            }
            all.Add(e);
        }

        // ---------------- INF 解析 ----------------

        private static string[] ReadLines(string path)
        {
            try
            {
                // INF 可能是 ANSI 或 UTF-16
                byte[] head = new byte[2];
                using (FileStream fs = File.OpenRead(path))
                {
                    if (fs.Read(head, 0, 2) != 2) return new string[0];
                }
                Encoding enc = (head[0] == 0xFF && head[1] == 0xFE)
                    ? Encoding.Unicode
                    : Encoding.GetEncoding(936);
                return File.ReadAllLines(path, enc);
            }
            catch { return new string[0]; }
        }

        private static string InfProvider(string path)
        {
            string[] lines = ReadLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith("Provider", StringComparison.OrdinalIgnoreCase) && t.IndexOf('=') > 0)
                    return t.Substring(t.IndexOf('=') + 1).Replace("\"", "").Trim();
            }
            return "";
        }

        private static string InfVersion(string path)
        {
            string[] lines = ReadLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith("DriverVer", StringComparison.OrdinalIgnoreCase) && t.IndexOf('=') > 0)
                {
                    string v = t.Substring(t.IndexOf('=') + 1).Trim();
                    int c = v.IndexOf(',');
                    if (c > 0) v = v.Substring(c + 1).Trim();
                    return v;
                }
            }
            return "";
        }

        /// <summary>
        /// 从 INF 里提取「这台驱动支持哪些型号」。
        ///
        /// 为什么不能只读 [Manufacturer] 段：真实 INF 的 [Manufacturer] 通常只有一行
        /// `%OEM% = OEM,NTamd64`，真正的型号表在它指向的子段 [OEM.NTamd64] 里；
        /// 而且 HP 这类万能驱动的每条目左边一律是 `%HPODPRINTERS%`（同一个显示名），
        /// **型号藏在右边的硬件 ID**（`MF\OfficeJet_Pro_9730_Series&amp;WSD&amp;IP_PRINT`）。
        /// 只读左边会得到一堆 `%OEM%`，等于没有信息 —— 这就是「驱动匹配不上」的根因。
        ///
        /// 所以这里做三件事：①顺着 Manufacturer 段找子段；②用 [Strings] 解析 %KEY%；
        /// ③把每条的左边（字面型号）和右边的硬件 ID（去掉 MF\、&amp;WSD 之类修饰）都当候选型号。
        /// </summary>
        private static List<string> InfModels(string path)
        {
            List<string> models = new List<string>();
            string[] lines = ReadLines(path);
            if (lines.Length == 0) return models;

            // ---- 第一遍：收集 [Strings] 与 [Manufacturer] 指向的子段名 ----
            var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var modelSections = new List<string>();
            bool inStrings = false, inManu = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0 || t[0] == ';') continue;

                if (t[0] == '[')
                {
                    int close = t.IndexOf(']');
                    string sec = close > 0 ? t.Substring(1, close - 1).Trim() : t.Substring(1).Trim();
                    inStrings = string.Compare(sec, "Strings", StringComparison.OrdinalIgnoreCase) == 0;
                    inManu = string.Compare(sec, "Manufacturer", StringComparison.OrdinalIgnoreCase) == 0;
                    // 名字里带 Models 的段直接当型号段（老式 INF 常见）
                    if (!inStrings && !inManu && sec.IndexOf("Models", StringComparison.OrdinalIgnoreCase) >= 0)
                        AddUnique(modelSections, sec);
                    continue;
                }

                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                string left = t.Substring(0, eq).Trim();
                string right = t.Substring(eq + 1).Trim();

                if (inStrings)
                {
                    if (left.Length > 0)
                    {
                        string v = right.Replace("\"", "").Trim();
                        strings[left] = v;
                    }
                }
                else if (inManu)
                {
                    // %Provider% = ModelsSection,Decoration  →  真实段名 ModelsSection.Decoration
                    string secPart = right;
                    int comma = secPart.IndexOf(',');
                    string deco = "";
                    if (comma >= 0) { deco = secPart.Substring(comma + 1).Trim(); secPart = secPart.Substring(0, comma).Trim(); }
                    string real = deco.Length > 0 ? secPart + "." + deco : secPart;
                    if (real.Length > 0) AddUnique(modelSections, real);
                    // 有的 INF 不写修饰（如 `%X% = OEM`），两种都登记
                    if (secPart.Length > 0) AddUnique(modelSections, secPart);
                }
            }

            // 一个子段都没找到就退回「所有像型号段的段」，别啥也不给
            if (modelSections.Count == 0)
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    if (t.Length > 2 && t[0] == '[')
                    {
                        int close = t.IndexOf(']');
                        string sec = close > 0 ? t.Substring(1, close - 1).Trim() : "";
                        if (sec.IndexOf("OEM", StringComparison.OrdinalIgnoreCase) >= 0
                         || sec.IndexOf("Models", StringComparison.OrdinalIgnoreCase) >= 0)
                            AddUnique(modelSections, sec);
                    }
                }
            }

            // ---- 第二遍：读每个型号段 ——---
            for (int s = 0; s < modelSections.Count; s++)
            {
                string want = modelSections[s];
                bool inSection = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    if (t.Length == 0 || t[0] == ';') continue;
                    if (t[0] == '[')
                    {
                        int close = t.IndexOf(']');
                        string sec = close > 0 ? t.Substring(1, close - 1).Trim() : t.Substring(1).Trim();
                        inSection = string.Compare(sec, want, StringComparison.OrdinalIgnoreCase) == 0;
                        continue;
                    }
                    if (!inSection) continue;

                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    string left = t.Substring(0, eq).Trim();
                    string right = t.Substring(eq + 1).Trim();

                    // ① 左边：字面型号，或者 %KEY% 去 [Strings] 里查
                    if (left.Length > 0)
                    {
                        string name = left;
                        if (name.StartsWith("%") && name.EndsWith("%") && name.Length > 2)
                        {
                            string key = name.Substring(1, name.Length - 2);
                            string val;
                            if (strings.TryGetValue(key, out val)) name = val;
                            else name = "";   // 解析不出来就当没有，别把 %OEM% 当型号
                        }
                        name = name.Replace("\"", "").Trim();
                        if (name.Length >= 3 && name.IndexOf('\\') < 0) AddUnique(models, name);
                    }

                    // ② 右边：InstallSection,HardwareID  —— 硬件 ID 才是真正的型号来源
                    int comma = right.IndexOf(',');
                    if (comma >= 0)
                    {
                        string hwid = right.Substring(comma + 1).Trim();
                        while (true)
                        {
                            int next = hwid.IndexOf(',');
                            if (next < 0) break;
                            // 只取第一个硬件 ID（后面的是兼容 ID，噪声大）
                            hwid = hwid.Substring(0, next).Trim();
                            break;
                        }
                        string pretty = PrettyHardwareId(hwid);
                        if (pretty.Length >= 3) AddUnique(models, pretty);
                    }

                    if (models.Count >= 400) break;   // 万能驱动包能有几百上千条，够用了
                }
                if (models.Count >= 400) break;
            }

            return models;
        }

        /// <summary>
        /// 把硬件 ID 变成人看得懂、可比对的型号：
        ///   MF\OfficeJet_Pro_9730_Series&amp;WSD&amp;IP_PRINT   →  OfficeJet Pro 9730 Series
        ///   WSDPRINT\HPHP_Color_LaserJet_2A33                 →  HPHP Color LaserJet 2A33
        /// </summary>
        private static string PrettyHardwareId(string hwid)
        {
            if (hwid == null) return "";
            string s = hwid.Trim();
            if (s.Length == 0) return "";

            // 去掉总线前缀（MF\ / PRINT\ / WSDPRINT\ / USBPRINT\ ...）
            int slash = s.IndexOf('\\');
            if (slash >= 0) s = s.Substring(slash + 1);

            // 去掉 &WSD &IP_PRINT &REV_xxx 之类的修饰
            int amp = s.IndexOf('&');
            if (amp > 0) s = s.Substring(0, amp);

            // 下划线/连字符变空格；连续的动词收起
            s = s.Replace('_', ' ').Replace('-', ' ').Trim();
            while (s.IndexOf("  ") >= 0) s = s.Replace("  ", " ");
            return s;
        }

        private static void AddUnique(List<string> list, string v)
        {
            if (v == null) return;
            string t = v.Trim();
            if (t.Length == 0) return;
            for (int i = 0; i < list.Count; i++)
                if (string.Compare(list[i], t, StringComparison.OrdinalIgnoreCase) == 0) return;
            list.Add(t);
        }

        // ---------------- 匹配 ----------------

        private static readonly string[] Noise =
        {
            "series", "series)", "printer", "打印机", "打印", "driver", "驱动",
            "class", "universal", "通用", "print", "支持", "windows"
        };

        private static List<string> Tokens(string s)
        {
            List<string> t = new List<string>();
            if (s == null) return t;
            string u = s.ToUpperInvariant();
            for (int i = 0; i < Noise.Length; i++) u = u.Replace(Noise[i].ToUpperInvariant(), " ");
            string[] parts = Regex.Split(u, "[^A-Z0-9]+");
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length < 2) continue;
                if (Array.IndexOf(Noise, p) >= 0) continue;
                if (!HasToken(t, p)) t.Add(p);
            }
            return t;
        }

        private static bool HasToken(List<string> list, string p)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == p) return true;
            return false;
        }

        /// <summary>型号 → 驱动库里最像的一条。没有强匹配时返回 null。</summary>
        public static DriverEntry Match(string model, string hardwareIds, List<DriverEntry> drivers)
        {
            if (drivers == null || drivers.Count == 0) return null;

            List<string> mt = Tokens(model);
            if (mt.Count == 0) mt = Tokens(hardwareIds);
            if (mt.Count == 0) return null;

            DriverEntry best = null;
            int bestScore = 0;

            for (int i = 0; i < drivers.Count; i++)
            {
                int score = ScoreOne(mt, drivers[i]);
                if (score > bestScore) { bestScore = score; best = drivers[i]; }
            }

            return bestScore >= 40 ? best : null;
        }

        /// <summary>
        /// 给一条驱动打分。**关键是同时看两个证据源**：
        ///   ① 驱动名（Name）—— 老驱动常把型号写进名字，如 "HP OfficeJet Pro 9720 Series"；
        ///   ② INF 里声明的型号表（Models）—— 新的通用/一体驱动名字里根本不含型号
        ///      （实测本机 HP 只有 "HP Smart Universal Printing"，而它的 INF 里
        ///      明明列着 OfficeJet Pro 9730），只比名字必然匹配不到。
        /// </summary>
        private static int ScoreOne(List<string> mt, DriverEntry d)
        {
            int score = 0;

            // 证据①：驱动名
            List<string> dt = Tokens(d.Name);
            score += TokenScore(mt, dt);

            // 证据②：INF 型号表（命中即视为「厂商自己声明支持这台机」，最硬）
            if (d.Models != null)
            {
                for (int i = 0; i < d.Models.Count; i++)
                {
                    List<string> im = Tokens(d.Models[i]);
                    int s = TokenScore(mt, im);
                    // 型号表命中比驱动名更可信，加权 1.4 倍后取上限
                    int weighted = (int)(s * 1.4);
                    if (weighted > score) score = weighted;
                }
            }

            // 厂商一致再加一点
            string prov = d.Provider;
            if (prov.Length > 0)
            {
                string p = prov.ToUpperInvariant();
                for (int a = 0; a < mt.Count; a++)
                    if (mt[a].Length >= 2 && p.IndexOf(mt[a], StringComparison.Ordinal) >= 0) { score += 12; break; }
            }
            // 本地驱动包优先级略高（能直接装）
            if (d.InfPath != null && d.InfPath.Length > 0) score += 3;

            return score;
        }

        /// <summary>两组 token 的重合分：数字主干相等最硬，纯字母相等次之，包含关系再次。</summary>
        private static int TokenScore(List<string> mt, List<string> dt)
        {
            int score = 0;
            if (mt == null || dt == null) return 0;
            for (int a = 0; a < mt.Count; a++)
            {
                for (int b = 0; b < dt.Count; b++)
                {
                    if (mt[a] == dt[b]) { score += Regex.IsMatch(mt[a], "[0-9]") ? 45 : 20; break; }
                    if (mt[a].Length >= 4 && dt[b].Length >= 4 &&
                        (mt[a].IndexOf(dt[b], StringComparison.Ordinal) >= 0 ||
                         dt[b].IndexOf(mt[a], StringComparison.Ordinal) >= 0))
                    { score += 10; break; }
                }
            }
            return score;
        }

        /// <summary>没有精确驱动时的兜底：同厂商的通用/万能驱动。</summary>
        public static DriverEntry MatchUniversal(string model, List<DriverEntry> drivers)
        {
            if (drivers == null) return null;
            List<string> mt = Tokens(model);
            for (int i = 0; i < drivers.Count; i++)
            {
                string n = drivers[i].Name.ToUpperInvariant();
                bool generic = n.IndexOf("UNIVERSAL") >= 0 || n.IndexOf("通用") >= 0
                            || n.IndexOf("GENERIC") >= 0 || n.IndexOf("CLASS DRIVER") >= 0;
                if (!generic) continue;
                for (int a = 0; a < mt.Count; a++)
                {
                    if (mt[a].Length < 2) continue;
                    if (n.IndexOf(mt[a], StringComparison.Ordinal) >= 0) return drivers[i];
                }
            }
            return null;
        }

        // ---------------- 安装 ----------------

        /// <summary>pnputil /add-driver &lt;inf&gt; /install。</summary>
        public static bool InstallInf(string infPath, out string error)
        {
            error = "";
            if (infPath == null || infPath.Length == 0) { error = "没有 INF 路径。"; return false; }
            if (!File.Exists(infPath)) { error = "文件不存在：" + infPath; return false; }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("pnputil.exe",
                    "/add-driver \"" + infPath + "\" /install");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    string e = p.StandardError.ReadToEnd();
                    p.WaitForExit(180000);
                    if (p.ExitCode == 0 && o.IndexOf("0x800", StringComparison.OrdinalIgnoreCase) < 0) return true;
                    error = ("exit=" + p.ExitCode + " " + o + " " + e).Trim();
                    return false;
                }
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static string S(object o)
        {
            if (o == null) return "";
            try { return o.ToString().Trim(); } catch { return ""; }
        }
    }
}
