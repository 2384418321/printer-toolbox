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

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>驱动库的枚举、匹配与安装。全部只读本机，不联网下包。</summary>
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
                    if (e.InfPath != null && e.InfPath.Length > 0) e.Inf = Path.GetFileName(e.InfPath);
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

        private static List<string> InfModels(string path)
        {
            List<string> models = new List<string>();
            string[] lines = ReadLines(path);
            bool inModels = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                string t = raw.Trim();
                if (t.Length == 0) continue;
                if (t[0] == '[')
                {
                    inModels = t.StartsWith("[Manufacturer]", StringComparison.OrdinalIgnoreCase)
                            || t.StartsWith("[Models]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inModels) continue;
                if (t[0] == ';') continue;

                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                string left = t.Substring(0, eq).Trim();
                left = left.Replace("\"", "").Trim();
                if (left.Length < 3) continue;
                if (left.IndexOf('\\') >= 0) continue;
                if (Regex.IsMatch(left, "^[A-Za-z0-9_]+$") && left.IndexOf(' ') < 0) continue;

                bool dup = false;
                for (int j = 0; j < models.Count; j++)
                    if (string.Compare(models[j], left, StringComparison.OrdinalIgnoreCase) == 0) { dup = true; break; }
                if (!dup) models.Add(left);
                if (models.Count >= 60) break;   // 万能驱动包的 INF 能有几千行，够用了
            }
            return models;
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
                List<string> dt = Tokens(drivers[i].Name);
                if (dt.Count == 0) continue;

                int score = 0;
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
                // 厂商一致再加一点
                string prov = drivers[i].Provider;
                if (prov.Length > 0)
                {
                    string p = prov.ToUpperInvariant();
                    for (int a = 0; a < mt.Count; a++)
                        if (mt[a].Length >= 2 && p.IndexOf(mt[a], StringComparison.Ordinal) >= 0) { score += 12; break; }
                }
                // 本地驱动包优先级略高（能直接装）
                if (drivers[i].InfPath != null && drivers[i].InfPath.Length > 0) score += 3;

                if (score > bestScore) { bestScore = score; best = drivers[i]; }
            }

            return bestScore >= 40 ? best : null;
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
