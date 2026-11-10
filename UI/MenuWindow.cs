using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PyDisplay.UI;

/// <summary>
/// Drop-down surface for the ≡ menu and every dropdown list. A WPF Popup proved unreliable here
/// (it closed the instant it opened), so each drop-down is its own small window: it takes focus while
/// open and closes when you pick something, click anywhere else, or press Esc.
/// </summary>
public sealed class MenuWindow : Window
{
    private static MenuWindow? _open;
    private bool _closing;

    public static bool IsOpenNow => _open != null;
    private static DateTime _lastClosed;

    /// <summary>True right after a drop-down closed — so clicking its own button again closes it instead of reopening.</summary>
    public static bool JustClosed => (DateTime.UtcNow - _lastClosed).TotalMilliseconds < 250;

    /// <param name="build">Builds the contents; gets the "close me" action.</param>
    /// <param name="at">Screen point in DIPs (top-left, or top-right when rightAlign).</param>
    public static MenuWindow Open(Func<Action, FrameworkElement> build, Point at, bool rightAlign, double minWidth = 0)
    {
        _open?.SafeClose();
        var w = new MenuWindow { MinWidth = minWidth };
        w.Content = build(w.SafeClose);
        w.Loaded += (_, _) =>
        {
            double left = rightAlign ? at.X - w.ActualWidth : at.X;
            double top = at.Y;
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
            double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
            if (top + w.ActualHeight > vb) top = at.Y - w.ActualHeight - 8;   // no room below: open upwards
            w.Left = Math.Clamp(left, vl, Math.Max(vl, vr - w.ActualWidth));
            w.Top = Math.Clamp(top, vt, Math.Max(vt, vb - w.ActualHeight));
            w.Opacity = 1;
            w.Activate();
        };
        _open = w;
        w.Show();
        return w;
    }

    /// <summary>Screen position (DIPs) of a point inside an element.</summary>
    public static Point ScreenDip(FrameworkElement e, Point local)
    {
        var src = PresentationSource.FromVisual(e);
        var m = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return m.Transform(e.PointToScreen(local));
    }

    private MenuWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -30000;          // measured off-screen first, then moved into place (no flicker)
        Opacity = 0;
        Deactivated += (_, _) => { if (!App.TestMode) SafeClose(); };   // tests: another app taking focus mustn't close it
        KeyDown += (_, e) => { if (e.Key == Key.Escape) SafeClose(); };
        Closed += (_, _) => { if (_open == this) _open = null; _watch.Stop(); _lastClosed = DateTime.UtcNow; };

        // Backup for when Windows refuses to give us focus (then "Deactivated" never fires):
        // close on any mouse click outside the menu.
        _watch.Tick += (_, _) =>
        {
            if (!IsLoaded || _closing || App.TestMode) return;   // tests: the real mouse (someone using the PC) mustn't close it
            bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (!down) { _armed = true; return; }   // ignore the click that opened us
            if (!_armed || IsMouseOver) return;
            SafeClose();
        };
        _watch.Start();
    }

    private readonly System.Windows.Threading.DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _armed;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    public void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.BeginInvoke(Close);
    }
}
