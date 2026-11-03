using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace PrinterIpFix
{
    internal sealed class MainForm : Form
    {
        // ---------- 数据 ----------
        private List<PrinterInfo> _printers = new List<PrinterInfo>();
        private List<DeviceInfo> _devices = new List<DeviceInfo>();
        private readonly object _devLock = new object();
        private DateTime? _lastProbe;      // 上一次完成端口探测的时刻，显示在状态栏

        // ---------- 控件 ----------
        private TextBox txtRange;
        private Button btnScan;
        private Button btnStop;
        private ToolStripStatusLabel lblStatus;
        private ToolStripStatusLabel lblProbeTime;
        private SplitContainer split;
        private ListView lvPrinters;
        private ListView lvDevices;
        private TextBox txtLog;
        private Button btnAuto;
        private Button btnBind;
        private Button btnDefault;
        private Button btnTest;
        private Button btnRefresh;
        private Button btnReprobe;
        private Button btnDelete;
        private Button btnAddPrinter;
        private Button btnHelp;
        private Label lblPrinterHint;
        private Label lblDeviceHint;

        // ---------- 状态 ----------
        private volatile bool _scanning;
        private volatile bool _cancelled;
        private int _scanTotal;
        private int _scanDone;
        private int _foundCount;

        public MainForm()
        {
            BuildUi();
        }

        // ================= 界面 =================

        private void BuildUi()
        {
            SuspendLayout();

            // 明确按 DPI 缩放：AutoScaleMode.Font 会因为字体度量差异把窗口缩小一圈
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);

            Text = "打印机 IP 校准工具";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1100, 720);
            MinimumSize = new Size(880, 580);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            // ---- 底部日志 + 按钮 ----
            var bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 186;
            bottom.Padding = new Padding(8, 4, 8, 6);

            var btnRow = new FlowLayoutPanel();
            btnRow.Dock = DockStyle.Top;
            btnRow.Height = 38;
            btnRow.FlowDirection = FlowDirection.LeftToRight;
            btnRow.WrapContents = false;
            btnRow.Padding = new Padding(0, 2, 0, 4);

            btnAuto = MakeButton("自动匹配并修复", 120);
            btnAuto.Click += delegate { OnAutoFix(); };

            btnBind = MakeButton("绑定所选 →", 100);
            btnBind.Click += delegate { OnBind(); };

            btnDefault = MakeButton("设为默认", 88);
            btnDefault.Click += delegate { OnSetDefault(); };

            btnTest = MakeButton("打印测试页", 100);
            btnTest.Click += delegate { OnTestPage(); };

            // 重新探测：只重测现有队列的端口地址，不重读队列列表（保留当前选中与已匹配结果）。
            btnReprobe = MakeButton("重新探测", 88);
            btnReprobe.Click += delegate { StartQuickProbe(); };

            // 刷新队列：重新从系统枚举打印机，再探测一次（外部增删打印机后用这个）。
            btnRefresh = MakeButton("刷新队列", 88);
            btnRefresh.Click += delegate { ReloadPrinters(true); StartQuickProbe(); };

            btnDelete = MakeButton("删除队列", 88);
            btnDelete.Click += delegate { OnDelete(); };

            btnAddPrinter = MakeButton("添加打印机", 88);
            btnAddPrinter.Click += delegate { OnAddPrinter(); };

            btnHelp = MakeButton("使用说明", 88);
            btnHelp.Click += delegate { ShowHelp(); };

            btnRow.Controls.Add(btnAuto);
            btnRow.Controls.Add(btnBind);
            btnRow.Controls.Add(btnDefault);
            btnRow.Controls.Add(btnTest);
            btnRow.Controls.Add(btnDelete);
            btnRow.Controls.Add(btnAddPrinter);
            btnRow.Controls.Add(btnReprobe);
            btnRow.Controls.Add(btnRefresh);
            btnRow.Controls.Add(btnHelp);

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Dock = DockStyle.Fill;
            txtLog.BackColor = Color.FromArgb(250, 250, 252);
            txtLog.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
            txtLog.WordWrap = false;

            bottom.Controls.Add(txtLog);
            bottom.Controls.Add(btnRow);

            // ---- 顶部扫描条 ----
            // 全用 Dock 排版：混用 Location+Anchor 时父容器一变宽控件就会溢出（实测过）
            var hint = new Label();
            hint.Dock = DockStyle.Fill;
            hint.ForeColor = Color.FromArgb(90, 90, 100);
            hint.TextAlign = ContentAlignment.TopLeft;
            hint.Text = "留空 = 扫描本机全部网段。也可填 192.168.1.0/24 或 192.168.1.10-60，多个用 ; 隔开。"
                       + "\r\n扫描只做「发现」，不会改动任何设置；改端口要你点按钮。";

            var row = new Panel();
            row.Dock = DockStyle.Top;
            row.Height = 30;
            row.Padding = new Padding(0, 2, 0, 2);

            var lblRange = new Label();
            lblRange.Dock = DockStyle.Left;
            lblRange.Width = 62;
            lblRange.TextAlign = ContentAlignment.MiddleLeft;
            lblRange.Text = "扫描范围";

            txtRange = new TextBox();
            txtRange.Dock = DockStyle.Fill;

            btnScan = MakeButton("扫描网络设备", 112);
            btnScan.Dock = DockStyle.Right;
            btnScan.Click += delegate { OnScan(); };

            btnStop = MakeButton("停止", 60);
            btnStop.Dock = DockStyle.Right;
            btnStop.Enabled = false;
            btnStop.Click += delegate { OnStop(); };

            row.Controls.Add(txtRange);
            row.Controls.Add(lblRange);
            row.Controls.Add(btnScan);
            row.Controls.Add(btnStop);

            var top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 76;
            top.Padding = new Padding(8, 6, 8, 6);
            top.Controls.Add(hint);
            top.Controls.Add(row);

            // ---- 中间两栏 ----
            split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Vertical;
            split.Panel1.Padding = new Padding(8, 4, 4, 4);
            split.Panel2.Padding = new Padding(4, 4, 8, 4);

            lblPrinterHint = new Label();
            lblPrinterHint.Dock = DockStyle.Top;
            lblPrinterHint.Height = 24;
            lblPrinterHint.Text = "本机打印队列";
            lblPrinterHint.Font = new Font(Font, FontStyle.Bold);
            lblPrinterHint.TextAlign = ContentAlignment.MiddleLeft;

            lvPrinters = new ListView();
            lvPrinters.Dock = DockStyle.Fill;
            lvPrinters.View = View.Details;
            lvPrinters.FullRowSelect = true;
            lvPrinters.GridLines = true;
            lvPrinters.MultiSelect = true;
            lvPrinters.HideSelection = false;
            lvPrinters.Columns.Add("队列名", 186);
            lvPrinters.Columns.Add("类型", 50);
            lvPrinters.Columns.Add("端口", 132);
            lvPrinters.Columns.Add("端口指向", 100);
            lvPrinters.Columns.Add("状态", 48);
            lvPrinters.Columns.Add("匹配到", 100);
            lvPrinters.Resize += delegate { FitPrinterColumns(); };
            lvPrinters.SelectedIndexChanged += delegate { SyncSelection(); };
            lvPrinters.DoubleClick += delegate { OnAutoFix(); };

            split.Panel1.Controls.Add(lvPrinters);
            split.Panel1.Controls.Add(lblPrinterHint);

            lblDeviceHint = new Label();
            lblDeviceHint.Dock = DockStyle.Top;
            lblDeviceHint.Height = 24;
            lblDeviceHint.Text = "网络上的打印设备";
            lblDeviceHint.Font = new Font(Font, FontStyle.Bold);
            lblDeviceHint.TextAlign = ContentAlignment.MiddleLeft;

            lvDevices = new ListView();
            lvDevices.Dock = DockStyle.Fill;
            lvDevices.View = View.Details;
            lvDevices.FullRowSelect = true;
            lvDevices.GridLines = true;
            lvDevices.HideSelection = false;
            lvDevices.Columns.Add("IP", 104);
            lvDevices.Columns.Add("设备名", 140);
            lvDevices.Columns.Add("型号", 150);
            lvDevices.Columns.Add("服务", 56);
            lvDevices.Resize += delegate { FitDeviceColumns(); };
            lvDevices.DoubleClick += delegate { OnBind(); };

            split.Panel2.Controls.Add(lvDevices);
            split.Panel2.Controls.Add(lblDeviceHint);

            // ---- 状态栏 ----
            var status = new StatusStrip();
            lblStatus = new ToolStripStatusLabel("就绪：先点「扫描网络设备」，再点「自动匹配并修复」");
            lblStatus.Spring = true;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            status.Items.Add(lblStatus);

            // 上一次探测时间。不加 Spring，免得和 lblStatus 抢空间
            lblProbeTime = new ToolStripStatusLabel("尚未探测");
            lblProbeTime.TextAlign = ContentAlignment.MiddleRight;
            status.Items.Add(lblProbeTime);

            status.SizingGrip = false;

            // 注意添加顺序：Fill 先加，边缘后加，最后加的拿最外圈
            Controls.Add(split);
            Controls.Add(bottom);
            Controls.Add(status);
            Controls.Add(top);

            ResumeLayout(false);
            PerformLayout();

            Load += delegate { OnLoad(); };
        }

        private Button MakeButton(string text, int width)
        {
            var b = new Button();
            b.Text = text;
            b.Width = width;
            b.Height = 28;
            b.Margin = new Padding(0, 0, 8, 0);
            b.UseVisualStyleBackColor = true;
            return b;
        }

        // ListView 的列宽不参与 DPI 缩放，必须按控件实际宽度按比例分配，
        // 否则 125% 缩放的屏幕上固定像素的列会把文字截断。
        private void FitPrinterColumns()
        {
            double[] fr = { 0.30, 0.09, 0.22, 0.16, 0.08, 0.15 };
            FitColumns(lvPrinters, fr);
        }

        private void FitDeviceColumns()
        {
            double[] fr = { 0.22, 0.26, 0.30, 0.22 };
            FitColumns(lvDevices, fr);
        }

        private static void FitColumns(ListView lv, double[] fr)
        {
            if (lv == null || !lv.IsHandleCreated) return;
            int w = lv.ClientSize.Width - 2;
            if (w < 200) return;
            int used = 0;
            for (int i = 0; i < fr.Length && i < lv.Columns.Count; i++)
            {
                int v = (i == fr.Length - 1) ? w - used : (int)(w * fr[i]);
                if (v < 44) v = 44;
                lv.Columns[i].Width = v;
                used += v;
            }
        }
        // ================= 启动 =================

        private void OnLoad()
        {
            // SplitterDistance 只能在容器已经有实际宽度后设，否则会被 MinSize 校验拦下抛异常
            try
            {
                int w = split.Width;
                int lo = 320;
                int hi = w - 320 - split.SplitterWidth;
                if (hi > lo)
                {
                    int want = (int)(w * 0.58);
                    split.SplitterDistance = Math.Max(lo, Math.Min(hi, want));
                }
            }
            catch { }

            txtRange.Text = DefaultRangeText();
            Log("工具已启动。" + (PrinterSystem.IsAdmin() ? "当前管理员权限。" : "当前普通权限（本工具不需要提权）。")
                + " 完整性级别：" + PrinterSystem.IntegrityText());
            if (PrinterSystem.IsRestricted())
            {
                Log("[注意] 当前进程跑在「低完整性」受限环境里，改打印配置会被系统拒绝。");
                Log("       多半是从聊天窗口或沙箱里点开的 exe。关掉本窗口，到文件资源管理器里双击同一个 exe 就行。");
            }
            Log("扫描范围：" + txtRange.Text);
            FitPrinterColumns();
            FitDeviceColumns();
            ReloadPrinters(false);
            StartQuickProbe();

            // 打开就自动跑一次发现：用户要的是「点一下就把事办了」，不该先教他怎么扫描
            var autoScan = new System.Windows.Forms.Timer();
            autoScan.Interval = 1200;
            autoScan.Tick += delegate
            {
                autoScan.Stop();
                autoScan.Dispose();
                if (!_scanning && lvDevices.Items.Count == 0) OnScan();
            };
            autoScan.Start();
        }

        private string DefaultRangeText()
        {
            var parts = new List<string>();
            foreach (uint[] r in Scanner.LocalRanges())
            {
                uint lo = r[0], hi = r[1];
                string a = Scanner.U2Ip(lo).ToString();
                string b = Scanner.U2Ip(hi).ToString();
                if (hi - lo + 1 <= 254)
                {
                    string[] sp = b.Split('.');
                    parts.Add(a + "-" + sp[3]);
                }
                else
                {
                    parts.Add(a + "-" + b);
                }
            }
            return string.Join("; ", parts.ToArray());
        }

        /// <summary>先只探已知地址（快），让用户立刻看到端口是不是死的</summary>
        private void StartQuickProbe()
        {
            var list = new List<PrinterInfo>(_printers);
            var th = new Thread(delegate()
            {
                foreach (PrinterInfo p in list)
                {
                    if (p.Kind != PortKind.TcpIp) continue;
                    if (string.IsNullOrEmpty(p.ResolvedIp)) { p.AddressAlive = false; p.PingAlive = false; continue; }

                    // 深睡设备第一次连不上很正常，交给 EnsureAlive 重试
                    bool ping;
                    bool ok = Scanner.EnsureAlive(p.ResolvedIp, out ping);
                    p.AddressAlive = ok;
                    p.PingAlive = ping;
                }
                Ui(delegate
                {
                    // 重新探测后端口地址可能变了，顺手把匹配结果重算一遍，
                    // 不然列表还停在旧结论上（用户会以为「点了没反应」）。
                    if (_devices.Count > 0) AutoMatch(false);
                    FillPrinterList();
                    _lastProbe = DateTime.Now;
                    if (lblProbeTime != null) lblProbeTime.Text = "上次探测 " + _lastProbe.Value.ToString("HH:mm:ss");

                    int gone = 0, silent = 0;
                    foreach (PrinterInfo p in _printers)
                    {
                        if (p.Kind != PortKind.TcpIp || p.AddressAlive != false) continue;
                        if (p.PingAlive == true) silent++; else gone++;
                    }
                    if (silent > 0)
                        Log("有 " + silent + " 台队列的端口地址在线，但打印机不应答（可能深睡了）。稍等一下点「重新探测」再试。");
                    if (gone > 0)
                        Log("有 " + gone + " 台队列的端口地址上没有设备（打印机可能离线或换过 IP）。");
                    if (silent == 0 && gone == 0) Log("已知端口地址均可达。");
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        // ================= 队列 =================

        private void ReloadPrinters(bool verbose)
        {
            try
            {
                Dictionary<string, PortInfo> ports = PrinterSystem.GetTcpPorts();
                List<PrinterInfo> fresh = PrinterSystem.GetPrinters();

                // 保留上一轮的匹配与探测结果
                var old = new Dictionary<string, PrinterInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (PrinterInfo p in _printers) old[p.Name] = p;

                foreach (PrinterInfo p in fresh)
                {
                    PrinterSystem.Classify(p, ports);
                    p.ResolvedIp = PrinterSystem.ResolveAddress(p.RawAddress);
                    PrinterInfo prev;
                    if (old.TryGetValue(p.Name, out prev))
                    {
                        p.AddressAlive = prev.AddressAlive;
                        p.PingAlive = prev.PingAlive;
                        p.Match = prev.Match;
                        p.MatchScore = prev.MatchScore;
                        p.MatchReason = prev.MatchReason;
                    }
                }
                _printers = fresh;
                if (verbose) Log("已刷新打印队列，共 " + _printers.Count + " 项。");

                // 队列变了但设备清单没变时，顺手按现有设备重新匹配一遍，
                // 免得用户以为"刷新了却没匹配"
                if (_devices.Count > 0) AutoMatch(false);

                FillPrinterList();
            }
            catch (Exception ex)
            {
                Log("读取打印队列失败：" + ex.Message);
            }
        }

        private void FillPrinterList()
        {
            lvPrinters.BeginUpdate();
            try
            {
                lvPrinters.Items.Clear();
                foreach (PrinterInfo p in _printers)
                {
                    var it = new ListViewItem(p.IsDefault ? p.Name + "  [默认]" : p.Name);
                    it.Tag = p;
                    it.SubItems.Add(p.KindText);
                    it.SubItems.Add(p.PortName);
                    it.SubItems.Add(string.IsNullOrEmpty(p.RawAddress) ? "-" : p.RawAddress);

                    string st = p.StatusText;
                    if (p.Kind == PortKind.TcpIp && p.AddressAlive == false)
                        st = p.PingAlive == true ? "设备无应答" : "地址不通";
                    it.SubItems.Add(st);

                    string m = "-";
                    if (p.Match != null)
                        m = p.Match.Ip + "  (" + p.MatchScore + ")";
                    else if (p.MatchScore > 0)
                        m = "低分候选 " + p.MatchScore;
                    it.SubItems.Add(m);

                    if (p.Kind == PortKind.TcpIp && p.AddressAlive == false)
                        it.ForeColor = Color.FromArgb(180, 40, 40);
                    else if (p.Match != null)
                        it.ForeColor = Color.FromArgb(20, 110, 50);

                    lvPrinters.Items.Add(it);
                }
            }
            finally { lvPrinters.EndUpdate(); }
        }

        private void FillDeviceList()
        {
            lvDevices.BeginUpdate();
            try
            {
                lvDevices.Items.Clear();
                List<DeviceInfo> copy;
                lock (_devLock) copy = new List<DeviceInfo>(_devices);
                copy.Sort(delegate(DeviceInfo a, DeviceInfo b) { return CompareIp(a.Ip, b.Ip); });
                foreach (DeviceInfo d in copy)
                {
                    var it = new ListViewItem(d.Ip);
                    it.Tag = d;
                    it.SubItems.Add(d.DisplayName);
                    it.SubItems.Add(string.IsNullOrEmpty(d.Model) ? "-" : d.Model);
                    it.SubItems.Add(d.Services);
                    if (!string.IsNullOrEmpty(d.Error)) it.ForeColor = Color.FromArgb(150, 110, 20);
                    lvDevices.Items.Add(it);
                }
            }
            finally { lvDevices.EndUpdate(); }
        }

        private static int CompareIp(string a, string b)
        {
            uint x, y;
            try { x = Scanner.Ip2U(IPAddress.Parse(a)); } catch { x = 0; }
            try { y = Scanner.Ip2U(IPAddress.Parse(b)); } catch { y = 0; }
            return x.CompareTo(y);
        }

        private void AddDevice(DeviceInfo d)
        {
            lock (_devLock)
            {
                foreach (DeviceInfo e in _devices)
                    if (e.Ip == d.Ip) return;
                _devices.Add(d);
            }
            _foundCount++;
            var it = new ListViewItem(d.Ip);
            it.Tag = d;
            it.SubItems.Add(d.DisplayName);
            it.SubItems.Add(string.IsNullOrEmpty(d.Model) ? "-" : d.Model);
            it.SubItems.Add(d.Services);
            lvDevices.Items.Add(it);
            Log("发现设备 " + d.Ip + "  " + d.DisplayName
                + (string.IsNullOrEmpty(d.Model) ? "" : "  [" + d.Model + "]")
                + "  " + d.Services);
        }

        // ================= 扫描 =================

        private void OnScan()
        {
            if (_scanning) return;
            string err;
            List<IPAddress> targets = Scanner.ParseTargets(txtRange.Text, out err);
            if (err.Length > 0)
            {
                MessageBox.Show(err, "范围不对", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (targets.Count == 0)
            {
                MessageBox.Show("没有解析出任何地址。", "范围不对", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _cancelled = false;
            _scanning = true;
            _scanTotal = targets.Count;
            _scanDone = 0;
            _foundCount = 0;
            lock (_devLock) _devices.Clear();
            lvDevices.Items.Clear();

            btnScan.Enabled = false;
            btnStop.Enabled = true;
            SetStatus("正在扫描 " + targets.Count + " 个地址…");
            Log("开始扫描 " + targets.Count + " 个地址（631 / 9100 / 515 端口）。");

            var th = new Thread(delegate()
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    Scanner.Sweep(targets,
                        delegate(DeviceInfo d) { Ui(delegate { AddDevice(d); }); },
                        delegate(int done, int total)
                        {
                            _scanDone = done;
                            Ui(delegate
                            {
                                SetStatus("扫描中 " + done + "/" + total + "，已发现 " + _foundCount + " 台设备…");
                            });
                        },
                        delegate { return _cancelled; },
                        350, 1800);
                }
                catch (Exception ex)
                {
                    Ui(delegate { Log("扫描出错：" + ex.Message); });
                }
                sw.Stop();
                Ui(delegate
                {
                    _scanning = false;
                    btnScan.Enabled = true;
                    btnStop.Enabled = false;
                    SetStatus(_cancelled ? "已停止" : "扫描完成");
                    Log((_cancelled ? "扫描被停止，用时 " : "扫描完成，用时 ")
                        + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0")
                        + " 秒，发现 " + _foundCount + " 台打印设备。");
                    AutoMatch(false);
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        private void OnStop()
        {
            _cancelled = true;
            SetStatus("正在停止…");
        }

        // ================= 匹配 =================

        private void AutoMatch(bool verbose)
        {
            int matched = 0;
            foreach (PrinterInfo p in _printers)
            {
                p.Match = null; p.MatchScore = 0; p.MatchReason = "";
                if (!p.Fixable) continue;
                List<DeviceInfo> devs;
                lock (_devLock) devs = new List<DeviceInfo>(_devices);
                if (devs.Count == 0) continue;

                List<MatchCandidate> ranked = Matcher.Rank(p, devs);
                if (ranked.Count == 0) continue;

                MatchCandidate best = ranked[0];
                p.MatchScore = best.Score;
                p.MatchReason = best.Reason;
                bool unique = ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= 15;
                if (best.Score >= 45 && unique)
                {
                    p.Match = best.Device;
                    matched++;
                    if (verbose)
                        Log("匹配：" + p.Name + " → " + best.Device.Ip
                            + "（" + best.Score + " 分：" + best.Reason + "）");
                }
                else if (verbose)
                {
                    Log("不确定：" + p.Name + " 最高分 " + best.Score + "（" + best.Reason + "），请手动选择设备后点『绑定』。");
                }
            }
            FillPrinterList();
            if (verbose)
                Log("自动匹配完成：" + matched + " 台队列锁定了设备。");
            else if (matched > 0)
                Log("自动匹配完成：" + matched + " 台队列锁定了设备，可点『自动匹配并修复』。");
        }

        private PrinterInfo GetSelectedPrinter()
        {
            if (lvPrinters.SelectedItems.Count == 0) return null;
            return lvPrinters.SelectedItems[0].Tag as PrinterInfo;
        }

        private DeviceInfo GetSelectedDevice()
        {
            if (lvDevices.SelectedItems.Count == 0) return null;
            return lvDevices.SelectedItems[0].Tag as DeviceInfo;
        }

        private void SyncSelection()
        {
            PrinterInfo p = GetSelectedPrinter();
            if (p == null) return;
            SetStatus(p.Name + "  |  端口 " + p.PortName
                + "  |  " + (string.IsNullOrEmpty(p.RawAddress) ? "无地址" : "指向 " + p.RawAddress)
                + (p.Match == null ? "" : "  |  匹配 " + p.Match.Ip));
            if (p.Match == null) return;
            foreach (ListViewItem it in lvDevices.Items)
            {
                DeviceInfo d = it.Tag as DeviceInfo;
                if (d != null && d.Ip == p.Match.Ip)
                {
                    it.Selected = true;
                    it.EnsureVisible();
                    break;
                }
            }
        }

        // ================= 修复 =================

        private void OnAutoFix()
        {
            List<PrinterInfo> targets = new List<PrinterInfo>();
            foreach (ListViewItem it in lvPrinters.SelectedItems)
            {
                PrinterInfo p = it.Tag as PrinterInfo;
                if (p != null) targets.Add(p);
            }
            if (targets.Count == 0)
            {
                foreach (PrinterInfo p in _printers)
                    if (p.Fixable && p.Match != null) targets.Add(p);
            }
            if (targets.Count == 0)
            {
                MessageBox.Show("没有可修复的队列。\n\n先点『扫描网络设备』，等匹配出设备后再说；"
                    + "或在右栏手动选中设备，再选中队列点『绑定所选 →』。",
                    "没有目标", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var lines = new List<string>();
            int need = 0;
            foreach (PrinterInfo p in targets)
            {
                if (p.Match == null)
                {
                    lines.Add("· " + p.Name + " —— 未匹配到设备，跳过");
                    continue;
                }
                if (p.Kind == PortKind.TcpIp && p.ResolvedIp == p.Match.Ip)
                {
                    lines.Add("· " + p.Name + " —— 端口已指向 " + p.Match.Ip + "，无需修改");
                    continue;
                }
                lines.Add("· " + p.Name + "：" + p.PortName + " → IP_" + p.Match.Ip);
                need++;
            }
            if (need == 0)
            {
                MessageBox.Show(string.Join("\r\n", lines.ToArray()) + "\r\n\r\n没有需要改动的队列。",
                    "无需修改", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult dr = MessageBox.Show(
                "即将修改以下 " + need + " 台队列的端口：" + "\r\n\r\n"
                + string.Join("\r\n", lines.ToArray())
                + "\r\n\r\n原理：为设备建标准 TCP/IP 端口（RAW 9100），把队列指过去。"
                + "不改驱动、不删队列，随时可以改回来。",
                "确认修改", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (dr != DialogResult.OK) return;

            int ok = 0, fail = 0;
            foreach (PrinterInfo p in targets)
            {
                if (p.Match == null) continue;
                if (p.Kind == PortKind.TcpIp && p.ResolvedIp == p.Match.Ip) continue;
                if (ApplyFix(p, p.Match)) ok++; else fail++;
            }
            ReloadPrinters(false);
            AutoMatch(false);
            Log("修复结束：成功 " + ok + " 台，失败 " + fail + " 台。");
            if (ok > 0)
                Log("建议下一步：选中队列点『打印测试页』，出纸即证明通了。");
        }

        private void OnBind()
        {
            PrinterInfo p = GetSelectedPrinter();
            DeviceInfo d = GetSelectedDevice();
            if (p == null)
            {
                MessageBox.Show("先在左栏选中一台打印机队列。", "缺少选择",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (d == null)
            {
                MessageBox.Show("先在右栏选中一台网络设备。", "缺少选择",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!p.Fixable)
            {
                MessageBox.Show("这台队列是「" + p.KindText + "」类型，改端口没有意义。",
                    "不支持", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult dr = MessageBox.Show(
                "把下面这台队列改到该设备上？\r\n\r\n"
                + "队列：" + p.Name + "\r\n"
                + "原端口：" + p.PortName + "\r\n"
                + "新端口：IP_" + d.Ip + "（" + d.DisplayName + "）",
                "确认绑定", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (dr != DialogResult.OK) return;

            if (ApplyFix(p, d))
            {
                p.Match = d;
                p.MatchScore = 100;
                ReloadPrinters(false);
                FillPrinterList();
            }
        }

        /// <summary>把队列指到设备 IP 的标准 TCP/IP 端口。返回是否成功。</summary>
        private bool ApplyFix(PrinterInfo p, DeviceInfo d)
        {
            string portName = "IP_" + d.Ip;
            try
            {
                if (!Scanner.TcpAlive(d.Ip, 9100, 700) && !Scanner.TcpAlive(d.Ip, 631, 700))
                {
                    Log("警告：" + d.Ip + " 的 9100/631 都连不上，仍然写入端口配置（设备可能刚离线）。");
                }

                // 复用已指向同一地址的端口，避免堆一堆相似端口
                string reuse = "";
                foreach (KeyValuePair<string, PortInfo> kv in PrinterSystem.GetTcpPorts())
                {
                    if (string.Compare(kv.Value.HostAddress, d.Ip, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        reuse = kv.Key;
                        break;
                    }
                }

                string usePort;
                if (reuse.Length > 0)
                {
                    usePort = reuse;
                    Log("复用已有端口 " + usePort + "（已指向 " + d.Ip + "）。");
                }
                else if (PrinterSystem.PortExists(portName))
                {
                    usePort = portName;
                    Log("端口 " + portName + " 已存在，直接使用。");
                }
                else
                {
                    PrinterSystem.CreateTcpPort(portName, d.Ip, 9100);
                    if (!PrinterSystem.PortExists(portName))
                    {
                        foreach (string line in PrinterSystem.LastPortLog) Log("  建端口诊断：" + line);
                        Log("失败：端口 " + portName + " 没建出来，队列没有改动。");
                        Log("救命命令（复制到 PowerShell 里跑一次即可）：");
                        Log("  Add-PrinterPort -Name '" + portName + "' -PrinterHostAddress '" + d.Ip + "'");
                        if (PrinterSystem.IsRestricted())
                            OfferElevatedRetry("创建打印端口（当前是低完整性受限权限）");
                        return false;
                    }
                    usePort = portName;
                    Log("已创建标准 TCP/IP 端口 " + portName + " → " + d.Ip + ":9100");
                }

                PrinterSystem.SetPrinterPort(p.Name, usePort);
                string back = PrinterSystem.GetPrinterPort(p.Name);
                if (string.Compare(back, usePort, StringComparison.OrdinalIgnoreCase) != 0)
                {
                    Log("失败：" + p.Name + " 回读端口是 " + back + "，不是 " + usePort + "。");
                    return false;
                }
                Log("成功：" + p.Name + " 现在指向 " + usePort + "（" + d.Ip + "）。");
                return true;
            }
            catch (Exception ex)
            {
                Log("失败：" + p.Name + " —— " + ex.Message);
                return false;
            }
        }

        // ================= 其它动作 =================

        private void OnSetDefault()
        {
            PrinterInfo p = GetSelectedPrinter();
            if (p == null)
            {
                MessageBox.Show("先在左栏选中一台打印机队列。", "缺少选择",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string err;
            if (PrinterSystem.SetDefaultPrinter(p.Name, out err))
            {
                Log("已把默认打印机设为：" + p.Name);
                ReloadPrinters(false);
            }
            else
            {
                Log("设置默认打印机失败：" + err);
                for (int i = 0; i < PrinterSystem.LastDefaultLog.Count; i++)
                    Log("    " + PrinterSystem.LastDefaultLog[i]);
                MessageBox.Show("设置失败：" + err, "失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (PrinterSystem.IsRestricted()) OfferElevatedRetry("设置默认打印机");
            }
        }

        private void OnTestPage()
        {
            PrinterInfo p = GetSelectedPrinter();
            if (p == null)
            {
                MessageBox.Show("先在左栏选中一台打印机队列。", "缺少选择",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                var doc = new PrintDocument();
                doc.DocumentName = "打印机连通性测试";
                doc.PrinterSettings.PrinterName = p.Name;
                if (!doc.PrinterSettings.IsValid)
                {
                    MessageBox.Show("这台队列的驱动不可用（IsValid=false）：" + p.Name,
                        "无法打印", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                DateTime now = DateTime.Now;
                string queue = p.Name;
                string port = p.PortName;
                doc.PrintPage += delegate(object s, PrintPageEventArgs e)
                {
                    Graphics g = e.Graphics;
                    var f1 = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold);
                    var f2 = new Font("Microsoft YaHei UI", 11F);
                    float y = 90;
                    g.DrawString("打印连通性测试", f1, Brushes.Black, 80, y);
                    y += 50;
                    g.DrawString("队列：" + queue, f2, Brushes.Black, 80, y); y += 26;
                    g.DrawString("端口：" + port, f2, Brushes.Black, 80, y); y += 26;
                    g.DrawString("时间：" + now.ToString("yyyy-MM-dd HH:mm:ss"), f2, Brushes.Black, 80, y); y += 26;
                    g.DrawString("机器：" + Environment.MachineName + " / " + Environment.UserName,
                        f2, Brushes.Black, 80, y); y += 40;
                    g.DrawString("看到这页就说明队列 → 设备链路是通的。", f2, Brushes.Black, 80, y);
                    e.HasMorePages = false;
                };
                doc.Print();
                Log("已向 " + p.Name + " 提交测试页。若没出纸，看队列里作业是否卡在「打印中」。");
            }
            catch (Exception ex)
            {
                Log("打印测试页失败：" + ex.Message);
                MessageBox.Show("打印失败：" + ex.Message
                    + "\r\n\r\n若是「无法启动打印作业」，多半还是端口地址或耗材问题。",
                    "打印失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ShowHelp()
        {
            const string help =
                "三步走\r\n" +
                "────────────\r\n" +
                "1) 点『扫描网络设备』。工具会在网段内找开着 631 / 9100 / 515 的机器，\r\n" +
                "   并用 IPP 读出设备型号、序列号（不需要管理员权限）。\r\n" +
                "2) 看左栏：『端口指向』变红 = 端口记的地址已经没有设备了（打印机换了 IP）。\r\n" +
                "   『匹配到』一栏是工具按型号/序列号/名称自动对上的设备。\r\n" +
                "3) 点『自动匹配并修复』，或自己选中队列与设备点『绑定所选 →』。\r\n\r\n" +
                "它到底改了什么\r\n" +
                "────────────\r\n" +
                "只新建一个标准 TCP/IP 端口（RAW 9100，指向设备当前 IP），然后把队列指过去。\r\n" +
                "不动驱动、不删队列、不动设备本身，随时可以改回来。\r\n\r\n" +
                "删除队列\r\n" +
                "────────────\r\n" +
                "左栏选中队列后点『删除队列』。队列里卡着没出的作业会一起消失，\r\n" +
                "名字里带 [ ] 的僵尸队列也能删掉；会先弹一次确认，删前能看清有几个作业。\r\n" +
                "驱动和端口保留，以后要恢复得重新添加打印机。\r\n\r\n" +
                "换任何打印机都能用\r\n" +
                "────────────\r\n" +
                "工具里没有任何「某个型号」的写死规则，靠 IPP 报出来的身份信息匹配：\r\n" +
                "序列号 > 设备 ID > 型号 > 名称。认不准时会让你手动选，不会瞎改。\r\n\r\n" +
                "常见现象\r\n" +
                "────────────\r\n" +
                "· 修完还是不出纸：先看耗材（黑色墨盒空了也会整机拒印）。\r\n" +
                "· 队列显示「脱机」：右键队列，取消勾选『脱机使用打印机』。\r\n" +
                "· 设备 631 不开但 9100 开：仍可修复，只是读不到型号，匹配靠名称。\r\n" +
                "· 长期解决：给打印机设固定 IP（或在路由器做 DHCP 保留），就再也不会漂。";

            MessageBox.Show(help, "使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ================= 删除队列 =================

        /// <summary>提权重启后要自动删的队列名（Program 从命令行传入）。</summary>
        public string[] PendingDelete;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (PendingDelete == null || PendingDelete.Length == 0) return;
            string[] names = PendingDelete;
            PendingDelete = null;
            Log("以管理员身份重试删除：" + string.Join("、", names));
            DeleteQueues(new List<string>(names), true);
        }

        private void OnAddPrinter()
        {
            using (var dlg = new AddPrinterForm())
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                ReloadPrinters(true);
                Log("已添加打印机：" + dlg.CreatedQueue);
            }
        }

        private void OnDelete()
        {
            var names = new List<string>();
            foreach (ListViewItem it in lvPrinters.SelectedItems)
            {
                PrinterInfo p = it.Tag as PrinterInfo;
                if (p != null) names.Add(p.Name);
            }
            if (names.Count == 0)
            {
                PrinterInfo one = GetSelectedPrinter();
                if (one != null) names.Add(one.Name);
            }
            if (names.Count == 0)
            {
                MessageBox.Show("先在左栏选中要删的队列（按住 Ctrl 可以多选）。",
                    "没选队列", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DeleteQueues(names, true);
        }

        /// <summary>删一批队列。confirm=是否先弹确认框；删不掉且没有管理员权限时提供提权重试。</summary>
        private void DeleteQueues(List<string> names, bool confirm)
        {
            var ports = new List<string>();
            foreach (string n in names)
            {
                foreach (PrinterInfo p in _printers)
                {
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                    {
                        ports.Add(p.PortName);
                        break;
                    }
                }
            }

            if (confirm)
            {
                string body = "";
                foreach (string n in names)
                {
                    int jobs = PrinterSystem.CountJobs(n);
                    body += "· " + n
                        + (jobs > 0 ? "   还有 " + jobs + " 个作业没出纸" : "") + "\r\n";
                }
                body += "\r\n"
                    + (names.Count == 1 ? "删掉这个队列？" : "删掉这 " + names.Count + " 个队列？")
                    + "\r\n队列和它排着的作业一起消失；驱动和端口不动，"
                    + "以后要恢复得重新添加打印机。";
                if (MessageBox.Show(body, "确认删除", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    Log("已取消删除。");
                    return;
                }
            }

            var failed = new List<string>();
            int ok = 0;
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (string n in names)
                {
                    string msg;
                    bool done = PrinterSystem.DeletePrinter(n, out msg);
                    if (done) ok++;
                    else failed.Add(n);
                    Log((done ? "已删除 " : "删除失败 ") + n + " —— " + msg);
                    if (!done)
                        foreach (string line in PrinterSystem.LastDeleteLog)
                            Log("    " + line);
                }
            }
            finally
            {
                Cursor = Cursors.Default;
                ReloadPrinters(false);
            }

            for (int i = 0; i < names.Count && i < ports.Count; i++)
            {
                if (string.IsNullOrEmpty(ports[i])) continue;
                if (PrinterSystem.PrintersOnPort(ports[i]).Count == 0)
                    Log("端口 " + ports[i] + " 已经没有队列在用了"
                        + "（要清端口用 PrinterQueueCleanup.exe）。");
            }

            Log("删除完成：" + ok + "/" + names.Count + " 成功，当前共 "
                + _printers.Count + " 个队列。");

            if (failed.Count > 0 && !PrinterSystem.IsAdmin())
                OfferElevatedDelete(failed);
        }

        /// <summary>普通权限删不掉时，用管理员身份重启自己再删一遍。</summary>
        private void OfferElevatedDelete(List<string> names)
        {
            string what = (names.Count == 1 ? "这个队列" : "这 " + names.Count + " 个队列");
            if (MessageBox.Show("普通权限删不掉" + what + "。\r\n\r\n"
                    + "要不要以管理员身份重新打开本工具、自动重试一次？\r\n"
                    + "（会弹一次用户账户控制确认，本窗口随后关闭）",
                    "需要管理员", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
            {
                Log("没有提权。也可以右键 exe 选『以管理员身份运行』再删。");
                return;
            }

            string payload = string.Join("\u0001", names.ToArray());
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(
                    Application.ExecutablePath, "--delete \"" + payload + "\"");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                System.Diagnostics.Process.Start(psi);
                Close();
            }
            catch (Exception ex)
            {
                Log("提权没成功：" + ex.Message);
                MessageBox.Show("提权没成功：" + ex.Message + "\r\n\r\n"
                    + "可以右键 exe 选『以管理员身份运行』再删。",
                    "提权失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        /// <summary>权限不够时的统一出口：问一句就以管理员身份重开一份。</summary>
        private bool OfferElevatedRetry(string what)
        {
            if (MessageBox.Show("这一步需要更高权限：" + what + "。\r\n\r\n"
                    + "要不要以管理员身份重新打开本工具？\r\n"
                    + "（会弹一次用户账户控制确认，本窗口随后关闭；重开后请再点一次同样的按钮）",
                    "权限不足", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                Log("没有提权。也可以关掉本窗口，到资源管理器里双击 exe 再试；或右键 exe 选『以管理员身份运行』。");
                return false;
            }
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                System.Diagnostics.Process.Start(psi);
                Close();
                return true;
            }
            catch (Exception ex)
            {
                Log("提权没成功：" + ex.Message);
                return false;
            }
        }
        // ================= 工具 =================

        private void SetStatus(string text)
        {
            if (lblStatus != null) lblStatus.Text = text;
        }

        private void Log(string text)
        {
            try
            {
                txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + "\r\n");
            }
            catch { }
        }

        private void Ui(Action action)
        {
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_scanning)
            {
                DialogResult dr = MessageBox.Show("扫描还在进行，确定退出？", "退出",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (dr != DialogResult.Yes) { e.Cancel = true; return; }
                _cancelled = true;
            }
            base.OnFormClosing(e);
        }
    }
}
