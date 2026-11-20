using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoMonitorBrightness;

public enum BrightnessMode { Sun, Schedule }

public enum SunAnchor { Clock, Dawn, Sunrise, SolarNoon, Sunset, Dusk }

public sealed class SchedulePoint
{
    public SunAnchor Anchor { get; set; }
    /// <summary>"HH:mm" — used when Anchor is Clock.</summary>
    public string Time { get; set; } = "00:00";
    /// <summary>Minutes relative to the sun event — used for every other anchor.</summary>
    public int OffsetMinutes { get; set; }
    public int Brightness { get; set; }
}

public sealed class MonitorSetting
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Added to the computed brightness for this monitor (e.g. -5 for a brighter panel).</summary>
    public int Offset { get; set; }
}

public sealed class AppSettings
{
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? PlaceName { get; set; }
    /// <summary>Find the location from the internet connection (re-checked every few hours). Off once a city is picked by hand.</summary>
    public bool AutoLocation { get; set; } = true;

    public BrightnessMode Mode { get; set; } = BrightnessMode.Sun;
    public int MinBrightness { get; set; } = 10;
    public int MaxBrightness { get; set; } = 70;
    /// <summary>Sun elevation (°) where the curve starts rising from night brightness. -6 = civil dawn.</summary>
    public double NightElevation { get; set; } = -6;
    /// <summary>Sun elevation (°) where daytime brightness is reached.</summary>
    public double FullBrightnessElevation { get; set; } = 20;

    public bool UseWeather { get; set; } = true;
    /// <summary>0–100: how much a fully overcast sky pulls brightness toward the night level.</summary>
    public int WeatherStrength { get; set; } = 50;
    public int WeatherRefreshMinutes { get; set; } = 15;

    public List<SchedulePoint> Schedule { get; set; } = DefaultSchedule();
    public List<MonitorSetting> Monitors { get; set; } = new();

    /// <summary>Gradual ramp: max brightness points changed per minute during normal operation.</summary>
    public int MaxStepPerMinute { get; set; } = 3;
    /// <summary>Ignore differences smaller than this to avoid needless DDC writes.</summary>
    public int Hysteresis { get; set; } = 2;
    public bool DetectManualChanges { get; set; } = true;
    public int OverrideMinutes { get; set; } = 120;

    [JsonIgnore] public bool HasLocation => Latitude.HasValue && Longitude.HasValue;

    public MonitorSetting? FindMonitor(string id) => Monitors.FirstOrDefault(m => m.Id == id);

    public static List<SchedulePoint> DefaultSchedule() =>
    [
        new() { Anchor = SunAnchor.Dawn, OffsetMinutes = 0, Brightness = 15 },
        new() { Anchor = SunAnchor.Sunrise, OffsetMinutes = 60, Brightness = 50 },
        new() { Anchor = SunAnchor.SolarNoon, OffsetMinutes = 0, Brightness = 70 },
        new() { Anchor = SunAnchor.Sunset, OffsetMinutes = -60, Brightness = 50 },
        new() { Anchor = SunAnchor.Dusk, OffsetMinutes = 0, Brightness = 20 },
        new() { Anchor = SunAnchor.Clock, Time = "22:30", Brightness = 10 },
    ];

    // ---- persistence ----

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath => Path.Combine(Log.Dir, "settings.json");
    public static bool Exists => File.Exists(FilePath);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Write("Could not read settings, using defaults: " + ex.Message);
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Log.Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, true);
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!;
}
