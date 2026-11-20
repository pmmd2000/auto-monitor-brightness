namespace Brightly;

/// <summary>
/// The everyday window: where you are (found automatically), which monitors were found, and how bright you
/// want them by day and by night. Changes apply as you make them. Everything else lives in Advanced settings.
/// </summary>
public sealed class SettingsForm : Form
{
    readonly Controller ctl;
    bool loading = true;

    readonly Label placeLabel = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 10F), Margin = new Padding(0, 2, 6, 0) };
    readonly Label placeHint = Grey();
    readonly LinkLabel changeLink = new() { Text = "Change", AutoSize = true, Margin = new Padding(6, 4, 0, 0) };
    readonly Label sunLabel = Grey();
    readonly FlowLayoutPanel searchPanel = new() { AutoSize = true, WrapContents = false, Visible = false, Margin = new Padding(0, 6, 0, 0) };
    readonly TextBox cityBox = new() { Width = 190, PlaceholderText = "Your city" };
    readonly Button searchBtn = new() { Text = "Search", AutoSize = true };
    readonly LinkLabel autoLink = new() { Text = "Detect automatically", AutoSize = true, Margin = new Padding(10, 7, 0, 0) };
    readonly ComboBox cityResults = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380, Visible = false };

    readonly Label monitorsLabel = new() { AutoSize = true, MaximumSize = new Size(480, 0) };

    readonly TrackBar dayBar = Bar(), nightBar = Bar();
    readonly Label dayValue = ValueLabel(), nightValue = ValueLabel();
    readonly Label scheduleNote = Grey("Daytime levels come from your custom schedule (Advanced settings).");
    readonly CheckBox clouds = new() { Text = "Dim the screens when it's cloudy outside", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    readonly PreviewChart preview = new() { Size = new Size(480, 160), Margin = new Padding(0, 8, 0, 0) };
    readonly Label nowLabel = Grey();
    readonly LinkLabel resumeLink = new() { Text = "Back to automatic", AutoSize = true, Visible = false, Margin = new Padding(8, 6, 0, 0) };

    readonly CheckBox autostart = new() { Text = "Start with Windows", AutoSize = true };
    readonly System.Windows.Forms.Timer applyTimer = new() { Interval = 400 };

    public SettingsForm(Controller ctl)
    {
        this.ctl = ctl;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Brightly";
        Icon = AppIcon.Full;
        Font = new Font("Segoe UI", 9F);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var root = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(18, 14, 18, 12),
        };
        void Add(Control c) => root.Controls.Add(c);

        Add(Header());

        Add(Heading("Location"));
        Add(Flow(placeLabel, placeHint, changeLink));
        Add(sunLabel);
        searchPanel.Controls.AddRange(new Control[] { cityBox, searchBtn, autoLink });
        Add(searchPanel);
        Add(cityResults);

        Add(Heading("Monitors"));
        Add(monitorsLabel);

        Add(Heading("Brightness"));
        var sliders = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Margin = Padding.Empty };
        sliders.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        sliders.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sliders.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sliders.Controls.Add(RowLabel("Daytime"), 0, 0);
        sliders.Controls.Add(dayBar, 1, 0);
        sliders.Controls.Add(dayValue, 2, 0);
        sliders.Controls.Add(RowLabel("Night"), 0, 1);
        sliders.Controls.Add(nightBar, 1, 1);
        sliders.Controls.Add(nightValue, 2, 1);
        Add(sliders);
        Add(scheduleNote);
        Add(clouds);
        Add(preview);
        Add(Flow(nowLabel, resumeLink));

        var advanced = new LinkLabel { Text = "Advanced settings…", AutoSize = true, Margin = new Padding(0, 9, 0, 0) };
        var close = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(90, 0), Anchor = AnchorStyles.Right };
        var footer = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(Flow(autostart, advanced), 0, 0);
        footer.Controls.Add(new Label(), 1, 0);
        footer.Controls.Add(close, 2, 0);
        Add(footer);
        Add(Grey($"Version {Application.ProductVersion.Replace("-", " ")}"));

        Controls.Add(root);
        CancelButton = close;
        ResumeLayout(true);

        LoadValues();

        dayBar.ValueChanged += (_, _) => { if (nightBar.Value > dayBar.Value) nightBar.Value = dayBar.Value; Changed(); };
        nightBar.ValueChanged += (_, _) => { if (nightBar.Value > dayBar.Value) dayBar.Value = nightBar.Value; Changed(); };
        clouds.CheckedChanged += (_, _) => Changed();
        autostart.CheckedChanged += (_, _) => { if (!loading) Autostart.Set(autostart.Checked); };
        applyTimer.Tick += (_, _) => Apply();
        close.Click += (_, _) => Close();
        resumeLink.LinkClicked += (_, _) => ctl.Resume();
        advanced.LinkClicked += (_, _) => OpenAdvanced();

        changeLink.LinkClicked += (_, _) =>
        {
            searchPanel.Visible = !searchPanel.Visible;
            cityResults.Visible = false;
            if (searchPanel.Visible) cityBox.Focus();
        };
        searchBtn.Click += async (_, _) => await SearchCity();
        cityBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await SearchCity();
        };
        cityResults.SelectionChangeCommitted += (_, _) =>
        {
            if (cityResults.SelectedItem is GeoResult r) UseCity(r);
        };
        autoLink.LinkClicked += (_, _) =>
        {
            Apply();
            var s = ctl.Settings.Clone();
            s.AutoLocation = true;
            ctl.UpdateSettings(s);
            _ = ctl.DetectLocationAsync(force: true);
            searchPanel.Visible = cityResults.Visible = false;
        };

        loading = false;
        RefreshInfo();
    }

    // ---------- small builders ----------

    static Label Grey(string text = "") => new()
    {
        Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(480, 0), Margin = new Padding(0, 4, 0, 0),
    };

    static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5F), Margin = new Padding(0, 16, 0, 4),
    };

    static Label RowLabel(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 6, 8) };

    static Label ValueLabel() => new()
    {
        AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font("Segoe UI Semibold", 10F), Margin = new Padding(4, 0, 0, 8),
    };

    static TrackBar Bar() => new()
    {
        Minimum = 0, Maximum = 100, TickFrequency = 10, SmallChange = 1, LargeChange = 5, Width = 340, Margin = new Padding(0),
    };

    static FlowLayoutPanel Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        f.Controls.AddRange(controls);
        return f;
    }

    Control Header()
    {
        var pic = new PictureBox
        {
            Image = new Icon(AppIcon.Full, 48, 48).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(44, 44), Margin = new Padding(0, 0, 12, 0),
        };
        var title = new Label { Text = "Brightly", AutoSize = true, Font = new Font("Segoe UI Semibold", 15F), Margin = Padding.Empty };
        var sub = Grey("Your monitors follow the daylight and the weather. Choose how bright you like them by day and at night; the rest is automatic.");
        sub.MaximumSize = new Size(420, 0);
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        text.Controls.AddRange(new Control[] { title, sub });
        return Flow(pic, text);
    }

    // ---------- values ----------

    void LoadValues()
    {
        bool was = loading;
        loading = true;
        var s = ctl.Settings;
        dayBar.Value = Math.Clamp(s.MaxBrightness, 0, 100);
        nightBar.Value = Math.Clamp(s.MinBrightness, 0, 100);
        clouds.Checked = s.UseWeather;
        autostart.Checked = Autostart.IsEnabled;
        loading = was;
        UpdateValueLabels();
    }

    AppSettings Build()
    {
        var s = ctl.Settings.Clone();
        s.MaxBrightness = dayBar.Value;
        s.MinBrightness = nightBar.Value;
        s.UseWeather = clouds.Checked;
        return s;
    }

    void UpdateValueLabels()
    {
        dayValue.Text = $"{dayBar.Value}%";
        nightValue.Text = $"{nightBar.Value}%";
    }

    void Changed()
    {
        UpdateValueLabels();
        if (loading) return;
        UpdatePreview(Build());
        applyTimer.Stop();
        applyTimer.Start(); // apply once the slider settles
    }

    void Apply()
    {
        applyTimer.Stop();
        var s = ctl.Settings;
        if (s.MaxBrightness == dayBar.Value && s.MinBrightness == nightBar.Value && s.UseWeather == clouds.Checked) return;
        ctl.UpdateSettings(Build());
    }

    // ---------- location ----------

    async Task SearchCity()
    {
        var q = cityBox.Text.Trim();
        if (q.Length < 2) return;
        searchBtn.Enabled = false;
        try
        {
            var results = await Geo.SearchAsync(q);
            cityResults.Items.Clear();
            foreach (var r in results) cityResults.Items.Add(r);
            if (results.Count == 1) { UseCity(results[0]); return; }
            cityResults.Visible = results.Count > 0;
            if (results.Count > 0)
            {
                cityResults.SelectedIndex = 0;
                cityResults.DroppedDown = true;
            }
            else placeHint.Text = "no places found";
        }
        catch (Exception ex)
        {
            placeHint.Text = "search failed: " + ex.Message;
        }
        finally
        {
            searchBtn.Enabled = true;
        }
    }

    void UseCity(GeoResult r)
    {
        Apply();
        var s = ctl.Settings.Clone();
        s.Latitude = Math.Round(r.Lat, 4);
        s.Longitude = Math.Round(r.Lon, 4);
        s.PlaceName = r.Display;
        s.AutoLocation = false;
        ctl.UpdateSettings(s);
        searchPanel.Visible = cityResults.Visible = false;
        RefreshInfo();
    }

    // ---------- live status ----------

    public void OnControllerChanged()
    {
        if (IsDisposed || loading) return;
        advancedForm?.OnControllerChanged();
        RefreshInfo();
    }

    void RefreshInfo()
    {
        var s = ctl.Settings;

        if (s.HasLocation)
        {
            placeLabel.Text = s.PlaceName ?? $"{s.Latitude:0.00}, {s.Longitude:0.00}";
            placeHint.Text = !s.AutoLocation ? "chosen by you"
                : ctl.LocationError != null ? "found automatically (kept: the latest check looked like a VPN)"
                : "found automatically";
            changeLink.Visible = true;
        }
        else if (ctl.LocationError != null && !ctl.DetectingLocation)
        {
            placeLabel.Text = "Couldn't find your location";
            placeHint.Text = "type your city below";
            changeLink.Visible = false;
            searchPanel.Visible = true;
        }
        else
        {
            placeLabel.Text = "Finding your location…";
            placeHint.Text = "";
            changeLink.Visible = false;
        }
        autoLink.Visible = !s.AutoLocation || !s.HasLocation;

        if (s.HasLocation)
        {
            var day = ctl.Sun.Get(DateTime.Today, s.Latitude!.Value, s.Longitude!.Value);
            string sun = day.Sunrise is DateTime sr && day.Sunset is DateTime ss
                ? $"Sunrise {sr:HH:mm}  ·  Sunset {ss:HH:mm}"
                : day.NoonElevation > 0 ? "The sun doesn't set today" : "The sun doesn't rise today";
            var w = ctl.Weather;
            if (s.UseWeather && w != null) sun += $"  ·  {Sky(w.CloudCover)} now";
            sunLabel.Text = sun;
        }
        else sunLabel.Text = "";

        monitorsLabel.Text = MonitorText();

        dayBar.Enabled = s.Mode == BrightnessMode.Sun;
        scheduleNote.Visible = s.Mode == BrightnessMode.Schedule;

        nowLabel.Text = NowText(s);
        resumeLink.Visible = ctl.IsPaused;
        UpdatePreview(applyTimer.Enabled ? Build() : s);
    }

    static string Sky(double cloudCover) => cloudCover switch
    {
        < 15 => "clear sky",
        < 45 => "a few clouds",
        < 80 => "partly cloudy",
        < 95 => "mostly cloudy",
        _ => "overcast",
    };

    string MonitorText()
    {
        var list = ctl.Monitors;
        if (list.Count == 0)
            return !ctl.MonitorsScanned
                ? "Looking for monitors…"
                : "No external monitor found yet. Brightly controls monitors that support DDC/CI (most do; built-in laptop screens aren't supported yet).";
        var ok = list.Where(m => m.Reachable).Select(m => m.Name).ToList();
        var bad = list.Where(m => !m.Reachable).Select(m => m.Name).ToList();
        var text = ok.Count switch
        {
            0 => "",
            1 => $"Controlling {ok[0]}.",
            _ => $"Controlling {string.Join(", ", ok.Take(ok.Count - 1))} and {ok[^1]}.",
        };
        if (bad.Count > 0)
            text += (text.Length > 0 ? "\n" : "") +
                    $"{string.Join(", ", bad)} didn't respond. Turn on DDC/CI in the monitor's on-screen menu.";
        return text;
    }

    string NowText(AppSettings s)
    {
        if (!s.HasLocation) return "";
        if (ctl.IsPaused)
        {
            var until = ctl.PausedUntilUtc == DateTime.MaxValue ? "until you resume" : $"until {ctl.PausedUntilUtc!.Value.ToLocalTime():HH:mm}";
            return $"Paused {until}: the brightness was changed by hand.";
        }
        var e = ctl.LastEvaluation;
        if (e == null) return "";
        var text = $"Right now: {e.Target}%";
        if (s.UseWeather && e.ClearSkyTarget - e.Target >= 1) text += $"  ({e.ClearSkyTarget - e.Target} lower than on a clear day)";
        return text;
    }

    void UpdatePreview(AppSettings s)
    {
        preview.Settings = s;
        preview.Sun = ctl.Sun;
        preview.CurrentRatio = s.UseWeather ? Engine.LightRatio(ctl.Weather, DateTime.UtcNow) : null;
        preview.Invalidate();
    }

    // ---------- advanced ----------

    AdvancedForm? advancedForm;

    public void OpenAdvanced()
    {
        Apply();
        using var f = advancedForm = new AdvancedForm(ctl);
        f.StartPosition = FormStartPosition.CenterParent;
        if (f.ShowDialog(this) == DialogResult.OK) LoadValues();
        advancedForm = null;
        RefreshInfo();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Apply();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) applyTimer.Dispose();
        base.Dispose(disposing);
    }
}
