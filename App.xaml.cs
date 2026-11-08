using System.Reflection;
using System.Windows;
using PyDisplay.Sensors;

namespace PyDisplay;

public partial class App : Application
{
    public static readonly string ShortVersion =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.1.2";

    /// <summary>Set by --uitest: blocks side effects (registry, UAC, dialogs, clipboard, tray, speed test).</summary>
    public static bool TestMode;

    private Mutex? _single;
    public const string ShowSignalName = @"Local\PyDisplay2.Show";
    private SystemMonitor? _mon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => { Log.Error("ui", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => { if (ex.ExceptionObject is Exception x) Log.Error("fatal", x); };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("task", ex.Exception); ex.SetObserved(); };

        var settings = AppSettings.Load();
        int sizeIdx = Array.IndexOf(e.Args, "--sizetest");
        if (sizeIdx >= 0)
        {
            SizeTest.Run(settings, sizeIdx + 1 < e.Args.Length ? e.Args[sizeIdx + 1] : "sizetest.txt");
            Shutdown();
            return;
        }
        int snapIdx = Array.IndexOf(e.Args, "--snap");
        int testIdx = Array.IndexOf(e.Args, "--selftest");
        int uiIdx = Array.IndexOf(e.Args, "--uitest");
        int effIdx = Array.IndexOf(e.Args, "--settingstest");
        if (testIdx >= 0 || uiIdx >= 0 || effIdx >= 0) snapIdx = -2;   // tests also skip the single-instance lock
        bool forceWinGpu = e.Args.Contains("--windows-gpu");

        if (snapIdx == -1 && !TakeSingleInstance(waitForOld: e.Args.Contains("--wait-mutex")))
        {
            Shutdown();
            return;
        }

        _mon = new SystemMonitor(settings.RefreshMs, settings.GpuIndex) { ForceWindowsGpu = forceWinGpu };
        if (snapIdx >= 0) _mon.IntervalMs = 500;
        _mon.Start();

        if (effIdx >= 0)
        {
            string file = effIdx + 1 < e.Args.Length ? e.Args[effIdx + 1] : "settingstest.txt";
            _ = EffectTest.RunAsync(settings, _mon, file);
            return;
        }

        if (uiIdx >= 0)
        {
            string file = uiIdx + 1 < e.Args.Length ? e.Args[uiIdx + 1] : "uitest.txt";
            _ = UiTest.RunAsync(settings, _mon, file);
            return;
        }

        if (testIdx >= 0)
        {
            string file = testIdx + 1 < e.Args.Length ? e.Args[testIdx + 1] : "selftest.txt";
            _ = SelfTest.RunAsync(_mon, file, e.Args.Contains("--speed")).ContinueWith(t =>
            {
                if (t.Exception != null) try { File.AppendAllText(file, "CRASH  " + t.Exception.InnerException + Environment.NewLine); } catch { }
                Dispatcher.Invoke(Shutdown);
            });
            return;
        }

        if (snapIdx >= 0)
        {
            string dir = snapIdx + 1 < e.Args.Length ? e.Args[snapIdx + 1] : "snaps";
            Snapper.Run(new AppHost(settings, _mon), dir, () => Shutdown());
            return;
        }

        new AppHost(settings, _mon).Start();
    }

    /// <summary>One PyDisplay at a time. "Restart as admin" passes --wait-mutex so the new copy waits for the old one to quit.</summary>
    private bool TakeSingleInstance(bool waitForOld)
    {
        _single = new Mutex(true, @"Local\PyDisplay2.SingleInstance", out bool first);
        if (!first && !waitForOld)
        {
            // already running: bring that copy to the front instead of silently doing nothing
            bool signalled = false;
            try { if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var show)) { using (show) signalled = show.Set(); } }
            catch (UnauthorizedAccessException) { }   // the running copy is elevated (admin) — we can't reach it
            if (!signalled)
                MessageBox.Show("PyDisplay is already running (probably as administrator, which this copy can't reach).\n\n" +
                                "Close that one first: hold Ctrl, click \u2261 on the overlay, then quit.",
                                "PyDisplay", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        if (first || !waitForOld) return first;
        try { return _single.WaitOne(TimeSpan.FromSeconds(15)); }
        catch (AbandonedMutexException) { return true; }   // old copy exited without releasing — it's ours now
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mon?.Dispose();
        try { _single?.ReleaseMutex(); } catch { }
        _single?.Dispose();
        base.OnExit(e);
    }
}
