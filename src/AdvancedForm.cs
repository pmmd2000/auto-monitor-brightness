using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace Brightly;

public sealed class AdvancedForm : Form
{
    readonly Controller ctl;
    readonly AppSettings original;
    bool loading = true;
    bool locationSet;
    string? placeName;

    // Location
    readonly TextBox cityBox = new() { Width = 220 };
    readonly Button searchBtn = new() { Text = "Search", AutoSize = true };
    readonly CheckBox autoLoc = new() { Text = "Detect automatically from my internet connection", AutoSize = true };
    readonly ComboBox cityResults = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420, Visible = false };
    readonly NumericUpDown lat = Num(-90, 90, 4), lon = Num(-180, 180, 4);
    readonly Label placeLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    readonly Label sunLabel = new() { AutoSize = true };

    // Brightness
    readonly RadioButton modeSun = new() { Text = "Follow the sun", AutoSize = true };
    readonly RadioButton modeSchedule = new() { Text = "Custom schedule (Schedule tab)", AutoSize = true };
    readonly NumericUpDown nightElev = Num(-18, 10, 0), fullElev = Num(1, 70, 0);

    // Weather
    readonly NumericUpDown weatherStrength = Num(0, 100, 0), weatherRefresh = Num(5, 120, 0);
    readonly Label weatherLabel = new() { AutoSize = true };

    // Schedule / monitors
    readonly DataGridView scheduleGrid = NewGrid();
    readonly DataGridView monitorGrid = NewGrid();

    // Behaviour
    readonly NumericUpDown maxStep = Num(0, 100, 0), hysteresis = Num(1, 10, 0), overrideMin = Num(5, 1440, 0);
    readonly CheckBox detectManual = new() { Text = "Pause automatic control when brightness is changed elsewhere (monitor buttons, other apps)", AutoSize = true };

    readonly PreviewChart preview = new() { Height = 230, Dock = DockStyle.Fill, Margin = new Padding(3, 10, 3, 3) };

    const int ColAnchor = 0, ColValue = 1, ColBright = 2, ColToday = 3;

    public AdvancedForm(Controller ctl)
    {
        this.ctl = ctl;
        original = ctl.Settings.Clone();

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Brightly · Advanced settings";
        Icon = AppIcon.Full;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 740);
        MinimumSize = new Size(640, 560);
        Font = new Font("Segoe UI", 9F);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("General", BuildGeneral()));
        tabs.TabPages.Add(Page("Schedule", BuildSchedule()));
        tabs.TabPages.Add(Page("Monitors", BuildMonitors()));
        tabs.TabPages.Add(Page("Behaviour", BuildBehaviour()));

        var save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(90, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => SaveAndClose();
        cancel.Click += (_, _) => Close();
        CancelButton = cancel;
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8),
        };
        footer.Controls.AddRange(new Control[] { cancel, save });

        Controls.Add(tabs);
        Controls.Add(footer);
        ResumeLayout(true);

        LoadFrom(original);
        WireChanges();
        loading = false;
        UpdatePreview();
    }

    // ---------- layout helpers ----------

    static NumericUpDown Num(decimal min, decimal max, int decimals) => new()
    {
        Minimum = min, Maximum = max, DecimalPlaces = decimals, Width = decimals > 0 ? 100 : 70,
        Increment = decimals > 0 ? 0.01m : 1,
    };

    static DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EditMode = DataGridViewEditMode.EditOnEnter,
        BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle,
        SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = false,
    };

    static TabPage Page(string title, Control content)
    {
        var p = new TabPage(title) { Padding = new Padding(6), UseVisualStyleBackColor = true };
        p.Controls.Add(content);
        return p;
    }

    static TableLayoutPanel Stack()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    static void AddRow(TableLayoutPanel stack, Control c, bool fill = false)
    {
        stack.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        c.Dock = DockStyle.Fill;
        stack.Controls.Add(c, 0, stack.RowStyles.Count - 1);
    }

    static GroupBox Group(string title, params (string label, Control[] controls)[] rows)
    {
        var g = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 4, 8, 8) };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (label, controls) in rows)
        {
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Dock = DockStyle.Fill };
            foreach (var c in controls) flow.Controls.Add(c);
            if (label.Length == 0)
            {
                t.Controls.Add(flow);
                t.SetColumnSpan(flow, 2);
            }
            else
            {
                t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 10, 3) });
                t.Controls.Add(flow);
            }
        }
        g.Controls.Add(t);
        return g;
    }

    static Label Hint(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 7, 3, 3),
    };

    // ---------- tabs ----------

    Control BuildGeneral()
    {
        var s = Stack();
        AddRow(s, Group("Location",
            ("", new Control[] { autoLoc }),
            ("Or pick a city", new Control[] { cityBox, searchBtn }),
            ("", new Control[] { cityResults }),
            ("Latitude / longitude", new Control[] { lat, lon, placeLabel }),
            ("Today", new Control[] { sunLabel })));

        AddRow(s, Group("Curve (day and night brightness are on the main settings window)",
            ("Mode", new Control[] { modeSun, modeSchedule }),
            ("Start brightening at sun", new Control[] { nightElev, Hint("° elevation  (−6° = civil dawn, 0° = sunrise)") }),
            ("Full brightness at sun", new Control[] { fullElev, Hint("° elevation  (higher = slower morning ramp)") })));

        AddRow(s, Group("Cloud dimming",
            ("Overcast effect", new Control[] { weatherStrength, Hint("%  (how far a fully overcast sky pulls toward night brightness)") }),
            ("Now", new Control[] { weatherLabel })));

        var chartBox = new GroupBox { Text = "Today's curve", Dock = DockStyle.Fill, Padding = new Padding(8) };
        chartBox.Controls.Add(preview);
        chartBox.MinimumSize = new Size(0, 250);
        AddRow(s, chartBox, fill: true);
        return s;
    }

    Control BuildSchedule()
    {
        var s = Stack();
        AddRow(s, Hint("Used when Mode = Custom schedule. Each point is pinned to a clock time or to a sun event that moves through the year.\n" +
                       "Brightness glides linearly between points. Offsets are in minutes, e.g. +60 = an hour after sunrise. Weather dimming still applies."));

        var anchorCol = new DataGridViewComboBoxColumn { HeaderText = "Anchor", FlatStyle = FlatStyle.Flat, FillWeight = 30 };
        anchorCol.Items.AddRange(ScheduleMath.AnchorLabels.Select(a => (object)a.Label).ToArray());
        scheduleGrid.Columns.Add(anchorCol);
        scheduleGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Time (HH:mm) or offset (± min)", FillWeight = 30 });
        scheduleGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Brightness %", FillWeight = 20 });
        scheduleGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Today at", ReadOnly = true, FillWeight = 20 });
        scheduleGrid.Columns[ColToday].DefaultCellStyle.ForeColor = SystemColors.GrayText;
        scheduleGrid.Columns[ColToday].DefaultCellStyle.BackColor = SystemColors.Control;
        AddRow(s, scheduleGrid, fill: true);

        var add = new Button { Text = "Add point", AutoSize = true };
        var remove = new Button { Text = "Remove selected", AutoSize = true };
        var reset = new Button { Text = "Reset to defaults", AutoSize = true };
        add.Click += (_, _) => { AddScheduleRow(new SchedulePoint { Anchor = SunAnchor.Clock, Time = "12:00", Brightness = 50 }); UpdatePreview(); };
        remove.Click += (_, _) =>
        {
            if (scheduleGrid.CurrentCell is { RowIndex: >= 0 } c) scheduleGrid.Rows.RemoveAt(c.RowIndex);
            UpdatePreview();
        };
        reset.Click += (_, _) =>
        {
            scheduleGrid.Rows.Clear();
            foreach (var p in AppSettings.DefaultSchedule()) AddScheduleRow(p);
            UpdatePreview();
        };
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.AddRange(new Control[] { add, remove, reset });
        AddRow(s, buttons);
        return s;
    }

    Control BuildMonitors()
    {
        var s = Stack();
        AddRow(s, Hint("Offset is added to the computed brightness for that screen, to match panels that look brighter or dimmer.\n" +
                       "Monitors need DDC/CI switched on in their on-screen menu (it is on by default on Dell)."));
        monitorGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Monitor", ReadOnly = true, FillWeight = 40 });
        monitorGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Now", ReadOnly = true, FillWeight = 20 });
        monitorGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Controlled", FillWeight = 20 });
        monitorGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Offset", FillWeight = 20 });
        monitorGrid.Columns[0].DefaultCellStyle.BackColor = SystemColors.Control;
        monitorGrid.Columns[1].DefaultCellStyle.BackColor = SystemColors.Control;
        AddRow(s, monitorGrid, fill: true);
        return s;
    }

    Control BuildBehaviour()
    {
        var s = Stack();
        var openFolder = new LinkLabel { Text = "Open settings and log folder", AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
        openFolder.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(Log.Dir) { UseShellExecute = true });

        AddRow(s, Group("Changes",
            ("Max change per minute", new Control[] { maxStep, Hint("points  (0 = jump instantly; after sleep/unlock it always jumps)") }),
            ("Ignore differences below", new Control[] { hysteresis, Hint("points") })));
        AddRow(s, Group("Manual control",
            ("", new Control[] { detectManual }),
            ("Pause for", new Control[] { overrideMin, Hint("minutes after a manual change (also used by the tray slider)") })));
        AddRow(s, Group("Data",
            ("Weather refresh every", new Control[] { weatherRefresh, Hint("minutes") }),
            ("", new Control[] { openFolder })));
        AddRow(s, new Panel(), fill: true);
        return s;
    }

    // ---------- load / save ----------

    void LoadFrom(AppSettings s)
    {
        locationSet = s.HasLocation;
        placeName = s.PlaceName;
        lat.Value = (decimal)Math.Clamp(s.Latitude ?? 0, -90, 90);
        lon.Value = (decimal)Math.Clamp(s.Longitude ?? 0, -180, 180);
        placeLabel.Text = s.HasLocation ? (s.PlaceName ?? "") : "not set";
        autoLoc.Checked = s.AutoLocation;

        modeSun.Checked = s.Mode == BrightnessMode.Sun;
        modeSchedule.Checked = s.Mode == BrightnessMode.Schedule;
        nightElev.Value = (decimal)Math.Clamp(s.NightElevation, -18, 10);
        fullElev.Value = (decimal)Math.Clamp(s.FullBrightnessElevation, 1, 70);

        weatherStrength.Value = Math.Clamp(s.WeatherStrength, 0, 100);
        weatherRefresh.Value = Math.Clamp(s.WeatherRefreshMinutes, 5, 120);

        scheduleGrid.Rows.Clear();
        foreach (var p in s.Schedule) AddScheduleRow(p);

        maxStep.Value = Math.Clamp(s.MaxStepPerMinute, 0, 100);
        hysteresis.Value = Math.Clamp(s.Hysteresis, 1, 10);
        detectManual.Checked = s.DetectManualChanges;
        overrideMin.Value = Math.Clamp(s.OverrideMinutes, 5, 1440);

        FillMonitors(s);
    }

    void FillMonitors(AppSettings s)
    {
        monitorGrid.Rows.Clear();
        var live = ctl.Monitors;
        foreach (var m in live)
        {
            var ms = s.FindMonitor(m.Id);
            int i = monitorGrid.Rows.Add(m.Name, m.Reachable && m.Percent >= 0 ? $"{m.Percent}%" : "no DDC/CI reply",
                ms?.Enabled ?? true, (ms?.Offset ?? 0).ToString());
            monitorGrid.Rows[i].Tag = m.Id;
        }
        foreach (var ms in s.Monitors.Where(ms => live.All(m => m.Id != ms.Id)))
        {
            int i = monitorGrid.Rows.Add(ms.Name, "not connected", ms.Enabled, ms.Offset.ToString());
            monitorGrid.Rows[i].Tag = ms.Id;
        }
    }

    void AddScheduleRow(SchedulePoint p)
    {
        string value = p.Anchor == SunAnchor.Clock ? p.Time
            : p.OffsetMinutes == 0 ? "0" : p.OffsetMinutes.ToString("+0;-0", CultureInfo.InvariantCulture);
        scheduleGrid.Rows.Add(ScheduleMath.Label(p.Anchor), value, p.Brightness.ToString(), "");
    }

    List<SchedulePoint> ReadSchedule(List<string>? errors)
    {
        var list = new List<SchedulePoint>();
        foreach (DataGridViewRow row in scheduleGrid.Rows)
        {
            var anchor = ScheduleMath.FromLabel(row.Cells[ColAnchor].Value as string);
            var valueText = (row.Cells[ColValue].Value?.ToString() ?? "").Trim();
            var p = new SchedulePoint { Anchor = anchor };
            bool ok = true;
            if (anchor == SunAnchor.Clock)
            {
                if (ScheduleMath.TryParseClock(valueText, out var ts)) p.Time = ts.ToString(@"hh\:mm");
                else { ok = false; errors?.Add($"Schedule row {row.Index + 1}: \"{valueText}\" is not a time like 07:30."); }
            }
            else
            {
                if (valueText.Length == 0) valueText = "0";
                if (int.TryParse(valueText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int off) && Math.Abs(off) <= 720)
                    p.OffsetMinutes = off;
                else { ok = false; errors?.Add($"Schedule row {row.Index + 1}: offset \"{valueText}\" must be minutes like +30 or -45."); }
            }
            if (int.TryParse(row.Cells[ColBright].Value?.ToString(), out int b) && b is >= 0 and <= 100) p.Brightness = b;
            else { ok = false; errors?.Add($"Schedule row {row.Index + 1}: brightness must be 0–100."); }
            if (ok) list.Add(p);
        }
        return list;
    }

    AppSettings Build(List<string>? errors)
    {
        // Start from the live settings so a location detected while this window was open isn't lost.
        var s = ctl.Settings.Clone();
        s.AutoLocation = autoLoc.Checked;
        if (locationSet)
        {
            s.Latitude = (double)lat.Value;
            s.Longitude = (double)lon.Value;
            s.PlaceName = placeName;
        }
        else if (!s.AutoLocation)
        {
            s.Latitude = s.Longitude = null;
            errors?.Add("Pick a city, or turn on automatic detection, so brightness can follow the sun.");
        }
        s.Mode = modeSchedule.Checked ? BrightnessMode.Schedule : BrightnessMode.Sun;
        s.NightElevation = (double)nightElev.Value;
        s.FullBrightnessElevation = (double)fullElev.Value;
        if (s.FullBrightnessElevation <= s.NightElevation) errors?.Add("Full-brightness sun elevation must be above the starting elevation.");
        s.WeatherStrength = (int)weatherStrength.Value;
        s.WeatherRefreshMinutes = (int)weatherRefresh.Value;
        s.Schedule = ReadSchedule(errors);
        if (s.Mode == BrightnessMode.Schedule && s.Schedule.Count == 0) errors?.Add("The custom schedule needs at least one point.");
        s.MaxStepPerMinute = (int)maxStep.Value;
        s.Hysteresis = (int)hysteresis.Value;
        s.DetectManualChanges = detectManual.Checked;
        s.OverrideMinutes = (int)overrideMin.Value;

        var monitors = new List<MonitorSetting>();
        foreach (DataGridViewRow row in monitorGrid.Rows)
        {
            if (row.Tag is not string id) continue;
            int.TryParse(row.Cells[3].Value?.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int off);
            monitors.Add(new MonitorSetting
            {
                Id = id,
                Name = row.Cells[0].Value?.ToString() ?? id,
                Enabled = row.Cells[2].Value is true,
                Offset = Math.Clamp(off, -100, 100),
            });
        }
        s.Monitors = monitors;
        return s;
    }

    void SaveAndClose()
    {
        scheduleGrid.EndEdit();
        monitorGrid.EndEdit();
        var errors = new List<string>();
        var s = Build(errors);
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), "Brightly", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            ctl.UpdateSettings(s);
            if (s.AutoLocation && !original.AutoLocation) _ = ctl.DetectLocationAsync(force: true);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save settings: " + ex.Message, "Brightly", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- live behaviour ----------

    void WireChanges()
    {
        EventHandler changed = (_, _) => UpdatePreview();
        foreach (var n in new[] { nightElev, fullElev, weatherStrength }) n.ValueChanged += changed;
        void ManualCoords()
        {
            if (loading) return;
            locationSet = true; placeName = null; placeLabel.Text = ""; autoLoc.Checked = false;
        }
        lat.ValueChanged += (_, _) => { ManualCoords(); UpdatePreview(); };
        lon.ValueChanged += (_, _) => { ManualCoords(); UpdatePreview(); };
        modeSun.CheckedChanged += changed;

        scheduleGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (scheduleGrid.IsCurrentCellDirty && scheduleGrid.CurrentCell is DataGridViewComboBoxCell)
                scheduleGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        scheduleGrid.CellValueChanged += (_, e) => { if (e.ColumnIndex != ColToday) UpdatePreview(); };
        scheduleGrid.DataError += (_, e) => e.ThrowException = false;
        monitorGrid.DataError += (_, e) => e.ThrowException = false;

        searchBtn.Click += async (_, _) => await SearchCity();
        cityBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await SearchCity();
        };
        cityResults.SelectedIndexChanged += (_, _) =>
        {
            if (cityResults.SelectedItem is not GeoResult r) return;
            UseLocation(r);
            autoLoc.Checked = false;
        };
        autoLoc.CheckedChanged += async (_, _) =>
        {
            if (loading || !autoLoc.Checked) return;
            autoLoc.Enabled = false;
            placeLabel.Text = "Looking up…";
            try { UseLocation(await Geo.FromIpAsync()); }
            catch (Exception ex) { placeLabel.Text = "Lookup failed: " + ex.Message; }
            finally { autoLoc.Enabled = true; }
        };
    }

    async Task SearchCity()
    {
        var q = cityBox.Text.Trim();
        if (q.Length < 2) return;
        searchBtn.Enabled = false;
        placeLabel.Text = "Searching…";
        try
        {
            var results = await Geo.SearchAsync(q);
            cityResults.Items.Clear();
            foreach (var r in results) cityResults.Items.Add(r);
            cityResults.Visible = results.Count > 0;
            if (results.Count > 0) cityResults.SelectedIndex = 0; // triggers UseLocation
            else placeLabel.Text = "No places found";
        }
        catch (Exception ex)
        {
            placeLabel.Text = "Search failed: " + ex.Message;
        }
        finally
        {
            searchBtn.Enabled = true;
        }
    }

    void UseLocation(GeoResult r)
    {
        loading = true;
        lat.Value = (decimal)Math.Round(r.Lat, 4);
        lon.Value = (decimal)Math.Round(r.Lon, 4);
        loading = false;
        locationSet = true;
        placeName = r.Display;
        placeLabel.Text = r.Display;
        UpdatePreview();
    }

    void UpdatePreview()
    {
        if (loading) return;
        var s = Build(null);
        var sun = ctl.Sun;

        if (s.HasLocation)
        {
            var day = sun.Get(DateTime.Today, s.Latitude!.Value, s.Longitude!.Value);
            string T(DateTime? t) => t?.ToString("HH:mm") ?? "—";
            sunLabel.Text = $"Dawn {T(day.Dawn)}  ·  Sunrise {T(day.Sunrise)}  ·  Noon {T(day.SolarNoon)} ({day.NoonElevation:0}°)  ·  Sunset {T(day.Sunset)}  ·  Dusk {T(day.Dusk)}";
            foreach (DataGridViewRow row in scheduleGrid.Rows)
            {
                var pts = ReadSchedule(null);
                var anchor = ScheduleMath.FromLabel(row.Cells[ColAnchor].Value as string);
                var text = (row.Cells[ColValue].Value?.ToString() ?? "").Trim();
                var p = new SchedulePoint { Anchor = anchor };
                if (anchor == SunAnchor.Clock) p.Time = text;
                else if (int.TryParse(text.Length == 0 ? "0" : text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int off)) p.OffsetMinutes = off;
                else { row.Cells[ColToday].Value = "?"; continue; }
                row.Cells[ColToday].Value = ScheduleMath.Resolve(p, day)?.ToString("HH:mm") ?? "—";
            }
        }
        else
        {
            sunLabel.Text = "Set a location to see sunrise and sunset";
        }

        UpdateWeatherLabel(s);
        preview.Settings = s;
        preview.Sun = sun;
        preview.CurrentRatio = s.UseWeather ? Engine.LightRatio(ctl.Weather, DateTime.UtcNow) : null;
        preview.Invalidate();
    }

    void UpdateWeatherLabel(AppSettings s)
    {
        if (!s.UseWeather) { weatherLabel.Text = "off"; return; }
        var w = ctl.Weather;
        if (w == null) { weatherLabel.Text = ctl.WeatherError != null ? "unavailable: " + ctl.WeatherError : "fetched after saving"; return; }
        var r = Engine.LightRatio(w, DateTime.UtcNow);
        weatherLabel.Text = $"Clouds {w.CloudCover:0}%  ·  sunlight {w.Shortwave?.ToString("0") ?? "?"} W/m²" +
                            (r is double x ? $"  ·  daylight {x:P0} of a clear sky" : "") +
                            $"  ·  updated {w.FetchedUtc.ToLocalTime():HH:mm}";
    }

    public void OnControllerChanged()
    {
        if (IsDisposed || loading) return;
        UpdateWeatherLabel(Build(null));
        // refresh "Now" column without disturbing edits
        foreach (DataGridViewRow row in monitorGrid.Rows)
        {
            if (row.Tag is not string id) continue;
            var m = ctl.Monitors.FirstOrDefault(x => x.Id == id);
            row.Cells[1].Value = m == null ? "not connected" : m.Reachable && m.Percent >= 0 ? $"{m.Percent}%" : "no DDC/CI reply";
        }
        if (monitorGrid.Rows.Count == 0 && ctl.Monitors.Count > 0) FillMonitors(original);
        preview.Invalidate();
    }
}

/// <summary>Plots today's brightness: clear sky (solid), fully overcast (dashed), with the current weather (dotted) and "now".</summary>
public sealed class PreviewChart : Control
{
    public AppSettings? Settings;
    public SunCache? Sun;
    public double? CurrentRatio;

    public PreviewChart()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = SystemColors.Window;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = Settings;
        if (s is not { HasLocation: true } || Sun == null)
        {
            TextRenderer.DrawText(g, "Set a location to preview the curve", Font, ClientRectangle, SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        float dpi = DeviceDpi / 96f;
        var plot = new RectangleF(36 * dpi, 8 * dpi, Width - 48 * dpi, Height - 56 * dpi);
        if (plot.Width < 50 || plot.Height < 30) return;

        var today = DateTime.Today;
        var day = Sun.Get(today, s.Latitude!.Value, s.Longitude!.Value);
        float X(DateTime local) => plot.Left + (float)((local - today).TotalMinutes / 1440.0) * plot.Width;
        float Y(double v) => plot.Bottom - (float)(v / 100.0) * plot.Height;

        // daylight band
        if (day.Sunrise is DateTime sr && day.Sunset is DateTime ss)
        {
            using var dayBrush = new SolidBrush(Color.FromArgb(40, 255, 193, 7));
            g.FillRectangle(dayBrush, X(sr), plot.Top, X(ss) - X(sr), plot.Height);
        }

        using var gridPen = new Pen(Color.FromArgb(40, 0, 0, 0));
        var small = new Font(Font.FontFamily, 7.5f);
        for (int v = 0; v <= 100; v += 25)
        {
            g.DrawLine(gridPen, plot.Left, Y(v), plot.Right, Y(v));
            TextRenderer.DrawText(g, v.ToString(), small, new Point((int)(plot.Left - 30 * dpi), (int)(Y(v) - 7 * dpi)), SystemColors.GrayText);
        }
        for (int h = 0; h <= 24; h += 3)
        {
            float x = plot.Left + plot.Width * h / 24f;
            g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
            TextRenderer.DrawText(g, $"{h:00}:00", small, new Point((int)(x - 14 * dpi), (int)(plot.Bottom + 4 * dpi)), SystemColors.GrayText);
        }

        PointF[] Curve(double? ratio)
        {
            var pts = new List<PointF>();
            for (int m = 0; m <= 1440; m += 10)
            {
                var local = today.AddMinutes(m);
                var ev = Engine.Evaluate(s, local.ToUniversalTime(), ratio, Sun);
                pts.Add(new PointF(X(local), Y(ev.Target)));
            }
            return pts.ToArray();
        }

        if (s.UseWeather)
        {
            using var overcast = new Pen(Color.FromArgb(120, 90, 110), 1.5f * dpi) { DashStyle = DashStyle.Dash };
            g.DrawLines(overcast, Curve(0.25));
            if (CurrentRatio is double cr && cr < 0.97)
            {
                using var nowPen = new Pen(Color.FromArgb(30, 136, 229), 1.5f * dpi) { DashStyle = DashStyle.Dot };
                g.DrawLines(nowPen, Curve(cr));
            }
        }
        using var clear = new Pen(Color.FromArgb(230, 150, 0), 2.5f * dpi);
        g.DrawLines(clear, Curve(null));

        var now = DateTime.Now;
        using var nowLine = new Pen(Color.FromArgb(200, 220, 40, 40), 1.5f * dpi);
        g.DrawLine(nowLine, X(now), plot.Top, X(now), plot.Bottom);

        string legend = s.UseWeather
            ? "━ clear sky   ┅ overcast" + (CurrentRatio is double c2 && c2 < 0.97 ? "   ··· current sky" : "") + "   │ now   ▒ daylight"
            : "━ brightness   │ now   ▒ daylight";
        TextRenderer.DrawText(g, legend, small, new Point((int)plot.Left, (int)(plot.Bottom + 22 * dpi)), SystemColors.GrayText);
        small.Dispose();
    }
}
