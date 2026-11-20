using System.Collections.Concurrent;

namespace Brightly;

/// <summary>Sun events for one local calendar day. Times are local; null when the event doesn't happen (polar day/night).</summary>
public sealed record SunDay(
    DateTime Date,
    DateTime? Dawn,
    DateTime? Sunrise,
    DateTime SolarNoon,
    DateTime? Sunset,
    DateTime? Dusk,
    double NoonElevation);

/// <summary>NOAA solar position algorithm (accurate to well under a minute for sunrise/sunset).</summary>
public static class Solar
{
    public const double SunriseElevation = -0.833; // refraction + solar disc radius
    public const double CivilTwilight = -6.0;

    static double Rad(double d) => d * Math.PI / 180.0;
    static double Deg(double r) => r * 180.0 / Math.PI;
    static double Mod(double a, double m) => ((a % m) + m) % m;

    /// <summary>Geometric solar elevation in degrees for a UTC instant.</summary>
    public static double Elevation(DateTime utc, double lat, double lon)
    {
        double jd = utc.ToOADate() + 2415018.5;
        double t = (jd - 2451545.0) / 36525.0;

        double l0 = Mod(280.46646 + t * (36000.76983 + t * 0.0003032), 360);
        double m = 357.52911 + t * (35999.05029 - 0.0001537 * t);
        double e = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);
        double mr = Rad(m);
        double c = Math.Sin(mr) * (1.914602 - t * (0.004817 + 0.000014 * t))
                 + Math.Sin(2 * mr) * (0.019993 - 0.000101 * t)
                 + Math.Sin(3 * mr) * 0.000289;
        double trueLong = l0 + c;
        double omega = 125.04 - 1934.136 * t;
        double lambda = trueLong - 0.00569 - 0.00478 * Math.Sin(Rad(omega));
        double eps0 = 23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60) / 60;
        double eps = eps0 + 0.00256 * Math.Cos(Rad(omega));
        double decl = Deg(Math.Asin(Math.Sin(Rad(eps)) * Math.Sin(Rad(lambda))));

        double y = Math.Tan(Rad(eps / 2));
        y *= y;
        double l0r = Rad(l0);
        double eqTime = 4 * Deg(y * Math.Sin(2 * l0r) - 2 * e * Math.Sin(mr)
                               + 4 * e * y * Math.Sin(mr) * Math.Cos(2 * l0r)
                               - 0.5 * y * y * Math.Sin(4 * l0r)
                               - 1.25 * e * e * Math.Sin(2 * mr));

        double trueSolarMinutes = Mod(utc.TimeOfDay.TotalMinutes + eqTime + 4 * lon, 1440);
        double hourAngle = trueSolarMinutes / 4 - 180;

        double cosZenith = Math.Sin(Rad(lat)) * Math.Sin(Rad(decl))
                         + Math.Cos(Rad(lat)) * Math.Cos(Rad(decl)) * Math.Cos(Rad(hourAngle));
        return 90 - Deg(Math.Acos(Math.Clamp(cosZenith, -1, 1)));
    }

    public static SunDay ComputeDay(DateTime localDate, double lat, double lon)
    {
        var startLocal = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Local);
        var startUtc = startLocal.ToUniversalTime();
        double totalMinutes = (startLocal.AddDays(1).ToUniversalTime() - startUtc).TotalMinutes; // 23/24/25 h on DST days
        const double step = 5;
        int steps = (int)Math.Ceiling(totalMinutes / step);

        double Elev(double minute) => Elevation(startUtc.AddMinutes(minute), lat, lon);
        var samples = new double[steps + 1];
        for (int i = 0; i <= steps; i++) samples[i] = Elev(Math.Min(i * step, totalMinutes));

        int best = 0;
        for (int i = 1; i <= steps; i++) if (samples[i] > samples[best]) best = i;
        double noonMin = best * step, noonElev = samples[best];
        for (double mm = noonMin - step; mm <= noonMin + step; mm += 0.25)
        {
            if (mm < 0 || mm > totalMinutes) continue;
            double ev = Elev(mm);
            if (ev > noonElev) { noonElev = ev; noonMin = mm; }
        }

        DateTime? Cross(double threshold, bool rising)
        {
            for (int i = 0; i < steps; i++)
            {
                double a = samples[i] - threshold, b = samples[i + 1] - threshold;
                bool hit = rising ? (a < 0 && b >= 0) : (a >= 0 && b < 0);
                if (!hit) continue;
                double lo = i * step, hi = Math.Min((i + 1) * step, totalMinutes);
                for (int k = 0; k < 30; k++)
                {
                    double mid = (lo + hi) / 2;
                    bool above = Elev(mid) >= threshold;
                    if (above == rising) hi = mid; else lo = mid;
                }
                return startUtc.AddMinutes((lo + hi) / 2).ToLocalTime();
            }
            return null;
        }

        return new SunDay(
            localDate.Date,
            Cross(CivilTwilight, true),
            Cross(SunriseElevation, true),
            startUtc.AddMinutes(noonMin).ToLocalTime(),
            Cross(SunriseElevation, false),
            Cross(CivilTwilight, false),
            noonElev);
    }
}

public sealed class SunCache
{
    readonly ConcurrentDictionary<(DateTime, double, double), SunDay> map = new();

    public SunDay Get(DateTime localDate, double lat, double lon)
    {
        if (map.Count > 16) map.Clear();
        return map.GetOrAdd((localDate.Date, Math.Round(lat, 4), Math.Round(lon, 4)),
            k => Solar.ComputeDay(k.Item1, k.Item2, k.Item3));
    }

    public void Clear() => map.Clear();
}
