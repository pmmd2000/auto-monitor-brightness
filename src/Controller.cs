namespace AutoMonitorBrightness;

public enum TickKind
{
    /// <summary>Regular minute tick: ramps gradually, detects manual changes.</summary>
    Timer,
    /// <summary>After sleep / screen on / unlock / startup: jump straight to target.</summary>
    Wake,
    /// <summary>User asked (settings saved, resume, re-apply): jump straight to target.</summary>
    Force,
}

public sealed record MonitorState(string Id, string Name, int Percent, bool Reachable, int Target, bool Enabled);

public sealed class Controller : IDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly WeatherService weather = new();
    readonly SunCache sun = new();
    readonly Dictionary<string, int> lastWritten = new();
    System.Threading.Timer? timer;
    DateTime lastTickUtc = DateTime.MinValue;

    public Controller(AppSettings settings) => Settings = settings;

    public AppSettings Settings { get; private set; }
    public Evaluation? LastEvaluation { get; private set; }
    public IReadOnlyList<MonitorState> Monitors { get; private set { field = value; MonitorsScanned = true; } } = [];
    public bool MonitorsScanned { get; private set; }
    /// <summary>Read-only monitor list for --render screenshots (never touches brightness).</summary>
    internal void ShowMonitorsForPreview(IReadOnlyList<MonitorState> list) => Monitors = list;
    public DateTime? PausedUntilUtc { get; private set; }
    public string? PauseReason { get; private set; }
    public bool IsPaused => PausedUntilUtc is DateTime p && DateTime.UtcNow < p;
    public WeatherSnapshot? Weather => weather.Latest;
    public string? WeatherError => weather.LastError;
    public SunCache Sun => sun;

    public event Action? Changed;

    public void Start()
    {
        Log.Write("Auto Monitor Brightness started");
        _ = TickAsync(TickKind.Wake, "startup");
        _ = DetectLocationAsync();
        timer = new System.Threading.Timer(_ =>
        {
            _ = TickAsync(TickKind.Timer, "timer");
            _ = DetectLocationAsync();
        }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    // ---- automatic location (city-level, from the public IP) ----

    DateTime nextGeoUtc = DateTime.MinValue;
    int geoBusy;

    public bool DetectingLocation => Volatile.Read(ref geoBusy) == 1;
    public string? LocationError { get; private set; }

    /// <summary>Looks the location up from the internet connection when AutoLocation is on (every 6 h, or now if forced).</summary>
    public async Task DetectLocationAsync(bool force = false)
    {
        if (!force && (!Settings.AutoLocation || DateTime.UtcNow < nextGeoUtc)) return;
        if (Interlocked.Exchange(ref geoBusy, 1) == 1) return;
        try
        {
            Changed?.Invoke();
            var r = await Geo.FromIpAsync();
            LocationError = null;
            nextGeoUtc = DateTime.UtcNow.AddHours(6);
            var cur = Settings;
            if (!cur.AutoLocation && !force) return;
            bool same = cur.HasLocation && cur.AutoLocation && cur.PlaceName == r.Display
                        && Math.Abs(cur.Latitude!.Value - r.Lat) < 0.05 && Math.Abs(cur.Longitude!.Value - r.Lon) < 0.05;
            if (same) return;
            var s = cur.Clone();
            s.Latitude = Math.Round(r.Lat, 4);
            s.Longitude = Math.Round(r.Lon, 4);
            s.PlaceName = r.Display;
            s.AutoLocation = true;
            Log.Write($"Location detected: {r.Display}");
            UpdateSettings(s);
        }
        catch (LocationMismatchException ex)
        {
            // VPN or proxy: keep the last good location and look again later.
            LocationError = ex.Message;
            nextGeoUtc = DateTime.UtcNow.AddHours(3);
            Log.Write(Settings.HasLocation
                ? $"Ignoring IP location {ex.Place} (different time zone, probably a VPN); keeping {Settings.PlaceName}"
                : $"Ignoring IP location {ex.Place} (different time zone, probably a VPN); please pick a city");
        }
        catch (Exception ex)
        {
            LocationError = ex.Message;
            nextGeoUtc = DateTime.UtcNow.AddMinutes(10);
            Log.Write("Location lookup failed: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref geoBusy, 0);
            Changed?.Invoke();
        }
    }

    public void UpdateSettings(AppSettings s)
    {
        bool moved = s.Latitude != Settings.Latitude || s.Longitude != Settings.Longitude;
        Settings = s;
        s.Save();
        sun.Clear();
        if (moved) weather.Invalidate();
        Log.Write("Settings saved");
        _ = TickAsync(TickKind.Force, "settings changed");
    }

    public void Pause(DateTime? untilUtc, string reason)
    {
        PausedUntilUtc = untilUtc ?? DateTime.MaxValue;
        PauseReason = reason;
        Log.Write(untilUtc is DateTime u ? $"Paused until {u.ToLocalTime():HH:mm} ({reason})" : $"Paused ({reason})");
        Changed?.Invoke();
    }

    public void Resume()
    {
        PausedUntilUtc = null;
        PauseReason = null;
        Log.Write("Resumed automatic control");
        _ = TickAsync(TickKind.Force, "resumed");
    }

    /// <summary>Manual brightness from the tray: applies to every enabled monitor (keeping offsets) and pauses automation.</summary>
    public async Task SetManualAsync(int percent)
    {
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(45))) { Log.Write("Manual set skipped: monitors busy"); return; }
        try
        {
            var s = Settings;
            PausedUntilUtc = DateTime.UtcNow.AddMinutes(Math.Max(5, s.OverrideMinutes));
            PauseReason = "manual brightness";
            Monitors = await RunMonitorWork(() =>
            {
                var states = new List<MonitorState>();
                var mons = MonitorManager.OpenAll();
                try
                {
                    foreach (var m in mons)
                    {
                        var ms = s.FindMonitor(m.Id);
                        if (ms is { Enabled: false })
                        {
                            bool r = m.TryGetPercent(out int c);
                            states.Add(new(m.Id, m.Name, c, r, -1, false));
                            continue;
                        }
                        int v = Math.Clamp(percent + (ms?.Offset ?? 0), 0, 100);
                        m.TryGetPercent(out _); // learns the monitor's raw range
                        bool ok = m.TrySetPercent(v);
                        if (ok) lastWritten[m.Id] = v;
                        states.Add(new(m.Id, m.Name, ok ? v : -1, ok, v, true));
                    }
                }
                finally { foreach (var m in mons) m.Dispose(); }
                return states;
            }, "manual set") ?? Monitors;
            Log.Write($"Manual brightness {percent}% — automation paused until {PausedUntilUtc.Value.ToLocalTime():HH:mm}");
        }
        catch (Exception ex) { Log.Write("Manual set failed: " + ex); }
        finally { gate.Release(); }
        Changed?.Invoke();
    }

    public async Task TickAsync(TickKind kind, string reason)
    {
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(45))) return;
        try
        {
            var now = DateTime.UtcNow;
            if (kind == TickKind.Timer && lastTickUtc != DateTime.MinValue && now - lastTickUtc > TimeSpan.FromMinutes(3))
            {
                kind = TickKind.Wake; // the machine slept without telling us, or the clock jumped
                reason = "missed ticks (sleep or clock change)";
            }
            lastTickUtc = now;
            if (kind != TickKind.Timer) Log.Write($"Re-evaluating: {reason}");

            if (PausedUntilUtc is DateTime pu && now >= pu)
            {
                PausedUntilUtc = null;
                PauseReason = null;
                Log.Write("Pause ended, resuming automatic control");
            }

            var s = Settings;
            if (!s.HasLocation)
            {
                LastEvaluation = null;
                Monitors = await RunMonitorWork(() => ReadOnly(s), "read") ?? Monitors;
                return;
            }

            if (s.UseWeather) await weather.RefreshIfDueAsync(s.Latitude!.Value, s.Longitude!.Value, s.WeatherRefreshMinutes);
            double? ratio = s.UseWeather ? Engine.LightRatio(weather.Latest, now) : null;
            var eval = Engine.Evaluate(s, now, ratio, sun);
            LastEvaluation = eval;

            Monitors = await RunMonitorWork(() => Apply(s, eval, kind, reason, now), "apply") ?? Monitors;
            Heartbeat(now);
        }
        catch (Exception ex)
        {
            Log.Write("Tick failed: " + ex);
        }
        finally
        {
            gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Runs DDC/CI work off the UI thread with a time limit. A monitor or driver that never answers must not
    /// hold the lock forever (that would freeze every later tick); the stuck call is abandoned and logged.
    /// </summary>
    static async Task<List<MonitorState>?> RunMonitorWork(Func<List<MonitorState>> work, string what)
    {
        var task = Task.Run(work);
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            Log.Write($"Monitor {what} took over 30 s and was abandoned (a monitor or its driver stopped answering)");
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return null;
        }
    }

    // ---- status line every 30 min, so the log shows what happened even when nothing changed ----

    DateTime nextHeartbeatUtc = DateTime.MinValue;
    static readonly string[] OtherBrightnessApps = ["DDPM.Subagent.User", "DDM", "DellDisplayManager"];

    void Heartbeat(DateTime now)
    {
        if (now < nextHeartbeatUtc) return;
        nextHeartbeatUtc = now.AddMinutes(30);
        var e = LastEvaluation;
        var mons = string.Join(", ", Monitors.Select(m => $"{m.Name} {(m.Reachable && m.Percent >= 0 ? m.Percent + "%" : "no reply")}"));
        var state = IsPaused ? $"paused ({PauseReason}) until {PausedUntilUtc!.Value.ToLocalTime():HH:mm}" : "automatic";
        Log.Write($"Status: {state}, target {e?.Target}%, sun {e?.Elevation:0.0}°; {mons}");
        if (OtherBrightnessAppRunning) Log.Write("Note: Dell Display and Peripheral Manager is running; if its brightness schedule is on, the two apps fight");
    }

    /// <summary>Dell Display and Peripheral Manager (or the older Dell Display Manager) is running.</summary>
    public static bool OtherBrightnessAppRunning
    {
        get
        {
            foreach (var name in OtherBrightnessApps)
            {
                var ps = System.Diagnostics.Process.GetProcessesByName(name);
                bool any = ps.Length > 0;
                foreach (var p in ps) p.Dispose();
                if (any) return true;
            }
            return false;
        }
    }

    static List<MonitorState> ReadOnly(AppSettings s)
    {
        var states = new List<MonitorState>();
        var mons = MonitorManager.OpenAll();
        try
        {
            foreach (var m in mons)
            {
                bool ok = m.TryGetPercent(out int c);
                states.Add(new(m.Id, m.Name, c, ok, -1, s.FindMonitor(m.Id)?.Enabled ?? true));
            }
        }
        finally { foreach (var m in mons) m.Dispose(); }
        return states;
    }

    List<MonitorState> Apply(AppSettings s, Evaluation eval, TickKind kind, string reason, DateTime now)
    {
        var states = new List<MonitorState>();
        var mons = MonitorManager.OpenAll();
        try
        {
            foreach (var m in mons)
            {
                var ms = s.FindMonitor(m.Id);
                int target = Math.Clamp(eval.Target + (ms?.Offset ?? 0), 0, 100);
                bool ok = m.TryGetPercent(out int cur);

                if (ms is { Enabled: false })
                {
                    states.Add(new(m.Id, m.Name, cur, ok, target, false));
                    continue;
                }

                // Someone changed brightness behind our back (monitor buttons, Dell app, …): respect it for a while.
                if (ok && kind == TickKind.Timer && s.DetectManualChanges && !IsPaused &&
                    lastWritten.TryGetValue(m.Id, out int expected) && Math.Abs(cur - expected) > 3)
                {
                    PausedUntilUtc = now.AddMinutes(Math.Max(5, s.OverrideMinutes));
                    PauseReason = $"manual change on {m.Name}";
                    Log.Write($"{m.Name}: brightness changed outside Auto Monitor Brightness ({expected}% → {cur}%). " +
                              $"Pausing until {PausedUntilUtc.Value.ToLocalTime():HH:mm}.");
                }

                if (IsPaused)
                {
                    if (ok) lastWritten[m.Id] = cur; // follow the user's value so resuming doesn't re-trigger detection
                    states.Add(new(m.Id, m.Name, cur, ok, target, true));
                    continue;
                }

                int next = target;
                if (ok)
                {
                    int diff = target - cur;
                    bool gradual = kind == TickKind.Timer;
                    if (diff == 0 || (gradual && Math.Abs(diff) < Math.Max(1, s.Hysteresis)))
                    {
                        lastWritten[m.Id] = cur;
                        states.Add(new(m.Id, m.Name, cur, true, target, true));
                        continue;
                    }
                    if (gradual && s.MaxStepPerMinute > 0 && Math.Abs(diff) > s.MaxStepPerMinute)
                        next = cur + Math.Sign(diff) * s.MaxStepPerMinute;
                }

                bool wrote = m.TrySetPercent(next);
                if (wrote)
                {
                    lastWritten[m.Id] = next;
                    if (kind != TickKind.Timer || next == target || Math.Abs(next - (ok ? cur : next)) >= 5 || next % 10 == 0)
                        Log.Write($"{m.Name}: {(ok ? cur + "%" : "?")} → {next}% (target {target}%, sun {eval.Elevation:0.0}°" +
                                  (eval.LightRatio is double r ? $", light {r:P0}" : "") + ")");
                }
                else
                {
                    Log.Write($"{m.Name}: could not set brightness (monitor off or DDC/CI disabled?)");
                }
                states.Add(new(m.Id, m.Name, wrote ? next : cur, ok || wrote, target, true));
            }
        }
        finally { foreach (var m in mons) m.Dispose(); }
        return states;
    }

    public void Dispose()
    {
        timer?.Dispose();
        timer = null;
    }
}
