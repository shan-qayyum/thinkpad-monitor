// Dashboard window + tray icon. Samples once a second on a worker thread, applies the fan mode, then repaints
// the tiles and graph and runs the emergency hibernate check on the UI thread.

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

        // Graph lines: fixed, high-contrast colours that stay distinct from each other on the dark background.
        public static readonly Color LineTemp = Color.FromArgb(255, 150, 40);    // orange
        public static readonly Color LineRpm = Color.FromArgb(60, 220, 150);     // green
        public static readonly Color LineMhz = Color.FromArgb(160, 140, 255);    // violet
    }

    // Sets a label's text at the largest font size (down to a minimum) at which all of it fits, so no text is
    // ever clipped. Fonts are cached because this runs every second.
    static class TextFit
    {
        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        public static Font Get(float size, FontStyle style)
        {
            string key = size.ToString(CultureInfo.InvariantCulture) + style;
            Font f;
            if (!fonts.TryGetValue(key, out f)) { f = new Font("Segoe UI", size, style); fonts[key] = f; }
            return f;
        }

        public static void Set(Label label, string text, float maxSize, float minSize, FontStyle style, bool wrap)
        {
            TextFormatFlags flags = wrap ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine;
            Size room = new Size(label.Width - 4, label.Height);
            float size = maxSize;
            Font f = Get(size, style);
            while (size > minSize)
            {
                Size need = TextRenderer.MeasureText(text, f, new Size(wrap ? room.Width : int.MaxValue, int.MaxValue), flags);
                if (need.Width <= room.Width && need.Height <= room.Height) break;
                size -= 0.5f;
                f = Get(size, style);
            }
            if (!ReferenceEquals(label.Font, f)) label.Font = f;
            if (label.Text != text) label.Text = text;
        }
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
            Size = new Size(174, 96);
            Margin = new Padding(6);
            caption = MakeLabel(8, 20);
            value = MakeLabel(26, 40);
            detail = MakeLabel(68, 22);
            Controls.Add(caption); Controls.Add(value); Controls.Add(detail);
            caption.ForeColor = Theme.Dim;
            TextFit.Set(caption, title.ToUpperInvariant(), 8f, 6.5f, FontStyle.Bold, false);
            Set("--", Theme.Text, "");
        }

        Label MakeLabel(int top, int height)
        {
            return new Label { AutoSize = false, Left = 8, Top = top, Width = 162, Height = height, BackColor = Color.Transparent, ForeColor = Theme.Dim };
        }

        public void Set(string v, Color c, string d)
        {
            TextFit.Set(value, v, 20f, 11f, FontStyle.Bold, false);
            value.ForeColor = c;
            TextFit.Set(detail, d, 8.5f, 6.5f, FontStyle.Regular, false);
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
            Rectangle r = new Rectangle(34, 24, Width - 80, Height - 44);

            using (var font = new Font("Segoe UI", 7.5f))
            using (var bold = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (var dim = new SolidBrush(Theme.Dim))
            using (var tempBrush = new SolidBrush(Theme.LineTemp))
            using (var rpmBrush = new SolidBrush(Theme.LineRpm))
            using (var mhzBrush = new SolidBrush(Theme.LineMhz))
            using (var bad = new SolidBrush(Theme.Bad))
            {
                g.DrawString("■ CPU TEMP (°C, left scale)", bold, tempBrush, 8, 5);
                g.DrawString("■ FAN RPM (0-" + RpmScale + ")", bold, rpmBrush, 210, 5);
                g.DrawString("last 5 minutes", font, dim, 360, 6);
                SizeF mhzSize = g.MeasureString("■ CPU MHz (right scale)", bold);
                g.DrawString("■ CPU MHz (right scale)", bold, mhzBrush, Width - mhzSize.Width - 8, 5);

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
                g.DrawString("red strip below the chart = fan stopped", font, bad, r.Left, r.Bottom + 5);
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
                using (var p = new Pen(Theme.LineMhz, 2.5f) { LineJoin = LineJoin.Round }) g.DrawLines(p, mhzPts.ToArray());
                using (var p = new Pen(Theme.LineRpm, 2.5f) { LineJoin = LineJoin.Round }) if (rpmPts.Count > 1) g.DrawLines(p, rpmPts.ToArray());
                using (var p = new Pen(Theme.LineTemp, 3f) { LineJoin = LineJoin.Round }) if (tempPts.Count > 1) g.DrawLines(p, tempPts.ToArray());

                // Mark the stretches where the fan was not turning.
                using (var shade = new SolidBrush(Theme.Bad))
                    for (int i = 0; i < History.Count; i++)
                        if (History[i].EcOk && !History[i].FanSpinning)
                            g.FillRectangle(shade, r.Left + (offset + i) * dx, r.Bottom + 1, dx + 0.5f, 3);
            }
        }
    }

    public class MainForm : Form
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        const string AppVersion = "1.2.0";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "ThinkPadMonitor";
        const int AlertTemp = 90;              // C, "CPU is hot" notification
        const string CsvHeader = "time,cpu_temp_c,fan_rpm,fan_register,cpu_mhz,cpu_load_pct,on_ac,battery_pct,charger_w,charger_limit_flag,thermal_flags,firmware_limit_reason";

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

        readonly Sampler sampler = new Sampler();
        readonly Tile tTemp = new Tile("CPU temperature"), tFan = new Tile("Fan"), tClock = new Tile("CPU speed"),
                      tLoad = new Tile("CPU load"), tPower = new Tile("Power"), tLight = new Tile("ThinkLight"),
                      tPeak = new Tile("Session temperature"), tHealth = new Tile("Fan this session");
        readonly Graph graph = new Graph();
        readonly Label status = new Label();
        readonly ComboBox cbFan = new ComboBox();
        readonly CheckBox cEmergency = new CheckBox(), cLog = new CheckBox(), cTop = new CheckBox(), cStartup = new CheckBox(),
                          cAutoLock = new CheckBox();
        readonly NumericUpDown nEmergency = new NumericUpDown(), nAutoLock = new NumericUpDown();
        readonly IdleLock idleLock = new IdleLock();
        readonly Button bLight = new Button(), bRestart = new Button(), bKick = new Button(), bLogs = new Button();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem fanMenu = new ToolStripMenuItem("Fan mode");
        System.Threading.Timer timer;
        IntPtr trayIconHandle = IntPtr.Zero;
        int sampling;
        bool loading = true, exiting, busy;
        bool wasSpinning = true, hotAlerted, lockAlerted;
        int criticalSecs, ticks, lockSecs;
        string lastTrigger;                    // most recent firmware limit condition seen, e.g. "charger power limit"
        DateTime lastTriggerTime, holdStatusUntil;

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
            ClientSize = new Size(756, 648);
            StartPosition = FormStartPosition.CenterScreen;

            var tiles = new FlowLayoutPanel { Left = 6, Top = 6, Width = 748, Height = 218, BackColor = Theme.Back };
            tiles.Controls.AddRange(new Control[] { tTemp, tFan, tClock, tLoad, tPower, tLight, tPeak, tHealth });
            Controls.Add(tiles);
            graph.SetBounds(12, 226, 732, 190);
            Controls.Add(graph);

            // Row 1: fan mode, CSV logging
            Label lFan = AddLabel("Fan mode", 12, 432);
            StyleCombo(cbFan, lFan.Right + 8, 428, 250);
            cbFan.Items.AddRange(FanModes);
            StyleCheck(cLog, "Log readings to CSV", 0, 430);
            cLog.Left = 744 - cLog.Width;

            // Row 2: emergency hibernate
            StyleCheck(cEmergency, "Emergency hibernate if the CPU stays at", 12, 464);
            StyleNumber(nEmergency, cEmergency.Right + 4, 462, 80, 104, Settings.EmergencyTemp);
            AddLabel("°C or more for 5 seconds", nEmergency.Right + 6, 466);

            // Row 3: auto-lock
            StyleCheck(cAutoLock, "Lock the PC after", 12, 494);
            StyleNumber(nAutoLock, cAutoLock.Right + 4, 492, 1, 120, Settings.AutoLockMinutes);
            AddLabel("minutes without keyboard or mouse, even while a video plays", nAutoLock.Right + 6, 496);

            // Row 4: actions
            int x = 12;
            x = StyleButton(bLight, "ThinkLight", x, 530) + 8;
            x = StyleButton(bRestart, "Restart fan (reboots)", x, 530) + 8;
            x = StyleButton(bKick, "Kick-start fan (experimental)", x, 530) + 8;
            StyleButton(bLogs, "Open log folder", x, 530);

            // Row 5: window options
            StyleCheck(cTop, "Always on top", 12, 578);
            StyleCheck(cStartup, "Start with Windows", cTop.Right + 16, 578);

            status.SetBounds(12, 606, 732, 38);
            status.ForeColor = Theme.Dim;
            Controls.Add(status);
            SetStatus("Starting...");

            // Initial values (no events while loading)
            cbFan.SelectedIndex = Math.Max(0, Array.FindIndex(FanModes, delegate(Choice c) { return c.Key == Settings.FanMode; }));
            cEmergency.Checked = Settings.EmergencyEnabled;
            cAutoLock.Checked = Settings.AutoLockEnabled;
            cLog.Checked = Settings.LogCsv;
            cTop.Checked = TopMost = Settings.AlwaysOnTop;
            cStartup.Checked = StartupEnabled();
            loading = false;

            cbFan.SelectedIndexChanged += delegate { if (!loading) { Settings.FanMode = ((Choice)cbFan.SelectedItem).Key; Settings.Save(); } };
            cEmergency.CheckedChanged += delegate { if (!loading) { Settings.EmergencyEnabled = cEmergency.Checked; Settings.Save(); } };
            nEmergency.ValueChanged += delegate { if (!loading) { Settings.EmergencyTemp = (int)nEmergency.Value; Settings.Save(); } };
            cAutoLock.CheckedChanged += delegate { if (!loading) { Settings.AutoLockEnabled = cAutoLock.Checked; Settings.Save(); } };
            nAutoLock.ValueChanged += delegate { if (!loading) { Settings.AutoLockMinutes = (int)nAutoLock.Value; Settings.Save(); } };
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

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            CheckPendingFanRestart();

            if (startHidden) { ShowInTaskbar = false; WindowState = FormWindowState.Minimized; Load += delegate { Hide(); ShowInTaskbar = true; WindowState = FormWindowState.Normal; }; }

            if (!EmbeddedController.DriverAvailable())
                SetStatus("InpOut driver not found - EC metrics unavailable. Run InstallDriver.exe once (see README).");

            timer = new System.Threading.Timer(delegate { Tick(); }, null, 0, 1000);
        }

        // ---------- layout helpers (every control is sized to its text, so nothing is clipped) ----------

        Label AddLabel(string text, int x, int y)
        {
            var l = new Label { Text = text, Left = x, Top = y, AutoSize = true, ForeColor = Theme.Dim };
            Controls.Add(l);
            return l;
        }

        void StyleCombo(ComboBox c, int x, int y, int w)
        {
            c.SetBounds(x, y, w, 24);
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            c.BackColor = Theme.Tile; c.ForeColor = Theme.Text;
            // A stray scroll over a focused dropdown must not change the fan mode.
            c.MouseWheel += delegate(object s, MouseEventArgs e) { if (!c.DroppedDown) ((HandledMouseEventArgs)e).Handled = true; };
            Controls.Add(c);
        }

        void StyleCheck(CheckBox c, string text, int x, int y)
        {
            c.Text = text; c.Left = x; c.Top = y; c.AutoSize = true; c.ForeColor = Theme.Text;
            Controls.Add(c);
        }

        void StyleNumber(NumericUpDown n, int x, int y, int min, int max, int value)
        {
            n.SetBounds(x, y, 52, 22);
            n.Minimum = min; n.Maximum = max; n.Value = Math.Max(min, Math.Min(max, value));
            n.BackColor = Theme.Tile; n.ForeColor = Theme.Text; n.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(n);
        }

        // Returns the button's right edge.
        int StyleButton(Button b, string text, int x, int y)
        {
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat; b.BackColor = Theme.Tile; b.ForeColor = Theme.Text;
            b.FlatAppearance.BorderColor = Theme.Grid;
            b.Font = TextFit.Get(9f, FontStyle.Bold);
            int w = TextRenderer.MeasureText(text, b.Font).Width + 32;
            b.SetBounds(x, y, w, 36);
            Controls.Add(b);
            return b.Right;
        }

        void SetStatus(string text) { TextFit.Set(status, text, 9f, 7f, FontStyle.Regular, true); }

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
            menu.Items.Add(fanMenu);
            menu.Items.Add("Restart fan (reboots)", null, delegate { ShowDashboard(); RestartFan(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { exiting = true; Close(); });
            menu.Opening += delegate
            {
                for (int i = 0; i < fanMenu.DropDownItems.Count; i++) ((ToolStripMenuItem)fanMenu.DropDownItems[i]).Checked = i == cbFan.SelectedIndex;
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

            if (s.EcOk)
            {
                statSamples++; statSum += s.CpuTemp;
                statMin = Math.Min(statMin, s.CpuTemp); statMax = Math.Max(statMax, s.CpuTemp);
                if (s.FanSpinning) statSpinning++;

                tTemp.Set(s.CpuTemp + " °C", Theme.ForTemp(s.CpuTemp),
                    s.CpuTemp >= AlertTemp ? "Hot - Intel limit 105 °C" : "Intel limit 105 °C");
                if (s.FanSpinning) tFan.Set(s.FanRpm + " rpm", Theme.Good, s.FanModeText);
                else tFan.Set("STOPPED", Theme.Bad, "Commanded: " + s.FanModeText);
                tLight.Set(s.ThinkLightOn ? "ON" : "OFF", s.ThinkLightOn ? Theme.Warn : Theme.Dim, "Use the ThinkLight button");
                tPeak.Set("max " + statMax + " °C", Theme.ForTemp(statMax),
                    string.Format("min {0} °C  ·  avg {1} °C", statMin, statSum / statSamples));
                int pct = statSpinning * 100 / statSamples;
                tHealth.Set(pct + " % turning", pct >= 95 ? Theme.Good : pct >= 50 ? Theme.Warn : Theme.Bad,
                    statStalls == 1 ? "1 stall" : statStalls + " stalls");
            }
            else
            {
                tTemp.Set("n/a", Theme.Dim, "EC unavailable");
                tFan.Set("n/a", Theme.Dim, "EC unavailable");
                tLight.Set("n/a", Theme.Dim, "EC unavailable");
            }

            bool locked = false;
            if (s.CpuMhz > 0)
            {
                string why = s.FirmwareLimitReason;
                if (why != null) { lastTrigger = why; lastTriggerTime = s.Time; }
                // The firmware's conditions often clear within a second while Windows keeps the limit it was
                // handed, so the CPU can stay at minimum speed with no condition active at that moment.
                bool held = s.HeldAtMinimum;
                lockSecs = held ? lockSecs + 1 : 0;
                locked = lockSecs >= 10;
                if (locked && !lockAlerted)
                {
                    lockAlerted = true;
                    tray.ShowBalloonTip(6000, "CPU held at minimum speed",
                        "The firmware is holding the CPU at " + s.CpuMhz + " MHz under load" + TriggerText() + ".", ToolTipIcon.Warning);
                }
                else if (!held) lockAlerted = false;
                string d = held ? (why != null ? "Held: " + why : "Held by firmware")
                         : s.CpuAtMinimum ? "Minimum clock" : "of " + s.CpuMaxMhz + " MHz max";
                tClock.Set(s.CpuMhz + " MHz", held ? Theme.Bad : s.CpuAtMinimum ? Theme.Warn : Theme.Good, d);
            }
            else tClock.Set("n/a", Theme.Dim, "counter unavailable");

            tLoad.Set(s.CpuLoad + " %", s.CpuLoad >= 85 ? Theme.Warn : Theme.Text, Environment.ProcessorCount + " logical CPUs");
            tPower.Set(s.BatteryPercent >= 0 ? s.BatteryPercent + " %" : (s.OnAc ? "AC" : "?"),
                s.BatteryPercent >= 0 && s.BatteryPercent < 20 && !s.OnAc ? Theme.Bad : Theme.Text,
                s.EcOk && s.ChargerWatts > 0 ? s.ChargerWatts + " W charger  ·  " + s.BatteryState : s.BatteryState);

            if (!busy && s.Time >= holdStatusUntil)
                SetStatus(!s.EcOk ? "EC read failed: " + s.EcError
                    : "Updated " + s.Time.ToString("HH:mm:ss")
                      + (locked ? "   •   Firmware is holding the CPU at minimum speed" + TriggerText() + "." : "")
                      + (s.FanSpinning ? "" : "   •   Fan not turning: try Restart fan, keep the load light."));

            UpdateTray(s);
            Alerts(s);
            Emergency(s);
            AutoLock();
            if (Settings.LogCsv && ticks % 5 == 0) LogCsv(s);
        }

        string TriggerText()
        {
            return lastTrigger == null ? "" : " (last trigger: " + lastTrigger + " at " + lastTriggerTime.ToString("HH:mm:ss") + ")";
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

        void Emergency(Sample s)
        {
            if (!s.EcOk || !Settings.EmergencyEnabled || s.CpuTemp < Settings.EmergencyTemp) { criticalSecs = 0; return; }
            if (++criticalSecs < 5) return;
            criticalSecs = 0;
            tray.ShowBalloonTip(5000, "Emergency hibernate", "CPU stayed at " + s.CpuTemp + " °C. Hibernating now.", ToolTipIcon.Error);
            var th = new Thread(delegate()
            {
                Thread.Sleep(1500);
                int err = PowerPlan.Hibernate();
                if (err == 0) return;
                BeginInvoke(new Action(delegate
                {
                    SetStatus("Emergency hibernate failed: Windows refused (error " + err + "). Shut the laptop down now.");
                    holdStatusUntil = DateTime.Now.AddSeconds(60);
                    tray.ShowBalloonTip(10000, "Emergency hibernate failed",
                        "Windows refused to hibernate (error " + err + "). Shut the laptop down now.", ToolTipIcon.Error);
                }));
            });
            th.IsBackground = true;
            th.Start();
        }

        static string CsvField(string v)
        {
            if (v == null) return "";
            return v.IndexOfAny(new[] { ',', '"', '\n' }) < 0 ? v : "\"" + v.Replace("\"", "\"\"") + "\"";
        }

        void LogCsv(Sample s)
        {
            try
            {
                Directory.CreateDirectory(Settings.LogFolder);
                string path = Path.Combine(Settings.LogFolder, s.Time.ToString("yyyy-MM-dd") + ".csv");
                if (File.Exists(path))
                {
                    string first;
                    using (var r = new StreamReader(path)) first = r.ReadLine();
                    if (first != CsvHeader)                 // written by an older version with other columns
                        File.Move(path, Path.Combine(Settings.LogFolder, s.Time.ToString("yyyy-MM-dd") + "-old-" + DateTime.Now.Ticks + ".csv"));
                }
                if (!File.Exists(path)) File.WriteAllText(path, CsvHeader + "\r\n");
                File.AppendAllText(path, string.Format(CultureInfo.InvariantCulture, "{0:HH:mm:ss},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11}\r\n",
                    s.Time,
                    s.EcOk ? s.CpuTemp.ToString(CultureInfo.InvariantCulture) : "",
                    s.EcOk ? Math.Max(0, s.FanRpm).ToString(CultureInfo.InvariantCulture) : "",
                    s.EcOk ? "0x" + s.FanLevel.ToString("X2") : "",
                    s.CpuMhz, s.CpuLoad, s.OnAc ? 1 : 0, s.BatteryPercent,
                    s.EcOk ? s.ChargerWatts.ToString(CultureInfo.InvariantCulture) : "",
                    s.EcOk ? (s.ChargerLimit ? "1" : "0") : "",
                    s.EcOk ? "0x" + s.ThermalFlags.ToString("X2") : "",
                    CsvField(s.FirmwareLimitReason)));
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
            catch (Exception ex) { SetStatus("ThinkLight: " + ex.Message); }
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
                string result = job(delegate(string msg) { BeginInvoke(new Action(delegate { SetStatus(msg); })); });
                BeginInvoke(new Action(delegate
                {
                    SetBusy(false);
                    SetStatus(result);
                    holdStatusUntil = DateTime.Now.AddSeconds(20);   // keep the result readable
                    tray.ShowBalloonTip(4000, balloonTitle, result, ToolTipIcon.Info);
                }));
            });
            th.IsBackground = true;
            th.Start();
        }

        void KickFan() { RunFanJob(FanKicker.Run, "Fan kick"); }

        // The firmware drives the fan blind while the machine boots, which can free a fan the EC has stopped driving.
        // A normal Windows restart triggers that and always comes back by itself (timed wake from hibernate proved
        // unreliable on the X230). The fan is checked once the app starts again after the reboot.
        void RestartFan()
        {
            if (busy) return;
            DialogResult answer = MessageBox.Show(this,
                "This restarts Windows. Save your work first: open programs will close.\n\n" +
                "While the laptop boots, its firmware runs the fan at full power, which can get a stuck fan turning again. " +
                "When ThinkPad Monitor starts after the restart, it checks the fan and shows the result.\n\nRestart now?",
                "Restart fan", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            Settings.FanCheckAfter = DateTime.Now.Ticks;
            Settings.Save();
            if (!StartupEnabled()) ScheduleRunOnce();          // so the app runs once after the reboot to check the fan
            SetStatus("Restarting Windows...");
            if (PowerPlan.Restart()) return;
            Settings.FanCheckAfter = 0;
            Settings.Save();
            SetStatus("Windows could not start the restart. Nothing was changed.");
        }

        static void ScheduleRunOnce()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce"))
                    k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
            }
            catch (Exception) { }                            // the check then simply runs at the next manual start
        }

        // Runs the fan check if "Restart fan" was clicked before the most recent boot.
        void CheckPendingFanRestart()
        {
            if (Settings.FanCheckAfter == 0) return;
            DateTime clicked = new DateTime(Settings.FanCheckAfter);
            if (PowerPlan.LastBoot < clicked) return;          // not rebooted yet (e.g. the restart was cancelled)
            Settings.FanCheckAfter = 0;
            Settings.Save();
            if ((DateTime.Now - clicked).TotalHours > 1) return; // too long ago to be this restart's result
            var t = new System.Windows.Forms.Timer { Interval = 5000 };
            t.Tick += delegate { t.Stop(); t.Dispose(); ShowDashboard(); RunFanJob(CheckFanAfterRestart, "Fan restart"); };
            t.Start();
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock) idleLock.SessionLocked = true;
            else if (e.Reason == SessionSwitchReason.SessionUnlock) idleLock.SessionLocked = false;
        }

        void AutoLock()
        {
            IdleLock.Step step = idleLock.Tick(Settings.AutoLockEnabled, Settings.AutoLockMinutes);
            if (step == IdleLock.Step.Warn)
                tray.ShowBalloonTip(IdleLock.WarningSeconds * 1000, "Locking in " + IdleLock.WarningSeconds + " seconds",
                    "No keyboard or mouse use for a while. Move the mouse to stay unlocked.", ToolTipIcon.Info);
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume && !IsDisposed) BeginInvoke(new Action(delegate { criticalSecs = 0; }));
        }

        // Gives the fan full drive for up to 40 s after the restart and reports whether it kept turning.
        static string CheckFanAfterRestart(Action<string> progress)
        {
            progress("After the restart - checking the fan...");
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
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            timer.Dispose();
            FanController.Suspended = true;
            FanController.Release(Settings.FanMode);   // never leave the fan pinned at a manual level after exit
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
