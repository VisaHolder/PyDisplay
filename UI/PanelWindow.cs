using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PyDisplay.UI;

/// <summary>
/// Shared frame for every PyDisplay window (settings, tools): borderless, rounded, themed,
/// title + × + tab row. Subclasses only build the content of each tab.
/// </summary>
public abstract class PanelWindow : Window
{
    protected readonly AppHost H;
    protected Kit K = null!;
    protected string Tab;
    private readonly string _title;
    private readonly (string Key, string Label)[] _tabs;
    private bool _placed;

    protected AppSettings S => H.S;
    protected double F => K.F;

    protected PanelWindow(AppHost host, string title, (string Key, string Label)[] tabs, string tab)
    {
        H = host; _title = title; _tabs = tabs;
        Tab = tabs.Any(t => t.Key == tab) ? tab : tabs[0].Key;
        Title = "PyDisplay " + title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Topmost = true;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/app.ico")); } catch { }
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        // subclasses call Rebuild() at the end of their own constructor (their fields must exist first)
    }

    protected virtual double WidthEm => 30;

    /// <summary>Builds the content of one tab.</summary>
    protected abstract FrameworkElement BuildTab(string tab);

    /// <summary>Rebuilds everything (theme/font/setting changed). Keeps the window where it is.</summary>
    public void Rebuild()
    {
        K = new Kit(Theme.For(S), S.FontSize) { Family = Fonts.Resolve(S.FontFamily) };
        Width = F * WidthEm;
        Content = BuildFrame();
    }

    public void ShowTab(string tab)
    {
        if (_tabs.Any(t => t.Key == tab)) Tab = tab;
        Rebuild();
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Test helper: the frame as a detached element (for rendering to PNG without a window).</summary>
    public FrameworkElement DetachFrame()
    {
        var f = (FrameworkElement)Content;
        Content = null;
        f.Width = Width;
        return f;
    }

    private FrameworkElement BuildFrame()
    {
        var T = K.T;
        var stack = new StackPanel();

        // title bar (drag to move)
        var bar = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, F * 0.2) };
        var x = K.Text("×", T.Sub, 1.15);
        x.Cursor = Cursors.Hand;
        x.MouseEnter += (_, _) => x.Foreground = T.B(T.Hot);
        x.MouseLeave += (_, _) => x.Foreground = T.B(T.Sub);
        x.MouseLeftButtonDown += (_, e) => { e.Handled = true; Close(); };
        DockPanel.SetDock(x, Dock.Right);
        bar.Children.Add(x);
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        title.Inlines.Add(new Run("PyDisplay ") { Foreground = T.B(T.Sub) });
        title.Inlines.Add(new Run(_title) { Foreground = T.B(K.Accent), FontWeight = FontWeights.SemiBold });
        bar.Children.Add(title);
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        stack.Children.Add(bar);

        // tabs
        if (_tabs.Length > 1)
        {
            var tabs = new WrapPanel { Margin = new Thickness(0, F * 0.3, 0, 0) };
            foreach (var (key, label) in _tabs)
            {
                bool on = key == Tab;
                var tb = K.Text(label, on ? K.Accent : T.Sub);
                var b = new Border
                {
                    Child = tb, Padding = new Thickness(0, 0, 0, F * 0.25), Margin = new Thickness(0, 0, F * 1.0, F * 0.15),
                    BorderBrush = on ? T.B(K.Accent) : Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 1.5),
                    Cursor = Cursors.Hand, Background = Brushes.Transparent,
                };
                if (!on)
                {
                    b.MouseEnter += (_, _) => tb.Foreground = T.B(T.Text);
                    b.MouseLeave += (_, _) => tb.Foreground = T.B(T.Sub);
                }
                b.MouseLeftButtonDown += (_, e) => { e.Handled = true; Tab = key; Rebuild(); };
                tabs.Children.Add(b);
            }
            stack.Children.Add(tabs);
            stack.Children.Add(new Border { Height = 1, Background = T.B(T.Border), Margin = new Thickness(0, 0, 0, F * 0.4) });
        }

        FrameworkElement content;
        try { content = BuildTab(Tab); }
        catch (Exception ex) { Log.Error("panel-" + Tab, ex); content = K.Text("couldn't build this tab: " + ex.Message, T.Hot); }
        // safety: never taller than the screen; wheel still scrolls, no ugly Windows scrollbar
        stack.Children.Add(new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            MaxHeight = SystemParameters.WorkArea.Height * 0.78, Focusable = false,
        });

        var root = new Border
        {
            Background = T.B(T.Panel), BorderBrush = T.B(T.Border), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(F * 1.1, F * 0.7, F * 1.1, F * 1.0),
            Child = stack,
        };
        TextElement.SetFontFamily(root, Fonts.Resolve(S.FontFamily));
        TextElement.SetFontSize(root, F);
        TextElement.SetForeground(root, T.B(T.Text));
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
        return root;
    }

    public static void MoveOffscreen(Window? w) { if (w != null) { w.Left = -30000; w.Top = 0; } }

    /// <summary>First show: sit beside the overlay (left if there's room, else right), on screen.</summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_placed) return;
        _placed = true;
        if (App.TestMode) { MoveOffscreen(this); return; }
        var o = H.Overlay;
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
        double left, top;
        if (o is { IsVisible: true })
        {
            left = o.Left - ActualWidth - 10;
            if (left < vl) left = o.Left + o.ActualWidth + 10;
            top = o.Top;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            left = wa.Left + (wa.Width - ActualWidth) / 2;
            top = wa.Top + (wa.Height - ActualHeight) / 3;
        }
        Left = Math.Clamp(left, vl, Math.Max(vl, vr - ActualWidth));
        Top = Math.Clamp(top, vt, Math.Max(vt, vb - ActualHeight - 40));
    }
}
