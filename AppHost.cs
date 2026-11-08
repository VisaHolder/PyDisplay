using System.Windows;
using System.Windows.Threading;
using PyDisplay.Sensors;
using PyDisplay.Tools;
using PyDisplay.UI;

namespace PyDisplay;

/// <summary>What the overlay's buttons can ask for. Null in test renders.</summary>
public interface IOverlayHost
{
    void OpenSettings(string tab);
    void OpenTools(string tab);
    void ToggleLog();
    bool Logging { get; }
    void CloseOverlay();
    void HideOverlay();
    void Quit();
    void ToggleSection(string key);
}

/// <summary>
/// Owns everything that lives for the whole run: settings, sensors, overlay, tray, logging,
/// the timed RAM cleaner and the settings/tools windows. Every settings change goes through <see cref="Apply"/>.
/// </summary>
public sealed class AppHost : IOverlayHost
{
    public AppSettings S { get; }
    public SystemMonitor Mon { get; }
    public StatsLogger Logger { get; }
    public MainWindow? Overlay { get; private set; }

    private SettingsWindow? _settings;
    private ToolsWindow? _tools;
    private Tray? _tray;
    private readonly DispatcherTimer _autoClean = new();
    public DateTime? NextAutoClean { get; private set; }
    public string? LastAutoClean { get; private set; }

    public AppHost(AppSettings s, SystemMonitor mon)
    {
        S = s; Mon = mon;
        Logger = new StatsLogger(mon, () => S);
        _autoClean.Tick += (_, _) => RunAutoClean();
    }

    public void Start()
    {
        string? imported = null;
        if (AppSettings.IsFirstRun && !App.TestMode)
        {
            // upgrading from PyDisplay 1.x: bring settings + themes over (never allowed to stop the app starting)
            try { imported = V1Import.ImportSettings(S); } catch (Exception ex) { Log.Error("first-run-import", ex); }
            S.Save();
        }
        Startup.RepairPath();

        Overlay = new MainWindow(this);
        Overlay.Show();
        ListenForShowSignal();
        if (AppSettings.IsFirstRun && !App.TestMode)
        {
            // first launch: the overlay is click-through, so say how to use it
            Overlay.Dispatcher.BeginInvoke(() => UI.Toast.Show(Overlay, Theme.For(S), S,
                "hold Ctrl to move PyDisplay or click \u2261 for the menu" + (imported != null ? $"\nbrought over {imported}" : ""), seconds: 9),
                DispatcherPriority.ApplicationIdle);
        }
        Mon.Sampled += snap => Application.Current?.Dispatcher.BeginInvoke(() => _tray?.Update(snap, S));
        UpdateTray();
        ScheduleAutoClean();
    }

    /// <summary>Save + push a settings change everywhere (overlay, windows, tray, timers).</summary>
    public void Apply()
    {
        S.Save();
        Mon.IntervalMs = S.RefreshMs;
        Mon.SelectGpu(S.GpuIndex);
        Overlay?.Rebuild();
        _settings?.Rebuild();
        _tools?.Rebuild();
        UpdateTray();
        ScheduleAutoClean();
        Logger.Restart();
    }

    /// <summary>A second launch of PyDisplay signals this one to come to the front (even from the tray).</summary>
    private void ListenForShowSignal()
    {
        EventWaitHandle sig;
        try { sig = new EventWaitHandle(false, EventResetMode.AutoReset, App.ShowSignalName); }
        catch (Exception ex) { Log.Error("show-signal", ex); return; }
        new Thread(() =>
        {
            while (sig.WaitOne())
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    ShowOverlay();
                    Toast("PyDisplay is already running");
                });
        }) { IsBackground = true, Name = "show-signal" }.Start();
    }

    // ── IOverlayHost ──
    public void OpenSettings(string tab)
    {
        if (_settings == null) { _settings = new SettingsWindow(this, tab); _settings.Closed += (_, _) => _settings = null; }
        _settings.ShowTab(tab);
    }

    public void OpenTools(string tab)
    {
        if (_tools == null) { _tools = new ToolsWindow(this, tab); _tools.Closed += (_, _) => _tools = null; }
        _tools.ShowTab(tab);
    }

    public bool Logging => Logger.Active;

    public void ToggleLog()
    {
        if (Logger.Active) Logger.Stop(); else Logger.Start();
        Overlay?.Rebuild();
        _tools?.Rebuild();
    }

    public void CloseOverlay()
    {
        if (S.TrayMode) { Overlay?.Hide(); UpdateTray(); }
        else Exit();
    }

    /// <summary>Hide to the tray (the tray icon appears so it can come back).</summary>
    public void HideOverlay() { Overlay?.Hide(); UpdateTray(); }

    public void Quit() => Exit();

    public void ToggleSection(string key)
    {
        if (!S.Collapsed.Remove(key)) S.Collapsed.Add(key);
        Apply();
    }

    public void ShowOverlay()
    {
        if (Overlay == null) return;
        Overlay.Show();
        Overlay.Activate();
        UpdateTray();
    }

    public void Exit()
    {
        try { Logger.Stop(); } catch { }
        try { if (Overlay != null) { S.X = Overlay.Left; S.Y = Overlay.Top; } S.Save(); } catch { }
        _tray?.Dispose();
        _tray = null;
        Overlay?.AllowClose();
        Application.Current.Shutdown();
    }

    // ── tray: on when "× hides to tray" is on, or whenever the overlay is hidden ──
    private void UpdateTray()
    {
        if (App.TestMode) return;
        bool want = WantTray;
        if (want && _tray == null) _tray = new Tray(this);
        else if (!want && _tray != null) { _tray.Dispose(); _tray = null; }
        _tray?.Refresh();
    }

    /// <summary>Tray icon: always when "tray icon always on", otherwise only while the overlay is hidden.</summary>
    public bool WantTray => S.TrayMode || Overlay is { IsVisible: false };

    public TimeSpan AutoCleanInterval => _autoClean.Interval;

    // ── timed RAM cleaner ──
    private void ScheduleAutoClean()
    {
        _autoClean.Stop();
        NextAutoClean = null;
        if (!S.AutoCleanOn) return;
        _autoClean.Interval = TimeSpan.FromMinutes(S.AutoCleanMinutes);
        _autoClean.Start();
        NextAutoClean = DateTime.Now + _autoClean.Interval;
    }

    private async void RunAutoClean()
    {
        NextAutoClean = DateTime.Now + _autoClean.Interval;
        if (MemoryCleaner.FullscreenAppRunning())
        {
            // a fullscreen game is in front: don't make it hitch — try again in 5 minutes
            _autoClean.Stop();
            _autoClean.Interval = TimeSpan.FromMinutes(5);
            _autoClean.Start();
            NextAutoClean = DateTime.Now + _autoClean.Interval;
            LastAutoClean = $"{DateTime.Now:HH:mm}  waited (fullscreen game)";
            _tools?.Rebuild();
            return;
        }
        if (_autoClean.Interval != TimeSpan.FromMinutes(S.AutoCleanMinutes)) ScheduleAutoClean();   // back to normal after a wait
        bool aggressive = S.AutoCleanAggressive;
        // never clears the clipboard: wiping it on a timer would be data loss, not cleaning
        var r = await Task.Run(() => MemoryCleaner.Run(aggressive, clearClipboard: false, log: null));
        if (r == null) return;   // a manual clean was running — skip this round
        LastAutoClean = $"{DateTime.Now:HH:mm}  freed {Fmt.Num(Math.Max(0, r.FreedGb), "0.00")} GB";
        if (r.FreedGb > 0.01) Toast($"auto-clean freed {Fmt.Num(r.FreedGb, "0.00")} GB");
        _tools?.Rebuild();
    }

    public void Toast(string msg)
    {
        if (Overlay is not { IsVisible: true }) return;   // nothing to anchor to (hidden in tray)
        UI.Toast.Show(Overlay, Theme.For(S), S, msg);
    }
}
