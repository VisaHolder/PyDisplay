using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using PyDisplay.Sensors;
using PyDisplay.Tools;
using PyDisplay.UI;

namespace PyDisplay;

/// <summary>
/// `PyDisplay.exe --settingstest &lt;file&gt;` — flips EVERY setting and checks that something real changes
/// (overlay text, colours, size, window state, timers, registry). A setting that changes nothing = FAIL.
/// Runs on a throwaway copy of the settings; the start-with-Windows check restores the registry after.
/// </summary>
internal static class EffectTest
{
    private static readonly StringBuilder Sb = new();
    private static int _fails;
    private static string _out = "";

    private static void W(string s) { Sb.AppendLine(s); try { File.WriteAllText(_out, Sb.ToString()); } catch { } }
    private static void Expect(string what, bool ok, string detail = "")
    {
        if (!ok) _fails++;
        W($"{(ok ? "ok  " : "FAIL")}  {what}{(detail.Length > 0 ? "  — " + detail : "")}");
    }

    public static async Task RunAsync(AppSettings real, SystemMonitor mon, string outFile)
    {
        _out = outFile;
        App.TestMode = true;
        AppSettings.NoSave = true;
        await Task.Delay(3000);
        var snap = mon.Last ?? new Snapshot();
        snap.PingMs ??= 20;   // make sure every value has something to show

        // ── 1. overlay: every info toggle, in every style and layout ──
        foreach (var style in new[] { "rows", "graphs", "compact" })
            foreach (var dir in new[] { "vertical", "horizontal" })
            {
                var b = Base(real, style, dir);
                string baseDump = Dump(b, snap);
                foreach (var (key, label) in AppSettings.AllExtras)
                {
                    var off = b.Clone(); off.Extras[key] = false;
                    var on = b.Clone(); on.Extras[key] = true;
                    string dOff = Dump(off, snap), dOn = Dump(on, snap);
                    Expect($"{style}/{dir}: '{label}' toggle", dOff != dOn, dOff == dOn ? "no visible change" : Diff(dOn, dOff));
                }
                var f = b.Clone(); f.Fahrenheit = true;
                Expect($"{style}/{dir}: °F", Dump(f, snap).Contains("°F") || !baseDump.Contains("°C"), "");
                foreach (var sec in AppSettings.AllSections)
                {
                    var h = b.Clone(); h.Hidden = [sec];
                    Expect($"{style}/{dir}: hide {sec}", Dump(h, snap) != baseDump);
                    var c = b.Clone(); c.Collapsed = [sec];
                    Expect($"{style}/{dir}: fold {sec}", Dump(c, snap) != baseDump);
                }
                var o = b.Clone(); o.SectionOrder = ["cpu", "gpu", "mem", "net", "disk", "storage"];
                Expect($"{style}/{dir}: reorder", Dump(o, snap) != baseDump);
            }

        // ── 2. look: colours, font, size, see-through ──
        {
            var b = Base(real, "rows", "vertical");
            var v1 = Build(b, snap);
            var t = b.Clone(); t.Theme = "ice";
            Expect("theme changes colours", ((SolidColorBrush)Build(t, snap).Root.Background).Color != ((SolidColorBrush)v1.Root.Background).Color);
            var op = b.Clone(); op.Opacity = 0.5;
            Expect("background see-through", ((SolidColorBrush)Build(op, snap).Root.Background).Color.A == 128, $"alpha {((SolidColorBrush)Build(op, snap).Root.Background).Color.A}");
            var fo = b.Clone(); fo.FontFamily = "Consolas";
            Expect("font", TextElement.GetFontFamily(Build(fo, snap).Root).Source == "Consolas");
            var sz = b.Clone(); sz.FontSize = 16;
            Expect("size", Build(sz, snap).Root.DesiredSize.Width > v1.Root.DesiredSize.Width);
            var cu = b.Clone(); cu.Theme = "custom"; cu.CustomColors = Theme.Presets["default"].ToColors(); cu.CustomColors["Bg"] = "#ff0000";
            Expect("custom colours", ((SolidColorBrush)Build(cu, snap).Root.Background).Color.R == 255);
            var sv = b.Clone(); sv.SavedThemes["mine"] = new() { ["Bg"] = "#00ff00" }; sv.Theme = "mine";
            Expect("saved theme", ((SolidColorBrush)Build(sv, snap).Root.Background).Color.G == 255);
        }

        // ── 3. behaviour: real app host, overlay parked off-screen ──
        var host = new AppHost(Base(real, "rows", "vertical"), mon);
        host.Start();
        host.Overlay!.Left = -30000;
        await Task.Delay(800);
        var S = host.S;
        IntPtr hwnd = new WindowInteropHelper(host.Overlay).Handle;
        bool Transparent() => (Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TRANSPARENT) != 0;

        S.Topmost = false; host.Apply();
        Expect("always on top: off", !host.Overlay.Topmost);
        S.Topmost = true; host.Apply();
        Expect("always on top: on", host.Overlay.Topmost);

        S.ClickThrough = true; host.Apply(); await Task.Delay(200);
        Expect("click-through: on", Transparent() || (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0);
        S.ClickThrough = false; host.Apply(); await Task.Delay(200);
        Expect("click-through: off", !Transparent());

        S.LockPosition = true; host.Apply();
        Expect("lock position", !host.Overlay.CanDrag);
        S.LockPosition = false; host.Apply();
        Expect("unlock position", host.Overlay.CanDrag);

        foreach (int ms in new[] { 500, 2000, 5000, 1000 })
        {
            S.RefreshMs = ms; host.Apply();
            Expect($"update every {ms} ms", mon.IntervalMs == ms);
        }

        S.TrayMode = true; host.Apply();
        Expect("tray icon always on", host.WantTray);
        S.TrayMode = false; host.Apply();
        Expect("tray icon only when hidden", !host.WantTray);
        host.HideOverlay();
        Expect("hidden overlay → tray icon", host.WantTray);
        host.ShowOverlay(); host.Overlay.Left = -30000;

        S.TrayShowGpu = true;
        Expect("tray shows stats: on", Tray.TooltipText(snap, S).Contains("GPU"));
        S.TrayShowGpu = false;
        Expect("tray shows stats: off", !Tray.TooltipText(snap, S).Contains("GPU"));

        S.AutoCleanOn = true; S.AutoCleanMinutes = 15; host.Apply();
        Expect("auto-clean on (15 min)", host.AutoCleanInterval == TimeSpan.FromMinutes(15) && host.NextAutoClean != null);
        S.AutoCleanMinutes = 5; host.Apply();
        Expect("auto-clean interval 5 min", host.AutoCleanInterval == TimeSpan.FromMinutes(5));
        S.AutoCleanOn = false; host.Apply();
        Expect("auto-clean off", host.NextAutoClean == null);

        S.LogIntervalMs = 5000; host.Apply();
        host.ToggleLog();
        Expect("logging starts at 5 s", host.Logging && host.Logger.IntervalMs == 5000);
        S.LogIntervalMs = 30000; host.Apply();
        Expect("logging interval change while recording", host.Logger.IntervalMs == 30000);
        host.ToggleLog();
        Expect("logging stops", !host.Logging);

        // start with Windows: real registry, original state restored afterwards
        // restore the exact raw value afterwards — it may belong to another copy (e.g. PyDisplay 1.x uses the same name)
        App.TestMode = false;
        using var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)!;
        object? origRaw = runKey.GetValue("PyDisplay");
        Startup.Enabled = true; bool afterOn = Startup.Enabled;
        Startup.Enabled = false; bool afterOff = Startup.Enabled;
        if (origRaw == null) runKey.DeleteValue("PyDisplay", false); else runKey.SetValue("PyDisplay", origRaw);
        App.TestMode = true;
        Expect("start with Windows", afterOn && !afterOff && Equals(runKey.GetValue("PyDisplay"), origRaw), $"restored exactly: {origRaw ?? "(none)"}");

        W(_fails == 0 ? "ALL PASSED — every setting does something" : $"{_fails} FAILED");
        host.Exit();
    }

    private static AppSettings Base(AppSettings real, string style, string dir)
    {
        var s = real.Clone();
        s.Style = style; s.Direction = dir; s.Hidden = []; s.Collapsed = []; s.Theme = "default";
        s.FontFamily = "Cascadia Mono"; s.FontSize = 12; s.Opacity = 0.94; s.Fahrenheit = false;
        s.SectionOrder = [.. AppSettings.AllSections];
        foreach (var (k, _) in AppSettings.AllExtras) s.Extras[k] = true;
        return s;
    }

    private static OverlayView Build(AppSettings s, Snapshot snap)
    {
        var v = new OverlayView(Theme.For(s), s, null);
        v.Update(snap);
        v.Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        v.Root.Arrange(new Rect(v.Root.DesiredSize));
        v.Root.UpdateLayout();
        v.Update(snap);
        return v;
    }

    /// <summary>Every piece of text actually showing, in order.</summary>
    private static string Dump(AppSettings s, Snapshot snap)
    {
        var v = Build(s, snap);
        var parts = new List<string>();
        void Walk(DependencyObject d)
        {
            if (d is UIElement { Visibility: not Visibility.Visible }) return;
            if (d is TextBlock tb && tb.Text.Length > 0) parts.Add(tb.Text);
            if (d is BlockBar or Sparkline or CoreBars) parts.Add("<" + d.GetType().Name + ">");
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(v.Root);
        return string.Join(" | ", parts);
    }

    private static string Diff(string a, string b)
    {
        var x = a.Split(" | ").Except(b.Split(" | ")).Take(3);
        return "shows: " + string.Join(", ", x);
    }
}
