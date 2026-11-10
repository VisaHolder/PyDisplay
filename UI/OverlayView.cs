using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PyDisplay.Sensors;

namespace PyDisplay.UI;

/// <summary>
/// Builds the overlay for one style (rows / graphs / compact) and direction.
/// Every live value registers an updater; <see cref="Update"/> runs them once per tick.
/// Rebuilt from scratch when the style, theme or font changes — no stale widgets.
/// </summary>
public sealed class OverlayView
{
    private readonly Theme T;
    private readonly AppSettings S;
    private readonly IOverlayHost? _host;
    private readonly List<Action<Snapshot>> _ups = [];
    private double F => S.FontSize;
    private bool Fh => S.Fahrenheit;

    public Border Root { get; }

    public OverlayView(Theme theme, AppSettings settings, IOverlayHost? host)
    {
        T = theme; S = settings; _host = host;

        bool horiz = S.Direction == "horizontal";
        bool strip = horiz && S.Style == "compact";
        FrameworkElement body;

        if (strip)
        {
            // one slim line: PyDisplay | GPU ... | CPU ... | ... | up 2d . 14:32   = x
            var line = new DockPanel { LastChildFill = true };
            var ctl = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(F * 0.8, 0, 0, 0) };
            ctl.Children.Add(MenuButton());
            DockPanel.SetDock(ctl, Dock.Right);
            line.Children.Add(ctl);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Brand());
            foreach (var key in VisibleSections)
            {
                row.Children.Add(Divider());
                row.Children.Add(Section(key));
            }
            if (Footer(inline: true) is { } f) { row.Children.Add(Divider()); row.Children.Add(f); }
            line.Children.Add(row);
            body = line;
        }
        else
        {
            var stack = new StackPanel();
            stack.Children.Add(TopBar());
            var secs = new StackPanel { Orientation = horiz ? Orientation.Horizontal : Orientation.Vertical };
            foreach (var key in VisibleSections)
            {
                if (horiz && secs.Children.Count > 0) secs.Children.Add(Divider(tall: true));
                var el = Section(key);
                if (S.Style != "compact") el.Width = SectionWidth;
                secs.Children.Add(el);
            }
            stack.Children.Add(secs);
            if (Footer() is { } foot) stack.Children.Add(foot);
            body = stack;
        }

        Root = new Border
        {
            Background = T.B(T.Bg, S.Opacity),
            BorderBrush = T.B(T.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = strip ? new Thickness(F * 0.9, F * 0.45, F * 0.7, F * 0.45) : new Thickness(F * 0.9, F * 0.55, F * 0.9, F * 0.7),
            Child = body,
        };
        TextElement.SetFontFamily(Root, Fonts.Resolve(S.FontFamily));
        TextElement.SetFontSize(Root, F);
        TextElement.SetForeground(Root, T.B(T.Text));
        TextOptions.SetTextFormattingMode(Root, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(Root, TextRenderingMode.ClearType);
    }

    public void Update(Snapshot s)
    {
        foreach (var u in _ups.ToArray())   // drive rows may be rebuilt mid-update
        {
            try { u(s); } catch (Exception ex) { Log.Error("ui-update", ex); }
        }
    }

    // ── never-resize rules ──
    // 1. every changing value sits in a slot as wide as its longest possible value (measured in the real font)
    // 2. a line that has shown once keeps its space when it's briefly empty (Hidden, not Collapsed)
    private Typeface? _face;
    private double W(string text, double scale = 1)
    {
        _face ??= new Typeface(Fonts.Resolve(S.FontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _face, F * scale, Brushes.White, 1.0);
        return Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 1;
    }

    private readonly HashSet<UIElement> _everShown = [];
    private void Show(UIElement e, bool show)
    {
        if (show) { _everShown.Add(e); e.Visibility = Visibility.Visible; }
        else e.Visibility = _everShown.Contains(e) ? Visibility.Hidden : Visibility.Collapsed;
    }

    private IEnumerable<string> VisibleSections => S.SectionOrder.Where(k => !S.Hidden.Contains(k));

    private double SectionWidth => F * (S.Style == "compact" ? 17 : S.Direction == "horizontal" ? 16.5 : 21);

    private Rectangle Divider(bool tall = false) => new()
    {
        Width = 1,
        Fill = T.B(T.Border),
        Margin = tall ? new Thickness(F * 0.9, F * 1.0, F * 0.9, 0) : new Thickness(F * 0.8, F * 0.15, F * 0.8, F * 0.15),
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    // ───────────────────────────── building blocks ─────────────────────────────

    private TextBlock Tx(string text = "", string? hex = null, double scale = 1, bool bold = false) => new()
    {
        Text = text,
        Foreground = T.B(hex ?? T.Text),
        FontSize = F * scale,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private TextBlock Btn(string text, Action? onClick)
    {
        var b = Tx(text, T.Sub);
        b.Cursor = Cursors.Hand;
        b.Margin = new Thickness(0, 0, F * 0.7, 0);
        b.MouseEnter += (_, _) => b.Foreground = T.B(T.Text);
        b.MouseLeave += (_, _) => b.Foreground = T.B(T.Sub);
        // act on mouse-down and swallow it, so the window drag never eats the click
        b.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick?.Invoke(); };
        return b;
    }

    /// <summary>"PyDisplay" + a red dot while logging.</summary>
    private TextBlock Brand()
    {
        var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        t.Inlines.Add(new Run("PyDisplay") { Foreground = T.B(T.Gpu), FontWeight = FontWeights.SemiBold });
        if (_host?.Logging == true) t.Inlines.Add(new Run(" ●") { Foreground = T.B(T.Hot), ToolTip = "logging" });
        return t;
    }

    /// <summary>The one button on the overlay: ≡ opens the menu.</summary>
    private TextBlock MenuButton()
    {
        // one handler only: Btn marks the click handled, which would stop any second handler from running
        TextBlock? b = null;
        b = Btn("≡", () => ShowMenu(b));
        b.Margin = new Thickness(0);
        b.FontSize = F * 1.15;
        return b;
    }

    /// <summary>One line: PyDisplay ● ........ ≡</summary>
    private UIElement TopBar()
    {
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, S.Style == "compact" ? 0 : -F * 0.2) };
        var m = MenuButton();
        DockPanel.SetDock(m, Dock.Right);
        d.Children.Add(m);
        d.Children.Add(Brand());
        return d;
    }

    /// <summary>Themed menu under ≡ (or at the mouse for right-click).</summary>
    public void ShowMenu(UIElement? anchor)
    {
        if (_host == null) return;
        // Where to put it, in screen DIPs: under the ≡ (right edges lined up), or at the mouse.
        Point at;
        bool rightAlign = anchor != null;
        try
        {
            var src = PresentationSource.FromVisual(Root);
            var toDip = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            if (anchor is FrameworkElement fe)
                at = toDip.Transform(fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight + 4)));
            else
                at = toDip.Transform(Root.PointToScreen(Mouse.GetPosition(Root)));
        }
        catch (Exception ex) { Log.Error("menu-pos", ex); return; }   // not on screen (test render)
        // open after this click finishes, so the click can't close it again
        Root.Dispatcher.BeginInvoke(() => MenuWindow.Open(MenuBox, at, rightAlign), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>The menu's contents (separate so tests can render it).</summary>
    public FrameworkElement MenuBox(Action close)
    {
        var list = new StackPanel();
        void Item(string text, string hex, Action act)
        {
            var tb = Tx(text, hex);
            var row = new Border { Child = tb, Padding = new Thickness(F * 0.8, F * 0.22, F * 1.4, F * 0.28), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            row.MouseEnter += (_, _) => row.Background = T.B(T.Border);
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonDown += (_, e) => { e.Handled = true; close(); act(); };
            list.Children.Add(row);
        }
        void Sep() => list.Children.Add(new Border { Height = 1, Background = T.B(T.Border), Margin = new Thickness(0, F * 0.25, 0, F * 0.25) });

        var h = _host!;
        Item("tools", T.Text, () => h.OpenTools("memory"));
        Item("theme", T.Text, () => h.OpenSettings("theme"));
        Item("settings", T.Text, () => h.OpenSettings("look"));
        Sep();
        if (h.Logging) Item("● stop logging", T.Hot, h.ToggleLog);
        else Item("start logging", T.Text, h.ToggleLog);
        Sep();
        Item("hide to tray", T.Sub, h.HideOverlay);
        Item("quit", T.Sub, h.Quit);

        var box = new Border
        {
            Child = list, Background = T.B(T.Panel), BorderBrush = T.B(T.Border), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(0, F * 0.3, 0, F * 0.3), MinWidth = F * 9,
        };
        TextElement.SetFontFamily(box, Fonts.Resolve(S.FontFamily));
        TextElement.SetFontSize(box, F);
        TextOptions.SetTextFormattingMode(box, TextFormattingMode.Display);
        return box;
    }

    private UIElement Header(string key, string title, Func<Snapshot, string>? sub)
    {
        string acc = T.Accent(key);
        var g = new Grid { Margin = new Thickness(0, F * 0.85, 0, F * 0.3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        bool folded = S.Collapsed.Contains(key);
        var t = Tx(folded ? title + " ▸" : title, acc, bold: true);
        Foldable(t, key);
        g.Children.Add(t);

        var right = new DockPanel { LastChildFill = true };
        Grid.SetColumn(right, 1);
        var subTx = Tx("", T.Sub, 0.92);
        subTx.Margin = new Thickness(F * 0.5, 0, 0, 0);
        DockPanel.SetDock(subTx, Dock.Left);
        right.Children.Add(subTx);
        if (S.Style == "rows")
        {
            right.Children.Add(new Rectangle
            {
                Height = 1, Fill = T.B(acc, 0.35), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(F * 0.5, 0, 0, 0),
            });
        }
        g.Children.Add(right);
        if (sub != null)
        {
            string sep = S.Style == "graphs" ? "· " : "";
            _ups.Add(s => { var v = sub(s); subTx.Text = string.IsNullOrEmpty(v) ? "" : sep + v; });
        }
        return g;
    }

    private BlockBar Bar(string acc, bool blocks, bool heat = true) => new()
    {
        Accent = T.B(acc), Track = T.B(T.Border), Warn = T.B(T.Warn), Hot = T.B(T.Hot),
        Blocks = blocks, Heat = heat, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>label ▕████░░░░▏ value — hidden when the value isn't available.</summary>
    private UIElement BarRow(string label, string acc, Func<Snapshot, double?> frac, Func<Snapshot, string> val, bool heat = true)
    {
        var d = new DockPanel { Margin = new Thickness(0, F * 0.08, 0, F * 0.08) };
        var l = Tx(label, T.Sub); l.Width = F * 4.3;
        DockPanel.SetDock(l, Dock.Left);
        var v = Tx("", T.Text); v.MinWidth = F * 5.2; v.TextAlignment = TextAlignment.Right;
        DockPanel.SetDock(v, Dock.Right);
        var b = Bar(acc, S.Style == "rows", heat);
        b.Height = F * 0.62;
        b.Margin = new Thickness(0, 0, F * 0.6, 0);
        d.Children.Add(l); d.Children.Add(v); d.Children.Add(b);
        _ups.Add(s =>
        {
            var f = frac(s);
            Show(d, f is not null);
            b.Value = f ?? 0;
            v.Text = val(s);
        });
        return d;
    }

    /// <summary>label ........ main secondary — hidden when f returns null.</summary>
    private UIElement KV(string label, Func<Snapshot, (string Main, string? Extra)?> f, string? mainHex = null)
    {
        var d = new DockPanel { Margin = new Thickness(0, F * 0.06, 0, F * 0.06) };
        var l = Tx(label, T.Sub);
        DockPanel.SetDock(l, Dock.Left);
        var v = new TextBlock { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var main = new Run { Foreground = T.B(mainHex ?? T.Text) };
        var extra = new Run { Foreground = T.B(T.Sub) };
        v.Inlines.Add(main); v.Inlines.Add(extra);
        d.Children.Add(l); d.Children.Add(v);
        _ups.Add(s =>
        {
            var r = f(s);
            Show(d, r is not null);
            if (r is { } x) { main.Text = x.Main; extra.Text = string.IsNullOrEmpty(x.Extra) ? "" : " " + x.Extra; }
        });
        return d;
    }

    /// <summary>Two text bits on one line: left and right, both live.</summary>
    private UIElement Line(Func<Snapshot, string?> left, Func<Snapshot, string?> right, string? leftHex = null, string? rightHex = null)
    {
        var d = new DockPanel { Margin = new Thickness(0, F * 0.06, 0, F * 0.06) };
        var r = Tx("", rightHex ?? T.Sub); r.TextAlignment = TextAlignment.Right;
        DockPanel.SetDock(r, Dock.Right);
        var l = Tx("", leftHex ?? T.Sub);
        d.Children.Add(r); d.Children.Add(l);
        _ups.Add(s =>
        {
            string? a = left(s), b = right(s);
            Show(d, a is not null || b is not null);
            l.Text = a ?? ""; r.Text = b ?? "";
        });
        return d;
    }

    /// <summary>Big number on the left, 60-second graph on the right (graphs style).</summary>
    private UIElement BigGraph(string acc, Func<Snapshot, string> big, Func<Snapshot, double[]> a,
                               Func<Snapshot, double[]>? b = null, bool auto = false, double bigScale = 1.9, string widest = "100%")
    {
        var d = new DockPanel { Margin = new Thickness(0, F * 0.05, 0, F * 0.2) };
        var t = Tx("", T.Text, bigScale);
        t.MinWidth = W(widest, bigScale);
        DockPanel.SetDock(t, Dock.Left);
        var g = new Sparkline
        {
            Stroke = T.B(acc), StrokeB = T.B(T.Sub), Baseline = T.B(T.Border), AutoMax = auto,
            Height = F * 2.3, Margin = new Thickness(F * 0.5, 0, 0, 0),
        };
        d.Children.Add(t); d.Children.Add(g);
        _ups.Add(s => { t.Text = big(s); g.Set(a(s), b?.Invoke(s)); });
        return d;
    }

    /// <summary>Compact style: label/value pairs, two per row.</summary>
    private UIElement Pairs(string acc, params (string Label, Func<Snapshot, string?> Val, bool Accent)[] items)
    {
        var g = new Grid();
        for (int c = 0; c < 4; c++)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = c % 2 == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        int rows = (items.Length + 1) / 2;
        for (int r = 0; r < rows; r++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < items.Length; i++)
        {
            var (label, val, isAcc) = items[i];
            int row = i / 2, col = (i % 2) * 2;
            var l = Tx(label, T.Sub);
            l.Margin = new Thickness(col == 0 ? 0 : F * 0.9, 0, F * 0.4, 0);
            var v = Tx("", isAcc ? acc : T.Text);
            v.TextAlignment = TextAlignment.Right;
            Grid.SetRow(l, row); Grid.SetColumn(l, col);
            Grid.SetRow(v, row); Grid.SetColumn(v, col + 1);
            g.Children.Add(l); g.Children.Add(v);
            _ups.Add(s => v.Text = val(s) ?? "–");
        }
        return g;
    }

    // ───────────────────────────── sections ─────────────────────────────

    private FrameworkElement Section(string key)
    {
        var p = new StackPanel();
        switch (S.Style)
        {
            case "graphs": Graphs(key, p); break;
            case "compact": Compact(key, p); break;
            default: Rows(key, p); break;
        }
        if (S.Collapsed.Contains(key))
        {
            while (p.Children.Count > 1) p.Children.RemoveAt(p.Children.Count - 1);
            if (S.Style == "compact" && p.Children[0] is Panel row && row.Children.Count > 1)
            {
                for (int i = 1; i < row.Children.Count; i++) row.Children[i].Visibility = Visibility.Collapsed;
                row.Children.Add(Tx("…", T.Dim));
            }
        }
        return p;
    }

    /// <summary>Clicking a section's title (Ctrl held when click-through is on) folds/unfolds it.</summary>
    private void Foldable(TextBlock title, string key)
    {
        if (_host == null) return;
        title.Cursor = Cursors.Hand;
        title.ToolTip = S.Collapsed.Contains(key) ? "click to unfold" : "click to fold";
        title.Tag = "fold:" + key;   // MainWindow folds on a click, but a drag that starts here just moves the overlay
    }

    private void Rows(string key, StackPanel p)
    {
        string a = T.Accent(key);
        switch (key)
        {
            case "gpu":
                p.Children.Add(Header(key, "GPU", s => s.GpuOk ? Fmt.ShortName(s.GpuName) : "not found"));
                p.Children.Add(BarRow("load", a, s => s.GpuLoad / 100, s => Fmt.Pct(s.GpuLoad)));
                p.Children.Add(BarRow("vram", a, s => s.VramUsed / s.VramTotal,
                    s => $"{Fmt.Gb(s.VramUsed)}/{Fmt.Gb(s.VramTotal, "0")}G"));
                p.Children.Add(KV("temp", s => s.GpuTemp is null ? null
                    : (Fmt.Temp(s.GpuTemp, Fh), $"▲{Fmt.TempN(s.GpuTempMax, Fh)} ▼{Fmt.TempN(s.GpuTempMin, Fh)}")));
                p.Children.Add(KV("power", s => s.GpuPower is not { } w ? null
                    : ($"{Fmt.Num(w, "0")} W", S.Extra("powerlimit") && s.GpuPowerLimit is { } lim ? $"/ {Fmt.Num(lim, "0")}" : null)));
                if (S.Extra("gpuclock"))
                    p.Children.Add(KV("clock", s => s.GpuClock is null ? null
                        : ($"{Fmt.Mhz(s.GpuClock)} GHz", s.GpuFan is { } fan ? $"fan {Fmt.Num(fan, "0")}%" : null)));
                if (S.Extra("encoder"))
                    p.Children.Add(KV("encoder", s => s.GpuEnc is null ? null : (Fmt.Pct(s.GpuEnc), null)));
                break;

            case "cpu":
                p.Children.Add(Header(key, "CPU", s => Fmt.ShortName(s.CpuName)));
                p.Children.Add(BarRow("load", a, s => s.CpuLoad / 100, s => Fmt.Pct(s.CpuLoad)));
                p.Children.Add(KV("clock", s => s.CpuClock is null ? null
                    : ($"{Fmt.Ghz(s.CpuClock)} GHz", $"▼{Fmt.Ghz(s.CpuClockMin)} ▲{Fmt.Ghz(s.CpuClockMax)}")));
                p.Children.Add(KV("tasks", s => (s.Procs.ToString(), $"· {s.Threads} thr")));
                if (S.Extra("topapp"))
                    p.Children.Add(KV("top", s => s.TopCpuName is null ? null : (s.TopCpuName, Fmt.Pct(s.TopCpuPct))));
                break;

            case "mem":
                p.Children.Add(Header(key, "MEMORY", s => $"{Fmt.Gb(s.RamTotal, "0")} GB"));
                p.Children.Add(BarRow("ram", a, s => s.RamUsed / s.RamTotal,
                    s => $"{Fmt.Gb(s.RamUsed)}/{Fmt.Gb(s.RamTotal, "0")}G"));
                p.Children.Add(KV("used", s => (Fmt.Pct(s.RamPct), $"peak {Fmt.Pct(s.RamPeak)}")));
                if (S.Extra("pagefile"))
                    p.Children.Add(KV("pagefile", s => ($"{Fmt.Gb(s.CommitUsed)}/{Fmt.Gb(s.CommitLimit, "0")}G", null)));
                if (S.Extra("topapp"))
                    p.Children.Add(KV("top", s => s.TopMemName is null ? null : (s.TopMemName, $"{Fmt.Gb(s.TopMemGb)}G")));
                break;

            case "net":
                p.Children.Add(Header(key, "NETWORK", s => S.Extra("ping") && s.PingMs is { } ms ? $"{Fmt.Num(ms, "0")} ms" : ""));
                p.Children.Add(BarRow("down", a, s => s.NetPeakDown > 0 ? s.NetDown / s.NetPeakDown : 0, s => Fmt.Rate(s.NetDown), heat: false));
                p.Children.Add(BarRow("up", a, s => s.NetPeakUp > 0 ? s.NetUp / s.NetPeakUp : 0, s => Fmt.Rate(s.NetUp), heat: false));
                p.Children.Add(KV("peak", s => ($"↓{Fmt.RateShort(s.NetPeakDown)}", $"↑{Fmt.RateShort(s.NetPeakUp)}")));
                if (S.Extra("session"))
                    p.Children.Add(KV("session", s => ($"↓{Fmt.Bytes(s.SessDown)}", $"↑{Fmt.Bytes(s.SessUp)}")));
                break;

            case "disk":
                p.Children.Add(Header(key, "DISK", null));
                p.Children.Add(BarRow("read", a, s => s.DiskRead is { } r ? (s.DiskPeakRead > 0 ? r / s.DiskPeakRead : 0) : null, s => Fmt.Rate(s.DiskRead), heat: false));
                p.Children.Add(BarRow("write", a, s => s.DiskWrite is { } w ? (s.DiskPeakWrite > 0 ? w / s.DiskPeakWrite : 0) : null, s => Fmt.Rate(s.DiskWrite), heat: false));
                break;

            default:
                p.Children.Add(Header(key, "STORAGE", null));
                p.Children.Add(Drives(a, d => BarRow(d, a,
                    s => s.Drives.FirstOrDefault(x => x.Name == d) is { } x ? x.UsedGb / x.TotalGb : null,
                    s => s.Drives.FirstOrDefault(x => x.Name == d) is { } x ? $"{Fmt.Gb(x.UsedGb, "0")}/{Fmt.Gb(x.TotalGb, "0")}G" : "")));
                break;
        }
    }

    private void Graphs(string key, StackPanel p)
    {
        string a = T.Accent(key);
        switch (key)
        {
            case "gpu":
                p.Children.Add(Header(key, "GPU", s => s.GpuOk ? Fmt.ShortName(s.GpuName) : "not found"));
                p.Children.Add(BigGraph(a, s => Fmt.Pct(s.GpuLoad), s => s.HGpu));
                p.Children.Add(Line(s => s.VramUsed is null ? null : $"vram {Fmt.Gb(s.VramUsed)}/{Fmt.Gb(s.VramTotal, "0")}G",
                                    s => Join(s.GpuTemp is null ? null : Fmt.Temp(s.GpuTemp, Fh),
                                              s.GpuPower is { } w ? $"{Fmt.Num(w, "0")}W" + (S.Extra("powerlimit") && s.GpuPowerLimit is { } l ? $"/{Fmt.Num(l, "0")}" : "") : null), T.Sub, T.Text));
                if (S.Extra("gpuclock") || S.Extra("encoder"))
                    p.Children.Add(Line(s => S.Extra("gpuclock") && s.GpuClock is not null ? $"{Fmt.Mhz(s.GpuClock)} GHz" : null,
                                        s => Join(S.Extra("gpuclock") && s.GpuFan is { } f ? $"fan {Fmt.Num(f, "0")}%" : null,
                                                  S.Extra("encoder") && s.GpuEnc is { } e ? $"enc {Fmt.Num(e, "0")}%" : null)));
                break;

            case "cpu":
                p.Children.Add(Header(key, "CPU", s => Fmt.ShortName(s.CpuName)));
                p.Children.Add(BigGraph(a, s => Fmt.Pct(s.CpuLoad), s => s.HCpu));
                var cores = new CoreBars { Accent = T.B(a), Track = T.B(T.Border), Height = F * 1.1, Margin = new Thickness(0, 0, 0, F * 0.25) };
                _ups.Add(s => cores.Set(s.Cores));
                p.Children.Add(cores);
                p.Children.Add(Line(s => s.CpuClock is null ? $"{s.Procs} tasks" : $"{Fmt.Ghz(s.CpuClock)} GHz",
                                    s => S.Extra("topapp") && s.TopCpuName != null ? $"{s.TopCpuName} {Fmt.Pct(s.TopCpuPct)}" : null, T.Text));
                break;

            case "mem":
                p.Children.Add(Header(key, "MEMORY", s => $"{Fmt.Gb(s.RamTotal, "0")} GB"));
                var big = new DockPanel();
                var used = Tx("", T.Text); DockPanel.SetDock(used, Dock.Right);
                var pct = Tx("", T.Text, 1.9);
                big.Children.Add(used); big.Children.Add(pct);
                p.Children.Add(big);
                var bar = Bar(a, false); bar.Height = F * 0.42; bar.Margin = new Thickness(0, F * 0.15, 0, F * 0.3);
                p.Children.Add(bar);
                _ups.Add(s => { pct.Text = Fmt.Pct(s.RamPct); used.Text = $"{Fmt.Gb(s.RamUsed)} GB"; bar.Value = s.RamTotal > 0 ? s.RamUsed / s.RamTotal : 0; });
                p.Children.Add(Line(s => S.Extra("topapp") && s.TopMemName != null ? $"{s.TopMemName} {Fmt.Gb(s.TopMemGb)}G" : null,
                                    s => $"peak {Fmt.Pct(s.RamPeak)}"));
                if (S.Extra("pagefile"))
                    p.Children.Add(Line(s => "pagefile", s => $"{Fmt.Gb(s.CommitUsed)}/{Fmt.Gb(s.CommitLimit, "0")}G"));
                break;

            case "net":
                p.Children.Add(Header(key, "NETWORK", s => S.Extra("ping") && s.PingMs is { } ms ? $"{Fmt.Num(ms, "0")} ms" : ""));
                p.Children.Add(BigGraph(a, s => "↓" + Fmt.RateShort(s.NetDown), s => s.HDown, s => s.HUp, auto: true, bigScale: 1.5, widest: "↓99999M"));
                p.Children.Add(Line(s => $"↑ {Fmt.Rate(s.NetUp)}",
                                    s => S.Extra("session") ? $"{Fmt.Bytes(s.SessDown)} used" : null, T.Text));
                break;

            case "disk":
                p.Children.Add(Header(key, "DISK", null));
                p.Children.Add(BigGraph(a, s => Fmt.RateShort((s.DiskRead ?? 0) + (s.DiskWrite ?? 0)), s => s.HDisk, auto: true, bigScale: 1.5, widest: "99999M"));
                p.Children.Add(Line(s => $"r {Fmt.Rate(s.DiskRead)}", s => $"w {Fmt.Rate(s.DiskWrite)}", T.Text, T.Text));
                break;

            default:
                p.Children.Add(Header(key, "STORAGE", null));
                p.Children.Add(Drives(a, d => BarRow(d, a,
                    s => s.Drives.FirstOrDefault(x => x.Name == d) is { } x ? x.UsedGb / x.TotalGb : null,
                    s => s.Drives.FirstOrDefault(x => x.Name == d) is { } x ? $"{Fmt.Pct(x.UsedGb / x.TotalGb * 100)}" : "")));
                break;
        }
    }

    /// <summary>
    /// Compact = meter bars: "GPU ▕███░░░▏  2%  36° 59W" — one line per section, a small bar, the main
    /// percentage, and dim details. Every part sits in a fixed-width slot, so nothing ever shifts.
    /// Extra info (clock/fan, top app, pagefile, data used) goes on a dim line under the bar
    /// (or into a dim slot in the horizontal strip).
    /// </summary>
    private void Compact(string key, StackPanel p)
    {
        string a = T.Accent(key);
        bool strip = S.Direction == "horizontal";
        string title = key switch { "gpu" => "GPU", "cpu" => "CPU", "mem" => "RAM", "net" => "NET", "disk" => "DISK", _ => "DRIVE" };

        Func<Snapshot, double?>? bar = null;           // 0..1
        Func<Snapshot, string>? value = null;          // the main number (defaults to the bar as a %)
        Func<Snapshot, string?>? detail = null;        // dim text after the %
        string detailTpl = "";
        Func<Snapshot, string?>? second = null;        // dim extra-info line
        string secondTpl = "";

        switch (key)
        {
            case "gpu":
                bar = s => s.GpuLoad / 100;
                bool lim = S.Extra("powerlimit");
                detail = s => JoinAll(
                    s.GpuTemp is null ? null : Fmt.TempN(s.GpuTemp, Fh) + "°",
                    s.GpuPower is not { } w ? null : Fmt.Num(w, "0") + (lim && s.GpuPowerLimit is { } l ? "/" + Fmt.Num(l, "0") : "") + "W");
                detailTpl = (Fh ? "212°" : "100°") + "  " + (lim ? "999/999W" : "999W");
                if (S.Extra("gpuclock") || S.Extra("encoder"))
                {
                    second = s => JoinAll(
                        S.Extra("gpuclock") && s.GpuClock is not null ? $"{Fmt.Mhz(s.GpuClock)} GHz" : null,
                        S.Extra("gpuclock") && s.GpuFan is { } f ? $"fan {Fmt.Num(f, "0")}%" : null,
                        S.Extra("encoder") && s.GpuEnc is { } e ? $"enc {Fmt.Num(e, "0")}%" : null);
                    secondTpl = JoinAll(S.Extra("gpuclock") ? "9.99 GHz" : null, S.Extra("gpuclock") ? "fan 100%" : null, S.Extra("encoder") ? "enc 100%" : null) ?? "";
                }
                break;
            case "cpu":
                bar = s => s.CpuLoad / 100;
                detail = s => s.CpuClock is null ? null : Fmt.Ghz(s.CpuClock) + "GHz";
                detailTpl = "9.99GHz";
                if (S.Extra("topapp"))
                {
                    second = s => s.TopCpuName is null ? null : $"top {Clip(s.TopCpuName)} {Fmt.Pct(s.TopCpuPct)}";
                    secondTpl = "top 00000000000… 100%";
                }
                break;
            case "mem":
                bar = s => s.RamTotal > 0 ? s.RamUsed / s.RamTotal : null;
                detail = s => $"{Fmt.Gb(s.RamUsed)}/{Fmt.Gb(s.RamTotal, "0")}G";
                detailTpl = "99.9/99G";
                if (S.Extra("pagefile") || S.Extra("topapp"))
                {
                    second = s => JoinAll(
                        S.Extra("topapp") && s.TopMemName != null ? $"top {Clip(s.TopMemName)} {Fmt.Gb(s.TopMemGb)}G" : null,
                        S.Extra("pagefile") ? $"page {Fmt.Gb(s.CommitUsed, "0")}/{Fmt.Gb(s.CommitLimit, "0")}G" : null);
                    secondTpl = JoinAll(S.Extra("topapp") ? "top 00000000000… 99.9G" : null, S.Extra("pagefile") ? "page 999/999G" : null) ?? "";
                }
                break;
            case "net":
                value = s => "↓" + Fmt.RateShort(s.NetDown);
                bar = s => s.NetPeakDown > 0 ? s.NetDown / s.NetPeakDown : 0;
                detail = s => JoinAll("↑" + Fmt.RateShort(s.NetUp), S.Extra("ping") && s.PingMs is { } ms ? Fmt.Num(ms, "0") + "ms" : null);
                detailTpl = "↑99999M" + (S.Extra("ping") ? "  999ms" : "");
                if (S.Extra("session"))
                {
                    second = s => $"used ↓{Fmt.Bytes(s.SessDown)} ↑{Fmt.Bytes(s.SessUp)}";
                    secondTpl = "used ↓999.9G ↑999.9G";
                }
                break;
            case "disk":
                value = s => Fmt.RateShort((s.DiskRead ?? 0) + (s.DiskWrite ?? 0));
                bar = s => s.DiskPeakRead + s.DiskPeakWrite > 0 ? ((s.DiskRead ?? 0) + (s.DiskWrite ?? 0)) / (s.DiskPeakRead + s.DiskPeakWrite) : 0;
                detail = s => JoinAll("r " + Fmt.RateShort(s.DiskRead), "w " + Fmt.RateShort(s.DiskWrite));
                detailTpl = "r 99999M  w 99999M";
                break;
        }

        // ── one grid for every line:  NAME | value | ▕bar▏ | details  (extra line under details) ──
        double titleW = W("DRIVE") + F * 0.5, valW = W("↓999.9M"), barW = F * 4.2, gap = F * 0.6;
        double detailX = titleW + valW + gap + barW + gap;

        FrameworkElement Line(string name, string acc, Func<Snapshot, string> val, Func<Snapshot, double?> frac,
                              Func<Snapshot, string?>? det, string detTpl, bool foldable)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = strip ? new Thickness(0) : new Thickness(0, F * 0.3, 0, 0) };
            var t = Tx(name, acc, bold: true);
            if (foldable) Foldable(t, key);
            t.Width = strip ? double.NaN : titleW;
            if (strip) t.Margin = new Thickness(0, 0, F * 0.4, 0);
            var v = Tx("", T.Text);
            // vertical: one shared column so numbers line up; strip: just as wide as this section needs
            v.Width = strip ? W(key is "net" ? "↓999.9M" : key == "disk" ? "999.9M" : "100%") : valW;
            v.TextAlignment = TextAlignment.Right;
            var b = Bar(acc, false);
            b.Width = barW; b.Height = F * 0.45; b.Margin = new Thickness(gap, 0, gap, 0);
            row.Children.Add(t); row.Children.Add(v); row.Children.Add(b);
            TextBlock? dt = null;
            if (det != null)
            {
                dt = Tx("", T.Sub);
                dt.Width = W(detTpl);   // exact slot: never grows or shrinks
                row.Children.Add(dt);
            }
            _ups.Add(s2 =>
            {
                v.Text = val(s2);
                b.Value = frac(s2) ?? 0;
                if (dt != null) dt.Text = det!(s2) ?? "";
            });
            return row;
        }

        if (key == "storage" && S.Collapsed.Contains(key))
        {
            // folded: just a clickable title to unfold it again
            var ft = Tx("DRIVES …", a, bold: true);
            ft.Margin = strip ? new Thickness(0) : new Thickness(0, F * 0.3, 0, 0);
            Foldable(ft, key);
            p.Children.Add(ft);
            return;
        }
        if (key == "storage")
        {
            var host = Drives(a, d => Line(d, a,
                s2 => s2.Drives.FirstOrDefault(z => z.Name == d) is { } x ? Fmt.Pct(x.UsedGb / x.TotalGb * 100) : "",
                s2 => s2.Drives.FirstOrDefault(z => z.Name == d) is { } x ? x.UsedGb / x.TotalGb : null,
                s2 => s2.Drives.FirstOrDefault(z => z.Name == d) is { } x ? $"{Fmt.Gb(x.UsedGb, "0")}/{Fmt.Gb(x.TotalGb, "0")}G" : null,
                "9999/9999G", foldable: true), horizontal: strip);
            var wrap = new StackPanel { Orientation = Orientation.Horizontal };
            var dtitle = Tx(strip ? "DRIVES" : "", a, bold: true);
            Foldable(dtitle, key);
            if (strip) { dtitle.Margin = new Thickness(0, 0, F * 0.4, 0); wrap.Children.Add(dtitle); }
            wrap.Children.Add(host);
            p.Children.Add(wrap);
            return;
        }

        p.Children.Add(Line(title, a, value ?? (s2 => bar!(s2) is { } x ? Fmt.Pct(x * 100) : "\u2013"), bar!, detail, detailTpl, foldable: true));

        if (second == null) return;
        var l = Tx("", strip ? T.Sub : T.Dim);
        l.Width = W(secondTpl);
        l.HorizontalAlignment = HorizontalAlignment.Left;
        _ups.Add(s2 => { var v = second(s2); l.Text = v ?? ""; Show(l, v is not null); });
        if (strip)
        {
            l.Margin = new Thickness(F * 0.8, 0, 0, 0);
            ((Panel)p.Children[0]).Children.Add(l);
        }
        else
        {
            l.Margin = new Thickness(detailX, 0, 0, 0);   // lines up under the details
            p.Children.Add(l);
        }
    }

    /// <summary>"56" + dim "W" in a fixed-width slot (sized for the widest value), so nothing ever shifts.</summary>
    private TextBlock Chunk(Func<Snapshot, (string Val, string Unit)?> f, string? hex, string widestVal, string widestUnit)
    {
        var tb = new TextBlock
        {
            Margin = new Thickness(0, 0, F * 0.75, 0), VerticalAlignment = VerticalAlignment.Center,
            MinWidth = W(widestVal) + W(widestUnit, 0.9), TextAlignment = TextAlignment.Right,
        };
        var v = new Run { Foreground = T.B(hex ?? T.Text) };
        var u = new Run { Foreground = T.B(T.Sub), FontSize = F * 0.9 };
        tb.Inlines.Add(v); tb.Inlines.Add(u);
        _ups.Add(s =>
        {
            var r = f(s);
            Show(tb, r is not null);
            if (r is { } x)
            {
                v.Text = x.Val; u.Text = x.Unit;
                // ratchet: a value wider than the slot widens it once, and it never shrinks back
                double need = W(x.Val) + W(x.Unit, 0.9);
                if (need > tb.MinWidth) tb.MinWidth = need;
            }
        });
        return tb;
    }

    private static string? JoinAll(params string?[] parts)
    {
        var p = parts.Where(x => !string.IsNullOrEmpty(x)).ToArray();
        return p.Length == 0 ? null : string.Join("  ", p);
    }

    private static string Clip(string n) => n.Length > 12 ? n[..11] + "…" : n;   // matches the 11-char + … slot templates

    private static string RateNum(double? b) => Fmt.Rate(b).Split(' ')[0];
    private static string RateUnit(double? b) { var p = Fmt.Rate(b).Split(' '); return p.Length > 1 ? p[1].Replace("B/s", "") : ""; }

    /// <summary>One row per drive; rebuilt only when drives are added/removed (USB, etc.).</summary>
    private UIElement Drives(string acc, Func<string, UIElement> row, bool horizontal = false)
    {
        var host = new StackPanel { Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical };
        string sig = "";
        var inner = new List<Action<Snapshot>>();
        _ups.Add(s =>
        {
            string now = string.Join(",", s.Drives.Select(d => d.Name));
            if (now != sig)
            {
                sig = now;
                host.Children.Clear();
                // rows register their own updaters; capture them so we only run the current set
                int before = _ups.Count;
                foreach (var d in s.Drives) host.Children.Add(row(d.Name));
                inner = _ups.GetRange(before, _ups.Count - before);
                _ups.RemoveRange(before, _ups.Count - before);
            }
            foreach (var u in inner) u(s);
        });
        return host;
    }

    private UIElement? Footer(bool inline = false)
    {
        bool up = S.Extra("uptime"), date = S.Extra("date"), clk = S.Extra("clock");
        if (!up && !date && !clk) return null;
        var t = Tx("", inline ? T.Sub : T.Dim, inline ? 1 : 0.92);
        t.Margin = inline ? new Thickness(0) : new Thickness(0, F * 0.8, 0, 0);
        t.MinWidth = W(JoinAll3(up ? "up 99d 23h" : null, date ? "Wed 30 Sep" : null, clk ? "00:00" : null) ?? "", inline ? 1 : 0.92);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        _ups.Add(s => t.Text = JoinAll3(
            up ? "up " + Fmt.Uptime(s.Uptime) : null,
            date ? DateTime.Now.ToString("ddd d MMM", inv) : null,
            clk ? DateTime.Now.ToString("HH:mm", inv) : null) ?? "");
        return t;
    }

    private static string? JoinAll3(params string?[] parts)
    {
        var p = parts.Where(x => !string.IsNullOrEmpty(x)).ToArray();
        return p.Length == 0 ? null : string.Join(" · ", p);
    }

    private static string? Join(string? a, string? b, string sep = "  ") =>
        a is null ? b : b is null ? a : a + sep + b;
}
