using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PrinterIpFix
{
    /// <summary>
    /// 「驱动修复」对话框：三件事合在一起 ——
    ///   ① 一键查/下载/安装官方驱动（联网，按型号搜，落到厂商官方源）
    ///   ② 手动搜索驱动（自己敲关键词，看结果、可下载、可打开详情页）
    ///   ③ 兼容性判定（这台设备认什么语言 / 当前驱动输出什么语言 / 要不要换）
    ///
    /// 全套逻辑不含任何品牌型号硬编码：型号从设备自己报的 printer-make-and-model 来，
    /// 厂商入口按厂商名映射（映射不到就给通用入口），驱动库按本机实际安装的驱动枚举。
    /// </summary>
    internal sealed class DriverRepairForm : Form
    {
        private TextBox txtKeyword;
        private Button btnSearch;
        private Button btnVendorSite;
        private ListView lsvResults;
        private TextBox txtLog;
        private Label lblCompat;
        private Button btnCompat;
        private Button btnAutoFix;

        private readonly string _model;
        private readonly string _ip;
        private readonly string _port;

        private List<DriverDownload> _found = new List<DriverDownload>();
        private int _seq;

        public DriverRepairForm(string modelHint, string ipHint, string portHint)
        {
            _model = modelHint == null ? "" : modelHint.Trim();
            _ip = ipHint == null ? "" : ipHint.Trim();
            _port = portHint == null ? "" : portHint.Trim();

            Text = "驱动修复";
            ClientSize = new Size(980, 660);
            MinimumSize = new Size(820, 540);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9F);

            BuildUi();
        }

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));   // 搜索行
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72F));   // 兼容判定行
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 结果表
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170F));  // 日志
            root.Padding = new Padding(10, 8, 10, 8);

            // ---- 第一行：关键词 + 搜索 + 厂商官网 ----
            Panel row1 = new Panel();
            row1.Dock = DockStyle.Fill;

            Label lbl = new Label();
            lbl.Dock = DockStyle.Left;
            lbl.Width = 68;
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            lbl.Text = "型号关键词";

            txtKeyword = new TextBox();
            txtKeyword.Dock = DockStyle.Fill;
            txtKeyword.Text = _model.Length > 0 ? _model : "输入打印机型号，例如 OfficeJet Pro 9730";

            btnVendorSite = new Button();
            btnVendorSite.Dock = DockStyle.Right;
            btnVendorSite.Width = 108;
            btnVendorSite.Text = "厂商官网";
            btnVendorSite.Click += delegate { OpenVendorSite(); };

            btnSearch = new Button();
            btnSearch.Dock = DockStyle.Right;
            btnSearch.Width = 108;
            btnSearch.Text = "搜索驱动";
            btnSearch.Click += delegate { DoSearch(); };

            row1.Controls.Add(txtKeyword);
            row1.Controls.Add(lbl);
            row1.Controls.Add(btnSearch);
            row1.Controls.Add(btnVendorSite);
            root.Controls.Add(row1, 0, 0);

            // ---- 第二行：兼容性判定 ----
            Panel row2 = new Panel();
            row2.Dock = DockStyle.Fill;

            lblCompat = new Label();
            lblCompat.Dock = DockStyle.Fill;
            lblCompat.TextAlign = ContentAlignment.MiddleLeft;
            lblCompat.ForeColor = Color.FromArgb(70, 70, 80);
            lblCompat.Text = _ip.Length > 0
                ? ("设备 " + _ip + (_port.Length > 0 ? "（端口 " + _port + "）" : "") + "：点「判定兼容」看这台机器认什么语言。")
                : "没有选中设备。先回主界面扫描并选中一台设备，再判定兼容。";

            btnAutoFix = new Button();
            btnAutoFix.Dock = DockStyle.Right;
            btnAutoFix.Width = 120;
            btnAutoFix.Text = "自动换驱动";
            btnAutoFix.Enabled = false;
            btnAutoFix.Click += delegate { DoAutoFix(); };

            btnCompat = new Button();
            btnCompat.Dock = DockStyle.Right;
            btnCompat.Width = 108;
            btnCompat.Text = "判定兼容";
            btnCompat.Enabled = _ip.Length > 0;
            btnCompat.Click += delegate { DoCompat(); };

            row2.Controls.Add(lblCompat);
            row2.Controls.Add(btnCompat);
            row2.Controls.Add(btnAutoFix);
            root.Controls.Add(row2, 0, 1);

            // ---- 第三行：结果表 ----
            lsvResults = new ListView();
            lsvResults.Dock = DockStyle.Fill;
            lsvResults.View = View.Details;
            lsvResults.FullRowSelect = true;
            lsvResults.GridLines = true;
            lsvResults.HideSelection = false;
            lsvResults.Columns.Add("驱动标题", 380);
            lsvResults.Columns.Add("真实下载地址", 540);
            lsvResults.DoubleClick += delegate { OpenSelectedPage(); };

            Panel row3 = new Panel();
            row3.Dock = DockStyle.Fill;

            FlowLayoutPanel acts = new FlowLayoutPanel();
            acts.Dock = DockStyle.Top;
            acts.Height = 38;
            acts.WrapContents = false;
            acts.FlowDirection = FlowDirection.LeftToRight;

            Button btnDownInstall = MakeButton("下载并安装", 108);
            btnDownInstall.Click += delegate { DownloadAndInstall(); };
            Button btnDownOnly = MakeButton("仅下载", 84);
            btnDownOnly.Click += delegate { DownloadOnly(); };
            Button btnPage = MakeButton("打开详情页", 100);
            btnPage.Click += delegate { OpenSelectedPage(); };
            Button btnFolder = MakeButton("下载目录", 92);
            btnFolder.Click += delegate { OpenDownloadDir(); };
            Button btnRecheck = MakeButton("刷新兼容", 92);
            btnRecheck.Click += delegate { DoCompat(); };

            acts.Controls.Add(btnDownInstall);
            acts.Controls.Add(btnDownOnly);
            acts.Controls.Add(btnPage);
            acts.Controls.Add(btnFolder);
            acts.Controls.Add(btnRecheck);

            row3.Controls.Add(lsvResults);
            row3.Controls.Add(acts);
            root.Controls.Add(row3, 0, 2);

            // ---- 第四行：日志 ----
            txtLog = new TextBox();
            txtLog.Dock = DockStyle.Fill;
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor = Color.FromArgb(250, 250, 252);
            txtLog.Font = new Font("Consolas", 9F);
            root.Controls.Add(txtLog, 0, 3);

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
            if (txtLog == null) return;
            txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s + "\r\n");
        }

        // ================= 搜索 =================

        private void DoSearch()
        {
            string kw = txtKeyword.Text.Trim();
            if (kw.Length == 0) { Log("先填型号关键词。"); return; }

            // 关键词里常混着「惠普HP OfficeJet Pro 9730」这种，厂商与型号拆开更准。
            string vendor = GuessVendor(kw);
            string model = kw;

            int seq = ++_seq;
            btnSearch.Enabled = false;
            Log("正在站点上搜「" + kw + "」…");

            Thread th = new Thread(delegate()
            {
                List<DriverDownload> list = null;
                string err = "";
                try { list = DriverOnline.Find(vendor, model, 20000, out err); }
                catch (Exception ex) { err = ex.GetType().Name + " " + ex.Message; }

                Ui(delegate
                {
                    if (seq != _seq) return;
                    btnSearch.Enabled = true;
                    _found = list == null ? new List<DriverDownload>() : list;
                    FillResults();
                    if (_found.Count == 0)
                    {
                        Log("没找到可下载的驱动。" + err);
                        Log("可点「厂商官网」直接去官方下载页，或在关键词里只留型号再搜一次。");
                    }
                    else
                    {
                        Log("找到 " + _found.Count + " 条。双击某条打开详情页；点「下载并安装」走一键流程。");
                    }
                    if (DriverOnline.LastNote.Length > 0) Log("  " + DriverOnline.LastNote);
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        private void FillResults()
        {
            lsvResults.BeginUpdate();
            try
            {
                lsvResults.Items.Clear();
                for (int i = 0; i < _found.Count; i++)
                {
                    DriverDownload d = _found[i];
                    ListViewItem it = new ListViewItem(d.Title.Length > 0 ? d.Title : d.LinkUrl);
                    it.SubItems.Add(d.RealUrl.Length > 0 ? d.RealUrl : d.LinkUrl);
                    it.Tag = d;
                    lsvResults.Items.Add(it);
                }
            }
            finally { lsvResults.EndUpdate(); }
        }

        private static string GuessVendor(string kw)
        {
            string up = kw.ToUpperInvariant();
            if (up.IndexOf("HP") >= 0 || up.IndexOf("惠普") >= 0 || up.IndexOf("HEWLETT") >= 0) return "HP";
            if (up.IndexOf("CANON") >= 0 || up.IndexOf("佳能") >= 0) return "Canon";
            if (up.IndexOf("EPSON") >= 0 || up.IndexOf("爱普生") >= 0) return "Epson";
            if (up.IndexOf("BROTHER") >= 0 || up.IndexOf("兄弟") >= 0) return "Brother";
            if (up.IndexOf("LENOVO") >= 0 || up.IndexOf("联想") >= 0) return "Lenovo";
            if (up.IndexOf("PANTUM") >= 0 || up.IndexOf("奔图") >= 0) return "Pantum";
            if (up.IndexOf("RICOH") >= 0 || up.IndexOf("理光") >= 0) return "Ricoh";
            return "";
        }

        private DriverDownload Selected()
        {
            if (lsvResults.SelectedItems.Count == 0) return null;
            return lsvResults.SelectedItems[0].Tag as DriverDownload;
        }

        private void OpenSelectedPage()
        {
            DriverDownload d = Selected();
            if (d == null) { Log("先选中一条结果。"); return; }
            string url = d.PageUrl.Length > 0 ? d.PageUrl : (d.RealUrl.Length > 0 ? d.RealUrl : d.LinkUrl);
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Log("已用浏览器打开：" + url);
            }
            catch (Exception ex) { Log("打不开：" + ex.Message); }
        }

        private void OpenVendorSite()
        {
            string kw = txtKeyword.Text.Trim();
            string url = DriverOnline.Portal(GuessVendor(kw), kw);
            if (url.Length == 0)
            {
                Log("这台机器的厂商官网页没有内置（只用内置几家大厂的入口）。");
                Log("请到厂商官网 → 支持/下载 → 输入型号，或用上面的搜索结果里的详情页。");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Log("已打开厂商官网搜索页：" + url);
            }
            catch (Exception ex) { Log("打不开：" + ex.Message); }
        }

        private void OpenDownloadDir()
        {
            try
            {
                string dir = DriverOnline.DownloadDir();
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                Log("下载目录：" + dir);
            }
            catch (Exception ex) { Log("打不开下载目录：" + ex.Message); }
        }

        // ================= 下载 / 安装 =================

        private void DownloadOnly()
        {
            DriverDownload d = Selected();
            if (d == null) { Log("先选中一条结果。"); return; }
            string url = d.RealUrl.Length > 0 ? d.RealUrl : d.LinkUrl;
            StartDownload(url, false);
        }

        private void DownloadAndInstall()
        {
            DriverDownload d = Selected();
            if (d == null) { Log("先选中一条结果。"); return; }
            string url = d.RealUrl.Length > 0 ? d.RealUrl : d.LinkUrl;

            if (MessageBox.Show(
                "从站点下这个驱动包并尝试安装？\r\n\r\n" + url
                + "\r\n\r\n流程：下载 → （压缩包则解压）→ 找 .inf → pnputil 安装。"
                + "\r\n装驱动需要管理员权限；权限不够时会自动改成「打开安装包让你手工装」。",
                "确认下载并安装", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            StartDownload(url, true);
        }

        private void StartDownload(string url, bool thenInstall)
        {
            if (url.Length == 0) { Log("没有可用的下载地址。"); return; }
            btnSearch.Enabled = false;

            Thread th = new Thread(delegate()
            {
                string error = "";
                string saved = "";
                try
                {
                    string dir = DriverOnline.DownloadDir();
                    Directory.CreateDirectory(dir);
                    string name = DriverOnline.SuggestedFile(url);
                    string dest = Path.Combine(dir, name);
                    Log("开始下载：" + url);
                    Log("  保存到：" + dest);

                    long lastPct = -1;
                    Action<long, long> progress = delegate(long got, long total)
                    {
                        if (total <= 0) return;
                        long pct = got * 100 / total;
                        if (pct == lastPct || (pct % 10) != 0) return;
                        lastPct = pct;
                        Ui(delegate { Log("  进度 " + pct + "%（" + (got / 1024) + " / " + (total / 1024) + " KB）"); });
                    };

                    bool ok = DriverOnline.Download(url, dest, 60000, progress, out error);
                    if (!ok)
                    {
                        Ui(delegate { btnSearch.Enabled = true; Log("下载失败：" + error); });
                        return;
                    }
                    saved = dest;
                    Ui(delegate { Log("下载完成：" + new FileInfo(dest).Length + " 字节"); });
                    if (DriverOnline.LastNote.Length > 0) Ui(delegate { Log("  " + DriverOnline.LastNote); });

                    if (!thenInstall)
                    {
                        Ui(delegate { btnSearch.Enabled = true; });
                        return;
                    }

                    DoInstall(saved);
                }
                catch (Exception ex)
                {
                    string m = ex.GetType().Name + " " + ex.Message;
                    Ui(delegate { btnSearch.Enabled = true; Log("出错：" + m); });
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void DoInstall(string file)
        {
            string kind = DriverOnline.Kind(file);
            Ui(delegate { Log("文件类型：" + kind); });

            string infPath = "";
            string workDir = file;

            if (kind == "zip" || kind == "cab")
            {
                string outDir = "";
                string err = "";
                bool ok = DriverOnline.Expand(file, out outDir, out err);
                Ui(delegate
                {
                    if (ok) Log("已解压到：" + outDir);
                    else Log("解压失败（" + err + "），改为直接打开让安装程序自己释放。");
                });
                if (ok) workDir = outDir;
            }

            if (kind == "exe" || kind == "other" || workDir == file)
            {
                infPath = Directory.Exists(workDir) ? DriverOnline.FindInf(workDir) : "";
            }

            if (infPath.Length == 0 && Directory.Exists(workDir))
                infPath = DriverOnline.FindInf(workDir);

            if (infPath.Length > 0)
            {
                Ui(delegate { Log("找到 INF：" + infPath); });
                string err = "";
                bool ok = DriverStore.InstallInf(infPath, out err);
                Ui(delegate
                {
                    if (ok)
                    {
                        Log("驱动已装进系统。" + err);
                        Log("回到主界面点「刷新队列」「重新探测」就能看到新驱动。");
                        btnSearch.Enabled = true;
                    }
                    else
                    {
                        Log("装 INF 失败：" + err);
                        Log("多半是需要管理员权限。改成打开安装包，你在向导里点下一步完成安装。");
                        LaunchInstaller(file);
                    }
                });
                return;
            }

            // 没有 INF（厂商只给 EXE 安装向导）—— 直接打开让用户点下一步
            Ui(delegate
            {
                Log("这个包里没找到 .inf（厂商给的是安装向导）。");
                LaunchInstaller(file);
            });
        }

        private void LaunchInstaller(string file)
        {
            string err = "";
            bool ok = DriverOnline.Launch(file, out err);
            if (ok) Log("已启动安装程序，请在弹出的向导里完成安装。");
            else Log("启动安装程序失败：" + err);
            btnSearch.Enabled = true;
        }

        // ================= 兼容判定 =================

        private void DoCompat()
        {
            if (_ip.Length == 0) { Log("没有设备 IP，判不了。回主界面选中一台设备。"); return; }
            btnCompat.Enabled = false;
            Log("正在判定 " + _ip + " 的语言兼容性（会真打一张测试页抓驱动输出）…");

            Thread th = new Thread(delegate()
            {
                List<string> langs = new List<string>();
                string raw = "";
                try { langs = DriverCompat.DeviceLanguages(_ip, 6000, out raw); }
                catch { }

                string curDriver = "";
                string curLang = "";
                try
                {
                    List<PrinterInfo> qs = PrinterSystem.GetPrinters();
                    for (int i = 0; i < qs.Count; i++)
                    {
                        if (_port.Length > 0 && string.Equals(qs[i].PortName, _port, StringComparison.OrdinalIgnoreCase))
                        { curDriver = qs[i].DriverName; break; }
                    }
                }
                catch { }

                if (curDriver.Length == 0 && _port.Length > 0)
                {
                    try
                    {
                        foreach (PrinterInfo q in PrinterSystem.GetPrinters())
                        {
                            if (string.Equals(q.PortName, _port, StringComparison.OrdinalIgnoreCase))
                            { curDriver = q.DriverName; break; }
                        }
                    }
                    catch { }
                }

                string detail = "";
                if (curDriver.Length > 0)
                {
                    try { curLang = DriverCompat.CaptureDriverLanguage(curDriver, 20000, out detail); }
                    catch { detail = "抓包异常"; }
                }

                Ui(delegate
                {
                    btnCompat.Enabled = true;
                    if (langs.Count == 0)
                    {
                        Log("设备没有回应语言表（9100 上 @PJL INFO CONFIG 无输出）。");
                        Log("这台机器可能不认 PJL —— 那就别指望 RAW 队列，直接用「IPP 直发」。");
                        lblCompat.Text = "设备没有上报语言表：只能用 IPP 直发。";
                        lblCompat.ForeColor = Color.FromArgb(170, 80, 0);
                        btnAutoFix.Enabled = false;
                        return;
                    }
                    Log("设备认：" + string.Join("/", langs.ToArray()));
                    if (curDriver.Length > 0)
                    {
                        Log("当前驱动：" + curDriver);
                        Log("实测输出语言：" + (curLang.Length == 0 ? "（没抓到）" : curLang) + " · " + detail);
                    }
                    else
                    {
                        Log("没找到这个端口对应的队列，跳过驱动抓包。");
                    }
                    string verdict = DriverCompat.VerdictText(langs, curLang);
                    Log(verdict);
                    lblCompat.Text = verdict;
                    bool okOne = curLang.Length > 0 && DriverCompat.Compatible(langs, curLang);
                    lblCompat.ForeColor = okOne ? Color.FromArgb(20, 120, 40) : Color.FromArgb(170, 80, 0);
                    btnAutoFix.Enabled = !okOne && curDriver.Length > 0;
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        private void DoAutoFix()
        {
            if (MessageBox.Show(
                "自动换驱动会做这些事：\r\n"
                + "1) 逐个试本机已装驱动，各真打一张测试页抓输出语言；\r\n"
                + "2) 找到设备认的那个就原地把队列换过去（Set-Printer -DriverName）；\r\n"
                + "3) 换不上就告诉你只能走 IPP 直发。\r\n\r\n"
                + "期间会真的出几张测试纸。继续？",
                "自动换驱动", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            btnAutoFix.Enabled = false;
            btnCompat.Enabled = false;

            Thread th = new Thread(delegate()
            {
                bool ok = false;
                try
                {
                    string curDriver = "";
                    PrinterInfo target = null;
                    List<PrinterInfo> qs = PrinterSystem.GetPrinters();
                    for (int i = 0; i < qs.Count; i++)
                    {
                        if (string.Equals(qs[i].PortName, _port, StringComparison.OrdinalIgnoreCase)) { target = qs[i]; break; }
                    }
                    if (target == null)
                    {
                        Ui(delegate { Log("找不到这个端口对应的队列，无法自动换驱动。"); btnBest(true); });
                        return;
                    }
                    curDriver = target.DriverName;

                    DeviceInfo dev = new DeviceInfo();
                    dev.Ip = _ip;
                    try { Ipp.Probe(dev, 6000); } catch { }

                    ok = DriverCompat.AutoCalibrate(target, dev, delegate(string m) { Ui(delegate { Log(m); }); });
                }
                catch (Exception ex)
                {
                    string m = ex.GetType().Name + " " + ex.Message;
                    Ui(delegate { Log("自动换驱动出错：" + m); });
                }

                Ui(delegate
                {
                    btnBest(true);
                    Log(DriverCompat.LastNote);
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        private void btnBest(bool enable)
        {
            btnAutoFix.Enabled = enable;
            btnCompat.Enabled = enable && _ip.Length > 0;
        }

        private void Ui(Action a)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke((MethodInvoker)delegate { try { a(); } catch { } });
                else a();
            }
            catch { }
        }
    }
}
