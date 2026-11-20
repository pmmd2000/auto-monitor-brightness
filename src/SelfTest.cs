using System.Globalization;
using System.Text;

namespace Brightly;

/// <summary>Brightly.exe --selftest out.txt [lat lon] — read-only diagnostics (never writes to the monitors).</summary>
internal static class SelfTest
{
    public static int Run(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "brightly-selftest.txt";
        var sb = new StringBuilder();

        if (args.Contains("--render"))
        {
            // Draws each settings tab to PNG next to outPath (layout check without touching the screen).
            ApplicationConfiguration.Initialize();
            var st = AppSettings.Load();
            st.Latitude ??= 51.5074; st.Longitude ??= -0.1278; st.PlaceName ??= "London (preview)";
            if (args.Contains("--demo"))
            {
                // Neutral values for README screenshots.
                st.Latitude = 51.5074; st.Longitude = -0.1278; st.PlaceName = "London, England, United Kingdom";
                st.AutoLocation = true; st.MinBrightness = 10; st.MaxBrightness = 70; st.UseWeather = true;
            }
            using var c = new Controller(st);
            var found = new List<MonitorState>();
            foreach (var m in MonitorManager.OpenAll())
                using (m) found.Add(new MonitorState(m.Id, m.Name, m.TryGetPercent(out int p) ? p : -1, true, -1, true));
            c.ShowMonitorsForPreview(found);
            using (var main = new SettingsForm(c))
            {
                main.StartPosition = FormStartPosition.Manual;
                main.Location = new Point(-5000, -5000);
                main.Show();
                Application.DoEvents();
                using var mb = new Bitmap(main.Width, main.Height);
                main.DrawToBitmap(mb, new Rectangle(0, 0, main.Width, main.Height));
                mb.Save(Path.ChangeExtension(outPath, ".main.png"));
            }
            using var f = new AdvancedForm(c);
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-5000, -5000);
            f.Show();
            Application.DoEvents();
            var tabs = f.Controls.OfType<TabControl>().First();
            for (int i = 0; i < tabs.TabCount; i++)
            {
                tabs.SelectedIndex = i;
                Application.DoEvents();
                using var bmp = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(Path.ChangeExtension(outPath, $".tab{i}.png"));
            }
            return 0;
        }

        if (args.Contains("--rewrite"))
        {
            // Writes each monitor's current value back to it: exercises the DDC/CI write path with no visible change.
            foreach (var m in MonitorManager.OpenAll())
                using (m)
                {
                    bool r = m.TryGetPercent(out int p);
                    bool w = r && m.TrySetPercent(p);
                    bool r2 = m.TryGetPercent(out int p2);
                    sb.AppendLine($"{m.Name}: read {r} {p}% → write {w} → reread {r2} {p2}%");
                }
            return Done(outPath, sb, 0);
        }
        try
        {
            var s = AppSettings.Load();
            if (args.Length >= 3)
            {
                s.Latitude = double.Parse(args[1], CultureInfo.InvariantCulture);
                s.Longitude = double.Parse(args[2], CultureInfo.InvariantCulture);
            }

            sb.AppendLine("== Monitors (read only)");
            foreach (var m in MonitorManager.OpenAll())
            {
                using (m)
                {
                    bool ok = m.TryGetPercent(out int p);
                    sb.AppendLine($"  {m.Name,-20} id={m.Id}  brightness={(ok ? p + "%" : "no reply")}  maxRaw={m.MaxRaw}");
                }
            }

            if (!s.HasLocation)
            {
                sb.AppendLine("No location configured; pass lat lon.");
                return Done(outPath, sb, 0);
            }

            var sun = new SunCache();
            var day = sun.Get(DateTime.Today, s.Latitude!.Value, s.Longitude!.Value);
            sb.AppendLine($"== Sun today at {s.Latitude:0.###},{s.Longitude:0.###}");
            sb.AppendLine($"  dawn {day.Dawn:HH:mm}  sunrise {day.Sunrise:HH:mm}  noon {day.SolarNoon:HH:mm} ({day.NoonElevation:0.0}°)  sunset {day.Sunset:HH:mm}  dusk {day.Dusk:HH:mm}");

            var w = new WeatherService();
            w.RefreshIfDueAsync(s.Latitude.Value, s.Longitude.Value, 15).GetAwaiter().GetResult();
            double? ratio = Engine.LightRatio(w.Latest, DateTime.UtcNow);
            sb.AppendLine("== Weather now");
            sb.AppendLine(w.Latest == null ? $"  failed: {w.LastError}"
                : $"  clouds {w.Latest.CloudCover}%  shortwave {w.Latest.Shortwave} W/m²  valid {w.Latest.ValidUtc:u}  ratio {ratio:0.00}");

            foreach (var mode in new[] { BrightnessMode.Sun, BrightnessMode.Schedule })
            {
                s.Mode = mode;
                sb.AppendLine($"== Curve today, mode {mode} (clear / overcast / current sky)");
                for (int h = 0; h < 24; h++)
                {
                    var local = DateTime.Today.AddHours(h);
                    var utc = local.ToUniversalTime();
                    var a = Engine.Evaluate(s, utc, null, sun);
                    var b = Engine.Evaluate(s, utc, 0.25, sun);
                    var c = Engine.Evaluate(s, utc, ratio, sun);
                    sb.AppendLine($"  {local:HH:mm}  sun {a.Elevation,6:0.0}°  {a.Target,3}% / {b.Target,3}% / {c.Target,3}%");
                }
            }
            var nowEval = Engine.Evaluate(s, DateTime.UtcNow, ratio, sun);
            sb.AppendLine($"== Now: target {nowEval.Target}% (clear sky {nowEval.ClearSkyTarget}%), sun {nowEval.Elevation:0.0}°");
            return Done(outPath, sb, 0);
        }
        catch (Exception ex)
        {
            sb.AppendLine("ERROR " + ex);
            return Done(outPath, sb, 1);
        }
    }

    static int Done(string path, StringBuilder sb, int code)
    {
        File.WriteAllText(path, sb.ToString());
        return code;
    }
}
