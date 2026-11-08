using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PyDisplay.Sensors;
using PyDisplay.UI;

namespace PyDisplay;

/// <summary>
/// `PyDisplay.exe --uitest &lt;file&gt;` — opens the real windows off-screen with a throwaway copy of the
/// settings and clicks every clickable thing on every tab, logging crashes. App.TestMode blocks
/// side effects (registry, UAC, file dialogs, opening folders, speed test, clipboard).
/// </summary>
internal static class UiTest
{
    public static async Task RunAsync(AppSettings real, SystemMonitor mon, string outFile)
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); try { File.WriteAllText(outFile, sb.ToString()); } catch { } }
        int clicks = 0, errors = 0;
        App.TestMode = true;
        AppSettings.NoSave = true;

        var errLog = new List<string>();
        Application.Current.DispatcherUnhandledException += (_, e) => { errLog.Add(e.Exception.ToString()); e.Handled = true; };

        var host = new AppHost(real.Clone(), mon);
        host.Start();
        host.Overlay!.Left = -30000;
        await Task.Delay(2500);   // let a few samples arrive

        async Task Settle() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        async Task Fuzz(string name, Func<FrameworkElement?> root, Func<Task> reset)
        {
            await reset();
            await Settle();
            int count = Clickables(root()).Count;
            for (int i = 0; i < count; i++)
            {
                await reset();
                await Settle();
                var items = Clickables(root());
                if (i >= items.Count) break;
                var el = items[i];
                string label = Label(el);
                if (label is "×") continue;   // closes the window under test
                int before = errLog.Count;
                try
                {
                    el.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                    clicks++;
                    await Settle();
                    await Task.Delay(30);
                }
                catch (Exception ex) { errLog.Add(ex.ToString()); }
                if (errLog.Count > before) { errors++; W($"ERROR  {name} → '{label}': {errLog[^1].Split('\n')[0]}"); }
            }
            W($"ok     {name}: {count} clickables");
        }

        // overlay, every style
        foreach (var style in new[] { "rows", "graphs", "compact" })
            foreach (var dir in new[] { "vertical", "horizontal" })
                await Fuzz($"overlay {style}/{dir}", () => host.Overlay.Content as FrameworkElement, async () =>
                {
                    host.S.Style = style; host.S.Direction = dir; host.S.Collapsed.Clear();
                    host.Apply();
                    await Task.CompletedTask;
                });

        foreach (var tab in new[] { "look", "info", "sections", "behaviour", "theme", "about" })
            await Fuzz("settings/" + tab, () => FindWindow<SettingsWindow>()?.Content as FrameworkElement, async () =>
            {
                host.OpenSettings(tab);
                PanelWindow.MoveOffscreen(FindWindow<SettingsWindow>());
                await Task.CompletedTask;
            });

        foreach (var tab in new[] { "memory", "speed", "ip", "log" })
            await Fuzz("tools/" + tab, () => FindWindow<ToolsWindow>()?.Content as FrameworkElement, async () =>
            {
                host.OpenTools(tab);
                PanelWindow.MoveOffscreen(FindWindow<ToolsWindow>());
                await Task.Delay(tab == "memory" ? 300 : 0);   // a clean may still be finishing
            });

        // the ≡ menu: opens, stays open, and its items work
        foreach (var style in new[] { "rows", "compact" })
            foreach (var dir in new[] { "vertical", "horizontal" })
            {
                foreach (var w in Application.Current.Windows.OfType<ToolsWindow>().ToList()) w.Close();
                host.S.Style = style; host.S.Direction = dir; host.Apply();
                await Settle();
                var menuBtn = Descendants<TextBlock>(host.Overlay.Content as DependencyObject).FirstOrDefault(t => t.Text == "≡");
                if (menuBtn == null) { errors++; W($"ERROR  {style}/{dir}: no ≡ button"); continue; }
                menuBtn.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                await Task.Delay(1000);
                var menu = Application.Current.Windows.OfType<MenuWindow>().FirstOrDefault(m => m.IsVisible);
                if (menu == null) { errors++; W($"ERROR  {style}/{dir}: menu didn't open / closed by itself"); continue; }
                var onScreen = menu.Left > -20000;
                var items = Descendants<TextBlock>(menu.Content as DependencyObject).Select(t => t.Text).ToList();
                var tools = Descendants<FrameworkElement>(menu.Content as DependencyObject)
                    .FirstOrDefault(e => e.Cursor == Cursors.Hand && Descendants<TextBlock>(e).Any(t => t.Text == "tools"));
                tools?.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                await Task.Delay(500);
                bool toolsOpen = Application.Current.Windows.OfType<ToolsWindow>().Any(w => w.IsVisible);
                bool menuGone = !Application.Current.Windows.OfType<MenuWindow>().Any(m => m.IsVisible);
                bool ok = onScreen && toolsOpen && menuGone && items.Count >= 6;
                if (!ok) errors++;
                W($"{(ok ? "ok   " : "ERROR")}  ≡ menu {style}/{dir}: stayed open, placed at {menu.Left:0},{menu.Top:0}, items [{string.Join(", ", items)}], tools opened={toolsOpen}, menu closed={menuGone}");
            }

        // ── functional checks: each control must actually change what it says it changes ──
        foreach (var w in Application.Current.Windows.OfType<ToolsWindow>().ToList()) w.Close();
        async Task<bool> ClickLabel(Window? win, string label, int nth = 0)
        {
            if (win == null) return false;
            var hits = Descendants<FrameworkElement>(win.Content as DependencyObject)
                .Where(e => e.Cursor == Cursors.Hand && e.IsVisible && Label(e) == label).ToList();
            if (hits.Count <= nth) return false;
            hits[nth].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            await Settle(); await Task.Delay(400); await Settle();
            return true;
        }
        void Expect(string what, bool ok, string detail = "") { if (!ok) errors++; W($"{(ok ? "ok   " : "ERROR")}  {what} {detail}"); }
        SettingsWindow? SW() => FindWindow<SettingsWindow>();

        host.S.Style = "rows"; host.S.FontSize = 12; host.S.FontFamily = "Cascadia Mono"; host.S.Theme = "default"; host.Apply();
        host.OpenSettings("look"); await Settle();

        await ClickLabel(SW(), "graphs");
        Expect("style picker", host.S.Style == "graphs", host.S.Style);
        await ClickLabel(SW(), "+");
        Expect("size +", host.S.FontSize == 13, host.S.FontSize.ToString());
        await ClickLabel(SW(), "−");
        Expect("size −", host.S.FontSize == 12, host.S.FontSize.ToString());

        // font dropdown: list must open, picking must change the font
        var drop = Descendants<FrameworkElement>(SW()!.Content as DependencyObject)
            .FirstOrDefault(e => e is Border && e.Cursor == Cursors.Hand && Label(e).Contains('▾'));
        drop?.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        await Task.Delay(700);
        var list = Application.Current.Windows.OfType<MenuWindow>().FirstOrDefault(m => m.IsVisible);
        var fonts = list == null ? [] : Descendants<TextBlock>(list.Content as DependencyObject).Select(t => t.Text).ToList();
        Expect("font dropdown opens and stays open", list != null && fonts.Count >= 2, $"[{string.Join(", ", fonts)}]");
        string pickFont = fonts.FirstOrDefault(f => f != "Cascadia Mono") ?? "";
        if (list != null && pickFont != "") await ClickLabel(list, pickFont);
        await Task.Delay(300);
        Expect("picking a font changes it", host.S.FontFamily == pickFont, $"now '{host.S.FontFamily}', picked '{pickFont}'");
        Expect("font list closes after picking", !Application.Current.Windows.OfType<MenuWindow>().Any(m => m.IsVisible));

        host.OpenSettings("info"); await Settle();
        bool before = host.S.Extra("gpuclock");
        var toggles = Descendants<Border>(SW()!.Content as DependencyObject).Where(b => b.Cursor == Cursors.Hand && b.Child is System.Windows.Shapes.Ellipse).ToList();
        toggles.FirstOrDefault()?.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        await Settle(); await Task.Delay(300);
        Expect("info toggle", host.S.Extra("gpuclock") != before);

        host.OpenSettings("sections"); await Settle();
        string first = host.S.SectionOrder[0];
        await ClickLabel(SW(), "↓");
        Expect("section reorder", host.S.SectionOrder[1] == first, string.Join(",", host.S.SectionOrder));

        host.OpenSettings("theme"); await Settle();
        await ClickLabel(SW(), "ice");
        Expect("theme card", host.S.Theme == "ice", host.S.Theme);

        host.OpenTools("log"); await Settle();
        bool logBefore = host.Logging;
        var ltog = Descendants<Border>(FindWindow<ToolsWindow>()!.Content as DependencyObject).FirstOrDefault(b => b.Cursor == Cursors.Hand && b.Child is System.Windows.Shapes.Ellipse);
        ltog?.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        await Settle(); await Task.Delay(300);
        Expect("logging switch", host.Logging != logBefore);
        if (host.Logging) host.ToggleLog();

        // typing into the theme colour boxes: bad value rejected, good value applied
        host.S.Theme = "default"; host.Apply();
        host.OpenSettings("theme");
        await Settle();
        var sw = FindWindow<SettingsWindow>()!;
        var boxes = Descendants<TextBox>(sw.Content as DependencyObject).ToList();
        W($"theme boxes: {boxes.Count}");
        if (boxes.Count > 0)
        {
            boxes[0].Text = "nothex";
            boxes[0].RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(boxes[0]) ?? new HwndSourceStub(), 0, Key.Enter) { RoutedEvent = UIElement.KeyDownEvent });
            await Settle();
            bool rejected = host.S.Theme == "default";
            boxes = Descendants<TextBox>(sw.Content as DependencyObject).ToList();
            boxes[0].Text = "#ff00ff";
            boxes[0].RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(boxes[0]) ?? new HwndSourceStub(), 0, Key.Enter) { RoutedEvent = UIElement.KeyDownEvent });
            await Settle();
            bool applied = host.S.Theme == "custom" && host.S.CustomColors.GetValueOrDefault("Gpu") == "#ff00ff";
            W($"{(rejected ? "ok   " : "ERROR")}  bad hex rejected");
            W($"{(applied ? "ok   " : "ERROR")}  good hex → custom theme ({host.S.Theme}, gpu={host.S.CustomColors.GetValueOrDefault("Gpu")})");
            if (!rejected || !applied) errors++;
        }

        // settings must still be valid after all that clicking
        var check = AppSettings.FromJson(System.Text.Json.JsonSerializer.Serialize(host.S));
        W($"settings still valid: style={check.Style} theme={check.Theme} sections={check.SectionOrder.Count} hidden={check.Hidden.Count}");

        foreach (var e in errLog.Distinct().Take(5)) W("---\n" + e);
        W($"{clicks} clicks, {errors} errors, {errLog.Count} exceptions");
        W(errors == 0 && errLog.Count == 0 ? "ALL PASSED" : "FAILED");
        host.Exit();
    }

    private static T? FindWindow<T>() where T : Window => Application.Current.Windows.OfType<T>().FirstOrDefault();

    private static List<FrameworkElement> Clickables(FrameworkElement? root) =>
        root == null ? [] : Descendants<FrameworkElement>(root)
            .Where(e => e.Cursor == Cursors.Hand && e.IsHitTestVisible && e.IsVisible)
            .Where(e => !Descendants<FrameworkElement>(e).Skip(1).Any(c => c.Cursor == Cursors.Hand))   // innermost only
            .ToList();

    private static string Label(FrameworkElement e) => e switch
    {
        TextBlock t => t.Text,
        _ => string.Join(" ", Descendants<TextBlock>(e).Select(t => t.Text)).Trim(),
    };

    private static IEnumerable<T> Descendants<T>(DependencyObject? d) where T : DependencyObject
    {
        if (d == null) yield break;
        if (d is T self) yield return self;
        int n = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
        for (int i = 0; i < n; i++)
            foreach (var c in Descendants<T>(VisualTreeHelper.GetChild(d, i))) yield return c;
    }

    /// <summary>Stand-in when a box has no presentation source yet.</summary>
    private sealed class HwndSourceStub : PresentationSource
    {
        protected override CompositionTarget GetCompositionTargetCore() => null!;
        public override Visual RootVisual { get => null!; set { } }
        public override bool IsDisposed => false;
    }
}
