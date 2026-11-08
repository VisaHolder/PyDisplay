using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PyDisplay.UI;

/// <summary>Small note that appears under the overlay for a few seconds. Click-through, never steals focus.</summary>
public static class Toast
{
    public static void Show(Window anchor, Theme T, AppSettings s, string message, double seconds = 3.5)
    {
        double F = s.FontSize;
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            Topmost = true, ShowInTaskbar = false, ShowActivated = false, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, Opacity = 0,
        };
        var b = new System.Windows.Controls.Border
        {
            Background = T.B(T.Panel), BorderBrush = T.B(T.Gpu), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(F * 0.8, F * 0.4, F * 0.8, F * 0.45),
            Child = new System.Windows.Controls.TextBlock { Text = message, Foreground = T.B(T.Text) },
        };
        TextElement.SetFontFamily(b, Fonts.Resolve(s.FontFamily));
        TextElement.SetFontSize(b, F);
        w.Content = b;
        w.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(w).Handle;
            int ex = Sensors.Native.GetWindowLong(h, Sensors.Native.GWL_EXSTYLE);
            Sensors.Native.SetWindowLong(h, Sensors.Native.GWL_EXSTYLE,
                ex | Sensors.Native.WS_EX_TRANSPARENT | Sensors.Native.WS_EX_TOOLWINDOW | Sensors.Native.WS_EX_LAYERED);
        };
        w.Loaded += (_, _) =>
        {
            double vl = SystemParameters.VirtualScreenLeft, vr = vl + SystemParameters.VirtualScreenWidth;
            w.Left = Math.Clamp(anchor.Left + (anchor.ActualWidth - w.ActualWidth) / 2, vl, Math.Max(vl, vr - w.ActualWidth));
            double below = anchor.Top + anchor.ActualHeight + 8;
            w.Top = below + w.ActualHeight < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight
                ? below : anchor.Top - w.ActualHeight - 8;
            w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        };
        w.Show();
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
            fade.Completed += (_, _) => w.Close();
            w.BeginAnimation(UIElement.OpacityProperty, fade);
        };
        t.Start();
    }
}
