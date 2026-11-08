using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PyDisplay.Tools;

namespace PyDisplay.UI;

public sealed class ToolsWindow : PanelWindow
{
    private static readonly (string, string)[] Tabs =
        [("memory", "ram cleaner"), ("speed", "speed test"), ("ip", "ip lookup"), ("log", "logging")];

    // state that survives rebuilds (theme changes, ticks)
    private readonly List<string> _cleanLog = [];
    private string? _cleanResult;
    private bool _cleaning;

    private CancellationTokenSource? _speedCts;
    private SpeedTest.Progress? _speedProgress;
    private SpeedTest.Result? _speedResult;

    private string? _localIp, _publicIp;
    private bool _ipLoading, _showPublic;
    private string? _copied;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private TextBlock? _nextCleanText;

    public ToolsWindow(AppHost host, string tab) : base(host, "tools", Tabs, tab)
    {
        _tick.Tick += (_, _) => { if (_nextCleanText != null) _nextCleanText.Text = NextCleanText(); };
        _tick.Start();
        Closed += (_, _) => { _tick.Stop(); _speedCts?.Cancel(); };
        Rebuild();
    }

    protected override double WidthEm => 31;

    private void Set(Action change) { change(); H.Apply(); }

    protected override FrameworkElement BuildTab(string tab) => tab switch
    {
        "speed" => SpeedTab(),
        "ip" => IpTab(),
        "log" => LogTab(),
        _ => MemoryTab(),
    };

    // ───────────── RAM cleaner ─────────────
    private FrameworkElement MemoryTab()
    {
        var p = new StackPanel();
        bool admin = MemoryCleaner.IsAdmin();
        if (admin)
            p.Children.Add(Note("running as admin — every step is available", K.Accent));
        else
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, F * 0.4) };
            var btn = K.Button("restart as admin", () => { if (MemoryCleaner.RestartAsAdmin()) H.Exit(); });
            DockPanel.SetDock(btn, Dock.Right);
            row.Children.Add(btn);
            var t = Note("not admin — only your own apps get trimmed; system steps are skipped", K.T.Warn);
            t.Margin = new Thickness(0, 0, F * 0.6, 0);
            row.Children.Add(t);
            p.Children.Add(row);
        }

        p.Children.Add(K.Row("mode", K.Seg([("safe", "safe"), ("aggressive", "aggressive")], S.CleanAggressive ? "aggressive" : "safe",
            v => Set(() => S.CleanAggressive = v == "aggressive")),
            S.CleanAggressive ? "also empties the standby list — a running game will likely stutter once" : "trims apps and flushes caches — a running game may hitch briefly"));

        var run = K.Button(_cleaning ? "cleaning…" : "clean now", RunClean, primary: !_cleaning);
        run.Margin = new Thickness(0, F * 0.4, 0, F * 0.2);
        run.HorizontalAlignment = HorizontalAlignment.Left;
        p.Children.Add(run);
        if (_cleanResult != null) p.Children.Add(Note(_cleanResult, K.T.Text));
        if (_cleanLog.Count > 0) p.Children.Add(LogBox(_cleanLog));

        p.Children.Add(K.Heading("auto-clean"));
        p.Children.Add(K.Row("auto-clean", K.Toggle(S.AutoCleanOn, v => Set(() => S.AutoCleanOn = v)), "waits while a fullscreen game is running · never touches your clipboard"));
        p.Children.Add(K.Row("every", K.Seg([("5m", "5"), ("15m", "15"), ("30m", "30"), ("60m", "60")], S.AutoCleanMinutes.ToString(),
            v => Set(() => S.AutoCleanMinutes = int.Parse(v)))));
        p.Children.Add(K.Row("mode", K.Seg([("safe", "safe"), ("aggressive", "aggressive")], S.AutoCleanAggressive ? "aggressive" : "safe",
            v => Set(() => S.AutoCleanAggressive = v == "aggressive"))));
        _nextCleanText = Note(NextCleanText(), K.T.Sub);
        p.Children.Add(_nextCleanText);
        return p;
    }

    private string NextCleanText()
    {
        string last = H.LastAutoClean is { } l ? $"last: {l}" : "";
        if (H.NextAutoClean is not { } next) return S.AutoCleanOn ? last : "off" + (last.Length > 0 ? " · " + last : "");
        var left = next - DateTime.Now;
        string when = left.TotalSeconds < 60 ? $"{Math.Max(0, (int)left.TotalSeconds)}s" : $"{(int)left.TotalMinutes}m {left.Seconds:00}s";
        return $"next clean in {when}" + (last.Length > 0 ? " · " + last : "");
    }

    private async void RunClean()
    {
        if (_cleaning) return;
        if (MemoryCleaner.Busy) { _cleanResult = "an auto-clean is running — try again in a moment"; Rebuild(); return; }
        _cleaning = true; _cleanLog.Clear(); _cleanResult = null; Rebuild();
        bool aggressive = S.CleanAggressive;
        if (App.TestMode)
        {
            // click tests must never trim the real PC's memory (it can hitch a running game)
            await Task.Delay(200);
            _cleanLog.Add("✓ (test mode — nothing cleaned)");
            _cleaning = false; _cleanResult = "test mode"; Rebuild();
            return;
        }
        bool clip = !App.TestMode;
        var r = await Task.Run(() => MemoryCleaner.Run(aggressive, clearClipboard: clip,
            log: line => Dispatcher.BeginInvoke(() => { _cleanLog.Add(line); Rebuild(); })));
        _cleaning = false;
        _cleanResult = r == null ? "an auto-clean was already running"
            : r.FreedGb > 0.005 ? $"freed {Fmt.Num(r.FreedGb, "0.00")} GB   ({Fmt.Num(r.BeforeGb, "0.0")} → {Fmt.Num(r.AfterGb, "0.0")} GB used)"
            : $"nothing to free   ({Fmt.Num(r.AfterGb, "0.0")} GB used)";
        await Dispatcher.InvokeAsync(Rebuild, DispatcherPriority.Background);
    }

    // ───────────── speed test ─────────────
    private FrameworkElement SpeedTab()
    {
        var p = new StackPanel();
        double? down = _speedResult?.DownMbps ?? _speedProgress?.Down;
        double? up = _speedResult?.UpMbps ?? _speedProgress?.Up;
        double? ping = _speedResult?.PingMs ?? _speedProgress?.PingMs;

        var big = new UniformGrid3();
        big.Add(Big("download", down, "Mbps", K.T.Gpu));
        big.Add(Big("upload", up, "Mbps", K.T.Net));
        big.Add(Big("ping", ping, "ms", K.T.Text, "0"));
        p.Children.Add(big.Panel);

        string server = _speedResult?.Server ?? _speedProgress?.Server ?? "";
        string phase = _speedResult?.Error is { } err ? "failed: " + err
            : _speedCts != null ? _speedProgress?.Phase ?? "starting…"
            : _speedResult != null ? "done" : "nearest speedtest.net server · 10 s down, 10 s up";
        p.Children.Add(Note(phase, _speedResult?.Error != null ? K.T.Hot : K.T.Sub));
        if (server.Length > 0) p.Children.Add(Note("server: " + server, K.T.Dim));

        var btn = _speedCts == null
            ? K.Button(_speedResult == null ? "start test" : "run again", StartSpeed, primary: true)
            : K.Button("cancel", () => _speedCts?.Cancel());
        btn.HorizontalAlignment = HorizontalAlignment.Left;
        btn.Margin = new Thickness(0, F * 0.6, 0, 0);
        p.Children.Add(btn);
        p.Children.Add(Note("uses about 100–500 MB of data on a fast line", K.T.Dim));
        return p;
    }

    private FrameworkElement Big(string label, double? v, string unit, string hex, string fmt = "0.0")
    {
        var s = new StackPanel { Margin = new Thickness(0, F * 0.3, 0, F * 0.3) };
        s.Children.Add(K.Text(label, K.T.Sub, 0.9));
        var t = new TextBlock();
        t.Inlines.Add(new System.Windows.Documents.Run(v is { } x ? Fmt.Num(x, fmt) : "–") { Foreground = K.T.B(hex), FontSize = F * 1.9 });
        t.Inlines.Add(new System.Windows.Documents.Run(" " + unit) { Foreground = K.T.B(K.T.Sub), FontSize = F * 0.9 });
        s.Children.Add(t);
        return s;
    }

    private async void StartSpeed()
    {
        if (_speedCts != null || App.TestMode) return;
        _speedCts = new CancellationTokenSource();
        _speedResult = null; _speedProgress = null; Rebuild();
        var ct = _speedCts.Token;
        long lastUi = 0;
        try
        {
            _speedResult = await Task.Run(() => SpeedTest.RunAsync(pr =>
            {
                _speedProgress = pr;
                long now = Environment.TickCount64;
                if (now - lastUi < 250) return;   // redraw at most 4×/s
                lastUi = now;
                Dispatcher.BeginInvoke(() => { if (IsLoaded && _speedCts != null) Rebuild(); });
            }, ct), ct);
        }
        catch (OperationCanceledException) { _speedResult = null; _speedProgress = _speedProgress is { } pp ? pp with { Phase = "cancelled" } : null; }
        _speedCts?.Dispose();
        _speedCts = null;
        if (IsLoaded) Rebuild();
    }

    // ───────────── IP lookup ─────────────
    private FrameworkElement IpTab()
    {
        if (_localIp == null && !_ipLoading) _ = LoadIps();
        var p = new StackPanel();
        p.Children.Add(IpRow("local (LAN)", _localIp, show: true));
        p.Children.Add(IpRow("public", _publicIp, show: _showPublic,
            hiddenText: "hidden — click to show", onReveal: () => { _showPublic = true; Rebuild(); }));
        var btn = K.Button(_ipLoading ? "looking up…" : "refresh", () => { if (!_ipLoading) _ = LoadIps(); });
        btn.HorizontalAlignment = HorizontalAlignment.Left;
        btn.Margin = new Thickness(0, F * 0.6, 0, 0);
        p.Children.Add(btn);
        p.Children.Add(Note("public IP is hidden until you click it, so it never shows up on stream", K.T.Dim));
        return p;
    }

    private FrameworkElement IpRow(string label, string? value, bool show, string? hiddenText = null, Action? onReveal = null)
    {
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        string text = _ipLoading && value == null ? "…" : value ?? "not found";
        var v = K.Text(show ? text : hiddenText ?? "", show ? K.T.Text : K.T.Sub);
        if (!show && onReveal != null) { v.Cursor = Cursors.Hand; v.MouseLeftButtonDown += (_, e) => { e.Handled = true; onReveal(); }; }
        right.Children.Add(v);
        if (show && value != null)
        {
            var copy = K.Button(_copied == label ? "copied" : "copy", () =>
            {
                try { if (!App.TestMode) Clipboard.SetText(value); } catch { }
                _copied = label; Rebuild();
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                t.Tick += (_, _) => { t.Stop(); _copied = null; if (IsLoaded) Rebuild(); };
                t.Start();
            }, small: true);
            copy.Margin = new Thickness(F * 0.6, 0, 0, 0);
            right.Children.Add(copy);
        }
        return K.Row(label, right);
    }

    private async Task LoadIps()
    {
        _ipLoading = true; Rebuild();
        _localIp = IpLookup.LocalIPv4();
        try { _publicIp = await IpLookup.PublicAsync(CancellationToken.None); } catch { _publicIp = null; }
        _ipLoading = false;
        if (IsLoaded) Rebuild();
    }

    // ───────────── logging ─────────────
    private FrameworkElement LogTab()
    {
        var p = new StackPanel();
        p.Children.Add(K.Row("logging", K.Toggle(H.Logging, _ => H.ToggleLog()), H.Logging ? "recording now" : "off"));
        p.Children.Add(K.Row("write every", K.Seg([("5s", "5000"), ("15s", "15000"), ("30s", "30000"), ("60s", "60000")], S.LogIntervalMs.ToString(),
            v => Set(() => S.LogIntervalMs = int.Parse(v)))));
        var btns = new WrapPanel { Margin = new Thickness(0, F * 0.6, 0, 0) };
        btns.Children.Add(K.Button("open log", () => { if (File.Exists(StatsLogger.FilePath)) Open(StatsLogger.FilePath); }));
        var folder = K.Button("open folder", () => { Directory.CreateDirectory(AppSettings.Dir); Open(AppSettings.Dir); });
        folder.Margin = new Thickness(F * 0.5, 0, 0, 0);
        btns.Children.Add(folder);
        p.Children.Add(btns);
        string size = File.Exists(StatsLogger.FilePath) ? $"{new FileInfo(StatsLogger.FilePath).Length / 1024.0:0} KB" : "not created yet";
        p.Children.Add(Note($"{StatsLogger.FilePath}  ({size})", K.T.Dim));
        p.Children.Add(Note("one line per interval: GPU, CPU, RAM, network, disk, drives — handy to see what happened while you were away", K.T.Sub));
        return p;
    }

    // ───────────── helpers ─────────────
    private TextBlock Note(string text, string hex)
    {
        var t = K.Text(text, hex, 0.9);
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        t.Margin = new Thickness(0, F * 0.2, 0, F * 0.2);
        return t;
    }

    private FrameworkElement LogBox(List<string> lines)
    {
        var s = new StackPanel();
        foreach (var l in lines.TakeLast(14))
            s.Children.Add(K.Text(l, l.StartsWith('✗') ? K.T.Hot : l.StartsWith('–') ? K.T.Dim : K.T.Sub, 0.88));
        return new Border
        {
            Child = s, Background = K.T.B(K.T.Bg), BorderBrush = K.T.B(K.T.Border), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(F * 0.6, F * 0.4, F * 0.6, F * 0.4), Margin = new Thickness(0, F * 0.3, 0, 0),
        };
    }

    private static void Open(string target)
    {
        if (App.TestMode) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Three equal columns.</summary>
    private sealed class UniformGrid3
    {
        public readonly System.Windows.Controls.Primitives.UniformGrid Panel = new() { Columns = 3 };
        public void Add(UIElement e) => Panel.Children.Add(e);
    }
}
