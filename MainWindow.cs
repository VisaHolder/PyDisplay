using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PyDisplay.Sensors;
using PyDisplay.UI;

namespace PyDisplay;

/// <summary>The overlay window: borderless, on top, click-through unless Ctrl is held.</summary>
public sealed class MainWindow : Window
{
    private readonly AppHost _h;
    private AppSettings S => _h.S;
    private OverlayView? _view;
    private IntPtr _hwnd;
    private bool _interactive = true;
    private readonly DispatcherTimer _ctrlTimer, _saveTimer;

    public MainWindow(AppHost host)
    {
        _h = host;
        Title = "PyDisplay";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;

        Rebuild();
        PlaceOnScreen();

        _h.Mon.Sampled += snap => Dispatcher.BeginInvoke(() => _view?.Update(snap));

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED);
            ApplyClickThrough(force: true);
        };

        _ctrlTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _ctrlTimer.Tick += (_, _) => ApplyClickThrough();
        _ctrlTimer.Start();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); S.X = Left; S.Y = Top; S.Save(); };
        LocationChanged += (_, _) => { _saveTimer.Stop(); _saveTimer.Start(); };

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || !_interactive) return;
            string? fold = FoldTarget(e.OriginalSource as DependencyObject);
            double x0 = Left, y0 = Top;
            if (CanDrag)
                try { DragMove(); } catch (InvalidOperationException) { }
            // a click on a section title (no movement) folds it; a drag that started there just moved the window
            if (fold != null && Math.Abs(Left - x0) < 2 && Math.Abs(Top - y0) < 2) _h.ToggleSection(fold);
        };
        MouseRightButtonUp += (_, e) => { e.Handled = true; _view?.ShowMenu(null); };
        // Alt+F4 or Windows closing us = same as ×
        Closing += (_, e) => { if (!_exiting) { e.Cancel = true; _h.CloseOverlay(); } };
    }

    private static string? FoldTarget(DependencyObject? d)
    {
        for (; d != null; d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is FrameworkElement { Tag: string t } && t.StartsWith("fold:")) return t[5..];
        return null;
    }

    public bool CanDrag => !S.LockPosition;

    private bool _exiting;
    public void AllowClose() => _exiting = true;

    /// <summary>Rebuilds the overlay (style, theme, font, info changed). Keeps the top-right corner where it was.</summary>
    public void Rebuild()
    {
        double right = Left + ActualWidth;
        bool anchorRight = IsLoaded && ActualWidth > 0 && Left + ActualWidth / 2 > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth / 2;
        Topmost = S.Topmost;
        _view = new OverlayView(Theme.For(S), S, _h);
        Content = _view.Root;
        if (_h.Mon.Last is { } last) _view.Update(last);
        if (anchorRight)
        {
            // overlay sits on the right half: grow/shrink leftwards so it doesn't run off the screen
            Dispatcher.BeginInvoke(() => { if (ActualWidth > 0) Left = right - ActualWidth; }, DispatcherPriority.Loaded);
        }
        ApplyClickThrough(force: true);
    }

    /// <summary>Saved position if it's still on a screen; otherwise top-right of the main screen.</summary>
    private void PlaceOnScreen()
    {
        var v = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                         SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (S.X is { } x && S.Y is { } y && double.IsFinite(x) && double.IsFinite(y) && v.Contains(new Point(x + 40, y + 20)))
        {
            Left = x; Top = y;
            return;
        }
        ResetPosition();
    }

    public void ResetPosition()
    {
        var wa = SystemParameters.WorkArea;
        UpdateLayout();
        double w = ActualWidth > 0 ? ActualWidth : S.FontSize * 23;
        Left = wa.Right - w - 16;
        Top = wa.Top + 16;
        S.X = Left; S.Y = Top; S.Save();
    }

    // ── click-through: mouse passes through unless Ctrl is held (or the setting is off) ──
    private void ApplyClickThrough(bool force = false)
    {
        if (_hwnd == IntPtr.Zero) return;
        bool ctrl = (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;
        bool want = !S.ClickThrough || ctrl;
        if (want == _interactive && !force) return;
        _interactive = want;
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        ex = want ? ex & ~Native.WS_EX_TRANSPARENT : ex | Native.WS_EX_TRANSPARENT;
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex);
    }
}
