using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Brightly;

public sealed record WeatherSnapshot(
    double CloudCover,       // %
    double? Shortwave,       // W/m², average over the 15 min before ValidUtc
    DateTime? ValidUtc,
    DateTime FetchedUtc,
    double Lat,
    double Lon);

/// <summary>Current conditions from Open-Meteo (free, no API key). Location is rounded to ~1 km before sending.</summary>
public sealed class WeatherService
{
    internal static readonly HttpClient Http = CreateClient();
    DateTime nextAttemptUtc = DateTime.MinValue;

    public WeatherSnapshot? Latest { get; private set; }
    public string? LastError { get; private set; }

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Brightly", "1.0"));
        return c;
    }

    public void Invalidate()
    {
        Latest = null;
        nextAttemptUtc = DateTime.MinValue;
    }

    public async Task RefreshIfDueAsync(double lat, double lon, int intervalMinutes)
    {
        var now = DateTime.UtcNow;
        if (Latest != null && (Math.Abs(Latest.Lat - lat) > 0.02 || Math.Abs(Latest.Lon - lon) > 0.02)) Invalidate();
        if (now < nextAttemptUtc) return;
        nextAttemptUtc = now.AddMinutes(Math.Max(5, intervalMinutes));

        try
        {
            var url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.open-meteo.com/v1/forecast?latitude={lat:0.00}&longitude={lon:0.00}&current=cloud_cover,shortwave_radiation&timezone=GMT");
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url).ConfigureAwait(false));
            var cur = doc.RootElement.GetProperty("current");
            double cloud = cur.GetProperty("cloud_cover").GetDouble();
            double? sw = cur.TryGetProperty("shortwave_radiation", out var swe) && swe.ValueKind == JsonValueKind.Number
                ? swe.GetDouble() : null;
            DateTime? valid = null;
            if (cur.TryGetProperty("time", out var te) &&
                DateTime.TryParse(te.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
                valid = t;

            bool first = Latest == null;
            Latest = new WeatherSnapshot(cloud, sw, valid, now, lat, lon);
            if (first || LastError != null) Log.Write($"Weather: clouds {cloud:0}%, sunlight {sw?.ToString("0") ?? "?"} W/m²");
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            nextAttemptUtc = now.AddMinutes(5); // network may not be up yet after resume
            Log.Write("Weather fetch failed: " + ex.Message);
        }
    }
}

public sealed record GeoResult(string Display, double Lat, double Lon)
{
    public override string ToString() => Display;
}

public static class Geo
{
    public static async Task<List<GeoResult>> SearchAsync(string query)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=8&language=en&format=json";
        using var doc = JsonDocument.Parse(await WeatherService.Http.GetStringAsync(url));
        var list = new List<GeoResult>();
        if (!doc.RootElement.TryGetProperty("results", out var results)) return list;
        foreach (var r in results.EnumerateArray())
        {
            var parts = new[] { "name", "admin1", "country" }
                .Select(k => r.TryGetProperty(k, out var v) ? v.GetString() : null)
                .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct();
            list.Add(new GeoResult(string.Join(", ", parts),
                r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble()));
        }
        return list;
    }

    /// <summary>Approximate (city-level) location from the public IP address.</summary>
    public static async Task<GeoResult> FromIpAsync()
    {
        using var doc = JsonDocument.Parse(await WeatherService.Http.GetStringAsync("https://ipwho.is/"));
        var r = doc.RootElement;
        if (r.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException(r.TryGetProperty("message", out var m) ? m.GetString() : "lookup failed");
        string name = string.Join(", ", new[] { "city", "region", "country" }
            .Select(k => r.TryGetProperty(k, out var v) ? v.GetString() : null)
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        return new GeoResult(name, r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble());
    }
}
