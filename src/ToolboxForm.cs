using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace PrinterIpFix
{
    /// <summary>打印机工具箱主窗口：四个页签。</summary>
    internal sealed class ToolboxForm : Form
    {
        private MainForm embedded;
        private DriverPanel driverPanel;
        private Label status;

        public ToolboxForm()
        {
            Text = "打印机工具箱";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1180, 740);
            MinimumSize = new Size(960, 620);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(245, 246, 248);

            try
            {
                string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                if (File.Exists(ico)) Icon = new Icon(ico);
            }
            catch { }

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Font = new Font("Microsoft YaHei UI", 9F);

            // --- 页签 1：IP 校准 / 队列（复用原来的主窗体） ---
            embedded = new MainForm();
            embedded.TopLevel = false;
            embedded.FormBorderStyle = FormBorderStyle.None;
            embedded.Dock = DockStyle.Fill;
            TabPage tp1 = new TabPage("校准 / 队列");
            tp1.BackColor = Color.White;
            tp1.Controls.Add(embedded);
            tabs.TabPages.Add(tp1);

            // --- 页签 2：共享修复 ---
            TabPage tp2 = new TabPage("共享修复");
            tp2.BackColor = Color.White;
            tp2.Controls.Add(new SharedFixPanel());
            tabs.TabPages.Add(tp2);

            // --- 页签 3：驱动检测 ---
            driverPanel = new DriverPanel();
            TabPage tp3 = new TabPage("驱动检测");
            tp3.BackColor = Color.White;
            tp3.Controls.Add(driverPanel);
            tabs.TabPages.Add(tp3);

            // --- 页签 4：说明 ---
            TabPage tp4 = new TabPage("说明");
            tp4.BackColor = Color.White;
            tp4.Controls.Add(BuildAbout());
            tabs.TabPages.Add(tp4);

            Controls.Add(tabs);

            Shown += delegate
            {
                try { embedded.Show(); }
                catch { }
                if (driverPanel != null) driverPanel.ScanDevices();
            };
        }

        private Control BuildAbout()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            p.Padding = new Padding(14);

            TextBox t = new TextBox();
            t.Dock = DockStyle.Fill;
            t.Multiline = true;
            t.ReadOnly = true;
            t.ScrollBars = ScrollBars.Vertical;
            t.BorderStyle = BorderStyle.None;
            t.BackColor = Color.White;
            t.Font = new Font("Microsoft YaHei UI", 9.5F);
            t.Text = AboutText();

            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = 40;
            bar.FlowDirection = FlowDirection.LeftToRight;

            Button b1 = new Button();
            b1.Text = "打开程序目录";
            b1.Width = 130;
            b1.Height = 28;
            b1.Margin = new Padding(0, 6, 8, 0);
            b1.Click += delegate
            {
                try { Process.Start(new ProcessStartInfo(AppDomain.CurrentDomain.BaseDirectory) { UseShellExecute = true }); }
                catch { }
            };

            Button b2 = new Button();
            b2.Text = "打开数据目录";
            b2.Width = 130;
            b2.Height = 28;
            b2.Margin = new Padding(0, 6, 8, 0);
            b2.Click += delegate
            {
                try
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "PrinterToolbox");
                    Directory.CreateDirectory(dir);
                    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                }
                catch { }
            };

            status = new Label();
            status.AutoSize = false;
            status.Width = 520;
            status.Height = 28;
            status.Margin = new Padding(6, 6, 0, 0);
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.ForeColor = Color.FromArgb(90, 90, 90);
            status.Text = "管理员：" + (PrinterSystem.IsAdmin() ? "有" : "没有")
                        + "　完整性：" + PrinterSystem.IntegrityText()
                        + "　版本：1.0.0.0";

            bar.Controls.Add(b1);
            bar.Controls.Add(b2);
            bar.Controls.Add(status);

            p.Controls.Add(t);
            p.Controls.Add(bar);
            return p;
        }

        private static string AboutText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("打印机工具箱　1.0");
            sb.AppendLine();
            sb.AppendLine("四个页签，各管一件事：");
            sb.AppendLine();
            sb.AppendLine("【校准 / 队列】");
            sb.AppendLine("· 扫描本网段，按 UUID / 序列号 / 型号认出网络打印机；队列端口指错地址时一键改到正确 IP。");
            sb.AppendLine("· 添加打印机（网络 IP、共享路径、USB），删除队列，设为默认，打测试页。");
            sb.AppendLine("· 所有写操作走多条路线（Add-PrinterPort / prnport.vbs / WMI），每条失败原因都写进日志。");
            sb.AppendLine();
            sb.AppendLine("【共享修复】");
            sb.AppendLine("· 九项常用的共享/打印服务修复，每项都标了副作用等级。");
            sb.AppendLine("· 改注册表前自动备份原值到 ProgramData\\PrinterToolbox\\backup，随时「还原上一次」。");
            sb.AppendLine("· 标「降安全」的项是把 Windows 的安全缓解关掉（比如 PrintNightmare 相关的那几条），");
            sb.AppendLine("  只在客户端实在改不动时用，用完最好还原。");
            sb.AppendLine();
            sb.AppendLine("【驱动检测】");
            sb.AppendLine("· ① 扫描设备：把本机打印队列和 PnP 设备（含没装驱动的）一起列出来。");
            sb.AppendLine("· ② 读驱动库：队列在用驱动 + 系统签名驱动库 + 你自己的驱动文件夹，三合一。");
            sb.AppendLine("· ③ 自动匹配：按型号词元和厂商给每台待装设备挑驱动，给不出就明说「库里没匹配」。");
            sb.AppendLine("· 装驱动：只装本地有 INF 的条目（pnputil /add-driver /install），需要管理员。");
            sb.AppendLine("· 驱动入口：只跳转打印机驱动网的站内搜索页，不代下、不代跑任何第三方安装包。");
            sb.AppendLine();
            sb.AppendLine("【关于权限，别浪费时间】");
            sb.AppendLine("· 这个工具不需要常驻管理员：日常校准 IP、改名、打测试页都不需要。");
            sb.AppendLine("· 改注册表、防火墙、DISM、装驱动才要提权，需要时它会自己以管理员身份重开一个窗口。");
            sb.AppendLine("· 如果日志里出现「低完整性（受限）」——说明这个进程是被别的受限进程拉起来的，");
            sb.AppendLine("  写系统配置会一直失败。这种情况请直接在桌面上双击本程序。");
            sb.AppendLine();
            sb.AppendLine("【两个约定】");
            sb.AppendLine("· 管理类工具不猜：给不出结论就写「没匹配 / 未知」，不编一个看起来对的答案。");
            sb.AppendLine("· 危险操作（删队列、关安全缓解、装驱动）都会先弹确认，并把改了什么写进日志。");
            sb.AppendLine();
            sb.AppendLine("代码与说明：");
            sb.AppendLine("  https://github.com/2384418321/printer-toolbox       源码 / 文档 / 下载");
            sb.AppendLine("  <程序目录>\\PrinterToolbox.exe                     可执行文件");
            sb.AppendLine("  %ProgramData%\\PrinterToolbox                      备份 / 驱动库 / 日志");
            return sb.ToString();
        }
    }
}
