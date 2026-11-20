namespace Brightly;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest")
            return SelfTest.Run(args.Skip(1).ToArray());

        using var mutex = new Mutex(true, @"Local\Brightly-SingleInstance", out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Brightly-ShowSettings");
        if (!first)
        {
            showSignal.Set(); // already running: just open its settings window
            return 0;
        }

        // Run from Downloads (or anywhere else)? Copy into %LOCALAPPDATA%\Programs\Brightly and start that copy,
        // so autostart keeps working after the download is cleaned up. --portable skips this.
        if (!Installer.IsInstalledCopy && !args.Contains("--portable") && !args.Contains("--autostart") && Installer.TryInstallSelf())
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = true });
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("UI error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Fatal: " + e.ExceptionObject);

        bool firstRun = !AppSettings.Exists;
        var settings = AppSettings.Load();
        if (firstRun)
        {
            settings.Save();
            Autostart.Set(true); // the whole point: it runs without being launched
        }
        else
        {
            Autostart.RefreshPath();
        }

        using var controller = new Controller(settings);
        Application.Run(new TrayApp(controller, args.Contains("--autostart"), showSignal));
        GC.KeepAlive(mutex);
        return 0;
    }
}
