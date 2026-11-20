using System.Globalization;

namespace AutoMonitorBrightness;

public sealed record Evaluation(
    int Target,             // final brightness % (before per-monitor offsets)
    int ClearSkyTarget,     // what it would be under a clear sky
    double Elevation,       // sun elevation now, degrees
    double? LightRatio,     // outdoor light as a fraction of clear sky (null = unknown / weather off)
    SunDay Today);

public static class Engine
{
    static double SmoothStep(double edge0, double edge1, double x)
    {
        if (edge1 <= edge0) return x >= edge1 ? 1 : 0;
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Target brightness at a UTC instant. Stateless: always derived from "now", so nothing is ever "missed".</summary>
    public static Evaluation Evaluate(AppSettings s, DateTime utc, double? lightRatio, SunCache sun)
    {
        double lat = s.Latitude ?? 0, lon = s.Longitude ?? 0;
        double elev = Solar.Elevation(utc, lat, lon);
        var local = utc.ToLocalTime();
        var today = sun.Get(local.Date, lat, lon);

        int min = Math.Clamp(s.MinBrightness, 0, 100);
        int max = Math.Clamp(Math.Max(s.MaxBrightness, min), 0, 100);

        double baseValue;
        double? scheduled = s.Mode == BrightnessMode.Schedule
            ? ScheduleMath.Interpolate(s.Schedule, local, d => sun.Get(d, lat, lon))
            : null;
        if (scheduled is double sv)
            baseValue = sv;
        else
            baseValue = min + (max - min) * SmoothStep(s.NightElevation, s.FullBrightnessElevation, elev);

        double final = baseValue;
        if (lightRatio is double r)
        {
            double wf = 1 - Math.Clamp(s.WeatherStrength, 0, 100) / 100.0 * (1 - Math.Clamp(r, 0, 1));
            double floor = Math.Min(min, baseValue);
            final = floor + (baseValue - floor) * wf;
        }

        return new Evaluation(
            (int)Math.Round(Math.Clamp(final, 0, 100)),
            (int)Math.Round(Math.Clamp(baseValue, 0, 100)),
            elev, lightRatio, today);
    }

    /// <summary>Outdoor light relative to a clear sky. Uses measured solar radiation when the sun is high enough
    /// for it to be meaningful, otherwise estimates from cloud cover (Kasten–Czeplak).</summary>
    public static double? LightRatio(WeatherSnapshot? w, DateTime nowUtc)
    {
        if (w == null || nowUtc - w.FetchedUtc > TimeSpan.FromHours(2)) return null;
        double cloudRatio = 1 - 0.75 * Math.Pow(Math.Clamp(w.CloudCover, 0, 100) / 100.0, 3.4);
        if (w.Shortwave is double sw && w.ValidUtc is DateTime vt)
        {
            double e = Solar.Elevation(vt.AddMinutes(-7.5), w.Lat, w.Lon); // value is a 15-min backward average
            if (e >= 8)
            {
                double sinE = Math.Sin(e * Math.PI / 180);
                double clearSky = 1098 * sinE * Math.Exp(-0.057 / sinE); // Haurwitz clear-sky GHI
                return Math.Clamp(sw / clearSky, 0.05, 1);
            }
        }
        return cloudRatio;
    }
}

public static class ScheduleMath
{
    public static readonly (SunAnchor Anchor, string Label)[] AnchorLabels =
    [
        (SunAnchor.Clock, "Time of day"),
        (SunAnchor.Dawn, "Dawn (civil)"),
        (SunAnchor.Sunrise, "Sunrise"),
        (SunAnchor.SolarNoon, "Solar noon"),
        (SunAnchor.Sunset, "Sunset"),
        (SunAnchor.Dusk, "Dusk (civil)"),
    ];

    public static string Label(SunAnchor a) => AnchorLabels.First(x => x.Anchor == a).Label;
    public static SunAnchor FromLabel(string? label) => AnchorLabels.FirstOrDefault(x => x.Label == label).Anchor;

    public static bool TryParseClock(string? text, out TimeSpan ts)
    {
        ts = default;
        return TimeSpan.TryParse((text ?? "").Trim(), CultureInfo.InvariantCulture, out ts)
               && ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1);
    }

    public static DateTime? Resolve(SchedulePoint p, SunDay day)
    {
        DateTime? Off(DateTime? t) => t?.AddMinutes(p.OffsetMinutes);
        return p.Anchor switch
        {
            SunAnchor.Clock => TryParseClock(p.Time, out var ts) ? day.Date.Date + ts : null,
            SunAnchor.Dawn => Off(day.Dawn),
            SunAnchor.Sunrise => Off(day.Sunrise),
            SunAnchor.SolarNoon => Off(day.SolarNoon),
            SunAnchor.Sunset => Off(day.Sunset),
            SunAnchor.Dusk => Off(day.Dusk),
            _ => null,
        };
    }

    /// <summary>Linear interpolation between the schedule points surrounding <paramref name="nowLocal"/>,
    /// looking at yesterday/today/tomorrow so the curve wraps across midnight.</summary>
    public static double? Interpolate(List<SchedulePoint> points, DateTime nowLocal, Func<DateTime, SunDay> dayFor)
    {
        if (points.Count == 0) return null;
        var list = new List<(DateTime t, int v)>();
        for (int d = -1; d <= 1; d++)
        {
            var day = dayFor(nowLocal.Date.AddDays(d));
            foreach (var p in points)
                if (Resolve(p, day) is DateTime t) list.Add((t, Math.Clamp(p.Brightness, 0, 100)));
        }
        if (list.Count == 0) return null;
        list.Sort((a, b) => a.t.CompareTo(b.t));

        (DateTime t, int v)? before = null, after = null;
        foreach (var x in list)
        {
            if (x.t <= nowLocal) before = x;
            else { after = x; break; }
        }
        if (before == null) return after!.Value.v;
        if (after == null) return before.Value.v;
        double span = (after.Value.t - before.Value.t).TotalMinutes;
        if (span <= 0) return after.Value.v;
        double f = (nowLocal - before.Value.t).TotalMinutes / span;
        return before.Value.v + (after.Value.v - before.Value.v) * f;
    }
}
