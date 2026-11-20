using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Brightly;

public static class Log
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Brightly");
    public static string FilePath => Path.Combine(Dir, "brightly.log");
    static readonly object Sync = new();
    /// <summary>Turned off during uninstall so nothing recreates the data folder.</summary>
    public static bool Enabled = true;

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024) File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never take the app down */ }
    }
}

/// <summary>Per-user "Run" key: starts at sign-in, no admin rights, no scheduled task.</summary>
public static class Autostart
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Windows' own on/off switch for each Run entry (Settings → Apps → Startup, Task Manager → Startup apps).
    // Recent Windows 11 builds skip a Run entry that has no record here, so we write "enabled" ourselves.
    const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string Name = "Brightly";
    static string Command => $"\"{Environment.ProcessPath}\" --autostart";

    /// <summary>True when the Run entry exists and Windows hasn't switched it off in Startup apps.</summary>
    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
            return k?.GetValue(Name) is string && !DisabledInWindows;
        }
    }

    static bool DisabledInWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(ApprovedPath);
            return k?.GetValue(Name) is byte[] { Length: > 0 } b && (b[0] & 1) == 1; // odd first byte = disabled
        }
    }

    public static void Set(bool enabled)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
        {
            if (enabled) k.SetValue(Name, Command);
            else k.DeleteValue(Name, false);
        }
        using var a = Registry.CurrentUser.CreateSubKey(ApprovedPath);
        if (enabled) a.SetValue(Name, Approved, RegistryValueKind.Binary);
        else a.DeleteValue(Name, false);
    }

    static byte[] Approved => [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>
    /// Keeps the registered path pointing at this exe if it was moved, and adds the missing "enabled" record
    /// for entries made by older builds. An entry the user switched off in Windows is left alone.
    /// </summary>
    public static void RefreshPath()
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (k?.GetValue(Name) is not string v) return;
        using var a = Registry.CurrentUser.OpenSubKey(ApprovedPath);
        bool hasRecord = a?.GetValue(Name) is byte[];
        if (v != Command || !hasRecord)
        {
            if (DisabledInWindows) return;
            Set(true);
        }
    }
}

/// <summary>Hidden top-level window that hears about sleep/resume, screen on/off and display changes.</summary>
public sealed class PowerWatcher : NativeWindow, IDisposable
{
    readonly IntPtr notification;
    int lastDisplayState = -1;

    public event Action<string>? Woke;
    public event Action? DisplaysChanged;

    public PowerWatcher()
    {
        CreateHandle(new CreateParams { Caption = "BrightlyPowerWatcher" });
        var g = Native.GUID_CONSOLE_DISPLAY_STATE;
        notification = Native.RegisterPowerSettingNotification(Handle, ref g, 0);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_POWERBROADCAST)
        {
            int ev = (int)m.WParam;
            if (ev == Native.PBT_APMRESUMEAUTOMATIC)
            {
                Woke?.Invoke("resumed from sleep");
            }
            else if (ev == Native.PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
            {
                var ps = Marshal.PtrToStructure<Native.POWERBROADCAST_SETTING>(m.LParam);
                if (ps.PowerSetting == Native.GUID_CONSOLE_DISPLAY_STATE)
                {
                    int state = (int)(ps.Data & 0xFF); // 0 off, 1 on, 2 dimmed
                    if (state == 1 && lastDisplayState == 0) Woke?.Invoke("screen turned on");
                    lastDisplayState = state;
                }
            }
            m.Result = 1;
        }
        else if (m.Msg == Native.WM_DISPLAYCHANGE)
        {
            DisplaysChanged?.Invoke();
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (notification != IntPtr.Zero) Native.UnregisterPowerSettingNotification(notification);
        DestroyHandle();
    }
}

/// <summary>The app icon, embedded once and used for the exe, the windows and the tray.</summary>
public static class AppIcon
{
    static Icon? full;

    public static Icon Full => full ??= Load();

    /// <summary>The best frame for a given size (the tray uses the small-icon size).</summary>
    public static Icon Sized(Size size) => new(Full, size);

    static Icon Load()
    {
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("Brightly.ico")!;
        return new Icon(s);
    }
}

/// <summary>Keeps one copy of Brightly in %LOCALAPPDATA%\Programs\Brightly so autostart survives a cleaned Downloads folder.</summary>
public static class Installer
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Brightly");

    public static string InstalledExe => Path.Combine(Dir, "Brightly.exe");

    public static bool IsInstalledCopy =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies this exe into the install folder. Returns false (and runs from here) if that fails.</summary>
    public static bool TryInstallSelf()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.Copy(Environment.ProcessPath!, InstalledExe, true);
            Log.Write($"Installed to {InstalledExe}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Could not install, running from the current folder: " + ex.Message);
            return false;
        }
    }

    /// <summary>Removes the startup entry, settings, log and (after this process exits) the program folder.</summary>
    public static void Uninstall()
    {
        Log.Enabled = false;
        Autostart.Set(false);
        try { if (Directory.Exists(Log.Dir)) Directory.Delete(Log.Dir, true); } catch { /* best effort */ }
        var dir = Path.GetDirectoryName(Environment.ProcessPath!)!;
        if (IsInstalledCopy)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{dir}\"")
            {
                CreateNoWindow = true, UseShellExecute = false,
            });
        }
    }
}
