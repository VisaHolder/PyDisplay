using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PyDisplay.UI;

/// <summary>
/// The shared parts every PyDisplay window is built from: text, rows, segmented pickers,
/// toggles, steppers, buttons, dropdowns, text boxes. One look, defined once.
/// </summary>
public sealed class Kit(Theme t, double f)
{
    public Theme T { get; } = t;
    public double F { get; } = f;
    public string Accent => T.Gpu;
    public FontFamily? Family { get; init; }   // drop-down windows don't inherit the font, so they set it themselves

    public TextBlock Text(string text, string? hex = null, double scale = 1, bool bold = false) => new()
    {
        Text = text,
        Foreground = T.B(hex ?? T.Text),
        FontSize = F * scale,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>Small dim heading inside a tab.</summary>
    public TextBlock Heading(string text)
    {
        var h = Text(text, T.Dim, 0.9);
        h.Margin = new Thickness(0, F * 1.1, 0, F * 0.35);
        return h;
    }

    /// <summary>label ............ control</summary>
    public DockPanel Row(string label, UIElement control, string? hint = null)
    {
        var d = new DockPanel { Margin = new Thickness(0, F * 0.3, 0, F * 0.3), LastChildFill = true };
        DockPanel.SetDock(control, Dock.Right);
        if (control is FrameworkElement fe) fe.Margin = new Thickness(F * 0.8, 0, 0, 0);
        d.Children.Add(control);
        // label takes the rest of the row; the hint wraps instead of getting cut off
        var l = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        l.Children.Add(Text(label));
        if (hint != null)
        {
            var h = Text(hint, T.Sub, 0.85);
            h.TextWrapping = TextWrapping.Wrap;
            h.TextTrimming = TextTrimming.None;
            l.Children.Add(h);
        }
        d.Children.Add(l);
        return d;
    }

    /// <summary>[ rows | graphs | compact ] — the selected one filled with the accent colour.</summary>
    public StackPanel Seg(IEnumerable<(string Label, string Value)> items, string current, Action<string> pick)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, value) in items)
        {
            bool on = string.Equals(value, current, StringComparison.OrdinalIgnoreCase);
            var tb = Text(label, on ? T.Bg : T.Sub, 0.95);
            var b = new Border
            {
                Child = tb,
                Background = on ? T.B(Accent) : Brushes.Transparent,
                BorderBrush = T.B(on ? Accent : T.Border),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(F * 0.6, F * 0.15, F * 0.6, F * 0.2),
                Margin = new Thickness(-1, 0, 0, 0),
                Cursor = Cursors.Hand,
            };
            if (!on)
            {
                b.MouseEnter += (_, _) => tb.Foreground = T.B(T.Text);
                b.MouseLeave += (_, _) => tb.Foreground = T.B(T.Sub);
            }
            b.MouseLeftButtonDown += (_, e) => { e.Handled = true; pick(value); };
            p.Children.Add(b);
        }
        if (p.Children.Count > 0)
        {
            ((Border)p.Children[0]).CornerRadius = new CornerRadius(4, 0, 0, 4);
            ((Border)p.Children[^1]).CornerRadius = p.Children.Count == 1 ? new CornerRadius(4) : new CornerRadius(0, 4, 4, 0);
        }
        return p;
    }

    /// <summary>On/off switch.</summary>
    public Border Toggle(bool on, Action<bool> set)
    {
        double h = F * 1.25, w = F * 2.3;
        var knob = new Ellipse { Width = h - 6, Height = h - 6, Fill = T.B(on ? T.Bg : T.Sub), HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(3) };
        var b = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(h / 2),
            Background = T.B(on ? Accent : T.Border), Child = knob, Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.MouseLeftButtonDown += (_, e) => { e.Handled = true; set(!on); };
        return b;
    }

    /// <summary>−  12  +</summary>
    public StackPanel Stepper(string value, Action minus, Action plus)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(Button("−", minus, small: true));
        var v = Text(value); v.MinWidth = F * 3.4; v.TextAlignment = TextAlignment.Center;
        p.Children.Add(v);
        p.Children.Add(Button("+", plus, small: true));
        return p;
    }

    public Border Button(string text, Action click, bool small = false, bool primary = false)
    {
        var tb = Text(text, primary ? T.Bg : T.Text, small ? 1 : 0.95);
        var b = new Border
        {
            Child = tb,
            Background = primary ? T.B(Accent) : Brushes.Transparent,
            BorderBrush = T.B(primary ? Accent : T.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = small ? new Thickness(F * 0.45, 0, F * 0.45, F * 0.05) : new Thickness(F * 0.8, F * 0.2, F * 0.8, F * 0.25),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (!primary)
        {
            b.MouseEnter += (_, _) => b.BorderBrush = T.B(T.Sub);
            b.MouseLeave += (_, _) => b.BorderBrush = T.B(T.Border);
        }
        b.MouseLeftButtonDown += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    /// <summary>Current value + ▾; opens a themed list.</summary>
    public Border Dropdown(IEnumerable<(string Label, string Value)> items, string current, Action<string> pick, Func<string, FontFamily?>? fontOf = null)
    {
        var list = items.ToList();
        string shown = list.FirstOrDefault(i => string.Equals(i.Value, current, StringComparison.OrdinalIgnoreCase)).Label ?? current;
        var row = new DockPanel();
        var arrow = Text(" ▾", T.Sub); DockPanel.SetDock(arrow, Dock.Right);
        row.Children.Add(arrow);
        var cur = Text(shown);
        if (fontOf?.Invoke(current) is { } cf) cur.FontFamily = cf;
        row.Children.Add(cur);
        var box = new Border
        {
            Child = row, BorderBrush = T.B(T.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(F * 0.6, F * 0.15, F * 0.5, F * 0.2), MinWidth = F * 10, Cursor = Cursors.Hand,
            Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center,
        };
        box.MouseEnter += (_, _) => box.BorderBrush = T.B(T.Sub);
        box.MouseLeave += (_, _) => box.BorderBrush = T.B(T.Border);

        FrameworkElement BuildList(Action close)
        {
            var opts = new StackPanel();
            foreach (var (label, value) in list)
            {
                bool on = string.Equals(value, current, StringComparison.OrdinalIgnoreCase);
                var tb = Text(label, on ? Accent : T.Text);
                if (fontOf?.Invoke(value) is { } ff) tb.FontFamily = ff;
                var it = new Border { Child = tb, Padding = new Thickness(F * 0.6, F * 0.2, F * 0.6, F * 0.25), Background = Brushes.Transparent, Cursor = Cursors.Hand };
                it.MouseEnter += (_, _) => it.Background = T.B(T.Border);
                it.MouseLeave += (_, _) => it.Background = Brushes.Transparent;
                it.MouseLeftButtonDown += (_, e) => { e.Handled = true; close(); pick(value); };
                opts.Children.Add(it);
            }
            var b = new Border
            {
                Child = opts, Background = T.B(T.Panel), BorderBrush = T.B(T.Border), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(0, F * 0.2, 0, F * 0.2),
            };
            if (Family != null) TextElement.SetFontFamily(b, Family);
            TextElement.SetFontSize(b, F);
            TextOptions.SetTextFormattingMode(b, TextFormattingMode.Display);
            return b;
        }
        // one click handler only (see OverlayView.MenuButton); open after the click is done
        box.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (MenuWindow.JustClosed) return;   // second click on the box = close, not reopen
            var at = MenuWindow.ScreenDip(box, new Point(0, box.ActualHeight + 2));
            box.Dispatcher.BeginInvoke(() => MenuWindow.Open(BuildList, at, rightAlign: false, minWidth: box.ActualWidth),
                System.Windows.Threading.DispatcherPriority.Input);
        };
        return box;
    }

    /// <summary>Flat text box with no blue Windows focus glow.</summary>
    public TextBox Input(string text, double widthEm = 7)
    {
        var tb = new TextBox
        {
            Text = text, Width = F * widthEm, Foreground = T.B(T.Text), Background = Brushes.Transparent,
            CaretBrush = T.B(T.Text), SelectionBrush = T.B(Accent), BorderBrush = T.B(T.Border),
            BorderThickness = new Thickness(1), Padding = new Thickness(F * 0.3, F * 0.1, F * 0.3, F * 0.15),
            FontSize = F, VerticalAlignment = VerticalAlignment.Center, FocusVisualStyle = null,
        };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(Control.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(host);
        tb.Template = new ControlTemplate(typeof(TextBox)) { VisualTree = border };
        tb.GotKeyboardFocus += (_, _) => tb.BorderBrush = T.B(Accent);
        tb.LostKeyboardFocus += (_, _) => tb.BorderBrush = T.B(T.Border);
        return tb;
    }

    public Border Swatch(string hex, double size = 1) => new()
    {
        Width = F * size, Height = F * size, CornerRadius = new CornerRadius(2),
        Background = T.B(hex), BorderBrush = T.B(T.Border), BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
    };
}
