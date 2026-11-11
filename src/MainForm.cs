// Dashboard window + tray icon. Samples once a second on a worker thread and repaints the tiles and graph.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ThinkPadMonitor
{
    static class Theme
    {
        public static readonly Color Back = Color.FromArgb(24, 26, 30);
        public static readonly Color Tile = Color.FromArgb(36, 39, 45);
        public static readonly Color Text = Color.FromArgb(232, 234, 237);
        public static readonly Color Dim = Color.FromArgb(150, 156, 165);
        public static readonly Color Good = Color.FromArgb(80, 200, 120);
        public static readonly Color Warn = Color.FromArgb(240, 180, 60);
        public static readonly Color Bad = Color.FromArgb(240, 85, 80);
        public static readonly Color Accent = Color.FromArgb(90, 160, 240);
        public static readonly Color Grid = Color.FromArgb(55, 59, 66);

        public static Color ForTemp(int c) { return c >= 90 ? Bad : c >= 75 ? Warn : Good; }
    }

    class Tile : Panel
    {
        readonly Label caption, value, detail;

        public Tile(string title)
        {
            BackColor = Theme.Tile;
            Size = new Size(170, 92);
            Margin = new Padding(6);
            caption = MakeLabel(title.ToUpperInvariant(), 8f, FontStyle.Bold, Theme.Dim, 8);
            value = MakeLabel("--", 20f, FontStyle.Bold, Theme.Text, 26);
            detail = MakeLabel("", 8.5f, FontStyle.Regular, Theme.Dim, 66);
            Controls.Add(caption); Controls.Add(value); Controls.Add(detail);
        }

        Label MakeLabel(string text, float size, FontStyle style, Color color, int top)
        {
            return new Label
            {
                Text = text, AutoSize = false, Left = 10, Top = top, Width = 155, Height = (int)(size * 2) + 4,
                Font = new Font("Segoe UI", size, style), ForeColor = color, BackColor = Color.Transparent
            };
        }

        public void Set(string v, Color c, string d)
        {
            value.Text = v; value.ForeColor = c; detail.Text = d;
        }
    }

    class Graph : Panel
    {
        public readonly List<Sample> History = new List<Sample>();
        public const int Capacity = 300;   // 5 minutes at 1 sample/s

        public Graph()
        {
            DoubleBuffered = true;
            BackColor = Theme.Tile;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var font = new Font("Segoe UI", 7.5f);
            var dim = new SolidBrush(Theme.Dim);
            Rectangle r = new Rectangle(34, 22, Width - 80, Height - 40);

            g.DrawString("CPU TEMP (C)", new Font("Segoe UI", 8f, FontStyle.Bold), new SolidBrush(Theme.Warn), 8, 4);
            g.DrawString("CPU MHz", new Font("Segoe UI", 8f, FontStyle.Bold), new SolidBrush(Theme.Accent), Width - 70, 4);
            g.DrawString("last 5 min", font, dim, Width / 2 - 25, 5);

            const int tMin = 30, tMax = 105;
            using (var gridPen = new Pen(Theme.Grid))
                foreach (int t in new[] { 40, 55, 70, 85, 100 })
                {
                    int y = r.Bottom - (t - tMin) * r.Height / (tMax - tMin);
                    g.DrawLine(gridPen, r.Left, y, r.Right, y);
                    g.DrawString(t.ToString(), font, dim, 6, y - 7);
                }

            Sample last = History.Count > 0 ? History[History.Count - 1] : null;
            int maxMhz = last != null && last.CpuMaxMhz > 0 ? last.CpuMaxMhz : 2600;
            g.DrawString(maxMhz.ToString(), font, dim, r.Right + 4, r.Top - 7);
            g.DrawString("0", font, dim, r.Right + 4, r.Bottom - 7);

            if (History.Count < 2) { font.Dispose(); dim.Dispose(); return; }
            float dx = (float)r.Width / (Capacity - 1);
            int offset = Capacity - History.Count;

            var mhzPts = new List<PointF>();
            var tempPts = new List<PointF>();
            for (int i = 0; i < History.Count; i++)
            {
                Sample s = History[i];
                float x = r.Left + (offset + i) * dx;
                mhzPts.Add(new PointF(x, r.Bottom - Math.Min(s.CpuMhz, maxMhz) * (float)r.Height / maxMhz));
                if (s.EcOk)
                    tempPts.Add(new PointF(x, r.Bottom - (Math.Max(tMin, Math.Min(tMax, s.CpuTemp)) - tMin) * (float)r.Height / (tMax - tMin)));
            }
            using (var p = new Pen(Color.FromArgb(160, Theme.Accent), 1.5f)) g.DrawLines(p, mhzPts.ToArray());
            if (tempPts.Count > 1)
                using (var p = new Pen(Theme.ForTemp(last.CpuTemp), 2.2f)) g.DrawLines(p, tempPts.ToArray());

            // Shade the stretches where the fan was not turning.
            using (var shade = new SolidBrush(Color.FromArgb(40, Theme.Bad)))
                for (int i = 0; i < History.Count; i++)
                    if (History[i].EcOk && !History[i].FanSpinning)
                        g.FillRectangle(shade, r.Left + (offset + i) * dx, r.Bottom - 4, dx + 0.5f, 4);
            g.DrawString("red strip = fan stopped", font, new SolidBrush(Theme.Bad), r.Left, r.Bottom + 3);

            font.Dispose(); dim.Dispose();
        }
    }

    public class MainForm : Form
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        readonly Sampler sampler = new Sampler();
        readonly Tile tTemp = new Tile("CPU temperature"), tFan = new Tile("Fan"), tClock = new Tile("CPU speed"),
                      tLoad = new Tile("CPU load"), tPower = new Tile("Power"), tLight = new Tile("ThinkLight");
        readonly Graph graph = new Graph();
        readonly Label status = new Label();
        readonly Button bLight = new Button(), bKick = new Button();
        readonly CheckBox cTop = new CheckBox(), cStartup = new CheckBox();
        readonly NotifyIcon tray = new NotifyIcon();
        System.Threading.Timer timer;
        int sampling;
        bool exiting, kicking, wasSpinning = true, hotAlerted;
        Sample latest;
        IntPtr trayIconHandle = IntPtr.Zero;

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "ThinkPadMonitor";

        public MainForm(bool startHidden)
        {
            Text = "ThinkPad Monitor";
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(564, 520);
            StartPosition = FormStartPosition.CenterScreen;

            var tiles = new FlowLayoutPanel { Left = 6, Top = 6, Width = 552, Height = 210, BackColor = Theme.Back };
            tiles.Controls.AddRange(new Control[] { tTemp, tFan, tClock, tLoad, tPower, tLight });
            Controls.Add(tiles);

            graph.SetBounds(12, 220, 540, 200);
            Controls.Add(graph);

            StyleButton(bLight, "Toggle ThinkLight", 12, 432);
            StyleButton(bKick, "Kick-start fan", 186, 432);
            bLight.Click += delegate { ToggleThinkLight(); };
            bKick.Click += delegate { KickFan(); };

            cTop.Text = "Always on top"; cTop.SetBounds(372, 428, 180, 22); cTop.ForeColor = Theme.Dim;
            cTop.CheckedChanged += delegate { TopMost = cTop.Checked; };
            cStartup.Text = "Start with Windows"; cStartup.SetBounds(372, 450, 180, 22); cStartup.ForeColor = Theme.Dim;
            cStartup.Checked = StartupEnabled();
            cStartup.CheckedChanged += delegate { SetStartup(cStartup.Checked); };

            status.SetBounds(12, 482, 540, 32);
            status.ForeColor = Theme.Dim;
            status.Text = "Starting...";
            Controls.AddRange(new Control[] { bLight, bKick, cTop, cStartup, status });

            var menu = new ContextMenuStrip();
            menu.Items.Add("Show dashboard", null, delegate { ShowDashboard(); });
            menu.Items.Add("Toggle ThinkLight", null, delegate { ToggleThinkLight(); });
            menu.Items.Add("Kick-start fan", null, delegate { KickFan(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { exiting = true; Close(); });
            tray.ContextMenuStrip = menu;
            tray.Text = "ThinkPad Monitor";
            tray.Icon = SystemIcons.Application;
            tray.Visible = true;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowDashboard(); };

            if (startHidden) { ShowInTaskbar = false; WindowState = FormWindowState.Minimized; Load += delegate { Hide(); ShowInTaskbar = true; WindowState = FormWindowState.Normal; }; }

            if (!EmbeddedController.DriverAvailable())
                status.Text = "InpOut driver not found - EC metrics unavailable. Run InstallDriver.exe once (see README).";

            timer = new System.Threading.Timer(delegate { Tick(); }, null, 0, 1000);
        }

        void StyleButton(Button b, string text, int x, int y)
        {
            b.Text = text; b.SetBounds(x, y, 164, 38);
            b.FlatStyle = FlatStyle.Flat; b.BackColor = Theme.Tile; b.ForeColor = Theme.Text;
            b.FlatAppearance.BorderColor = Theme.Grid;
            b.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        }

        void Tick()
        {
            if (Interlocked.Exchange(ref sampling, 1) == 1) return;   // previous sample still running
            try
            {
                Sample s = sampler.Take();
                if (!IsDisposed) BeginInvoke(new Action<Sample>(Render), s);
            }
            catch (InvalidOperationException) { }                      // form closing
            finally { Interlocked.Exchange(ref sampling, 0); }
        }

        void Render(Sample s)
        {
            latest = s;
            graph.History.Add(s);
            if (graph.History.Count > Graph.Capacity) graph.History.RemoveAt(0);
            graph.Invalidate();

            if (s.EcOk)
            {
                tTemp.Set(s.CpuTemp + " °C", Theme.ForTemp(s.CpuTemp),
                    s.CpuTemp >= 90 ? "Hot - Intel limit 105 °C" : "Intel limit 105 °C");
                if (s.FanSpinning) tFan.Set(s.FanRpm + " rpm", Theme.Good, s.FanModeText);
                else tFan.Set("STOPPED", Theme.Bad, "Commanded: " + s.FanModeText);
                tLight.Set(s.ThinkLightOn ? "ON" : "OFF", s.ThinkLightOn ? Theme.Warn : Theme.Dim, "Ctrl+Alt+L shortcut too");
            }
            else
            {
                tTemp.Set("n/a", Theme.Dim, "EC unavailable");
                tFan.Set("n/a", Theme.Dim, "EC unavailable");
                tLight.Set("n/a", Theme.Dim, "EC unavailable");
            }

            if (s.CpuMhz > 0)
            {
                string d = s.FirmwareThrottled ? "THROTTLED by firmware"
                         : s.CpuAtMinimum ? "Minimum clock" : "of " + s.CpuMaxMhz + " MHz max";
                tClock.Set(s.CpuMhz + " MHz", s.FirmwareThrottled ? Theme.Bad : s.CpuAtMinimum ? Theme.Warn : Theme.Good, d);
            }
            else tClock.Set("n/a", Theme.Dim, "counter unavailable");

            tLoad.Set(s.CpuLoad + " %", s.CpuLoad >= 85 ? Theme.Warn : Theme.Text, Environment.ProcessorCount + " logical CPUs");
            tPower.Set(s.BatteryPercent >= 0 ? s.BatteryPercent + " %" : (s.OnAc ? "AC" : "?"),
                s.BatteryPercent >= 0 && s.BatteryPercent < 20 && !s.OnAc ? Theme.Bad : Theme.Text, s.BatteryState);

            if (!kicking)
                status.Text = s.EcOk ? "Updated " + s.Time.ToString("HH:mm:ss") + (s.FanSpinning ? "" : "   •   Fan not turning: try Kick-start, keep load light.")
                                     : "EC read failed: " + s.EcError;

            UpdateTray(s);
            Alerts(s);
        }

        void UpdateTray(Sample s)
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                    Color c = s.EcOk ? Theme.ForTemp(s.CpuTemp) : Theme.Dim;
                    if (s.EcOk && !s.FanSpinning) g.FillRectangle(new SolidBrush(Color.FromArgb(150, 20, 20)), 0, 0, 16, 16);
                    string txt = s.EcOk ? s.CpuTemp.ToString() : "--";
                    using (var f = new Font("Arial", txt.Length > 2 ? 7f : 10f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(c))
                        g.DrawString(txt, f, brush, (16 - g.MeasureString(txt, f).Width) / 2 + 1, 1);
                }
                IntPtr h = bmp.GetHicon();
                tray.Icon = Icon.FromHandle(h);
                if (trayIconHandle != IntPtr.Zero) DestroyIcon(trayIconHandle);
                trayIconHandle = h;
            }
            string t = s.EcOk ? string.Format("CPU {0} °C   Fan {1}\n{2} MHz   Load {3}%", s.CpuTemp,
                s.FanSpinning ? s.FanRpm + " rpm" : "STOPPED", s.CpuMhz, s.CpuLoad) : "ThinkPad Monitor - EC unavailable";
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        void Alerts(Sample s)
        {
            if (!s.EcOk) return;
            if (wasSpinning && !s.FanSpinning && graph.History.Count > 3)
                tray.ShowBalloonTip(5000, "Fan stopped", "The fan is not turning. CPU is " + s.CpuTemp + " °C.", ToolTipIcon.Warning);
            wasSpinning = s.FanSpinning;
            if (s.CpuTemp >= 90 && !hotAlerted)
            {
                hotAlerted = true;
                tray.ShowBalloonTip(5000, "CPU is hot", "CPU temperature " + s.CpuTemp + " °C. Reduce load.", ToolTipIcon.Error);
            }
            else if (s.CpuTemp < 85) hotAlerted = false;
        }

        void ToggleThinkLight()
        {
            try
            {
                byte cur = EmbeddedController.Read(Ec.THINKLIGHT);
                EmbeddedController.Write(Ec.THINKLIGHT, (byte)(cur ^ Ec.THINKLIGHT_BIT));
            }
            catch (Exception ex) { status.Text = "ThinkLight: " + ex.Message; }
        }

        void KickFan()
        {
            if (kicking) return;
            kicking = true; bKick.Enabled = false;
            var th = new Thread(delegate()
            {
                string result = FanKicker.Run(delegate(string msg) { BeginInvoke(new Action(delegate { status.Text = msg; })); });
                BeginInvoke(new Action(delegate
                {
                    kicking = false; bKick.Enabled = true; status.Text = result;
                    tray.ShowBalloonTip(4000, "Fan kick", result, ToolTipIcon.Info);
                }));
            });
            th.IsBackground = true;
            th.Start();
        }

        void ShowDashboard() { Show(); WindowState = FormWindowState.Normal; Activate(); }

        static bool StartupEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null;
        }

        static void SetStartup(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --tray");
                else k.DeleteValue(RunName, false);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true; Hide();      // X button hides to the tray; use the tray menu to exit
                return;
            }
            timer.Dispose();
            tray.Visible = false;
            tray.Dispose();
            sampler.Dispose();
            if (trayIconHandle != IntPtr.Zero) DestroyIcon(trayIconHandle);
            base.OnFormClosing(e);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool first;
            using (var single = new Mutex(true, @"Local\ThinkPadMonitor_SingleInstance", out first))
            {
                if (!first) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool hidden = Array.IndexOf(args, "--tray") >= 0;
                Application.Run(new MainForm(hidden));
            }
        }
    }
}
