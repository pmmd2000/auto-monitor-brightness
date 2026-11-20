using System.Diagnostics;
using Microsoft.Win32;

namespace AutoMonitorBrightness;

public sealed class TrayApp : ApplicationContext
{
    readonly Controller ctl;
    readonly NotifyIcon tray;
    readonly ContextMenuStrip menu;
    readonly PowerWatcher power;
    readonly SynchronizationContext ui;
    readonly RegisteredWaitHandle showWait;
    readonly ToolStripMenuItem statusItem, infoItem, resumeItem, pauseMenu;
    readonly Icon trayIcon;
    SettingsForm? settingsForm;
    SliderPopup? slider;
    bool locationBalloonShown;

    public TrayApp(Controller ctl, bool fromAutostart, EventWaitHandle showSignal)
    {
        this.ctl = ctl;
        menu = new ContextMenuStrip { ShowImageMargin = false };
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        infoItem = new ToolStripMenuItem("") { Enabled = false };

        pauseMenu = new ToolStripMenuItem("Pause");
        pauseMenu.DropDownItems.Add("For 1 hour", null, (_, _) => ctl.Pause(DateTime.UtcNow.AddHours(1), "paused for 1 hour"));
        pauseMenu.DropDownItems.Add("For 3 hours", null, (_, _) => ctl.Pause(DateTime.UtcNow.AddHours(3), "paused for 3 hours"));
        pauseMenu.DropDownItems.Add("Until tomorrow morning", null, (_, _) =>
            ctl.Pause(DateTime.Today.AddDays(1).AddHours(4).ToUniversalTime(), "paused until tomorrow"));
        resumeItem = new ToolStripMenuItem("Back to automatic", null, (_, _) => ctl.Resume());

        var advanced = new ToolStripMenuItem("Advanced");
        advanced.DropDownItems.Add("Advanced settings…", null, (_, _) => ShowAdvanced());
        advanced.DropDownItems.Add("Re-apply brightness now", null, (_, _) => _ = ctl.TickAsync(TickKind.Force, "re-apply requested"));
        advanced.DropDownItems.Add("Open log", null, (_, _) => OpenLog());
        advanced.DropDownItems.Add(new ToolStripSeparator());
        advanced.DropDownItems.Add("Uninstall Auto Monitor Brightness…", null, (_, _) => Uninstall());

        var settings = new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()) { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.AddRange(new ToolStripItem[]
        {
            statusItem, infoItem,
            new ToolStripSeparator(),
            settings, pauseMenu, resumeItem, advanced,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        });
        menu.Opening += (_, _) => RefreshMenu();

        trayIcon = AppIcon.Sized(SystemInformation.SmallIconSize);
        tray = new NotifyIcon { ContextMenuStrip = menu, Icon = trayIcon, Text = "Auto Monitor Brightness", Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleSlider(); };
        tray.BalloonTipClicked += (_, _) => ShowSettings();

        power = new PowerWatcher();
        power.Woke += reason => Wake(reason);
        power.DisplaysChanged += () => Delayed(TickKind.Wake, "display configuration changed", 4);
        SystemEvents.SessionSwitch += OnSessionSwitch;

        ctl.Changed += () => ui.Post(_ => RefreshUi(), null);
        showWait = ThreadPool.RegisterWaitForSingleObject(showSignal,
            (_, _) => ui.Post(_ => ShowSettings(), null), null, Timeout.Infinite, false);

        ctl.Start();

        // First run (no location yet): open the window so the two sliders can be set. The location is found automatically.
        if (!ctl.Settings.HasLocation && !fromAutostart) ShowSettings();
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            Delayed(TickKind.Wake, "session unlocked", 2);
    }

    void Wake(string reason)
    {
        // Monitors often ignore DDC/CI for a few seconds after waking, so try twice.
        Delayed(TickKind.Wake, reason, 4);
        Delayed(TickKind.Wake, reason + " (retry)", 20);
    }

    void Delayed(TickKind kind, string reason, int seconds) =>
        _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ => ctl.TickAsync(kind, reason));

    int ShownBrightness()
    {
        var vals = ctl.Monitors.Where(m => m.Enabled && m.Reachable && m.Percent >= 0).Select(m => m.Percent).ToList();
        if (vals.Count > 0) return (int)Math.Round(vals.Average());
        return ctl.LastEvaluation?.Target ?? -1;
    }

    string StatusText()
    {
        if (!ctl.Settings.HasLocation) return ctl.DetectingLocation ? "Finding your location…" : "Location needed: open Settings";
        int b = ShownBrightness();
        string level = b >= 0 ? $"Brightness {b}%" : "Brightness —";
        if (ctl.IsPaused)
            return ctl.PausedUntilUtc == DateTime.MaxValue
                ? $"{level} · paused"
                : $"{level} · paused until {ctl.PausedUntilUtc!.Value.ToLocalTime():HH:mm}";
        return $"{level} · automatic";
    }

    string InfoText()
    {
        var e = ctl.LastEvaluation;
        if (e == null) return "";
        string text = e.Today.Sunrise is DateTime sr && e.Today.Sunset is DateTime ss
            ? $"Sunrise {sr:HH:mm} · Sunset {ss:HH:mm}"
            : $"Sun {e.Elevation:0}°";
        if (ctl.Settings.UseWeather && ctl.Weather is { } w) text += $" · {w.CloudCover:0}% cloud";
        return text;
    }

    void RefreshMenu()
    {
        statusItem.Text = StatusText();
        infoItem.Text = InfoText();
        infoItem.Visible = infoItem.Text.Length > 0;
        resumeItem.Visible = ctl.IsPaused;
        pauseMenu.Visible = !ctl.IsPaused;
    }

    void RefreshUi()
    {
        var tip = "Auto Monitor Brightness\n" + StatusText();
        tray.Text = tip.Length > 127 ? tip[..127] : tip;
        if (menu.Visible) RefreshMenu();
        if (slider is { Visible: true }) slider.SetStatus(ShownBrightness(), StatusText());
        settingsForm?.OnControllerChanged();

        bool windowOpen = settingsForm is { IsDisposed: false, Visible: true };
        if (!ctl.Settings.HasLocation && ctl.LocationError != null && !ctl.DetectingLocation && !windowOpen && !locationBalloonShown)
        {
            locationBalloonShown = true;
            tray.ShowBalloonTip(10000, "Auto Monitor Brightness", "Couldn't find your location automatically. Click here to choose your city.", ToolTipIcon.Info);
        }
    }

    void ToggleSlider()
    {
        slider ??= CreateSlider();
        if (slider.Visible) { slider.Hide(); return; }
        slider.ShowNear(Cursor.Position, ShownBrightness(), StatusText());
    }

    SliderPopup CreateSlider()
    {
        var s = new SliderPopup();
        s.ValueCommitted += v => _ = ctl.SetManualAsync(v);
        s.AutoRequested += () => ctl.Resume();
        s.SettingsRequested += () => ShowSettings();
        return s;
    }

    SettingsForm ShowSettings()
    {
        if (settingsForm == null || settingsForm.IsDisposed)
            settingsForm = new SettingsForm(ctl);
        settingsForm.Show();
        if (settingsForm.WindowState == FormWindowState.Minimized) settingsForm.WindowState = FormWindowState.Normal;
        settingsForm.Activate();
        return settingsForm;
    }

    void ShowAdvanced()
    {
        var f = ShowSettings();
        ui.Post(_ => f.OpenAdvanced(), null);
    }

    static void OpenLog()
    {
        Log.Write("Log opened");
        Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
    }

    void Uninstall()
    {
        var answer = MessageBox.Show(
            "Remove Auto Monitor Brightness from this PC?\n\nThis stops it, removes it from startup and deletes its settings. " +
            "Your monitors stay at their current brightness.",
            "Uninstall Auto Monitor Brightness", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) return;
        ExitThread();
        Installer.Uninstall();
    }

    protected override void ExitThreadCore()
    {
        Log.Write("Auto Monitor Brightness exiting");
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        showWait.Unregister(null);
        ctl.Dispose();
        power.Dispose();
        tray.Visible = false;
        tray.Dispose();
        trayIcon.Dispose();
        slider?.Dispose();
        settingsForm?.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>Small popup with a slider, opened by left-clicking the tray icon.</summary>
public sealed class SliderPopup : Form
{
    readonly TrackBar bar;
    readonly Label valueLabel, statusLabel;
    readonly System.Windows.Forms.Timer debounce = new() { Interval = 250 };
    bool suppress;

    public event Action<int>? ValueCommitted;
    public event Action? AutoRequested;
    public event Action? SettingsRequested;

    public SliderPopup()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Text = "Auto Monitor Brightness";
        Font = new Font("Segoe UI", 9F);
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        MaximizeBox = MinimizeBox = false;
        KeyPreview = true;

        valueLabel = new Label { AutoSize = true, Font = new Font("Segoe UI", 16F, FontStyle.Bold), Margin = new Padding(3, 0, 3, 0) };
        bar = new TrackBar { Minimum = 0, Maximum = 100, TickFrequency = 10, SmallChange = 1, LargeChange = 10, Width = 260 };
        statusLabel = new Label { AutoSize = true, MaximumSize = new Size(280, 0), ForeColor = SystemColors.GrayText };
        var auto = new Button { Text = "Back to automatic", AutoSize = true };
        var settings = new LinkLabel { Text = "Settings…", AutoSize = true, Margin = new Padding(10, 9, 3, 3) };

        bar.ValueChanged += (_, _) =>
        {
            valueLabel.Text = $"{bar.Value}%";
            if (suppress) return;
            debounce.Stop();
            debounce.Start();
        };
        debounce.Tick += (_, _) => { debounce.Stop(); ValueCommitted?.Invoke(bar.Value); };
        auto.Click += (_, _) => { AutoRequested?.Invoke(); Hide(); };
        settings.LinkClicked += (_, _) => { Hide(); SettingsRequested?.Invoke(); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        buttons.Controls.AddRange(new Control[] { auto, settings });
        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10), WrapContents = false,
        };
        layout.Controls.AddRange(new Control[] { valueLabel, bar, statusLabel, buttons });
        Controls.Add(layout);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ResumeLayout(true);
    }

    public void SetStatus(int value, string status)
    {
        statusLabel.Text = status + "\nMoving the slider pauses automatic control for a while.";
        if (value >= 0 && !bar.Focused)
        {
            suppress = true;
            bar.Value = Math.Clamp(value, 0, 100);
            valueLabel.Text = $"{bar.Value}%";
            suppress = false;
        }
    }

    public void ShowNear(Point cursor, int value, string status)
    {
        SetStatus(value, status);
        if (value < 0) valueLabel.Text = "—";
        Show();
        var wa = Screen.FromPoint(cursor).WorkingArea;
        int x = Math.Clamp(cursor.X - Width / 2, wa.Left, wa.Right - Width);
        int y = Math.Clamp(cursor.Y - Height - 12, wa.Top, wa.Bottom - Height);
        Location = new Point(x, y);
        Activate();
        bar.Focus();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }
}
