using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace PrinterIpFix
{
    /// <summary>
    /// 无桌面自检：把窗口在屏幕外创建一次，导出全部控件的实际坐标尺寸。
    /// 用于在没有可视桌面的环境里核对布局（是否越界、是否重叠、是否被挤压到 0）。
    /// 参数 --add 改为导出「添加打印机」对话框；
    /// 参数 --toolbox 导出工具箱外壳（含各页签）；
    /// 参数 --drvrepair 导出「驱动修复」对话框。
    /// </summary>
    internal static class UiDump
    {
        public static int Run(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            bool add = false;
            bool toolbox = false;
            bool drvrepair = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--add", StringComparison.OrdinalIgnoreCase)) add = true;
                if (string.Equals(args[i], "--toolbox", StringComparison.OrdinalIgnoreCase)) toolbox = true;
                if (string.Equals(args[i], "--drvrepair", StringComparison.OrdinalIgnoreCase)) drvrepair = true;
            }

            Form f;
            if (drvrepair)
            {
                // 反射构造，避免本轮之前的构建里没有这个类型时直接编不过
                Type t = typeof(UiDump).Assembly.GetType("PrinterIpFix.DriverRepairForm");
                if (t == null) { Console.WriteLine("DriverRepairForm 不在这个程序集里"); return 2; }
                f = (Form)Activator.CreateInstance(t, new object[] { "OfficeJet Pro 9730", "192.168.1.100", "IP_192.168.1.100" });
            }
            else if (toolbox)
            {
                // 反射构造，避免 gui 版编译时依赖 ToolboxForm
                Type t = typeof(UiDump).Assembly.GetType("PrinterIpFix.ToolboxForm");
                if (t == null) { Console.WriteLine("ToolboxForm 不在这个程序集里"); return 2; }
                f = (Form)Activator.CreateInstance(t);
            }
            else
            {
                f = add ? (Form)new AddPrinterForm() : (Form)new MainForm();
            }

            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-6000, -6000);
            f.ShowInTaskbar = false;
            f.Show();
            Application.DoEvents();
            System.Threading.Thread.Sleep(600);
            Application.DoEvents();

            Dump(f, "PASS 1");

            TabControl tc = FindTab(f);
            if (tc != null)
            {
                for (int i = 1; i < tc.TabPages.Count; i++)
                {
                    tc.SelectedIndex = i;
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(200);
                    Application.DoEvents();
                    Dump(f, "PASS " + (i + 1) + " (tab " + tc.TabPages[i].Text + ")");
                }
            }

            f.Close();
            return 0;
        }

        private static TabControl FindTab(Control root)
        {
            if (root is TabControl) return (TabControl)root;
            foreach (Control c in root.Controls)
            {
                TabControl found = FindTab(c);
                if (found != null) return found;
            }
            return null;
        }

        private static void Dump(Form f, string title)
        {
            Console.WriteLine("===== " + title + " =====");
            Console.WriteLine("FORM " + f.GetType().Name
                + " client=" + f.ClientSize.Width + "x" + f.ClientSize.Height
                + "  window=" + f.Width + "x" + f.Height
                + "  min=" + f.MinimumSize.Width + "x" + f.MinimumSize.Height
                + "  autoScale=" + f.AutoScaleMode
                + "  base96=" + f.AutoScaleDimensions.Width);

            Walk(f, 0, f.DisplayRectangle);

            Console.WriteLine();
            Console.WriteLine("OVERLAP CHECK (siblings in same container)");
            CheckOverlaps(f, 0);
            Console.WriteLine();
        }

        private static void Walk(Control c, int depth, Rectangle parentArea)
        {
            string pad = new string(' ', depth * 2);
            Rectangle b = c.Bounds;
            string text = (c.Text == null ? "" : c.Text).Replace("\r", " ").Replace("\n", " ");
            if (text.Length > 26) text = text.Substring(0, 26) + "...";

            string flag = "";
            if (c.Visible && (b.Width <= 0 || b.Height <= 0)) flag = "   <<< ZERO SIZE";
            if (c.Visible && depth > 0 && parentArea.Width > 0)
            {
                if (b.Right > parentArea.Width + 1 || b.Bottom > parentArea.Height + 1)
                    flag += "   <<< OUT OF PARENT";
            }

            Console.WriteLine(pad + c.GetType().Name
                + "  bounds=" + b.X + "," + b.Y + " " + b.Width + "x" + b.Height
                + "  dock=" + c.Dock
                + (c.Visible ? "" : "  hidden")
                + (text.Length == 0 ? "" : "  text=[" + text + "]")
                + flag);

            foreach (Control ch in c.Controls)
                Walk(ch, depth + 1, new Rectangle(0, 0, c.ClientSize.Width, c.ClientSize.Height));
        }

        private static void CheckOverlaps(Control parent, int depth)
        {
            if (depth > 4) return;
            var kids = new List<Control>();
            foreach (Control c in parent.Controls) if (c.Visible) kids.Add(c);

            int found = 0;
            for (int i = 0; i < kids.Count; i++)
            {
                for (int j = i + 1; j < kids.Count; j++)
                {
                    Rectangle a = kids[i].Bounds;
                    Rectangle b = kids[j].Bounds;
                    Rectangle r = Rectangle.Intersect(a, b);
                    if (r.Width > 4 && r.Height > 4)
                    {
                        Console.WriteLine("  " + new string(' ', depth * 2)
                            + kids[i].GetType().Name + " x " + kids[j].GetType().Name
                            + "  overlap " + r.Width + "x" + r.Height + " at " + r.X + "," + r.Y);
                        found++;
                    }
                }
            }
            Console.WriteLine("  " + new string(' ', depth * 2)
                + (parent.GetType().Name) + ": " + kids.Count + " children, " + found + " overlaps");

            foreach (Control c in parent.Controls) CheckOverlaps(c, depth + 1);
        }
    }
}
