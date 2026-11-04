using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PrinterIpFix
{
    /// <summary>设备行：本机 PnP 设备 + 本机打印队列，统一成一行。</summary>
    internal sealed class DeviceRow
    {
        public string State;        // 已装 / 待装驱动 / 未连接
        public string Name;
        public string Port;
        public string Model;
        public string BestDriver;   // 匹配到的驱动库条目
        public string Verdict;      // 人话结论
        public string Url;          // 驱动站跳转地址
        public string InstanceId;
        public string HardwareIds;  // 分号分隔
    }

    /// <summary>「驱动检测」页签：枚举设备、扫驱动库、自动匹配、装驱动、找官网。</summary>
    internal sealed class DriverPanel : UserControl
    {
        private ListView lsvDevices;
        private ListView lsvDrivers;
        private TextBox log;
        private Label lblInfo;
        private TextBox txtVerdict;
        private List<DeviceRow> rows = new List<DeviceRow>();
        private List<DriverEntry> drivers = new List<DriverEntry>();
        private int scanSeq;

        public DriverPanel()
        {
            Dock = DockStyle.Fill;
            BuildUi();
        }

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150F));
            root.Padding = new Padding(8, 8, 8, 8);

            // ---- 工具栏 ----
            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.WrapContents = false;
            bar.FlowDirection = FlowDirection.LeftToRight;

            // 按钮宽度都收到 100 及以下，才能给新增的「驱动修复(联网)」留出位置。
            // 这一行原来是 6 个 110 宽按钮 + 420 宽标签，加任何一个都会溢出父容器。
            Button bDev = MakeButton("① 扫描设备", 100);
            bDev.Click += delegate { ScanDevices(); };

            Button bDrv = MakeButton("② 读驱动库", 100);
            bDrv.Click += delegate { ScanDrivers(); };

            Button bMatch = MakeButton("③ 自动匹配", 100);
            bMatch.Click += delegate { Rematch(); };

            Button bInstall = MakeButton("装建议驱动", 100);
            bInstall.Click += delegate { InstallSelected(); };

            Button bWeb = MakeButton("打开驱动入口", 100);
            bWeb.Click += delegate { OpenSelectedSite(); };

            Button bRepair = MakeButton("驱动修复(联网)", 116);
            bRepair.Click += delegate { OpenDriverRepair(); };

            Button bStore = MakeButton("驱动库目录", 96);
            bStore.Click += delegate { OpenStore(); };

            lblInfo = new Label();
            lblInfo.AutoSize = false;
            lblInfo.Width = 300;
            lblInfo.Height = 26;
            lblInfo.TextAlign = ContentAlignment.MiddleLeft;
            lblInfo.ForeColor = Color.FromArgb(90, 90, 90);
            lblInfo.Text = "先 ① 再 ②，然后 ③ 看推荐。库里没有就去「驱动修复」联网搜官方包。";

            bar.Controls.Add(bDev);
            bar.Controls.Add(bDrv);
            bar.Controls.Add(bMatch);
            bar.Controls.Add(bInstall);
            bar.Controls.Add(bWeb);
            bar.Controls.Add(bRepair);
            bar.Controls.Add(bStore);
            bar.Controls.Add(lblInfo);
            root.Controls.Add(bar, 0, 0);

            // ---- 上下分：设备表 / 驱动库 ----
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.SplitterWidth = 6;

            lsvDevices = new ListView();
            lsvDevices.Dock = DockStyle.Fill;
            lsvDevices.View = View.Details;
            lsvDevices.FullRowSelect = true;
            lsvDevices.GridLines = true;
            lsvDevices.HideSelection = false;
            lsvDevices.Font = new Font("Microsoft YaHei UI", 9F);
            lsvDevices.Columns.Add("状态", 84);
            lsvDevices.Columns.Add("设备", 210);
            lsvDevices.Columns.Add("端口", 100);
            lsvDevices.Columns.Add("型号", 220);
            lsvDevices.Columns.Add("建议驱动", 240);
            lsvDevices.Columns.Add("结论", 300);
            lsvDevices.SelectedIndexChanged += delegate { ShowVerdict(); };

            Panel right = new Panel();
            right.Dock = DockStyle.Fill;

            lsvDrivers = new ListView();
            lsvDrivers.Dock = DockStyle.Fill;
            lsvDrivers.View = View.Details;
            lsvDrivers.FullRowSelect = true;
            lsvDrivers.GridLines = true;
            lsvDrivers.Font = new Font("Microsoft YaHei UI", 9F);
            lsvDrivers.Columns.Add("驱动名", 300);
            lsvDrivers.Columns.Add("厂商", 110);
            lsvDrivers.Columns.Add("版本", 80);
            lsvDrivers.Columns.Add("INF", 260);

            Label lblTop = new Label();
            lblTop.Dock = DockStyle.Top;
            lblTop.Height = 22;
            lblTop.Text = "本机驱动库（Get-PrinterDriver + DriverStore\\FileRepository + 已安装 INF）";
            lblTop.ForeColor = Color.FromArgb(70, 70, 70);

            txtVerdict = new TextBox();
            txtVerdict.Dock = DockStyle.Bottom;
            txtVerdict.Height = 58;
            txtVerdict.Multiline = true;
            txtVerdict.ReadOnly = true;
            txtVerdict.ScrollBars = ScrollBars.Vertical;
            txtVerdict.BackColor = Color.White;
            txtVerdict.Font = new Font("Microsoft YaHei UI", 9F);

            right.Controls.Add(lsvDrivers);
            right.Controls.Add(lblTop);
            right.Controls.Add(txtVerdict);

            split.Panel1.Controls.Add(lsvDevices);
            split.Panel2.Controls.Add(right);
            root.Controls.Add(split, 0, 1);

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

        private void Log(string s)
        {
            StringBuilder sb = new StringBuilder(log.Text);
            sb.AppendLine(s);
            log.Text = sb.ToString();
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
        }

        private void LogLines(List<string> lines)
        {
            StringBuilder sb = new StringBuilder(log.Text);
            for (int i = 0; i < lines.Count; i++) sb.AppendLine(lines[i]);
            log.Text = sb.ToString();
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
        }

        // ---------------- 扫描 ----------------

        public void ScanAll()
        {
            ScanDevices();
            ScanDrivers();
        }

        public void ScanDevices()
        {
            lsvDevices.Items.Clear();
            rows.Clear();
            lblInfo.Text = "正在扫描设备…";

            ThreadPool.QueueUserWorkItem(delegate
            {
                List<DeviceRow> found = new List<DeviceRow>();
                try { found = CollectDevices(); }
                catch (Exception ex) { found.Add(Warn("扫描设备出错：" + ex.Message)); }

                BeginInvoke((MethodInvoker)delegate
                {
                    rows = found;
                    FillDeviceList();
                    lblInfo.Text = "设备 " + rows.Count + " 条；驱动库 " + drivers.Count + " 条。";
                    if (drivers.Count == 0) Log("驱动库还没读，点 ② 读驱动库。");
                });
            });
        }

        public void ScanDrivers()
        {
            lsvDrivers.Items.Clear();
            drivers.Clear();
            lblInfo.Text = "正在读取驱动库…";
            int seq = ++scanSeq;

            ThreadPool.QueueUserWorkItem(delegate
            {
                List<DriverEntry> list = new List<DriverEntry>();
                List<string> note = new List<string>();
                try { list = DriverStore.Scan(note); }
                catch (Exception ex) { note.Add("读取驱动库出错：" + ex.Message); }

                BeginInvoke((MethodInvoker)delegate
                {
                    if (seq != scanSeq) return;
                    drivers = list;
                    for (int i = 0; i < drivers.Count; i++)
                    {
                        ListViewItem it = new ListViewItem(drivers[i].Name);
                        it.SubItems.Add(drivers[i].Provider ?? "");
                        it.SubItems.Add(drivers[i].Version ?? "");
                        it.SubItems.Add(drivers[i].Inf ?? "");
                        it.Tag = drivers[i];
                        lsvDrivers.Items.Add(it);
                    }
                    LogLines(note);
                    Log("驱动库：" + drivers.Count + " 条（Get-PrinterDriver / DriverStore / 已装 INF）。");
                    lblInfo.Text = "设备 " + rows.Count + " 条；驱动库 " + drivers.Count + " 条。";
                    AutoMatchIfReady();
                });
            });
        }

        private void AutoMatchIfReady()
        {
            if (rows.Count > 0 && drivers.Count > 0) Rematch();
        }

        private DeviceRow Warn(string msg)
        {
            DeviceRow r = new DeviceRow();
            r.State = "提示";
            r.Name = msg;
            return r;
        }

        /// <summary>枚举本机 PnP 打印设备 + 本机打印队列，合并成设备行。</summary>
        private List<DeviceRow> CollectDevices()
        {
            List<DeviceRow> list = new List<DeviceRow>();
            List<string> notes = new List<string>();

            // 1) 本机打印队列（已装、能用）
            List<PrinterInfo> queues = PrinterSystem.GetPrinters();
            for (int i = 0; i < queues.Count; i++)
            {
                PrinterInfo q = queues[i];
                DeviceRow r = new DeviceRow();
                r.State = q.WorkOffline ? "离线" : "已装队列";
                r.Name = q.Name;
                r.Port = q.PortName;
                r.Model = q.DriverName;
                r.BestDriver = "(队列已存在)";
                r.Verdict = "本机已有队列，驱动：" + q.DriverName;
                list.Add(r);
            }

            // 2) PnP 设备（含没装驱动/只插了 USB 的）
            try
            {
                ManagementObjectSearcher es = new ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID, Manufacturer, Status FROM Win32_PnPEntity "
                  + "WHERE PNPClass='Printer' OR PNPDeviceID LIKE 'USBPRINT%'");
                foreach (ManagementObject mo in es.Get())
                {
                    string name = Str(mo["Name"]);
                    string id = Str(mo["PNPDeviceID"]);
                    string manu = Str(mo["Manufacturer"]);
                    if (name.Length == 0) continue;

                    bool dup = false;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].Name == name) { dup = true; list[i].InstanceId = id; break; }
                    }
                    if (dup) continue;

                    DeviceRow r = new DeviceRow();
                    r.Name = name;
                    r.InstanceId = id;
                    r.Port = PortFromInstanceId(id);
                    r.State = "待装驱动";
                    r.Model = name;
                    r.Verdict = "设备在，" + (r.Port.Length > 0 ? "端口 " + r.Port : "端口未知")
                              + "，但没有可用队列。先看建议驱动再装。";
                    if (manu.Length > 0) r.Verdict += " 厂商：" + manu;
                    r.HardwareIds = id;
                    list.Add(r);
                }
            }
            catch (Exception ex) { notes.Add("WMI 枚举 PnP 设备失败：" + ex.Message); }

            if (notes.Count > 0) LogLines(notes);
            return list;
        }

        private static string Str(object o)
        {
            if (o == null) return "";
            return o.ToString().Trim();
        }

        /// <summary>USBPRINT\...&0&USB001 → USB001</summary>
        private static string PortFromInstanceId(string id)
        {
            if (id == null || id.Length == 0) return "";
            int i = id.LastIndexOf('&');
            if (i < 0 || i + 1 >= id.Length) return "";
            string p = id.Substring(i + 1).Trim();
            if (p.Length == 0) return "";
            return p.ToUpperInvariant();
        }

        private void FillDeviceList()
        {
            lsvDevices.Items.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                ListViewItem it = new ListViewItem(rows[i].State);
                it.SubItems.Add(rows[i].Name ?? "");
                it.SubItems.Add(rows[i].Port ?? "");
                it.SubItems.Add(rows[i].Model ?? "");
                it.SubItems.Add(rows[i].BestDriver ?? "");
                it.SubItems.Add(rows[i].Verdict ?? "");
                if (rows[i].State == "待装驱动") it.ForeColor = Color.FromArgb(170, 80, 0);
                if (rows[i].State == "离线") it.ForeColor = Color.FromArgb(150, 150, 150);
                it.Tag = rows[i];
                lsvDevices.Items.Add(it);
            }
        }

        private void Rematch()
        {
            if (rows.Count == 0) { Log("先扫描设备。"); return; }
            if (drivers.Count == 0) { Log("驱动库是空的，先点 ② 读驱动库。"); return; }

            int hit = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].State == "已装队列") continue;

                string model = rows[i].Model;
                if (model == null || model.Length == 0) model = rows[i].Name;

                DriverEntry best = DriverStore.Match(model, rows[i].HardwareIds, drivers);
                if (best != null)
                {
                    rows[i].BestDriver = best.Name + (best.Version != null && best.Version.Length > 0 ? "  " + best.Version : "");
                    rows[i].Verdict = "型号「" + model + "」与驱动库条目「" + best.Name + "」匹配（" + best.Source + "）。"
                                    + (best.InfPath != null && best.InfPath.Length > 0
                                        ? " 这条有本地 INF，可以直接装。"
                                        : " 这条只能看不能直接装（没有本地 INF），要走官网下包。");
                    hit++;
                }
                else
                {
                    DriverEntry uni = DriverStore.MatchUniversal(model, drivers);
                    if (uni != null)
                    {
                        rows[i].BestDriver = uni.Name + "（通用兜底）";
                        rows[i].Verdict = "驱动库里没有「" + model + "」的专用驱动，但有同厂商的通用驱动「"
                                        + uni.Name + "」。打印机能出纸但高级功能（双面/扫描/耗材）可能不全。";
                    }
                    else
                    {
                        rows[i].BestDriver = "(库里没匹配)";
                        rows[i].Verdict = "驱动库里没有匹配项，也没有同厂商通用驱动。点「驱动修复(联网)」按型号搜官方包"
                                        + "（可直接下载并安装），或去官网下 Full 包解压到驱动库目录后点「② 读驱动库」重新匹配。";
                    }
                    rows[i].Url = DriverLookup.SearchUrl(model);
                }
            }

            FillDeviceList();
            Log("自动匹配完成：待装设备中命中 " + hit + " 条。");
            lblInfo.Text = "设备 " + rows.Count + " 条；驱动库 " + drivers.Count + " 条；命中 " + hit + " 条。";
        }

        private void ShowVerdict()
        {
            if (lsvDevices.SelectedItems.Count == 0) return;
            DeviceRow r = lsvDevices.SelectedItems[0].Tag as DeviceRow;
            if (r == null) return;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(r.Name + "　[" + r.State + "]");
            if (r.Port != null && r.Port.Length > 0) sb.AppendLine("端口：" + r.Port);
            if (r.InstanceId != null && r.InstanceId.Length > 0) sb.AppendLine("实例：" + r.InstanceId);
            if (r.Url != null && r.Url.Length > 0) sb.AppendLine("驱动入口：" + r.Url);
            sb.AppendLine(r.Verdict ?? "");
            txtVerdict.Text = sb.ToString();
        }

        private DeviceRow Selected()
        {
            if (lsvDevices.SelectedItems.Count == 0) return null;
            return lsvDevices.SelectedItems[0].Tag as DeviceRow;
        }

        // ---------------- 动作 ----------------

        /// <summary>把选中的 .inf 装进系统（pnputil /add-driver /install）。</summary>
        private void InstallSelected()
        {
            DeviceRow r = Selected();
            if (r == null) { MessageBox.Show("先在设备表里选一行。", "装驱动", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            if (r.BestDriver == null || r.BestDriver.Length == 0 || r.BestDriver == "(库里没匹配)")
            {
                MessageBox.Show(
                    "这一行在驱动库里没有匹配到可用条目，没有可装的东西。\r\n\r\n"
                  + "三条路：\r\n"
                  + "1) 点「驱动修复(联网)」按型号搜官方包，可「仅下载」或「下载并安装」；\r\n"
                  + "2) 点「打开驱动入口」去官网下对应型号的驱动包，解压到驱动库目录，再点「读驱动库」重新匹配；\r\n"
                  + "3) 直接用设备自带的 USB 线接上，让 Windows 自己装通用驱动。",
                    "装驱动", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DriverEntry entry = FindDriverEntry(r.BestDriver);
            if (entry == null || entry.InfPath == null || entry.InfPath.Length == 0)
            {
                MessageBox.Show("这条驱动没有本地 INF 文件路径，装不了。用「打开驱动入口」去官网下包。",
                    "装驱动", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(
                "把下面这个驱动装进系统？\r\n\r\n" + entry.Name + "\r\nINF：" + entry.InfPath
              + "\r\n\r\n这会往系统驱动库里加驱动（pnputil /add-driver ... /install）。",
                "确认装驱动", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                string err;
                bool ok = DriverStore.InstallInf(entry.InfPath, out err);
                BeginInvoke((MethodInvoker)delegate
                {
                    if (ok)
                    {
                        Log("已安装：" + entry.Name);
                        Log("  路径 " + entry.InfPath);
                        ScanDrivers();
                    }
                    else
                    {
                        Log("安装失败：" + err);
                        MessageBox.Show("安装失败：" + err
                            + "\r\n\r\n多数情况是权限问题——这个工具需要管理员身份才能装驱动。",
                            "装驱动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                });
            });
        }

        private DriverEntry FindDriverEntry(string display)
        {
            for (int i = 0; i < drivers.Count; i++)
            {
                string s = drivers[i].Name + (drivers[i].Version != null && drivers[i].Version.Length > 0 ? "  " + drivers[i].Version : "");
                if (s == display) return drivers[i];
            }
            for (int i = 0; i < drivers.Count; i++)
            {
                if (display != null && display.IndexOf(drivers[i].Name, StringComparison.OrdinalIgnoreCase) >= 0) return drivers[i];
            }
            return null;
        }

        private void OpenSelectedSite()
        {
            DeviceRow r = Selected();
            string url = null;
            if (r != null) url = r.Url;
            if (url == null || url.Length == 0)
                url = DriverLookup.SearchUrl(r == null ? "" : (r.Model != null && r.Model.Length > 0 ? r.Model : r.Name));

            if (url == null || url.Length == 0)
            {
                MessageBox.Show(
                    "给不出型号对应的入口地址。\r\n\r\n"
                  + "手工路线：打开 https://www.dyjqd.com/ ，在搜索框里输型号（例如「MF210」），"
                  + "进详情页先核对列表里的「型号 / 接口 / 版本」和你手上的机器是否一致，再下 Full 包。",
                    "驱动入口", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(
                "要打开这个地址吗？\r\n\r\n" + url
              + "\r\n\r\n第三方驱动站只做跳转，下包前请自己核对型号和版本。",
                "打开驱动入口", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log("打开失败：" + ex.Message); }
        }

        private void OpenStore()
        {
            try
            {
                string dir = DriverStore.DriverDir();
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                Log("驱动库目录：" + dir);
                Log("把官网下的驱动包解压到这里，再点「② 读驱动库」。");
            }
            catch (Exception ex) { Log("打不开目录：" + ex.Message); }
        }

        /// <summary>
        /// 打开「驱动修复」对话框（联网搜官方驱动 + 一键下载安装 + 手动搜索 + 兼容性判定）。
        /// 从选中的设备行里带出型号/IP/端口；选的是队列就把队列的端口带过去，那样兼容判定能直接抓到
        /// 这台机器当前驱动吐什么语言。
        /// </summary>
        private void OpenDriverRepair()
        {
            string model = "";
            string ip = "";
            string port = "";

            DeviceRow r = Selected();
            if (r != null)
            {
                model = r.Model == null ? "" : r.Model;
                port = r.Port == null ? "" : r.Port;
                // 队列行的 Model 存的是驱动名，不是设备型号 —— 换成更合适的搜索词：
                // 优先用设备行里已经拿到的型号，没有就把驱动名去掉后缀再用。
                if (r.State == "已装队列")
                {
                    model = CleanDriverName(model);
                }

                if (port.Length > 0)
                {
                    Dictionary<string, PortInfo> ports = PrinterSystem.GetTcpPorts();
                    PortInfo pi = null;
                    if (ports.TryGetValue(port, out pi) && pi != null && pi.HostAddress.Length > 0)
                        ip = pi.HostAddress;
                }
            }

            DriverRepairForm f = new DriverRepairForm(model, ip, port);
            f.ShowDialog(FindForm());
        }

        /// <summary>把驱动名洗成适合搜索的型号词（去掉 ",3,Windows x64"、"(USB Group 2)" 这类尾巴）。</summary>
        private static string CleanDriverName(string driverName)
        {
            if (driverName == null) return "";
            string s = driverName;
            int cut = s.IndexOf(',');
            if (cut > 0) s = s.Substring(0, cut);
            s = s.Replace("(USB Group 2)", "").Replace("(USB Group 1)", "");
            s = s.Replace("Universal Printing", "").Replace("Universal Print", "");
            s = s.Replace("Class Driver", "").Replace("Print Class Driver", "");
            s = s.Replace("Point and Print compatibility driver", "");
            return s.Trim();
        }
    }
}
