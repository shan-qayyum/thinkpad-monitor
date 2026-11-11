// Dashboard window + tray icon. Samples once a second on a worker thread, applies the fan mode, then repaints
// the tiles and graph and runs the protections (thermal guard, emergency hibernate) on the UI thread.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
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

    class Choice
    {
        public readonly string Text, Key;
        public Choice(string text, string key) { Text = text; Key = key; }
        public override string ToString() { return Text; }
    }

    class Tile : Panel
    {
        readonly Label caption, value, detail;

        public Tile(string title)
        {
            BackColor = Theme.Tile;
            Size = new Size(174, 92);
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
                Text = text, AutoSize = false, Left = 10, Top = top, Width = 160, Height = (int)(size * 2) + 4,
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
        const int RpmScale = 5000;

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
            Rectangle r = new Rectangle(34, 22, Width - 80, Height - 40);

            using (var font = new Font("Segoe UI", 7.5f))
            using (var bold = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (var dim = new SolidBrush(Theme.Dim))
            using (var warn = new SolidBrush(Theme.Warn))
            using (var accent = new SolidBrush(Theme.Accent))
            using (var bad = new SolidBrush(Theme.Bad))
            {
                g.DrawString("CPU TEMP (C)", bold, warn, 8, 4);
                g.DrawString("FAN RPM (0-" + RpmScale + ")", bold, dim, 110, 4);
                g.DrawString("last 5 min", font, dim, Width / 2 + 10, 5);
                g.DrawString("CPU MHz", bold, accent, Width - 70, 4);

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
                if (History.Count < 2) return;

                float dx = (float)r.Width / (Capacity - 1);
                int offset = Capacity - History.Count;
                var mhzPts = new List<PointF>();
                var tempPts = new List<PointF>();
                var rpmPts = new List<PointF>();
                for (int i = 0; i < History.Count; i++)
                {
                    Sample s = History[i];
                    float x = r.Left + (offset + i) * dx;
                    mhzPts.Add(new PointF(x, r.Bottom - Math.Min(s.CpuMhz, maxMhz) * (float)r.Height / maxMhz));
                    if (!s.EcOk) continue;
                    tempPts.Add(new PointF(x, r.Bottom - (Math.Max(tMin, Math.Min(tMax, s.CpuTemp)) - tMin) * (float)r.Height / (tMax - tMin)));
                    rpmPts.Add(new PointF(x, r.Bottom - Math.Min(Math.Max(s.FanRpm, 0), RpmScale) * (float)r.Height / RpmScale));
                }
                using (var p = new Pen(Color.FromArgb(120, Theme.Dim), 1.2f)) if (rpmPts.Count > 1) g.DrawLines(p, rpmPts.ToArray());
                using (var p = new Pen(Color.FromArgb(160, Theme.Accent), 1.5f)) g.DrawLines(p, mhzPts.ToArray());
                using (var p = new Pen(Theme.ForTemp(last.CpuTemp), 2.2f)) if (tempPts.Count > 1) g.DrawLines(p, tempPts.ToArray());

                // Mark the stretches where the fan was not turning.
                using (var shade = new SolidBrush(Color.FromArgb(90, Theme.Bad)))
                    for (int i = 0; i < History.Count; i++)
                        if (History[i].EcOk && !History[i].FanSpinning)
                            g.FillRectangle(shade, r.Left + (offset + i) * dx, r.Bottom - 4, dx + 0.5f, 4);
                g.DrawString("red strip = fan stopped", font, bad, r.Left, r.Bottom + 3);
            }
        }
    }

    public class MainForm : Form
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        const string AppVersion = "1.1.0";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "ThinkPadMonitor";
        const int AlertTemp = 90;              // C, "CPU is hot" notification
        const int RestartWakeSeconds = 75;     // long enough for the hibernate image to be written first

        static readonly Choice[] FanModes =
        {
            new Choice("Leave alone (monitor only)", "none"),
            new Choice("Automatic (firmware)", "auto"),
            new Choice("Smart curve (quiet when cool)", "smart"),
            new Choice("Full speed", "full"),
            new Choice("Level 7", "7"), new Choice("Level 6", "6"), new Choice("Level 5", "5"),
            new Choice("Level 4", "4"), new Choice("Level 3", "3"), new Choice("Level 2", "2"),
            new Choice("Level 1 (slowest)", "1"),
        };
        static readonly int[] CpuLimits = { 100, 99, 85, 70, 50, 30 };

        readonly Sampler sampler = new Sampler();
        readonly Tile tTemp = new Tile("CPU temperature"), tFan = new Tile("Fan"), tClock = new Tile("CPU speed"),
                      tLoad = new Tile("CPU load"), tPower = new Tile("Power"), tLight = new Tile("ThinkLight"),
                      tPeak = new Tile("Session temperature"), tHealth = new Tile("Fan this session");
        readonly Graph graph = new Graph();
        readonly Label status = new Label();
        readonly ComboBox cbFan = new ComboBox(), cbCpu = new ComboBox();
        readonly CheckBox cGuard = new CheckBox(), cEmergency = new CheckBox(), cLog = new CheckBox(),
                          cTop = new CheckBox(), cStartup = new CheckBox();
        readonly NumericUpDown nGuard = new NumericUpDown(), nEmergency = new NumericUpDown();
        readonly Button bLight = new Button(), bRestart = new Button(), bKick = new Button(), bLogs = new Button();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem fanMenu = new ToolStripMenuItem("Fan mode"), cpuMenu = new ToolStripMenuItem("CPU limit");
        readonly System.Windows.Forms.Timer awakeTimer = new System.Windows.Forms.Timer();
        System.Threading.Timer timer;
        WakeTimer wakeTimer;
        IntPtr trayIconHandle = IntPtr.Zero;
        int sampling;
        bool loading = true, exiting, busy, restartPending;
        bool wasSpinning = true, hotAlerted, guardActive;
        int cpuLimit = 100, stallSecs, calmSecs, criticalSecs, ticks;

        // Session statistics
        int statSamples, statMin = int.MaxValue, statMax = int.MinValue, statStalls, statSpinning;
        long statSum;

        public MainForm(bool startHidden)
        {
            Settings.Load();
            Text = "ThinkPad Monitor " + AppVersion;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(756, 624);
            StartPosition = FormStartPosition.CenterScreen;

            var tiles = new FlowLayoutPanel { Left = 6, Top = 6, Width = 748, Height = 210, BackColor = Theme.Back };
            tiles.Controls.AddRange(new Control[] { tTemp, tFan, tClock, tLoad, tPower, tLight, tPeak, tHealth });
            Controls.Add(tiles);
            graph.SetBounds(12, 220, 732, 190);
            Controls.Add(graph);

            // Row 1: fan mode and CPU limit
            AddLabel("Fan mode", 12, 424, 62);
            StyleCombo(cbFan, 76, 420, 230);
            cbFan.Items.AddRange(FanModes);
            AddLabel("CPU limit", 330, 424, 62);
            StyleCombo(cbCpu, 394, 420, 230);
            int maxMhz = sampler.MaxMhz > 0 ? sampler.MaxMhz : 2600;
            foreach (int pct in CpuLimits)
                cbCpu.Items.Add(new Choice(pct == 100 ? "No limit (100 %)"
                    : pct == 99 ? "No turbo boost (99 %)"
                    : string.Format("{0} %  (about {1} MHz)", pct, maxMhz * pct / 100), pct.ToString()));

            // Row 2 and 3: protections
            StyleCheck(cGuard, "Thermal guard: cap CPU to " + Settings.GuardPercent + " % at", 12, 454, 232);
            StyleNumber(nGuard, 246, 453, 60, 100, Settings.GuardTemp);
            AddLabel("°C or when the fan stops, and lift the cap once it is cool again", 304, 457, 440);
            StyleCheck(cEmergency, "Emergency hibernate if the CPU stays at", 12, 482, 250);
            StyleNumber(nEmergency, 264, 481, 80, 104, Settings.EmergencyTemp);
            AddLabel("°C or more for 5 seconds", 322, 485, 200);
            StyleCheck(cLog, "Log readings to CSV", 560, 482, 150);

            // Row 4: actions
            StyleButton(bLight, "Toggle ThinkLight", 12, 514, 170);
            StyleButton(bRestart, "Restart fan (hibernate)", 190, 514, 190);
            StyleButton(bKick, "Kick-start (experimental)", 388, 514, 190);
            StyleButton(bLogs, "Open log folder", 586, 514, 158);

            // Row 5: window options
            StyleCheck(cTop, "Always on top", 12, 560, 130);
            StyleCheck(cStartup, "Start with Windows", 150, 560, 160);

            status.SetBounds(12, 588, 732, 32);
            status.ForeColor = Theme.Dim;
            status.Text = "Starting...";
            Controls.Add(status);

            // Initial values (no events while loading)
            cbFan.SelectedIndex = Math.Max(0, Array.FindIndex(FanModes, delegate(Choice c) { return c.Key == Settings.FanMode; }));
            if (Settings.GuardActive)             // the app last ended with the guard's cap still applied: undo it
            {
                PowerPlan.SetCpuMaxPercent(Settings.GuardRestore);
                Settings.GuardActive = false;
                Settings.Save();
            }
            RefreshCpuLimit();
            cGuard.Checked = Settings.GuardEnabled;
            cEmergency.Checked = Settings.EmergencyEnabled;
            cLog.Checked = Settings.LogCsv;
            cTop.Checked = TopMost = Settings.AlwaysOnTop;
            cStartup.Checked = StartupEnabled();
            loading = false;

            cbFan.SelectedIndexChanged += delegate { if (!loading) { Settings.FanMode = ((Choice)cbFan.SelectedItem).Key; Settings.Save(); } };
            cbCpu.SelectedIndexChanged += delegate { if (!loading) SetCpuLimit(int.Parse(((Choice)cbCpu.SelectedItem).Key)); };
            cGuard.CheckedChanged += delegate { if (!loading) { Settings.GuardEnabled = cGuard.Checked; Settings.Save(); } };
            nGuard.ValueChanged += delegate { if (!loading) { Settings.GuardTemp = (int)nGuard.Value; Settings.Save(); } };
            cEmergency.CheckedChanged += delegate { if (!loading) { Settings.EmergencyEnabled = cEmergency.Checked; Settings.Save(); } };
            nEmergency.ValueChanged += delegate { if (!loading) { Settings.EmergencyTemp = (int)nEmergency.Value; Settings.Save(); } };
            cLog.CheckedChanged += delegate { if (!loading) { Settings.LogCsv = cLog.Checked; Settings.Save(); } };
            cTop.CheckedChanged += delegate { TopMost = cTop.Checked; if (!loading) { Settings.AlwaysOnTop = cTop.Checked; Settings.Save(); } };
            cStartup.CheckedChanged += delegate { if (!loading) SetStartup(cStartup.Checked); };
            bLight.Click += delegate { ToggleThinkLight(); };
            bRestart.Click += delegate { RestartFan(); };
            bKick.Click += delegate { KickFan(); };
            bLogs.Click += delegate { Directory.CreateDirectory(Settings.LogFolder); Process.Start(Settings.LogFolder); };

            BuildTrayMenu();
            tray.Text = "ThinkPad Monitor";
            tray.Icon = SystemIcons.Application;
            tray.Visible = true;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowDashboard(); };

            awakeTimer.Interval = 180000;
            awakeTimer.Tick += delegate { awakeTimer.Stop(); PowerPlan.KeepAwake(false); };
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            if (startHidden) { ShowInTaskbar = false; WindowState = FormWindowState.Minimized; Load += delegate { Hide(); ShowInTaskbar = true; WindowState = FormWindowState.Normal; }; }

            if (!EmbeddedController.DriverAvailable())
                status.Text = "InpOut driver not found - EC metrics unavailable. Run InstallDriver.exe once (see README).";

            timer = new System.Threading.Timer(delegate { Tick(); }, null, 0, 1000);
        }

        // ---------- layout helpers ----------

        void AddLabel(string text, int x, int y, int w)
        {
            Controls.Add(new Label { Text = text, Left = x, Top = y, Width = w, Height = 20, ForeColor = Theme.Dim });
        }

        void StyleCombo(ComboBox c, int x, int y, int w)
        {
            c.SetBounds(x, y, w, 24);
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            c.BackColor = Theme.Tile; c.ForeColor = Theme.Text;
            // A stray scroll over a focused dropdown must not change the fan mode or the CPU limit.
            c.MouseWheel += delegate(object s, MouseEventArgs e) { if (!c.DroppedDown) ((HandledMouseEventArgs)e).Handled = true; };
            Controls.Add(c);
        }

        void StyleCheck(CheckBox c, string text, int x, int y, int w)
        {
            c.Text = text; c.SetBounds(x, y, w, 22); c.ForeColor = Theme.Text;
            Controls.Add(c);
        }

        void StyleNumber(NumericUpDown n, int x, int y, int min, int max, int value)
        {
            n.SetBounds(x, y, 52, 22);
            n.Minimum = min; n.Maximum = max; n.Value = Math.Max(min, Math.Min(max, value));
            n.BackColor = Theme.Tile; n.ForeColor = Theme.Text; n.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(n);
        }

        void StyleButton(Button b, string text, int x, int y, int w)
        {
            b.Text = text; b.SetBounds(x, y, w, 36);
            b.FlatStyle = FlatStyle.Flat; b.BackColor = Theme.Tile; b.ForeColor = Theme.Text;
            b.FlatAppearance.BorderColor = Theme.Grid;
            b.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            Controls.Add(b);
        }

        void BuildTrayMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show dashboard", null, delegate { ShowDashboard(); });
            menu.Items.Add("Toggle ThinkLight", null, delegate { ToggleThinkLight(); });
            for (int i = 0; i < cbFan.Items.Count; i++)
            {
                int index = i;
                fanMenu.DropDownItems.Add(cbFan.Items[i].ToString(), null, delegate { cbFan.SelectedIndex = index; });
            }
            for (int i = 0; i < cbCpu.Items.Count; i++)
            {
                int index = i;
                cpuMenu.DropDownItems.Add(cbCpu.Items[i].ToString(), null, delegate { if (cbCpu.Enabled) cbCpu.SelectedIndex = index; });
            }
            menu.Items.Add(fanMenu);
            menu.Items.Add(cpuMenu);
            menu.Items.Add("Restart fan (hibernate)", null, delegate { ShowDashboard(); RestartFan(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { exiting = true; Close(); });
            menu.Opening += delegate
            {
                for (int i = 0; i < fanMenu.DropDownItems.Count; i++) ((ToolStripMenuItem)fanMenu.DropDownItems[i]).Checked = i == cbFan.SelectedIndex;
                for (int i = 0; i < cpuMenu.DropDownItems.Count; i++) ((ToolStripMenuItem)cpuMenu.DropDownItems[i]).Checked = i == cbCpu.SelectedIndex;
                cpuMenu.Enabled = cbCpu.Enabled;
            };
            tray.ContextMenuStrip = menu;
        }

        // ---------- sampling ----------

        void Tick()
        {
            if (Interlocked.Exchange(ref sampling, 1) == 1) return;   // previous sample still running
            try
            {
                Sample s = sampler.Take();
                FanController.Apply(s, Settings.FanMode);
                if (!IsDisposed) BeginInvoke(new Action<Sample>(Render), s);
            }
            catch (InvalidOperationException) { }                      // form closing
            finally { Interlocked.Exchange(ref sampling, 0); }
        }

        void Render(Sample s)
        {
            ticks++;
            graph.History.Add(s);
            if (graph.History.Count > Graph.Capacity) graph.History.RemoveAt(0);
            graph.Invalidate();
            if (ticks % 10 == 0 && !guardActive) RefreshCpuLimit();   // picks up changes made in Windows settings

            if (s.EcOk)
            {
                statSamples++; statSum += s.CpuTemp;
                statMin = Math.Min(statMin, s.CpuTemp); statMax = Math.Max(statMax, s.CpuTemp);
                if (s.FanSpinning) statSpinning++;

                tTemp.Set(s.CpuTemp + " °C", Theme.ForTemp(s.CpuTemp),
                    s.CpuTemp >= AlertTemp ? "Hot - Intel limit 105 °C" : "Intel limit 105 °C");
                if (s.FanSpinning) tFan.Set(s.FanRpm + " rpm", Theme.Good, s.FanModeText);
                else tFan.Set("STOPPED", Theme.Bad, "Commanded: " + s.FanModeText);
                tLight.Set(s.ThinkLightOn ? "ON" : "OFF", s.ThinkLightOn ? Theme.Warn : Theme.Dim, "Use the button below");
                tPeak.Set("max " + statMax + " °C", Theme.ForTemp(statMax),
                    string.Format("min {0} °C  ·  avg {1} °C", statMin, statSum / statSamples));
                int pct = statSpinning * 100 / statSamples;
                tHealth.Set(pct + " % up", pct >= 95 ? Theme.Good : pct >= 50 ? Theme.Warn : Theme.Bad,
                    statStalls == 1 ? "1 stall" : statStalls + " stalls");
            }
            else
            {
                tTemp.Set("n/a", Theme.Dim, "EC unavailable");
                tFan.Set("n/a", Theme.Dim, "EC unavailable");
                tLight.Set("n/a", Theme.Dim, "EC unavailable");
            }

            if (s.CpuMhz > 0)
            {
                bool cappedLow = cpuLimit * s.CpuMaxMhz / 100 <= 1400;     // our own limit explains a low clock
                bool throttled = s.HeldAtMinimum && !cappedLow;
                string why = s.FirmwareLimitReason;
                string d = throttled ? (why != null ? "THROTTLED: " + why : "THROTTLED by firmware")
                         : guardActive ? "Capped by thermal guard"
                         : cpuLimit < 100 ? "Limit " + cpuLimit + " %"
                         : s.CpuAtMinimum ? "Minimum clock" : "of " + s.CpuMaxMhz + " MHz max";
                tClock.Set(s.CpuMhz + " MHz", throttled ? Theme.Bad : guardActive || s.CpuAtMinimum ? Theme.Warn : Theme.Good, d);
            }
            else tClock.Set("n/a", Theme.Dim, "counter unavailable");

            tLoad.Set(s.CpuLoad + " %", s.CpuLoad >= 85 ? Theme.Warn : Theme.Text, Environment.ProcessorCount + " logical CPUs");
            tPower.Set(s.BatteryPercent >= 0 ? s.BatteryPercent + " %" : (s.OnAc ? "AC" : "?"),
                s.BatteryPercent >= 0 && s.BatteryPercent < 20 && !s.OnAc ? Theme.Bad : Theme.Text,
                s.EcOk && s.ChargerWatts > 0 ? s.ChargerWatts + " W charger  ·  " + s.BatteryState : s.BatteryState);

            if (!busy)
                status.Text = !s.EcOk ? "EC read failed: " + s.EcError
                    : "Updated " + s.Time.ToString("HH:mm:ss")
                      + (guardActive ? "   •   Thermal guard is capping the CPU." : "")
                      + (s.FirmwareLimitReason != null ? "   •   Firmware is limiting CPU speed: " + s.FirmwareLimitReason + "." : "")
                      + (s.FanSpinning ? "" : "   •   Fan not turning: try Restart fan, keep the load light.");

            UpdateTray(s);
            Alerts(s);
            ThermalGuard(s);
            Emergency(s);
            if (Settings.LogCsv && ticks % 5 == 0) LogCsv(s);
        }

        void UpdateTray(Sample s)
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                    if (s.EcOk && !s.FanSpinning)
                        using (var red = new SolidBrush(Color.FromArgb(150, 20, 20))) g.FillRectangle(red, 0, 0, 16, 16);
                    string txt = s.EcOk ? s.CpuTemp.ToString() : "--";
                    using (var f = new Font("Arial", txt.Length > 2 ? 7f : 10f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(s.EcOk ? Theme.ForTemp(s.CpuTemp) : Theme.Dim))
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
            if (wasSpinning && !s.FanSpinning && statSamples > 3)
            {
                statStalls++;
                tray.ShowBalloonTip(5000, "Fan stopped", "The fan is not turning. CPU is " + s.CpuTemp + " °C.", ToolTipIcon.Warning);
            }
            wasSpinning = s.FanSpinning;
            if (s.CpuTemp >= AlertTemp && !hotAlerted)
            {
                hotAlerted = true;
                tray.ShowBalloonTip(5000, "CPU is hot", "CPU temperature " + s.CpuTemp + " °C. Reduce load.", ToolTipIcon.Error);
            }
            else if (s.CpuTemp < AlertTemp - 5) hotAlerted = false;
        }

        // ---------- CPU limit and protections ----------

        void RefreshCpuLimit()
        {
            int now = PowerPlan.GetCpuMaxPercent();
            if (now <= 0) return;
            cpuLimit = now;
            int best = 0;
            for (int i = 1; i < CpuLimits.Length; i++)
                if (Math.Abs(CpuLimits[i] - now) < Math.Abs(CpuLimits[best] - now)) best = i;
            if (cbCpu.SelectedIndex == best) return;
            bool was = loading;
            loading = true;
            cbCpu.SelectedIndex = best;
            loading = was;
        }

        void SetCpuLimit(int percent)
        {
            if (PowerPlan.SetCpuMaxPercent(percent)) cpuLimit = percent;
            else status.Text = "Windows refused the CPU limit change.";
        }

        // Caps the CPU while it is hot or the fan is stopped; lifts the cap after 30 calm seconds.
        void ThermalGuard(Sample s)
        {
            if (!s.EcOk) return;
            stallSecs = s.FanSpinning ? 0 : stallSecs + 1;
            if (!guardActive)
            {
                if (!Settings.GuardEnabled || (s.CpuTemp < Settings.GuardTemp && stallSecs < 10)) return;
                int before = PowerPlan.GetCpuMaxPercent();
                if (before <= 0) return;
                Settings.GuardRestore = before;
                Settings.GuardActive = true;
                Settings.Save();
                SetCpuLimit(Math.Min(before, Settings.GuardPercent));
                guardActive = true; calmSecs = 0;
                cbCpu.Enabled = false;
                tray.ShowBalloonTip(5000, "Thermal guard", s.FanSpinning
                    ? "CPU reached " + s.CpuTemp + " °C. CPU capped to " + Settings.GuardPercent + " %."
                    : "The fan stopped. CPU capped to " + Settings.GuardPercent + " %.", ToolTipIcon.Warning);
                return;
            }
            bool calm = s.FanSpinning && s.CpuTemp <= Settings.GuardTemp - 10;
            calmSecs = calm ? calmSecs + 1 : 0;
            if (!Settings.GuardEnabled || calmSecs >= 30) ReleaseGuard();
        }

        void ReleaseGuard()
        {
            if (!guardActive) return;
            SetCpuLimit(Settings.GuardRestore);
            guardActive = false;
            Settings.GuardActive = false;
            Settings.Save();
            cbCpu.Enabled = true;
            RefreshCpuLimit();
        }

        void Emergency(Sample s)
        {
            if (!s.EcOk || !Settings.EmergencyEnabled || s.CpuTemp < Settings.EmergencyTemp) { criticalSecs = 0; return; }
            if (++criticalSecs < 5) return;
            criticalSecs = 0;
            tray.ShowBalloonTip(5000, "Emergency hibernate", "CPU stayed at " + s.CpuTemp + " °C. Hibernating now.", ToolTipIcon.Error);
            var th = new Thread(delegate() { Thread.Sleep(1500); PowerPlan.Hibernate(); });
            th.IsBackground = true;
            th.Start();
        }

        void LogCsv(Sample s)
        {
            try
            {
                Directory.CreateDirectory(Settings.LogFolder);
                string path = Path.Combine(Settings.LogFolder, s.Time.ToString("yyyy-MM-dd") + ".csv");
                if (!File.Exists(path))
                    File.WriteAllText(path, "time,cpu_temp_c,fan_rpm,fan_register,cpu_mhz,cpu_load_pct,cpu_limit_pct,on_ac,battery_pct,charger_w,charger_limit_flag,thermal_flags,firmware_limit_reason\r\n");
                File.AppendAllText(path, string.Format(CultureInfo.InvariantCulture, "{0:HH:mm:ss},{1},{2},0x{3:X2},{4},{5},{6},{7},{8},{9},{10},0x{11:X2},{12}\r\n",
                    s.Time, s.EcOk ? s.CpuTemp.ToString() : "", s.EcOk ? Math.Max(0, s.FanRpm).ToString() : "", s.FanLevel,
                    s.CpuMhz, s.CpuLoad, cpuLimit, s.OnAc ? 1 : 0, s.BatteryPercent,
                    s.ChargerWatts, s.ChargerLimit ? 1 : 0, s.ThermalFlags, s.FirmwareLimitReason));
            }
            catch (IOException) { }                 // file open in a spreadsheet: skip this line
            catch (UnauthorizedAccessException) { }
        }

        // ---------- actions ----------

        void ToggleThinkLight()
        {
            try
            {
                byte cur = EmbeddedController.Read(Ec.THINKLIGHT);
                EmbeddedController.Write(Ec.THINKLIGHT, (byte)(cur ^ Ec.THINKLIGHT_BIT));
            }
            catch (Exception ex) { status.Text = "ThinkLight: " + ex.Message; }
        }

        void SetBusy(bool on)
        {
            busy = on;
            bKick.Enabled = bRestart.Enabled = !on;
            FanController.Suspended = on;
            if (on) FanController.SignalOwner(true);   // keeps a background fan service out of the way
        }

        void RunFanJob(Func<Action<string>, string> job, string balloonTitle)
        {
            if (busy) return;
            SetBusy(true);
            var th = new Thread(delegate()
            {
                string result = job(delegate(string msg) { BeginInvoke(new Action(delegate { status.Text = msg; })); });
                BeginInvoke(new Action(delegate
                {
                    SetBusy(false);
                    status.Text = result;
                    tray.ShowBalloonTip(4000, balloonTitle, result, ToolTipIcon.Info);
                }));
            });
            th.IsBackground = true;
            th.Start();
        }

        void KickFan() { RunFanJob(FanKicker.Run, "Fan kick"); }

        // The firmware runs the fan blind during a cold start, which can free a fan the EC has given up on.
        // Hibernate plus a wake timer triggers that cold start without anyone touching the power button.
        void RestartFan()
        {
            if (busy) return;
            if (!PowerPlan.HibernateAvailable)
            {
                MessageBox.Show(this, "Hibernate is switched off on this PC, so the fan restart cannot run.\n\n" +
                    "To enable it, run this in an administrator command prompt:\n    powercfg /hibernate on",
                    "Restart fan", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult answer = MessageBox.Show(this,
                "This hibernates the laptop and powers it back on by itself about " + RestartWakeSeconds + " seconds later.\n\n" +
                "Starting up from cold is what makes the firmware spin the fan again. Your open programs are kept, " +
                "but save your work first.\n\nIf the laptop has not come back after 5 minutes, press the power button.\n\nHibernate now?",
                "Restart fan", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            PowerPlan.EnableWakeTimers();
            if (wakeTimer != null) wakeTimer.Dispose();
            wakeTimer = new WakeTimer();
            if (!wakeTimer.Arm(RestartWakeSeconds))
            {
                wakeTimer.Dispose(); wakeTimer = null;
                status.Text = "This PC cannot wake itself from hibernate, so the fan restart was cancelled.";
                return;
            }
            restartPending = true;
            status.Text = "Hibernating... the laptop will power back on by itself.";
            var th = new Thread(delegate()
            {
                Thread.Sleep(800);
                if (PowerPlan.Hibernate()) return;
                BeginInvoke(new Action(delegate
                {
                    restartPending = false;
                    wakeTimer.Dispose(); wakeTimer = null;
                    status.Text = "Windows refused to hibernate. Fan restart cancelled.";
                }));
            });
            th.IsBackground = true;
            th.Start();
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume || IsDisposed) return;
            BeginInvoke(new Action(delegate
            {
                criticalSecs = 0;
                if (!restartPending) return;
                restartPending = false;
                if (wakeTimer != null) { wakeTimer.Dispose(); wakeTimer = null; }
                PowerPlan.KeepAwake(true);        // a timer wake goes back to sleep unless told otherwise
                awakeTimer.Start();
                ShowDashboard();
                RunFanJob(CheckFanAfterRestart, "Fan restart");
            }));
        }

        // Gives the fan full drive for up to 40 s after the restart and reports whether it kept turning.
        static string CheckFanAfterRestart(Action<string> progress)
        {
            progress("Back from hibernate - checking the fan...");
            try
            {
                int streak = 0, rpm = 0;
                for (int i = 0; i < 80 && streak < 10; i++)
                {
                    EmbeddedController.Write(Ec.FAN_LEVEL, Ec.FAN_FULL);
                    Thread.Sleep(500);
                    int r = EmbeddedController.Read16Stable(Ec.FAN_RPM, 300);
                    bool spinning = r > 0 && r < 0xFFFF;
                    streak = spinning ? streak + 1 : 0;
                    if (spinning) rpm = r;
                }
                if (Settings.FanMode == "none") EmbeddedController.Write(Ec.FAN_LEVEL, Ec.FAN_AUTO);
                return streak >= 10 ? "Fan restarted: " + rpm + " rpm."
                                    : "The fan did not keep turning after the restart. It likely needs oil or replacing.";
            }
            catch (Exception ex) { return "Fan check failed: " + ex.Message; }
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
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            timer.Dispose();
            ReleaseGuard();                   // never leave the CPU capped or the fan pinned after exit
            FanController.Suspended = true;
            FanController.Release(Settings.FanMode);
            PowerPlan.KeepAwake(false);
            if (wakeTimer != null) wakeTimer.Dispose();
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
