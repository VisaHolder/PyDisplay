using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PyDisplay.Sensors;
using PyDisplay.UI;

namespace PyDisplay;

/// <summary>
/// Test helper: `PyDisplay.exe --snap &lt;dir&gt;` collects ~15 s of live data, then saves PNGs of every
/// style × layout, theme and font, plus every settings/tools tab — without opening a single window.
/// Uses a copy of the settings, so it never changes the real ones.
/// </summary>
internal static class Snapper
{
    public static void Run(AppHost realHost, string dir, Action done)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                Directory.CreateDirectory(dir);
                var snap = realHost.Mon.Last ?? new Snapshot();
                var baseS = realHost.S.Clone();
                baseS.Opacity = 1;

                foreach (var style in new[] { "rows", "graphs", "compact" })
                    foreach (var direction in new[] { "vertical", "horizontal" })
                    {
                        var s = baseS.Clone(); s.Style = style; s.Direction = direction;
                        Overlay(s, snap, Path.Combine(dir, $"{style}-{direction}.png"));
                    }
                foreach (var theme in Theme.Presets.Keys)
                {
                    var s = baseS.Clone(); s.Theme = theme;
                    Overlay(s, snap, Path.Combine(dir, $"theme-{theme}.png"));
                }
                foreach (var font in Fonts.Available())
                {
                    var s = baseS.Clone(); s.FontFamily = font;
                    Overlay(s, snap, Path.Combine(dir, $"font-{font.Replace(' ', '_')}.png"));
                }
                {
                    var s = baseS.Clone();
                    foreach (var (k, _) in AppSettings.AllExtras) s.Extras[k] = true;
                    s.Style = "compact"; s.Direction = "horizontal";
                    Overlay(s, snap, Path.Combine(dir, "allextras-strip.png"));
                    s.Direction = "vertical";
                    Overlay(s, snap, Path.Combine(dir, "allextras-compact.png"));
                    s.Style = "rows";
                    Overlay(s, snap, Path.Combine(dir, "allextras-rows.png"));
                }
                {
                    var s = baseS.Clone(); s.Collapsed = ["cpu", "net"];
                    Overlay(s, snap, Path.Combine(dir, "folded-rows.png"));
                    s.Style = "compact";
                    Overlay(s, snap, Path.Combine(dir, "folded-compact.png"));
                }

                // windows, rendered from a throwaway host so nothing real is touched
                var host = new AppHost(baseS.Clone(), realHost.Mon);
                foreach (var tab in new[] { "look", "info", "sections", "behaviour", "theme", "about" })
                    Render(new SettingsWindow(host, tab).DetachFrame(), Path.Combine(dir, $"settings-{tab}.png"));
                {
                    // overlay with its real header + the ≡ menu, as the user sees them
                    var s = baseS.Clone(); s.Style = "graphs";
                    var v = new OverlayView(Theme.For(s), s, host);
                    v.Update(snap); Layout(v.Root); v.Update(snap);
                    Render(v.Root, Path.Combine(dir, "header-graphs.png"));
                    Render(v.MenuBox(() => { }), Path.Combine(dir, "menu.png"));
                    s.Style = "compact"; s.Direction = "horizontal";
                    Overlay(s, snap, Path.Combine(dir, "header-strip.png"), host);
                }
                foreach (var tab in new[] { "memory", "speed", "ip", "log" })
                    Render(new ToolsWindow(host, tab).DetachFrame(), Path.Combine(dir, $"tools-{tab}.png"));
            }
            catch (Exception ex) { Log.Error("snap", ex); File.WriteAllText(Path.Combine(dir, "snap-error.txt"), ex.ToString()); }
            done();
        };
        timer.Start();
    }

    private static void Overlay(AppSettings s, Snapshot snap, string path, IOverlayHost? host = null)
    {
        var v = new OverlayView(Theme.For(s), s, host);
        v.Update(snap);
        Layout(v.Root);
        v.Update(snap);   // rows that hid/showed after the first update
        Render(v.Root, path);
    }

    private static void Layout(FrameworkElement el)
    {
        el.Measure(new Size(double.IsNaN(el.Width) ? double.PositiveInfinity : el.Width, double.PositiveInfinity));
        el.Arrange(new Rect(el.DesiredSize));
        el.UpdateLayout();
    }

    private static void Render(FrameworkElement el, string path)
    {
        Layout(el);
        const double scale = 1.5;
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(el.ActualWidth * scale), (int)Math.Ceiling(el.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
