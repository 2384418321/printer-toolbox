using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PrinterIpFix
{
    internal sealed class FixOp
    {
        public RegistryHive Hive;
        public string SubKey;
        public string Name;
        public int Value;

        public FixOp(RegistryHive hive, string subKey, string name, int value)
        {
            Hive = hive; SubKey = subKey; Name = name; Value = value;
        }
    }

    internal sealed class FixItem
    {
        public string Id;
        public string Title;
        public string Detail;
        /// <summary>0 = 无副作用，1 = 会降低安全性，2 = 需要重启/耗时长</summary>
        public int Risk;
        public List<FixOp> Ops = new List<FixOp>();
        /// <summary>附加命令（netsh / dism / net），按顺序用 cmd /c 执行，可为 null。</summary>
        public string[] Commands;

        public string RiskText()
        {
            if (Risk >= 2) return "需重启";
            if (Risk == 1) return "降安全";
            return "安全";
        }

        public override string ToString()
        {
            return "[" + RiskText() + "] " + Title;
        }
    }

    /// <summary>打印共享/打印服务修复项的定义与执行。所有写操作先备份原值。</summary>
    internal static class SharedFixes
    {
        public static string BackupDir()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PrinterToolbox\\backup");
            if (Writable(dir)) return dir;

            // 受限（低完整性）进程写不了 ProgramData，退到用户目录，别让备份凭空消失
            string alt = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PrinterToolbox\\backup");
            if (Writable(alt)) return alt;
            return dir;
        }

        private static bool Writable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".w");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        private const string PrintKey = @"SYSTEM\CurrentControlSet\Control\Print";
        private const string PnpKey = @"SOFTWARE\Policies\Microsoft\Windows NT\Printers\PointAndPrint";
        private const string RpcKey = @"SOFTWARE\Policies\Microsoft\Windows NT\Printers\RPC";
        private const string LmWkKey = @"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters";
        private const string LmWkPolicy = @"SOFTWARE\Policies\Microsoft\Windows\LanmanWorkstation";
        private const string DrvSearch = @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching";

        public static List<FixItem> Build()
        {
            List<FixItem> list = new List<FixItem>();

            FixItem a = new FixItem();
            a.Id = "11b";
            a.Title = "修复共享连接报 0x0000011b";
            a.Detail = "关闭打印服务的 RPC 隐私认证级别。2021-09 补丁默认开启它之后，"
                     + "未打补丁的客户端连共享打印机就会报 0x0000011b。\n"
                     + "改的是 HKLM\\SYSTEM\\CurrentControlSet\\Control\\Print : RpcAuthnLevelPrivacyEnabled = 0\n"
                     + "副作用：等于放宽回补丁前的认证要求，只在客户端无法升级时用。";
            a.Risk = 1;
            a.Ops.Add(new FixOp(RegistryHive.LocalMachine, PrintKey, "RpcAuthnLevelPrivacyEnabled", 0));
            list.Add(a);

            FixItem b = new FixItem();
            b.Id = "pnp-driver";
            b.Title = "允许非管理员安装共享打印机驱动（0x0000007c）";
            b.Detail = "PointAndPrint 默认只允许管理员装驱动，普通用户连共享打印机时会报 0x0000007c。\n"
                     + "PointAndPrint : RestrictDriverInstallationToAdministrators = 0\n"
                     + "副作用：这是 PrintNightmare 的缓解项之一，关掉后任何能连到共享的人都能装驱动。";
            b.Risk = 1;
            b.Ops.Add(new FixOp(RegistryHive.LocalMachine, PnpKey, "RestrictDriverInstallationToAdministrators", 0));
            list.Add(b);

            FixItem c = new FixItem();
            c.Id = "pnp-prompt";
            c.Title = "驱动安装时不再弹提权/警告框";
            c.Detail = "连共享打印机时 Windows 会弹「是否信任此打印机」并要求管理员确认，"
                     + "某些环境下确认了也装不上。\n"
                     + "PointAndPrint : NoWarningNoElevationOnInstall = 0、UpdatePromptSettings = 0\n"
                     + "副作用：降低提示门槛。";
            c.Risk = 1;
            c.Ops.Add(new FixOp(RegistryHive.LocalMachine, PnpKey, "NoWarningNoElevationOnInstall", 0));
            c.Ops.Add(new FixOp(RegistryHive.LocalMachine, PnpKey, "UpdatePromptSettings", 0));
            list.Add(c);

            FixItem d = new FixItem();
            d.Id = "guest";
            d.Title = "允许来宾方式访问共享（0x80004005 / 拒绝访问）";
            d.Detail = "对方的共享没设账号密码，Windows 10/11 默认拒绝这种「不安全的来宾登录」。\n"
                     + "LanmanWorkstation\\Parameters : AllowInsecureGuestAuth = 1（本机 + 策略两处）\n"
                     + "副作用：允许无凭据 SMB 访问，公司网内更推荐给共享设账号后用「连接共享打印机」并保存凭据。";
            d.Risk = 1;
            d.Ops.Add(new FixOp(RegistryHive.LocalMachine, LmWkKey, "AllowInsecureGuestAuth", 1));
            d.Ops.Add(new FixOp(RegistryHive.LocalMachine, LmWkPolicy, "AllowInsecureGuestAuth", 1));
            list.Add(d);

            FixItem e = new FixItem();
            e.Id = "rpc-pipe";
            e.Title = "打印 RPC 改走命名管道（0x000006ba 系列）";
            e.Detail = "把打印子系统之间的 RPC 通信强制走命名管道，并允许三种协议同时侦听。\n"
                     + "Printers\\RPC : RpcUseNamedPipeProtocol = 1、RpcProtocols = 7\n"
                     + "不含 ForceKerberosForRpc —— 工作组环境打开它反而连不上。";
            e.Risk = 0;
            e.Ops.Add(new FixOp(RegistryHive.LocalMachine, RpcKey, "RpcUseNamedPipeProtocol", 1));
            e.Ops.Add(new FixOp(RegistryHive.LocalMachine, RpcKey, "RpcProtocols", 7));
            list.Add(e);

            FixItem f = new FixItem();
            f.Id = "fw";
            f.Title = "放行共享打印端口（445/139/515/721-731）";
            f.Detail = "启用防火墙规则组「文件和打印机共享」，并补三条入站规则：\n"
                     + "SMB 445、NetBIOS 139、LPR 515、LPD 721-731（TCP）。\n"
                     + "副作用：本机对局域网多开了这几个端口，仅当你确实是共享主机时才需要。";
            f.Risk = 1;
            f.Commands = new string[]
            {
                "netsh advfirewall firewall set rule group=\"文件和打印机共享\" new enable=yes",
                "netsh advfirewall firewall add rule name=\"PrinterToolbox SMB\" dir=in action=allow protocol=TCP localport=139,445",
                "netsh advfirewall firewall add rule name=\"PrinterToolbox LPR\" dir=in action=allow protocol=TCP localport=515",
                "netsh advfirewall firewall add rule name=\"PrinterToolbox LPD\" dir=in action=allow protocol=TCP localport=721-731"
            };
            list.Add(f);

            FixItem g = new FixItem();
            g.Id = "lpr";
            g.Title = "安装 LPR/LPD 打印组件（老设备 LPR 直连用）";
            g.Detail = "启用 Windows 可选功能：LPD 打印服务、LPR 端口监视器、Internet 打印客户端。\n"
                     + "走 DISM，可能要几分钟，装完需要重启。老式打印机/共享盒子用 LPR 直连时必须要它。";
            g.Risk = 2;
            g.Commands = new string[]
            {
                "dism /online /enable-feature /featurename:Printing-Foundation-LPDPrintService /all /norestart",
                "dism /online /enable-feature /featurename:Printing-Foundation-LPRPortMonitor /all /norestart",
                "dism /online /enable-feature /featurename:Printing-Foundation-InternetPrinting-Client /all /norestart"
            };
            list.Add(g);

            FixItem h = new FixItem();
            h.Id = "spooler";
            h.Title = "重置打印后台：停服务 → 清空卡死作业 → 起服务";
            h.Detail = "停掉 Spooler、清空 C:\\Windows\\System32\\spool\\PRINTERS 里卡住的作业文件、再启动。\n"
                     + "这是「作业一直排队不出纸」「状态 Deleting 删不掉」的标准解法。不改任何注册表。";
            h.Risk = 0;
            h.Commands = new string[]
            {
                "net stop spooler /y",
                "del /f /q \"%SystemRoot%\\System32\\spool\\PRINTERS\\*.*\"",
                "net start spooler"
            };
            list.Add(h);

            FixItem i = new FixItem();
            i.Id = "nodrvsearch";
            i.Title = "装驱动时不联网搜 Windows Update";
            i.Detail = "驱动安装向导默认会去 Windows Update 找驱动，网络差时会卡很久，"
                     + "也常装上一个什么都不支持的通用驱动。\n"
                     + "DriverSearching : DriverUpdateWizardWuSearchEnabled = 0、SearchOrderConfig = 0\n"
                     + "没有副作用，只是让安装过程可控。";
            i.Risk = 0;
            i.Ops.Add(new FixOp(RegistryHive.LocalMachine, DrvSearch, "DriverUpdateWizardWuSearchEnabled", 0));
            i.Ops.Add(new FixOp(RegistryHive.LocalMachine, DrvSearch, "SearchOrderConfig", 0));
            list.Add(i);

            return list;
        }

        public static FixItem Find(List<FixItem> all, string id)
        {
            for (int i = 0; i < all.Count; i++) if (all[i].Id == id) return all[i];
            return null;
        }

        // ---------------- 注册表读写（统一 64 位视图） ----------------

        private static RegistryKey BaseKey(RegistryHive hive)
        {
            return RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        }

        public static object ReadValue(FixOp op)
        {
            try
            {
                using (RegistryKey b = BaseKey(op.Hive))
                using (RegistryKey k = b.OpenSubKey(op.SubKey, false))
                {
                    if (k == null) return null;
                    return k.GetValue(op.Name, null);
                }
            }
            catch { return null; }
        }

        private static bool WriteValue(FixOp op, int value, out string error)
        {
            error = "";
            try
            {
                using (RegistryKey b = BaseKey(op.Hive))
                using (RegistryKey k = b.CreateSubKey(op.SubKey))
                {
                    if (k == null) { error = "打不开键 " + op.SubKey; return false; }
                    k.SetValue(op.Name, value, RegistryValueKind.DWord);
                    return true;
                }
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static bool DeleteValue(FixOp op, out string error)
        {
            error = "";
            try
            {
                using (RegistryKey b = BaseKey(op.Hive))
                using (RegistryKey k = b.OpenSubKey(op.SubKey, true))
                {
                    if (k == null) return true;
                    k.DeleteValue(op.Name, false);
                    return true;
                }
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static string ValueText(object v)
        {
            if (v == null) return "(不存在)";
            return v.ToString() + " (" + v.GetType().Name + ")";
        }

        // ---------------- 诊断 / 应用 / 还原 ----------------

        public static List<string> Diagnose()
        {
            List<string> log = new List<string>();
            List<FixItem> all = Build();
            log.Add("=== 每项修复的当前状态 ===");
            for (int i = 0; i < all.Count; i++)
            {
                FixItem it = all[i];
                if (it.Ops.Count == 0)
                {
                    log.Add("· " + it.Title + "：无注册表项（命令型，需手动执行）");
                    continue;
                }
                bool done = true;
                StringBuilder sb = new StringBuilder();
                for (int j = 0; j < it.Ops.Count; j++)
                {
                    object cur = ReadValue(it.Ops[j]);
                    bool ok = (cur != null && cur is int && (int)cur == it.Ops[j].Value);
                    if (!ok) done = false;
                    sb.Append("\r\n    " + it.Ops[j].Name + " = " + ValueText(cur)
                             + "  → 期望 " + it.Ops[j].Value + (ok ? "  [已达标]" : "  [未达标]"));
                }
                log.Add((done ? "√ " : "× ") + it.Title + sb.ToString());
            }
            log.Add("");
            log.Add("管理员权限：" + (PrinterSystem.IsAdmin() ? "有" : "没有（改注册表/防火墙会失败）"));
            log.Add("进程完整性：" + PrinterSystem.IntegrityText());
            return log;
        }

        /// <summary>备份 + 应用。返回逐条日志。</summary>
        public static List<string> Apply(IList<string> ids)
        {
            List<string> log = new List<string>();
            List<FixItem> all = Build();
            List<FixItem> picked = new List<FixItem>();
            for (int i = 0; i < ids.Count; i++)
            {
                FixItem it = Find(all, ids[i]);
                if (it != null) picked.Add(it);
            }
            if (picked.Count == 0) { log.Add("没有选中任何修复项。"); return log; }

            bool admin = PrinterSystem.IsAdmin();
            log.Add("管理员权限：" + (admin ? "有" : "没有 —— 注册表/防火墙/DISM 都会失败"));
            log.Add("");

            // 1) 备份
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string bak = Path.Combine(BackupDir(), "shared-" + stamp + ".txt");
            StringBuilder sbBackup = new StringBuilder();
            sbBackup.AppendLine("# PrinterToolbox 共享修复备份 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sbBackup.AppendLine("# 格式：HIVE|子键|值名|REG_DWORD|原值   原值写 (none) 表示原先不存在");
            for (int i = 0; i < picked.Count; i++)
            {
                for (int j = 0; j < picked[i].Ops.Count; j++)
                {
                    FixOp op = picked[i].Ops[j];
                    object cur = ReadValue(op);
                    string hive = op.Hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
                    sbBackup.AppendLine(hive + "|" + op.SubKey + "|" + op.Name + "|REG_DWORD|"
                        + (cur == null ? "(none)" : cur.ToString()));
                }
            }
            try
            {
                File.WriteAllText(bak, sbBackup.ToString(), new UTF8Encoding(true));
                log.Add("已备份原值 → " + bak);
            }
            catch (Exception ex) { log.Add("备份写入失败：" + ex.Message); }

            // 2) 写注册表
            for (int i = 0; i < picked.Count; i++)
            {
                FixItem it = picked[i];
                if (it.Ops.Count == 0) continue;
                log.Add("· " + it.Title);
                for (int j = 0; j < it.Ops.Count; j++)
                {
                    string err;
                    if (WriteValue(it.Ops[j], it.Ops[j].Value, out err))
                        log.Add("    " + it.Ops[j].Name + " = " + it.Ops[j].Value + "  OK");
                    else
                        log.Add("    " + it.Ops[j].Name + " 失败：" + err);
                }
            }

            // 3) 跑命令
            for (int i = 0; i < picked.Count; i++)
            {
                FixItem it = picked[i];
                if (it.Commands == null) continue;
                log.Add("· " + it.Title + "（执行命令）");
                for (int j = 0; j < it.Commands.Length; j++)
                {
                    string outp;
                    int code = PrinterSystem.RunHidden("cmd.exe", "/c " + it.Commands[j], 300000, out outp);
                    string first = FirstLine(outp);
                    log.Add("    [" + code + "] " + it.Commands[j]);
                    if (first.Length > 0) log.Add("         " + first);
                }
            }

            log.Add("");
            log.Add("完成。标「需重启」的项重启后生效。备份目录：" + BackupDir());
            return log;
        }

        public static List<string> RevertLatest()
        {
            List<string> log = new List<string>();
            string dir = BackupDir();
            string[] files = Directory.GetFiles(dir, "shared-*.txt");
            if (files.Length == 0) { log.Add("没有找到备份文件。"); return log; }
            Array.Sort(files);
            string latest = files[files.Length - 1];
            log.Add("从备份还原：" + latest);

            string[] lines = File.ReadAllLines(latest);
            List<FixItem> all = Build();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0 || line[0] == '#') continue;
                string[] p = line.Split('|');
                if (p.Length < 5) continue;

                FixOp op = new FixOp(
                    p[0].IndexOf("CURRENT_USER", StringComparison.OrdinalIgnoreCase) >= 0
                        ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
                    p[1], p[2], 0);

                string err;
                if (p[4] == "(none)")
                {
                    if (DeleteValue(op, out err)) log.Add("  删除 " + p[2] + "（原本不存在）");
                    else log.Add("  删除 " + p[2] + " 失败：" + err);
                }
                else
                {
                    int v;
                    if (int.TryParse(p[4], out v))
                    {
                        if (WriteValue(op, v, out err)) log.Add("  " + p[2] + " = " + v + "（原值）");
                        else log.Add("  " + p[2] + " 还原失败：" + err);
                    }
                }
            }
            log.Add("还原完成。防火墙规则组/命令型项（LPR 组件等）不会自动撤销。");
            return log;
        }

        private static string FirstLine(string s)
        {
            if (s == null) return "";
            string[] lines = s.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.Length > 0) return t.Length > 160 ? t.Substring(0, 160) : t;
            }
            return "";
        }

        /// <summary>提权重开自己时用的无界面入口。</summary>
        public static int RunHeadless(string[] args)
        {
            bool revert = false;
            bool quiet = false;
            List<string> ids = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--revert-shared") revert = true;
                if (args[i] == "--quiet") quiet = true;
                if (args[i] == "--apply-shared" && i + 1 < args.Length)
                {
                    string[] parts = args[i + 1].Split(',');
                    for (int j = 0; j < parts.Length; j++)
                        if (parts[j].Trim().Length > 0) ids.Add(parts[j].Trim());
                }
            }

            List<string> log;
            if (revert) log = RevertLatest();
            else if (ids.Count > 0) log = Apply(ids);
            else log = Diagnose();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < log.Count; i++) sb.AppendLine(log[i]);
            if (quiet)
            {
                try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
                try { Console.WriteLine(sb.ToString()); } catch { }
            }
            try
            {
                string outFile = Path.Combine(BackupDir(), "apply-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(true));
                sb.AppendLine();
                sb.AppendLine("日志已保存：" + outFile);
            }
            catch { }
            if (!quiet)
                MessageBox.Show(sb.ToString(), "共享修复（管理员）", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
    }

    /// <summary>「共享修复」页签。</summary>
    internal sealed class SharedFixPanel : UserControl
    {
        private CheckedListBox list;
        private TextBox detail;
        private TextBox log;
        private Label status;
        private List<FixItem> items;

        public SharedFixPanel()
        {
            Dock = DockStyle.Fill;
            BuildUi();
            LoadItems();
        }

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190F));
            root.Padding = new Padding(8, 8, 8, 8);

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Vertical;
            split.SplitterWidth = 6;

            list = new CheckedListBox();
            list.Dock = DockStyle.Fill;
            list.CheckOnClick = true;
            list.IntegralHeight = false;
            list.Font = new Font("Microsoft YaHei UI", 9F);
            list.SelectedIndexChanged += delegate { ShowDetail(); };

            detail = new TextBox();
            detail.Dock = DockStyle.Fill;
            detail.Multiline = true;
            detail.ReadOnly = true;
            detail.ScrollBars = ScrollBars.Vertical;
            detail.BackColor = Color.White;
            detail.Font = new Font("Microsoft YaHei UI", 9F);
            detail.Text = "左边勾选要修的项，右边看它到底改什么。\r\n\r\n"
                        + "· 标「降安全」的项会把 Windows 的安全缓解措施关掉，只在客户端确实改不了时用。\r\n"
                        + "· 标「需重启」的项走 DISM 装系统组件，慢且必须重启。\r\n"
                        + "· 所有注册表改动都会先备份原值，随时能用「还原上一次」退回去。";

            split.Panel1.Controls.Add(list);
            split.Panel2.Controls.Add(detail);
            root.Controls.Add(split, 0, 0);

            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.FlowDirection = FlowDirection.LeftToRight;
            bar.WrapContents = false;
            bar.Padding = new Padding(0, 4, 0, 0);

            Button bDia = MakeButton("① 体检当前状态", 150);
            bDia.Click += delegate { RunDiagnose(); };

            Button bAll = MakeButton("全选", 70);
            bAll.Click += delegate { SetAll(true); };

            Button bNone = MakeButton("全不选", 80);
            bNone.Click += delegate { SetAll(false); };

            Button bApply = MakeButton("② 应用勾选项", 150);
            bApply.Click += delegate { RunApply(); };

            Button bRevert = MakeButton("还原上一次", 110);
            bRevert.Click += delegate { RunRevert(); };

            Button bDir = MakeButton("备份目录", 90);
            bDir.Click += delegate
            {
                try { Process.Start(new ProcessStartInfo(SharedFixes.BackupDir()) { UseShellExecute = true }); }
                catch { }
            };

            status = new Label();
            status.AutoSize = false;
            status.Width = 300;
            status.Height = 26;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.ForeColor = Color.FromArgb(90, 90, 90);

            bar.Controls.Add(bDia);
            bar.Controls.Add(bAll);
            bar.Controls.Add(bNone);
            bar.Controls.Add(bApply);
            bar.Controls.Add(bRevert);
            bar.Controls.Add(bDir);
            bar.Controls.Add(status);
            root.Controls.Add(bar, 0, 1);

            log = new TextBox();
            log.Dock = DockStyle.Fill;
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Both;
            log.WordWrap = false;
            log.BackColor = Color.FromArgb(250, 250, 250);
            log.Font = new Font("Consolas", 9F);
            root.Controls.Add(log, 0, 2);

            Controls.Add(root);
            UpdateStatus();
        }

        private Button MakeButton(string text, int width)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = width;
            b.Height = 28;
            b.Margin = new Padding(0, 0, 8, 0);
            b.UseVisualStyleBackColor = true;
            return b;
        }

        private void LoadItems()
        {
            items = SharedFixes.Build();
            list.Items.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                list.Items.Add(items[i], items[i].Risk == 0);
            }
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }

        private void UpdateStatus()
        {
            bool admin = PrinterSystem.IsAdmin();
            status.Text = (admin ? "管理员：有" : "管理员：没有") +
                          "　完整性：" + PrinterSystem.IntegrityText();
            status.ForeColor = admin ? Color.FromArgb(0, 120, 60) : Color.FromArgb(180, 90, 0);
        }

        private void ShowDetail()
        {
            int i = list.SelectedIndex;
            if (i < 0 || i >= items.Count) return;
            FixItem it = items[i];
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(it.Title);
            sb.AppendLine();
            sb.AppendLine("副作用：" + (it.Risk == 0
                ? "无（可以随时用「还原上一次」撤回）"
                : (it.Risk == 1 ? "会降低 Windows 的安全性缓解措施" : "需要重启，DISM 可能要几分钟")));
            sb.AppendLine();
            sb.AppendLine(it.Detail);
            if (it.Ops.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("准备写入：");
                for (int j = 0; j < it.Ops.Count; j++)
                {
                    FixOp op = it.Ops[j];
                    sb.AppendLine("  " + op.SubKey + "\\" + op.Name + " = " + op.Value
                        + "　　当前 " + ValueOf(op));
                }
            }
            if (it.Commands != null)
            {
                sb.AppendLine();
                sb.AppendLine("准备执行：");
                for (int j = 0; j < it.Commands.Length; j++) sb.AppendLine("  " + it.Commands[j]);
            }
            detail.Text = sb.ToString();
        }

        private static string ValueOf(FixOp op)
        {
            object v = SharedFixes.ReadValue(op);
            if (v == null) return "(不存在)";
            return v.ToString();
        }

        private void SetAll(bool on)
        {
            for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, on);
        }

        private void Append(string[] lines)
        {
            StringBuilder sb = new StringBuilder(log.Text);
            for (int i = 0; i < lines.Length; i++) sb.AppendLine(lines[i]);
            log.Text = sb.ToString();
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
        }

        private void RunDiagnose()
        {
            log.Text = "";
            Append(SharedFixes.Diagnose().ToArray());
            UpdateStatus();
        }

        private void RunApply()
        {
            List<string> ids = new List<string>();
            StringBuilder names = new StringBuilder();
            for (int i = 0; i < list.Items.Count; i++)
            {
                if (!list.GetItemChecked(i)) continue;
                ids.Add(items[i].Id);
                names.AppendLine("  · " + items[i].Title);
            }
            if (ids.Count == 0)
            {
                MessageBox.Show("一项都没勾。", "共享修复", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int risky = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                FixItem it = SharedFixes.Find(items, ids[i]);
                if (it != null && it.Risk >= 1) risky++;
            }

            string msg = "将执行 " + ids.Count + " 项：\r\n" + names
                + (risky > 0 ? "\r\n其中 " + risky + " 项会降低 Windows 的安全性缓解措施。" : "")
                + "\r\n所有注册表改动会先备份原值。继续？";
            if (MessageBox.Show(msg, "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            if (!PrinterSystem.IsAdmin())
            {
                if (MessageBox.Show(
                    "改注册表、防火墙、DISM 都需要管理员权限。\r\n是否以管理员身份重新打开本工具来执行？",
                    "需要管理员", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;

                StringBuilder arg = new StringBuilder();
                for (int i = 0; i < ids.Count; i++)
                {
                    if (i > 0) arg.Append(",");
                    arg.Append(ids[i]);
                }
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--apply-shared " + arg);
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    Process.Start(psi);
                    Append(new string[] { "已在管理员窗口里执行，结果在那个窗口和备份目录的 apply-*.log 里。" });
                }
                catch (Exception ex)
                {
                    Append(new string[] { "提权启动失败：" + ex.Message });
                }
                return;
            }

            log.Text = "";
            Append(SharedFixes.Apply(ids).ToArray());
            UpdateStatus();
        }

        private void RunRevert()
        {
            if (!PrinterSystem.IsAdmin())
            {
                if (MessageBox.Show("还原要写注册表，需要管理员权限。以管理员身份重开本工具来还原？",
                        "需要管理员", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                LaunchElevated("--revert-shared");
                return;
            }
            log.Text = "";
            Append(SharedFixes.RevertLatest().ToArray());
        }

        private void LaunchElevated(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, args);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                Append(new string[] { "已在管理员窗口里执行，结果在那个窗口和备份目录的日志里。" });
            }
            catch (Exception ex)
            {
                Append(new string[] { "提权启动失败：" + ex.Message });
            }
        }
    }
}
