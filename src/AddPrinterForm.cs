using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace PrinterIpFix
{
    /// <summary>添加打印机：网络（按 IP 建 TCP/IP 端口 / 按 \\主机\共享名 连共享）与 USB（USB00x 端口）。</summary>
    internal sealed class AddPrinterForm : Form
    {
        private TabControl tabs;

        // 网络页
        private TextBox txtHost;
        private Button btnProbe;
        private TextBox txtProbe;
        private TextBox txtPort;
        private TextBox txtRange;
        private Button btnScan;
        private Button btnStop;
        private ListView lvFound;
        private Label lblScanState;
        private bool _scanning;
        private bool _scanCancelled;
        private int _foundCount;

        // USB 页
        private ComboBox cboUsbDevice;
        private ComboBox cboUsbPort;
        private Button btnRefreshUsb;
        private List<UsbDevice> _usbDevices = new List<UsbDevice>();

        // 公共
        private TextBox txtName;
        private ComboBox cboDriver;
        private CheckBox chkDefault;
        private TextBox txtLog;
        private Button btnAdd;
        private Button btnCancel;

        private List<string> _drivers = new List<string>();

        /// <summary>添加成功后带回去的队列名。</summary>
        public string CreatedQueue = "";

        public AddPrinterForm()
        {
            BuildUi();
            LoadListsAsync();
        }

        // ---------------- 界面 ----------------

        private void BuildUi()
        {
            Text = "添加打印机";
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(760, 740);
            MinimumSize = new Size(720, 680);
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.Padding = new Padding(10, 10, 10, 10);
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 196F));

            tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(14, 5);

            var pageNet = new TabPage("网络打印机");
            pageNet.Padding = new Padding(2);
            BuildNetworkPage(pageNet);

            var pageUsb = new TabPage("USB 打印机");
            pageUsb.Padding = new Padding(2);
            BuildUsbPage(pageUsb);

            tabs.TabPages.Add(pageNet);
            tabs.TabPages.Add(pageUsb);
            tabs.SelectedIndexChanged += delegate { SyncDriverPick(); };

            var common = BuildCommon();
            var footer = BuildFooter();

            root.Controls.Add(tabs, 0, 0);
            root.Controls.Add(common, 0, 1);
            root.Controls.Add(footer, 0, 2);
            Controls.Add(root);
        }

        private void BuildNetworkPage(TabPage page)
        {
            var g = Grid(7, 76, 96);
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            g.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 76F));
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));

            var lblHost = Cell("打印机地址");
            txtHost = new TextBox();
            txtHost.Dock = DockStyle.Fill;
            txtHost.Margin = new Padding(2, 5, 6, 5);
            txtHost.TextChanged += delegate { _probed = null; };

            btnProbe = new Button();
            btnProbe.Text = "检测";
            btnProbe.Dock = DockStyle.Fill;
            btnProbe.Margin = new Padding(2, 4, 2, 4);
            btnProbe.Click += delegate { OnProbe(); };

            // ---- 自动扫描 ----
            var lblScan = Cell("扫描范围");

            txtRange = new TextBox();
            txtRange.Dock = DockStyle.Fill;
            txtRange.Margin = new Padding(2, 5, 6, 5);
            txtRange.Text = Scanner.DescribeLocalRanges();
            txtRange.Enter += delegate
            {
                if (txtRange.Text == Scanner.DescribeLocalRanges()) txtRange.SelectAll();
            };

            btnScan = new Button();
            btnScan.Text = "扫描网络";
            btnScan.Dock = DockStyle.Fill;
            btnScan.Margin = new Padding(2, 4, 2, 4);
            btnScan.Click += delegate { OnScanNetwork(); };

            var scanBar = new Panel();
            scanBar.Dock = DockStyle.Fill;
            scanBar.Margin = new Padding(2, 0, 2, 2);

            lblScanState = new Label();
            lblScanState.Dock = DockStyle.Fill;
            lblScanState.TextAlign = ContentAlignment.MiddleLeft;
            lblScanState.ForeColor = Color.FromArgb(90, 90, 90);
            lblScanState.Text = "点「扫描网络」自动找在线打印机；扫到后点一行即可自动填好地址、端口和名称。";

            btnStop = new Button();
            btnStop.Text = "停止";
            btnStop.Dock = DockStyle.Right;
            btnStop.Width = 68;
            btnStop.Enabled = false;
            btnStop.Margin = new Padding(0);
            btnStop.Click += delegate { _scanCancelled = true; };

            scanBar.Controls.Add(lblScanState);
            scanBar.Controls.Add(btnStop);

            lvFound = new ListView();
            lvFound.Dock = DockStyle.Fill;
            lvFound.View = View.Details;
            lvFound.FullRowSelect = true;
            lvFound.MultiSelect = false;
            lvFound.HideSelection = false;
            lvFound.Margin = new Padding(2, 2, 2, 6);
            lvFound.Columns.Add("地址", 108);
            lvFound.Columns.Add("设备名", 150);
            lvFound.Columns.Add("型号", 190);
            lvFound.Columns.Add("服务", 150);
            lvFound.DoubleClick += delegate { OnFoundPicked(); };
            lvFound.SelectedIndexChanged += delegate { OnFoundPicked(); };

            txtProbe = new TextBox();
            txtProbe.Multiline = true;
            txtProbe.ReadOnly = true;
            txtProbe.ScrollBars = ScrollBars.Vertical;
            txtProbe.Dock = DockStyle.Fill;
            txtProbe.BackColor = Color.White;
            txtProbe.Margin = new Padding(2, 2, 2, 6);
            txtProbe.Text = "填 IP 或主机名后点「检测」，会把设备型号、状态读出来；也可以直接填 \\\\主机\\共享名 连共享打印机。";

            var lblPort = Cell("端口");
            txtPort = new TextBox();
            txtPort.Dock = DockStyle.Fill;
            txtPort.Margin = new Padding(2, 5, 6, 5);

            var hint = new Label();
            hint.AutoSize = false;
            hint.Dock = DockStyle.Fill;
            hint.ForeColor = Color.FromArgb(90, 90, 90);
            hint.Margin = new Padding(2, 2, 2, 2);
            hint.Text = "・ 扫描添加：自动找同网段开 631 / 9100 / 515 的设备，点一行自动填好，不用记 IP。\r\n"
                + "・ 按 IP 添加：自动建/复用一个指向该地址的 TCP/IP 端口（通常叫 IP_地址），走 9100 直接打印。\r\n"
                + "・ 按共享添加：地址写成 \\\\主机\\共享名，连对方共享出来的打印机（需要对方开机、共享名对、你有权限）。";

            var lblProbe = Cell("探测结果");

            g.Controls.Add(lblHost, 0, 0);
            g.Controls.Add(txtHost, 1, 0);
            g.Controls.Add(btnProbe, 2, 0);
            g.Controls.Add(lblScan, 0, 1);
            g.Controls.Add(txtRange, 1, 1);
            g.Controls.Add(btnScan, 2, 1);
            g.Controls.Add(scanBar, 1, 2);
            g.SetColumnSpan(scanBar, 2);
            g.Controls.Add(lvFound, 1, 3);
            g.SetColumnSpan(lvFound, 2);
            g.Controls.Add(lblProbe, 0, 4);
            g.Controls.Add(txtProbe, 1, 4);
            g.SetColumnSpan(txtProbe, 2);
            g.Controls.Add(lblPort, 0, 5);
            g.Controls.Add(txtPort, 1, 5);
            g.SetColumnSpan(txtPort, 2);
            g.Controls.Add(hint, 0, 6);
            g.SetColumnSpan(hint, 3);

            page.Controls.Add(g);
        }

        private void BuildUsbPage(TabPage page)
        {
            var g = Grid(4, 76, 96);
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            g.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var lblDev = Cell("USB 设备");
            cboUsbDevice = new ComboBox();
            cboUsbDevice.Dock = DockStyle.Fill;
            cboUsbDevice.DropDownStyle = ComboBoxStyle.DropDownList;
            cboUsbDevice.Margin = new Padding(2, 5, 6, 5);
            cboUsbDevice.SelectedIndexChanged += delegate { OnUsbDevicePicked(); };

            btnRefreshUsb = new Button();
            btnRefreshUsb.Text = "刷新";
            btnRefreshUsb.Dock = DockStyle.Fill;
            btnRefreshUsb.Margin = new Padding(2, 4, 2, 4);
            btnRefreshUsb.Click += delegate { LoadListsAsync(); };

            var lblPort = Cell("USB 端口");
            cboUsbPort = new ComboBox();
            cboUsbPort.Dock = DockStyle.Fill;
            cboUsbPort.DropDownStyle = ComboBoxStyle.DropDown;
            cboUsbPort.Margin = new Padding(2, 5, 6, 5);

            var lblState = Cell("端口占用");
            var lblOccupied = new Label();
            lblOccupied.AutoSize = false;
            lblOccupied.Dock = DockStyle.Fill;
            lblOccupied.TextAlign = ContentAlignment.MiddleLeft;
            lblOccupied.ForeColor = Color.FromArgb(90, 90, 90);
            lblOccupied.Margin = new Padding(2, 2, 2, 2);
            lblOccupied.Text = "";
            cboUsbPort.TextChanged += delegate { UpdatePortState(lblOccupied); };
            _occupiedLabel = lblOccupied;

            var hint = new Label();
            hint.AutoSize = false;
            hint.Dock = DockStyle.Fill;
            hint.ForeColor = Color.FromArgb(90, 90, 90);
            hint.Margin = new Padding(2, 4, 2, 2);
            hint.Text = "・ USB 打印机不需要填 IP：插好线，选它占的 USB00x 端口，再挑对应驱动即可。\r\n"
                + "・ 一台 USB 打印机只能有一个队列占着同一个端口；如果该端口已被占用，直接用它原来的队列就行，不必重复添加。\r\n"
                + "・ 驱动名要和设备对得上（例如 HP Smart Universal Printing (USB Group 2) 对应 HP 系列）。";

            g.Controls.Add(lblDev, 0, 0);
            g.Controls.Add(cboUsbDevice, 1, 0);
            g.Controls.Add(btnRefreshUsb, 2, 0);
            g.Controls.Add(lblPort, 0, 1);
            g.Controls.Add(cboUsbPort, 1, 1);
            g.SetColumnSpan(cboUsbPort, 2);
            g.Controls.Add(lblState, 0, 2);
            g.Controls.Add(lblOccupied, 1, 2);
            g.SetColumnSpan(lblOccupied, 2);
            g.Controls.Add(hint, 0, 3);
            g.SetColumnSpan(hint, 3);

            page.Controls.Add(g);
        }

        private Label _occupiedLabel;

        private Control BuildCommon()
        {
            var g = Grid(2, 76, 8);
            g.Padding = new Padding(4, 6, 4, 4);
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));

            var lblName = Cell("队列名");
            txtName = new TextBox();
            txtName.Dock = DockStyle.Fill;
            txtName.Margin = new Padding(2, 6, 2, 6);

            var lblDriver = Cell("驱动");
            cboDriver = new ComboBox();
            cboDriver.Dock = DockStyle.Fill;
            cboDriver.DropDownStyle = ComboBoxStyle.DropDown;
            cboDriver.Margin = new Padding(2, 6, 2, 6);
            cboDriver.TextChanged += delegate { if (!_driverProgrammatic) _driverTouched = true; };

            g.Controls.Add(lblName, 0, 0);
            g.Controls.Add(txtName, 1, 0);
            g.SetColumnSpan(txtName, 2);
            g.Controls.Add(lblDriver, 0, 1);
            g.Controls.Add(cboDriver, 1, 1);
            g.SetColumnSpan(cboDriver, 2);
            return g;
        }

        private Control BuildFooter()
        {
            var g = new TableLayoutPanel();
            g.Dock = DockStyle.Fill;
            g.ColumnCount = 1;
            g.RowCount = 3;
            g.Padding = new Padding(4, 0, 4, 2);
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            g.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Dock = DockStyle.Fill;
            txtLog.BackColor = Color.FromArgb(250, 250, 250);
            txtLog.Margin = new Padding(2, 2, 2, 4);

            chkDefault = new CheckBox();
            chkDefault.Text = "添加后设为默认打印机";
            chkDefault.AutoSize = true;
            chkDefault.Dock = DockStyle.Left;
            chkDefault.Margin = new Padding(2, 6, 2, 2);

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.WrapContents = false;
            bar.Margin = new Padding(0, 2, 0, 0);

            btnAdd = new Button();
            btnAdd.Text = "添加";
            btnAdd.Size = new Size(96, 30);
            btnAdd.Margin = new Padding(6, 0, 2, 0);
            btnAdd.Click += delegate { OnAdd(); };

            btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.Size = new Size(86, 30);
            btnCancel.Margin = new Padding(6, 0, 2, 0);
            btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            bar.Controls.Add(btnAdd);
            bar.Controls.Add(btnCancel);

            g.Controls.Add(txtLog, 0, 0);
            g.Controls.Add(chkDefault, 0, 1);
            g.Controls.Add(bar, 0, 2);
            return g;
        }

        private static TableLayoutPanel Grid(int rows, int labelWidth, int rightWidth)
        {
            var t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.ColumnCount = 3;
            t.RowCount = rows;
            t.Padding = new Padding(4, 4, 4, 4);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, rightWidth));
            return t;
        }

        private static Label Cell(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = new Padding(2, 4, 6, 4);
            return l;
        }

        // ---------------- 数据加载 ----------------

        private DeviceInfo _probed;

        private void LoadListsAsync()
        {
            AppendLog("正在读取本机驱动与 USB 端口…");
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> drivers = null;
                List<UsbDevice> usb = null;
                List<string> usbPorts = null;
                string err = "";
                try
                {
                    drivers = PrinterSystem.GetPrinterDrivers();
                    usb = PrinterSystem.GetUsbPrintDevices();
                    usbPorts = PrinterSystem.GetUsbPorts();
                }
                catch (Exception ex) { err = ex.Message; }

                Ui(delegate
                {
                    if (err.Length > 0) AppendLog("读取失败：" + err);
                    if (drivers != null) FillDrivers(drivers);
                    if (usbPorts != null) FillUsbPorts(usbPorts);
                    if (usb != null) FillUsbDevices(usb);
                });
            });
        }

        private void FillDrivers(List<string> drivers)
        {
            _drivers = drivers;
            string keep = cboDriver.Text;
            cboDriver.Items.Clear();
            for (int i = 0; i < drivers.Count; i++) cboDriver.Items.Add(drivers[i]);
            SetDriverText(keep);
            SyncDriverPick();
        }

        private void FillUsbPorts(List<string> ports)
        {
            string keep = cboUsbPort.Text;
            cboUsbPort.Items.Clear();
            for (int i = 0; i < ports.Count; i++) cboUsbPort.Items.Add(ports[i]);
            cboUsbPort.Text = keep;
            if (keep.Length == 0 && ports.Count > 0) cboUsbPort.Text = ports[0];
            if (_occupiedLabel != null) UpdatePortState(_occupiedLabel);
        }

        private void FillUsbDevices(List<UsbDevice> devices)
        {
            _usbDevices = devices;
            cboUsbDevice.Items.Clear();
            for (int i = 0; i < devices.Count; i++) cboUsbDevice.Items.Add(devices[i].DisplayText);
            if (cboUsbDevice.Items.Count > 0 && cboUsbDevice.SelectedIndex < 0)
            {
                cboUsbDevice.SelectedIndex = 0;
            }
            else if (cboUsbDevice.Items.Count == 0)
            {
                AppendLog("没枚举到 USB 打印设备（线没插或驱动没装）。端口列表仍可手动选。");
            }
        }

        private void OnUsbDevicePicked()
        {
            int i = cboUsbDevice.SelectedIndex;
            if (i < 0 || i >= _usbDevices.Count) return;
            UsbDevice d = _usbDevices[i];

            // 只在 USB 页上把设备信息铺进表单；网络页不该被 USB 设备干扰
            if (tabs.SelectedIndex == 1)
            {
                if (d.Port.Length > 0) cboUsbPort.Text = d.Port;
                if (txtName.Text.Trim().Length == 0 || _nameFromUsb)
                {
                    string name = CleanQueueName(d.FriendlyName);
                    if (name.Length > 0) { txtName.Text = name; _nameFromUsb = true; }
                }
                if (!_driverTouched) PickDriverFor(d.FriendlyName);
            }
            if (_occupiedLabel != null) UpdatePortState(_occupiedLabel);
        }

        private bool _nameFromUsb;
        private bool _driverTouched;
        private bool _driverProgrammatic;

        /// <summary>程序设驱动。带抑制标记，不会把「用户手动改过」误判成已改。</summary>
        private void SetDriverText(string text)
        {
            _driverProgrammatic = true;
            try { cboDriver.Text = text; }
            finally { _driverProgrammatic = false; }
        }

        private void PickDriverFor(string friendlyName)
        {
            if (friendlyName == null || friendlyName.Length == 0) return;
            for (int i = 0; i < _drivers.Count; i++)
            {
                if (string.Equals(_drivers[i], friendlyName, StringComparison.OrdinalIgnoreCase))
                {
                    SetDriverText(_drivers[i]);
                    return;
                }
            }
        }

        private void UpdatePortState(Label target)
        {
            string port = cboUsbPort.Text.Trim();
            if (port.Length == 0) { target.Text = ""; target.ForeColor = Color.FromArgb(90, 90, 90); return; }

            List<string> owners = PrinterSystem.PrintersOnPort(port);
            if (owners.Count == 0)
            {
                target.Text = "空闲，可以直接建新队列";
                target.ForeColor = Color.FromArgb(20, 120, 40);
            }
            else
            {
                var sb = new StringBuilder();
                sb.Append("已被占用：");
                for (int i = 0; i < owners.Count; i++)
                {
                    if (i > 0) sb.Append("、");
                    sb.Append(owners[i]);
                }
                target.Text = sb.ToString();
                target.ForeColor = Color.FromArgb(170, 90, 0);
            }
        }

        private void SyncDriverPick()
        {
            if (cboDriver == null) return;

            if (tabs.SelectedIndex == 0)
            {
                if (_nameFromUsb) { txtName.Text = ""; _nameFromUsb = false; }
                if (!_driverTouched) PickDriverByName("Microsoft IPP Class Driver");
                return;
            }

            int i = cboUsbDevice.SelectedIndex;
            if (i < 0 || i >= _usbDevices.Count) return;
            UsbDevice d = _usbDevices[i];

            if (d.Port.Length > 0 && cboUsbPort.Text.Trim().Length == 0) cboUsbPort.Text = d.Port;
            if (txtName.Text.Trim().Length == 0 || _nameFromUsb)
            {
                string name = CleanQueueName(d.FriendlyName);
                if (name.Length > 0) { txtName.Text = name; _nameFromUsb = true; }
            }
            if (!_driverTouched) PickDriverFor(d.FriendlyName);
            if (_occupiedLabel != null) UpdatePortState(_occupiedLabel);
        }

        private void PickDriverByName(string name)
        {
            for (int i = 0; i < _drivers.Count; i++)
            {
                if (string.Equals(_drivers[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    SetDriverText(_drivers[i]);
                    return;
                }
            }
            if (cboDriver.Text.Trim().Length == 0 && _drivers.Count > 0)
            {
                for (int i = 0; i < _drivers.Count; i++)
                {
                    if (_drivers[i].IndexOf("IPP", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        SetDriverText(_drivers[i]);
                        return;
                    }
                }
            }
        }

        // ---------------- 自动扫描 ----------------

        private static readonly Color ScanIdleColor = Color.FromArgb(90, 90, 90);

        private void OnScanNetwork()
        {
            if (_scanning) return;

            string err;
            List<System.Net.IPAddress> targets = Scanner.ParseTargets(txtRange.Text, out err);
            if (err.Length > 0)
            {
                MessageBox.Show(err, "范围不对", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (targets.Count == 0)
            {
                MessageBox.Show("这个范围里没有地址可扫。\r\n留空表示扫本机所在的网段。", "范围不对",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _scanCancelled = false;
            _foundCount = 0;
            lvFound.Items.Clear();
            SetScanState(true);
            lblScanState.Text = "正在扫描 " + targets.Count + " 个地址…";
            AppendLog("开始扫描 " + targets.Count + " 个地址（631 / 9100 / 515）。");
            int total = targets.Count;

            System.Threading.Thread th = new System.Threading.Thread(delegate()
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    Scanner.Sweep(targets,
                        delegate(DeviceInfo d) { Ui(delegate { AddFound(d); }); },
                        delegate(int done, int all)
                        {
                            Ui(delegate
                            {
                                lblScanState.Text = "扫描中 " + done + "/" + all
                                    + "，已发现 " + _foundCount + " 台…";
                            });
                        },
                        delegate { return _scanCancelled; },
                        350, 1800);
                }
                catch (Exception ex)
                {
                    Ui(delegate { AppendLog("扫描出错：" + ex.Message); });
                }
                sw.Stop();
                Ui(delegate
                {
                    SetScanState(false);
                    lblScanState.Text = _scanCancelled
                        ? "已停止，扫到 " + _foundCount + " 台。"
                        : "扫完 " + total + " 个地址，发现 " + _foundCount + " 台打印设备"
                            + (_foundCount > 0 ? "，点一行即可填好。" : "。可以改范围重扫，或直接手填 IP。");
                    AppendLog((_scanCancelled ? "扫描被停止" : "扫描完成")
                        + "，用时 " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0")
                        + " 秒，发现 " + _foundCount + " 台。");
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        private void SetScanState(bool scanning)
        {
            _scanning = scanning;
            btnScan.Enabled = !scanning;
            btnScan.Text = scanning ? "扫描中…" : "扫描网络";
            btnStop.Enabled = scanning;
            Cursor = scanning ? Cursors.AppStarting : Cursors.Default;
        }

        private void AddFound(DeviceInfo d)
        {
            if (d == null || d.Ip == null) return;
            for (int i = 0; i < lvFound.Items.Count; i++)
            {
                if (string.Equals(lvFound.Items[i].Text, d.Ip, StringComparison.OrdinalIgnoreCase)) return;
            }

            ListViewItem it = new ListViewItem(d.Ip);
            it.Tag = d;
            it.SubItems.Add(d.DisplayName.Length > 0 ? d.DisplayName : "-");
            it.SubItems.Add(string.IsNullOrEmpty(d.Model) ? "-" : d.Model);
            it.SubItems.Add(d.Services);
            lvFound.Items.Add(it);
            _foundCount++;

            AppendLog("发现 " + d.Ip + "  " + d.DisplayName
                + (string.IsNullOrEmpty(d.Model) ? "" : "  [" + d.Model + "]")
                + "  " + d.Services);
        }

        /// <summary>点中扫到的设备：把地址、端口、名称、驱动都填好。</summary>
        private void OnFoundPicked()
        {
            if (_picking || lvFound.SelectedItems.Count == 0) return;
            DeviceInfo d = lvFound.SelectedItems[0].Tag as DeviceInfo;
            if (d == null) return;

            _picking = true;
            try
            {
                txtHost.Text = d.Ip;

                string existing = PrinterSystem.FindTcpPortByHost(d.Ip);
                txtPort.Text = existing.Length > 0 ? existing : MakePortName(d.Ip);

                if (txtName.Text.Trim().Length == 0 || _nameFromNet)
                {
                    string name = SuggestName(d, d.Ip);
                    if (name.Length > 0) { txtName.Text = name; _nameFromNet = true; }
                }
                if (!_driverTouched) PickDriverByName("Microsoft IPP Class Driver");

                _probed = d;
                txtProbe.Text = Describe(d, d.Ip);
                AppendLog("已选中 " + d.Ip + "，地址/端口/名称已填好，点「添加」即可。");
            }
            finally
            {
                _picking = false;
            }
        }

        private bool _picking;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _scanCancelled = true;
            base.OnFormClosing(e);
        }

        // ---------------- 网络检测 ----------------

        private void OnProbe()
        {
            string raw = txtHost.Text.Trim();
            if (raw.Length == 0)
            {
                AppendLog("先在地址栏填打印机 IP（例如 192.168.1.100）或主机名。");
                return;
            }

            if (raw.StartsWith("\\\\"))
            {
                txtProbe.Text = "这是共享打印机路径：" + raw + "\r\n直接点「添加」即可按共享方式连接，不需要检测。";
                if (txtPort.Text.Trim().Length == 0) txtPort.Text = "(共享，不用端口)";
                return;
            }

            string ip = PrinterSystem.ResolveAddress(raw);
            if (ip.Length == 0 || ip == raw && !IsIpv4(raw))
            {
                txtProbe.Text = "解析不到 IPv4 地址：" + raw + "\r\n确认拼写，或直接用 IP。";
                AppendLog("解析失败：" + raw);
                return;
            }

            btnProbe.Enabled = false;
            Cursor = Cursors.WaitCursor;
            AppendLog("正在检测 " + ip + " …");
            try
            {
                DeviceInfo d = Scanner.ProbeHost(ip, 1200, 2500);
                _probed = d;
                if (d == null)
                {
                    txtProbe.Text = "这个地址没有任何打印端口响应（631 / 9100 / 515 都不通）。\r\n\r\n"
                        + "可能原因：打印机没开机、网线/WiFi 断了，或者它又换了 IP。\r\n"
                        + "打印机 IP 漂移是常态 —— 回主界面点「扫描网络设备」把在线设备找出来，再把新地址填到这里。";
                    AppendLog("检测完成：无响应 " + ip);
                    return;
                }
                txtProbe.Text = Describe(d, ip);

                if (txtPort.Text.Trim().Length == 0)
                {
                    string existing = PrinterSystem.FindTcpPortByHost(ip);
                    txtPort.Text = existing.Length > 0 ? existing : MakePortName(ip);
                }
                if (txtName.Text.Trim().Length == 0 || _nameFromNet)
                {
                    string name = SuggestName(d, ip);
                    txtName.Text = name;
                    _nameFromNet = true;
                }
                if (!_driverTouched) PickDriverByName("Microsoft IPP Class Driver");
                AppendLog("检测完成：" + ip + "  服务=" + d.Services);
            }
            catch (Exception ex)
            {
                txtProbe.Text = "检测出错：" + ex.Message;
                AppendLog("检测出错：" + ex.Message);
            }
            finally
            {
                btnProbe.Enabled = true;
                Cursor = Cursors.Default;
            }
        }

        private bool _nameFromNet;

        private static bool IsIpv4(string text)
        {
            System.Net.IPAddress addr;
            return System.Net.IPAddress.TryParse(text, out addr)
                && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        }

        private static string Describe(DeviceInfo d, string ip)
        {
            var sb = new StringBuilder();
            sb.Append("地址：" + ip + "    开放服务：" + d.Services);
            if (d.RttMs >= 0) sb.Append("    往返 " + d.RttMs + " ms");
            sb.Append("\r\n");

            if (d.HasIpp)
            {
                sb.Append("设备名：" + (d.IppName.Length > 0 ? d.IppName : "(未报)" ) + "\r\n");
                sb.Append("型号：" + (d.Model.Length > 0 ? d.Model : "(未报)") );
                if (d.Serial.Length > 0) sb.Append("    序列号：" + d.Serial);
                sb.Append("\r\n");
                string reasons = d.StateReasons;
                if (reasons.Length > 0 && !string.Equals(reasons, "none", StringComparison.OrdinalIgnoreCase))
                    sb.Append("设备状态：" + reasons + "（可能需要看耗材/卡纸）\r\n");
                else
                    sb.Append("设备状态：就绪\r\n");
            }
            else if (d.Has9100 || d.HasLpd)
            {
                sb.Append("这台设备没有开放 IPP（631），但有 9100 原始端口，仍可添加（走 RAW 打印）。\r\n");
            }
            else
            {
                sb.Append("这个地址没有响应任何打印端口。检查电源、网线、以及是否和电脑在同一网段。\r\n");
            }

            if (d.Error.Length > 0) sb.Append("备注：" + d.Error);
            return sb.ToString();
        }

        private static string MakePortName(string ip)
        {
            return "IP_" + ip.Replace(':', '_');
        }

        private static string SuggestName(DeviceInfo d, string ip)
        {
            string name = CleanQueueName(d.IppName);
            if (name.Length == 0) name = CleanQueueName(d.Model);
            if (name.Length == 0) name = "网络打印机 " + ip;
            return name;
        }

        private static string CleanQueueName(string raw)
        {
            if (raw == null) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '\\' || c == '/' || c == ',' || c == ';' || c == '|' || c == '"' || c < ' ') continue;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        // ---------------- 添加 ----------------

        private void OnAdd()
        {
            string name = CleanQueueName(txtName.Text);
            if (name.Length == 0)
            {
                AppendLog("队列名不能为空。");
                txtName.Focus();
                return;
            }
            if (PrinterSystem.PrinterExists(name))
            {
                AppendLog("本机已经有同名队列：" + name + "。换个名字，或直接在列表里用它。");
                txtName.Focus();
                return;
            }

            string driver = cboDriver.Text.Trim();
            if (driver.Length == 0)
            {
                AppendLog("先选一个打印驱动。网络打印机一般用 Microsoft IPP Class Driver；USB 选和设备同名的驱动。");
                cboDriver.Focus();
                return;
            }

            btnAdd.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                bool ok = (tabs.SelectedIndex == 0) ? AddNetwork(name, driver) : AddUsb(name, driver);
                if (!ok) return;

                if (chkDefault.Checked)
                {
                    string err;
                    if (!PrinterSystem.SetDefaultPrinter(name, out err))
                        AppendLog("设为默认失败：" + err);
                    else
                        AppendLog("已设为默认打印机：" + name);
                }

                CreatedQueue = name;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                AppendLog("添加出错：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                btnAdd.Enabled = true;
                Cursor = Cursors.Default;
            }
        }

        private bool AddNetwork(string name, string driver)
        {
            string raw = txtHost.Text.Trim();
            if (raw.Length == 0)
            {
                AppendLog("先填打印机地址。");
                return false;
            }

            if (raw.StartsWith("\\\\"))
            {
                string msg;
                bool okShare = PrinterSystem.ConnectSharedPrinter(raw, out msg);
                AppendLog(msg);
                return okShare;
            }

            string ip = PrinterSystem.ResolveAddress(raw);
            if (ip.Length == 0)
            {
                AppendLog("解析不到 IP：" + raw);
                return false;
            }

            string port = txtPort.Text.Trim();
            if (port.Length == 0) port = MakePortName(ip);

            // 建端口之前先问设备：叫什么、支持哪些文档格式、UUID 是多少。这一步是必要的：
            // 标准 TCP/IP 端口（RAW 9100）只能把文档原样推给设备，而 Windows 的
            // Microsoft IPP Class Driver 在 RAW 端口上渲染出来的是 PDF。设备不支持 PDF 时
            // 会把作业静默丢掉，spooler 却照样报「打印成功、1 页」—— 这就是「电脑说打完了、
            // 打印机没动静」的根子。所以：先探测；如果本机已经有这台设备的发现型端口
            // （WSD / IPP，按 printer-uuid 认），就直接复用它，别再造一条 RAW 假队列。
            DeviceInfo probe = new DeviceInfo();
            probe.Ip = ip;
            try { Ipp.Probe(probe, 6000); }
            catch (Exception ex) { AppendLog("探测设备失败：" + ex.Message); }

            if (probe.FormatsProbed && probe.Formats.Length > 0)
            {
                AppendLog("设备名称：" + (probe.IppName.Length > 0 ? probe.IppName : "-")
                    + "    型号：" + (probe.Model.Length > 0 ? probe.Model : "-"));
                AppendLog("设备支持的文档格式：" + probe.Formats);
                if (!probe.SupportsPdf)
                {
                    AppendLog("⚠ 这台设备不接收 PDF。用 Microsoft IPP Class Driver + 标准 TCP/IP 端口（RAW 9100）"
                        + "建出来的队列，会出现「电脑提示打印成功、打印机不出纸」。");
                }
            }
            else
            {
                AppendLog("设备没有回应格式列表（" + Ipp.LastNote + "）。按常规建端口，若不出纸请用『IPP 直发』复查。");
            }

            // 关键一步：设备不收 PDF 时，优先复用本机已有的发现型端口。
            // 发现型端口（WSD / IPP）按设备身份绑定，会跟设备协商格式，是唯一能真出纸的
            // Windows 通道；RAW 9100 对这类设备只会造假成功。
            string reusePort = "";
            if (probe.FormatsProbed && !probe.SupportsPdf && probe.Uuid.Length > 0)
            {
                try { reusePort = IpRebind.FindWsdPort(probe.Uuid); }
                catch (Exception ex) { AppendLog("查找发现型端口失败：" + ex.Message); }
                if (reusePort.Length > 0)
                {
                    AppendLog("本机已经有这台设备的发现型端口：" + reusePort + "  —— 复用它，不建 RAW 9100 端口。");
                    port = reusePort;
                }
                else
                {
                    AppendLog("本机还没有这台设备的发现型端口。");
                    AppendLog("建议：先在系统「设置 → 蓝牙和其他设备 → 打印机和扫描仪 → 添加打印机」"
                        + "让 Windows 自己发现并添加这台设备（那条走 IPP 协商，能正常出纸）；"
                        + "或者直接用本工具主界面的『IPP 直发』把文件送过去。");
                    AppendLog("如果仍要继续用标准 TCP/IP 端口，请改选打印机厂商的完整驱动（不要用 Microsoft IPP Class Driver），"
                        + "否则大概率还是不出纸。");
                }
            }

            bool needPort = (reusePort.Length == 0);
            if (needPort)
            {
                if (PrinterSystem.PortExists(port))
                {
                    AppendLog("端口已存在，直接复用：" + port);
                }
                else
                {
                    AppendLog("新建端口 " + port + " → " + ip + ":9100");
                    bool okPort = PrinterSystem.CreateTcpPort(port, ip, 9100);
                    for (int i = 0; i < PrinterSystem.LastPortLog.Count; i++)
                        AppendLog("  " + PrinterSystem.LastPortLog[i]);
                    if (!okPort && !PrinterSystem.PortExists(port))
                    {
                        AppendLog("端口没建成。若日志是拒绝访问，用『以管理员身份运行』再试一次。");
                        return false;
                    }
                }
            }

            string err;
            bool ok = PrinterSystem.CreateQueue(name, driver, port, out err);
            for (int i = 0; i < PrinterSystem.LastAddLog.Count; i++) AppendLog("  " + PrinterSystem.LastAddLog[i]);
            AppendLog(err);
            if (ok)
            {
                // 记进绑定表，以后设备换 IP 时「校准IP」能按身份认回来。
                try { IpRebind.Remember(name, probe, port); }
                catch { }
            }
            return ok;
        }

        private bool AddUsb(string name, string driver)
        {
            string port = cboUsbPort.Text.Trim();
            if (port.Length == 0)
            {
                AppendLog("先选一个 USB 端口（USB001 这类）。如果列表是空的，说明打印机没插好或没被识别。");
                return false;
            }

            List<string> owners = PrinterSystem.PrintersOnPort(port);
            if (owners.Count > 0)
            {
                var sb = new StringBuilder();
                sb.Append("端口 " + port + " 已被占用：");
                for (int i = 0; i < owners.Count; i++)
                {
                    if (i > 0) sb.Append("、");
                    sb.Append(owners[i]);
                }
                AppendLog(sb.ToString() + "。一台设备只需要一个队列，直接用原来那个即可。");
                return false;
            }

            AppendLog("建队列 " + name + "（端口 " + port + "，驱动 " + driver + "）");
            string err;
            bool ok = PrinterSystem.CreateQueue(name, driver, port, out err);
            for (int i = 0; i < PrinterSystem.LastAddLog.Count; i++) AppendLog("  " + PrinterSystem.LastAddLog[i]);
            AppendLog(err);
            return ok;
        }

        // ---------------- 杂项 ----------------

        private void AppendLog(string text)
        {
            if (text == null || text.Length == 0) return;
            txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + "\r\n");
        }

        private void Ui(MethodInvoker action)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch { }
        }
    }
}
