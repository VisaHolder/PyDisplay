using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PyDisplay.Tools;

namespace PyDisplay.UI;

public sealed class SettingsWindow : PanelWindow
{
    private static readonly (string, string)[] Tabs =
        [("look", "look"), ("info", "info"), ("sections", "sections"), ("behaviour", "behaviour"), ("theme", "theme"), ("about", "about")];

    private static readonly Dictionary<string, string> SectionNames = new()
    {
        ["gpu"] = "GPU", ["cpu"] = "CPU", ["mem"] = "memory", ["net"] = "network", ["disk"] = "disk activity", ["storage"] = "drives",
    };

    // state that survives a rebuild
    private string? _status;          // one-line result under a button (update check, import, …)
    private bool _saving;             // theme "save as" box open
    private string? _confirmDelete;   // saved theme waiting for a second click
    private bool _confirmReset, _confirmRemove;

    public SettingsWindow(AppHost host, string tab) : base(host, "settings", Tabs, tab) => Rebuild();

    private void Set(Action change) { change(); _status = null; H.Apply(); }

    protected override FrameworkElement BuildTab(string tab) => tab switch
    {
        "info" => InfoTab(),
        "sections" => SectionsTab(),
        "behaviour" => BehaviourTab(),
        "theme" => ThemeTab(),
        "about" => AboutTab(),
        _ => LookTab(),
    };

    // ───────────── look ─────────────
    private FrameworkElement LookTab()
    {
        var p = new StackPanel();
        p.Children.Add(K.Row("style", K.Seg([("rows", "rows"), ("graphs", "graphs"), ("compact", "compact")], S.Style, v => Set(() => S.Style = v))));
        p.Children.Add(K.Row("layout", K.Seg([("vertical", "vertical"), ("horizontal", "horizontal")], S.Direction, v => Set(() => S.Direction = v))));
        string font = Fonts.Resolve(S.FontFamily).Source;
        p.Children.Add(K.Row("font", K.Dropdown(Fonts.Available().Select(f => (f, f)), font, v => Set(() => S.FontFamily = v), f => new FontFamily(f))));
        p.Children.Add(K.Row("size", K.Stepper(Fmt.Num(S.FontSize, "0"),
            () => Set(() => S.FontSize = Math.Max(9, S.FontSize - 1)),
            () => Set(() => S.FontSize = Math.Min(24, S.FontSize + 1)))));
        p.Children.Add(K.Row("background", K.Stepper(Fmt.Num(S.Opacity * 100, "0") + "%",
            () => Set(() => S.Opacity = Math.Max(0.3, Math.Round(S.Opacity - 0.05, 2))),
            () => Set(() => S.Opacity = Math.Min(1, Math.Round(S.Opacity + 0.05, 2)))), "text stays solid"));
        p.Children.Add(K.Row("temperature", K.Seg([("°C", "c"), ("°F", "f")], S.Fahrenheit ? "f" : "c", v => Set(() => S.Fahrenheit = v == "f"))));
        p.Children.Add(K.Heading("styles"));
        p.Children.Add(Hint("rows: labels, block bars and details.\ngraphs: big numbers with 60-second graphs.\ncompact: one line per section — smallest."));
        return p;
    }

    // ───────────── info ─────────────
    private FrameworkElement InfoTab()
    {
        var p = new StackPanel();
        p.Children.Add(Hint("Extra lines on the overlay. Anything your PC can't read hides itself."));
        var hints = new Dictionary<string, string>
        {
            ["gpuclock"] = "core clock, fan speed", ["powerlimit"] = "watts out of the card's limit",
            ["encoder"] = "NVIDIA video encoder — busy while OBS records", ["topapp"] = "app using the most CPU / RAM",
            ["pagefile"] = "memory committed out of RAM + pagefile", ["ping"] = "to 1.1.1.1, every 2 s",
            ["session"] = "downloaded / uploaded since PyDisplay started", ["uptime"] = "time since Windows started",
            ["clock"] = "current time", ["date"] = "today's date, e.g. Sat 7 Nov",
        };
        foreach (var (key, label) in AppSettings.AllExtras)
            p.Children.Add(K.Row(label, K.Toggle(S.Extra(key), v => Set(() => S.Extras[key] = v)), hints.GetValueOrDefault(key)));
        return p;
    }

    // ───────────── sections ─────────────
    private FrameworkElement SectionsTab()
    {
        var p = new StackPanel();
        p.Children.Add(Hint("Show, hide and reorder. On the overlay, Ctrl+click a section's title to fold it."));
        var order = S.SectionOrder;
        for (int i = 0; i < order.Count; i++)
        {
            string key = order[i];
            int idx = i;
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            var up = K.Button("↑", () => Set(() => (order[idx - 1], order[idx]) = (order[idx], order[idx - 1])), small: true);
            var down = K.Button("↓", () => Set(() => (order[idx + 1], order[idx]) = (order[idx], order[idx + 1])), small: true);
            up.Opacity = idx == 0 ? 0.25 : 1; up.IsHitTestVisible = idx > 0;
            down.Opacity = idx == order.Count - 1 ? 0.25 : 1; down.IsHitTestVisible = idx < order.Count - 1;
            down.Margin = new Thickness(F * 0.3, 0, F * 0.7, 0);
            left.Children.Add(up); left.Children.Add(down);
            bool shown = !S.Hidden.Contains(key);
            var name = K.Text(SectionNames[key], shown ? K.T.Accent(key) : K.T.Dim, bold: true);
            left.Children.Add(name);
            if (S.Collapsed.Contains(key)) left.Children.Add(K.Text("  folded", K.T.Dim, 0.9));

            var row = new DockPanel { Margin = new Thickness(0, F * 0.25, 0, F * 0.25) };
            var tog = K.Toggle(shown, v => Set(() => { if (v) S.Hidden.Remove(key); else if (S.Hidden.Count < order.Count - 1) S.Hidden.Add(key); }));
            DockPanel.SetDock(tog, Dock.Right);
            row.Children.Add(tog);
            row.Children.Add(left);
            p.Children.Add(row);
        }
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, F * 0.8, 0, 0) };
        btns.Children.Add(K.Button("reset order", () => Set(() => { S.SectionOrder = [.. AppSettings.AllSections]; S.Hidden.Clear(); })));
        if (S.Collapsed.Count > 0)
        {
            var unfold = K.Button("unfold all", () => Set(() => S.Collapsed.Clear()));
            unfold.Margin = new Thickness(F * 0.5, 0, 0, 0);
            btns.Children.Add(unfold);
        }
        p.Children.Add(btns);
        return p;
    }

    // ───────────── behaviour ─────────────
    private FrameworkElement BehaviourTab()
    {
        var p = new StackPanel();
        p.Children.Add(K.Row("click-through", K.Toggle(S.ClickThrough, v => Set(() => S.ClickThrough = v)), "mouse passes through · hold Ctrl to use it"));
        p.Children.Add(K.Row("always on top", K.Toggle(S.Topmost, v => Set(() => S.Topmost = v))));
        p.Children.Add(K.Row("lock position", K.Toggle(S.LockPosition, v => Set(() => S.LockPosition = v)), "stops accidental dragging"));
        p.Children.Add(K.Row("update every", K.Seg([("0.5s", "500"), ("1s", "1000"), ("2s", "2000"), ("5s", "5000")], S.RefreshMs.ToString(),
            v => Set(() => S.RefreshMs = int.Parse(v))), "how often numbers refresh"));

        p.Children.Add(K.Heading("tray"));
        p.Children.Add(K.Row("tray icon always on", K.Toggle(S.TrayMode, v => Set(() => S.TrayMode = v)), "otherwise it only shows while the overlay is hidden"));
        p.Children.Add(K.Row("tray shows stats", K.Toggle(S.TrayShowGpu, v => Set(() => S.TrayShowGpu = v)), "GPU / CPU / RAM in the tray tooltip"));

        p.Children.Add(K.Heading("windows"));
        p.Children.Add(K.Row("start with Windows", K.Toggle(Startup.Enabled, v => { Startup.Enabled = v; Rebuild(); })));
        var gpus = H.Mon.GpuNames;
        if (gpus.Count > 1)
            p.Children.Add(K.Row("GPU", K.Dropdown(gpus.Select((n, i) => (Fmt.ShortName(n) + $"  #{i}", i.ToString())),
                S.GpuIndex.ToString(), v => Set(() => S.GpuIndex = int.Parse(v)))));
        p.Children.Add(K.Row("position", K.Button("move to top-right", () => { H.Overlay?.ResetPosition(); })));
        return p;
    }

    // ───────────── theme ─────────────
    private FrameworkElement ThemeTab()
    {
        var p = new StackPanel();
        var cur = Theme.For(S);

        // preset + saved theme cards, each drawn in its own colours
        var cards = new UniformGrid { Columns = 3 };
        foreach (var name in Theme.Presets.Keys) cards.Children.Add(Card(name, Theme.Presets[name], deletable: false));
        foreach (var name in S.SavedThemes.Keys.OrderBy(n => n)) cards.Children.Add(Card(name, Theme.FromColors(name, S.SavedThemes[name]), deletable: true));
        if (S.CustomColors.Count > 0) cards.Children.Add(Card("custom", Theme.FromColors("custom", S.CustomColors), deletable: false, label: "unsaved edits"));
        p.Children.Add(cards);

        p.Children.Add(K.Heading("colours of '" + cur.Name + "' — type a hex code, Enter to apply"));
        var grid = new UniformGrid { Columns = 2 };
        foreach (var (key, label) in Theme.Editable) grid.Children.Add(ColorRow(cur, key, label));
        p.Children.Add(grid);

        var btns = new WrapPanel { Margin = new Thickness(0, F * 0.8, 0, 0) };
        if (_saving)
        {
            var box = K.Input(cur.Name is "custom" ? "my theme" : cur.Name + " 2", 10);
            box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            void Save()
            {
                string n = box.Text.Trim().ToLowerInvariant();
                if (n.Length == 0 || n.Length > 24 || Theme.Presets.ContainsKey(n) || n == "custom")
                { box.BorderBrush = K.T.B(K.T.Hot); return; }
                _saving = false;
                Set(() => { S.SavedThemes[n] = cur.ToColors(); S.Theme = n; S.CustomColors.Clear(); });
            }
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Save(); if (e.Key == Key.Escape) { e.Handled = true; _saving = false; Rebuild(); } };
            btns.Children.Add(box);
            btns.Children.Add(Gap(K.Button("save", Save, primary: true)));
            btns.Children.Add(Gap(K.Button("cancel", () => { _saving = false; Rebuild(); })));
        }
        else
        {
            btns.Children.Add(K.Button("save as…", () => { _saving = true; Rebuild(); }));
            btns.Children.Add(Gap(K.Button("import v1 themes", () =>
            {
                int n = V1Import.ImportInto(S);
                H.Apply();
                _status = n == 0 ? "no new v1 themes found" : $"imported {n} theme{(n == 1 ? "" : "s")} from PyDisplay 1.x";
                Rebuild();
            })));
        }
        p.Children.Add(btns);
        if (_status != null) p.Children.Add(Hint(_status));
        return p;
    }

    private Border Card(string name, Theme t, bool deletable, string? label = null)
    {
        bool on = S.Theme == name;
        var sw = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, F * 0.3) };
        foreach (var c in new[] { t.Gpu, t.Cpu, t.Mem, t.Net })
            sw.Children.Add(new Border { Width = F * 0.8, Height = F * 0.8, CornerRadius = new CornerRadius(2), Background = t.B(c), Margin = new Thickness(0, 0, 3, 0) });
        var top = new DockPanel();
        if (deletable)
        {
            bool confirm = _confirmDelete == name;
            var del = new TextBlock { Text = confirm ? "delete?" : "×", Foreground = t.B(confirm ? t.Hot : t.Sub), FontSize = F * 0.9, Cursor = Cursors.Hand };
            del.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (_confirmDelete != name) { _confirmDelete = name; Rebuild(); return; }
                _confirmDelete = null;
                Set(() => { S.SavedThemes.Remove(name); if (S.Theme == name) S.Theme = "default"; });
            };
            DockPanel.SetDock(del, Dock.Right);
            top.Children.Add(del);
        }
        top.Children.Add(sw);
        var body = new StackPanel();
        body.Children.Add(top);
        body.Children.Add(new TextBlock { Text = name, Foreground = t.B(t.Text), FontSize = F * 0.95, TextTrimming = TextTrimming.CharacterEllipsis });
        if (label != null) body.Children.Add(new TextBlock { Text = label, Foreground = t.B(t.Sub), FontSize = F * 0.8 });
        var b = new Border
        {
            Child = body, Background = t.B(t.Bg), BorderBrush = on ? K.T.B(K.Accent) : t.B(t.Border),
            BorderThickness = new Thickness(on ? 2 : 1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(F * 0.55, F * 0.45, F * 0.5, F * 0.45), Margin = new Thickness(0, 0, F * 0.4, F * 0.4),
            Cursor = Cursors.Hand,
        };
        b.MouseLeftButtonDown += (_, e) => { e.Handled = true; _confirmDelete = null; Set(() => S.Theme = name); };
        return b;
    }

    private FrameworkElement ColorRow(Theme cur, string key, string label)
    {
        string hex = cur.GetColor(key);
        var d = new DockPanel { Margin = new Thickness(0, F * 0.12, F * 0.8, F * 0.12) };
        var sw = K.Swatch(hex, 0.95);
        sw.Margin = new Thickness(0, 0, F * 0.4, 0);
        DockPanel.SetDock(sw, Dock.Left);
        var box = K.Input(hex, 5.6);
        DockPanel.SetDock(box, Dock.Right);
        void Commit()
        {
            var ok = Theme.NormalizeHex(box.Text);
            if (ok == null) { box.BorderBrush = K.T.B(K.T.Hot); return; }
            if (ok == hex) return;
            Set(() =>
            {
                var colors = cur.ToColors();
                colors[key] = ok;
                if (S.SavedThemes.ContainsKey(S.Theme)) S.SavedThemes[S.Theme] = colors;   // your own theme: edit in place
                else { S.CustomColors = colors; S.Theme = "custom"; }                       // a preset: edits go to "custom"
            });
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        box.LostKeyboardFocus += (_, _) => { if (Theme.NormalizeHex(box.Text) is { } ok && ok != hex) Commit(); };
        box.TextChanged += (_, _) => { if (Theme.NormalizeHex(box.Text) is { } ok) sw.Background = K.T.B(ok); };
        d.Children.Add(sw); d.Children.Add(box);
        d.Children.Add(K.Text(label, K.T.Sub, 0.92));
        return d;
    }

    // ───────────── about ─────────────
    private FrameworkElement AboutTab()
    {
        var p = new StackPanel();
        var head = new TextBlock();
        head.Inlines.Add(new System.Windows.Documents.Run("PyDisplay ") { Foreground = K.T.B(K.Accent), FontWeight = FontWeights.SemiBold });
        head.Inlines.Add(new System.Windows.Documents.Run(App.ShortVersion) { Foreground = K.T.B(K.T.Sub) });
        p.Children.Add(head);
        p.Children.Add(Hint("System monitor overlay for Windows 10 / 11. MIT licence."));

        var b1 = new WrapPanel { Margin = new Thickness(0, F * 0.6, 0, 0) };
        b1.Children.Add(K.Button("check for updates", CheckUpdates));
        b1.Children.Add(Gap(K.Button("GitHub", () => Open($"https://github.com/{Updates.Repo}"))));
        b1.Children.Add(Gap(K.Button("settings folder", () => { Directory.CreateDirectory(AppSettings.Dir); Open(AppSettings.Dir); })));
        p.Children.Add(b1);
        if (_status != null) p.Children.Add(Hint(_status));

        p.Children.Add(K.Heading("settings file"));
        var b2 = new WrapPanel();
        b2.Children.Add(K.Button("export…", Export));
        b2.Children.Add(Gap(K.Button("import…", Import)));
        b2.Children.Add(Gap(K.Button(_confirmReset ? "really reset everything?" : "reset all", () =>
        {
            if (!_confirmReset) { _confirmReset = true; Rebuild(); return; }
            _confirmReset = false;
            var fresh = new AppSettings();
            foreach (var prop in typeof(AppSettings).GetProperties().Where(pp => pp.CanWrite))
                prop.SetValue(S, prop.GetValue(fresh));
            H.Apply();
            H.Overlay?.ResetPosition();
        })));
        p.Children.Add(b2);

        p.Children.Add(K.Heading("this PC"));
        p.Children.Add(Hint(AppSettings.Portable
            ? $"portable: settings are kept next to the exe ({AppSettings.Dir})"
            : $"settings: {AppSettings.Dir}"));
        var rm = K.Button(_confirmRemove ? "really remove? (click again)" : "remove PyDisplay from this PC", () =>
        {
            if (!_confirmRemove) { _confirmRemove = true; Rebuild(); return; }
            if (App.TestMode) { _confirmRemove = false; Rebuild(); return; }
            Startup.Enabled = false;
            if (H.Logging) H.ToggleLog();
            AppSettings.DeleteAll();
            MessageBox.Show("PyDisplay's settings, logs and startup entry are removed.\n\nDelete PyDisplay.exe to finish.",
                "PyDisplay", MessageBoxButton.OK, MessageBoxImage.Information);
            H.Exit();
        });
        rm.HorizontalAlignment = HorizontalAlignment.Left;
        p.Children.Add(rm);
        p.Children.Add(Hint("turns off start-with-Windows and deletes settings + logs. Your PyDisplay 1.x files are left alone."));

        p.Children.Add(K.Heading("how to use"));
        p.Children.Add(Hint(
            "hold Ctrl — click, drag or right-click the overlay\n" +
            "right-click the overlay — opens these settings\n" +
            "Ctrl+click a section title — fold / unfold it\n" +
            "[log] — record stats to a file every few seconds\n" +
            "[tools] — RAM cleaner, speed test, IP lookup\n" +
            "Esc — closes this window"));
        return p;
    }

    private async void CheckUpdates()
    {
        _status = "checking…"; Rebuild();
        try
        {
            var r = await Updates.LatestAsync();
            _status = r is { } x && Updates.IsNewer(x.Tag) ? $"{x.Tag} is out — opening the download page" : "you're on the latest version";
            if (r is { } y && Updates.IsNewer(y.Tag)) Open(y.Url);
        }
        catch { _status = "couldn't reach GitHub"; }
        Rebuild();
    }

    private void Export()
    {
        if (App.TestMode) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "PyDisplay-settings.json", Filter = "Settings (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try { File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(S, new JsonSerializerOptions { WriteIndented = true })); _status = "exported"; }
        catch (Exception ex) { _status = "export failed: " + ex.Message; }
        Rebuild();
    }

    private void Import()
    {
        if (App.TestMode) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Settings (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var src = AppSettings.FromJson(File.ReadAllText(dlg.FileName));
            double? x = S.X, y = S.Y;   // keep this PC's screen position
            foreach (var prop in typeof(AppSettings).GetProperties().Where(pp => pp.CanWrite)) prop.SetValue(S, prop.GetValue(src));
            S.X = x; S.Y = y;
            _status = "imported";
            H.Apply();
        }
        catch { _status = "that file isn't a PyDisplay settings file"; Rebuild(); }
    }

    // ───────────── helpers ─────────────
    private TextBlock Hint(string text)
    {
        var t = K.Text(text, K.T.Sub, 0.9);
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        t.Margin = new Thickness(0, F * 0.2, 0, F * 0.4);
        return t;
    }

    private FrameworkElement Gap(FrameworkElement e) { e.Margin = new Thickness(F * 0.5, 0, 0, F * 0.3); return e; }

    private static void Open(string target)
    {
        if (App.TestMode) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
