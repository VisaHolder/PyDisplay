using System.Windows.Media;

namespace PyDisplay;

/// <summary>A colour theme. Themes only change colours — styles (rows/graphs/compact) change layout.</summary>
public sealed class Theme
{
    public string Name = "default";
    public string Bg = "#0b0d10", Panel = "#12161b", Border = "#1f252d";
    public string Text = "#d8dee9", Sub = "#8b949e", Dim = "#4b535d";
    public string Gpu = "#4ade80", Cpu = "#ff7a7a", Mem = "#2dd4bf", Net = "#a78bfa", Disk = "#fbbf24", Storage = "#94a3b8";
    public string Warn = "#fbbf24", Hot = "#ff4d6d";

    private readonly Dictionary<string, SolidColorBrush> _cache = new();

    public SolidColorBrush B(string hex)
    {
        if (_cache.TryGetValue(hex, out var b)) return b;
        try { b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { b = new SolidColorBrush(Colors.Magenta); }
        b.Freeze();
        return _cache[hex] = b;
    }

    public SolidColorBrush B(string hex, double alpha)
    {
        var c = B(hex).Color;
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    public string Accent(string section) => section switch
    {
        "gpu" => Gpu, "cpu" => Cpu, "mem" => Mem, "net" => Net, "disk" => Disk, _ => Storage,
    };

    /// <summary>Every colour a user can edit, in the order the theme editor shows them.</summary>
    public static readonly (string Key, string Label)[] Editable =
    [
        ("Gpu", "gpu"), ("Cpu", "cpu"), ("Mem", "memory"), ("Net", "network"), ("Disk", "disk"), ("Storage", "storage"),
        ("Bg", "background"), ("Panel", "panels"), ("Border", "lines"),
        ("Text", "text"), ("Sub", "labels"), ("Dim", "faint text"),
        ("Warn", "busy (60%+)"), ("Hot", "full (85%+)"),
    ];

    public string GetColor(string key) => (string)typeof(Theme).GetField(key)!.GetValue(this)!;
    private void SetColor(string key, string hex) => typeof(Theme).GetField(key)!.SetValue(this, hex);

    public Theme Copy(string name)
    {
        var t = new Theme { Name = name };
        foreach (var (k, _) in Editable) t.SetColor(k, GetColor(k));
        return t;
    }

    /// <summary>#rgb / #rrggbb only — no names, no alpha, so a typo can never paint the overlay invisible.</summary>
    public static string? NormalizeHex(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (!s.StartsWith('#')) s = "#" + s;
        if (s.Length == 4 && s.Skip(1).All(Uri.IsHexDigit)) s = $"#{s[1]}{s[1]}{s[2]}{s[2]}{s[3]}{s[3]}";
        return s.Length == 7 && s.Skip(1).All(Uri.IsHexDigit) ? s.ToLowerInvariant() : null;
    }

    public Dictionary<string, string> ToColors() => Editable.ToDictionary(e => e.Key, e => GetColor(e.Key));

    /// <summary>Default colours with the given ones laid on top (bad/missing values keep the default).</summary>
    public static Theme FromColors(string name, IReadOnlyDictionary<string, string>? colors)
    {
        var t = Presets["default"].Copy(name);
        if (colors != null)
            foreach (var (k, _) in Editable)
                if (colors.TryGetValue(k, out var hex) && NormalizeHex(hex) is { } ok) t.SetColor(k, ok);
        return t;
    }

    /// <summary>The theme to draw with: a preset, a saved theme, or "custom" (unsaved edits).</summary>
    public static Theme For(AppSettings s)
    {
        if (Presets.TryGetValue(s.Theme, out var p)) return p;
        if (s.Theme == "custom") return FromColors("custom", s.CustomColors);
        if (s.SavedThemes.TryGetValue(s.Theme, out var c)) return FromColors(s.Theme, c);
        return Presets["default"];
    }

    public static readonly Dictionary<string, Theme> Presets = new()
    {
        ["default"] = new Theme(),
        ["terminal"] = new Theme
        {
            Name = "terminal", Bg = "#020c02", Panel = "#061206", Border = "#0d2b0d",
            Text = "#00ff41", Sub = "#00aa2a", Dim = "#0a5a1a",
            Gpu = "#00ff41", Cpu = "#39ff14", Mem = "#00cc33", Net = "#00dd44", Disk = "#33ff77", Storage = "#00aa22",
            Warn = "#d4ff00", Hot = "#ff5533",
        },
        ["ice"] = new Theme
        {
            Name = "ice", Bg = "#020810", Panel = "#060f1e", Border = "#0d1f3c",
            Text = "#c8e8ff", Sub = "#5a86c0", Dim = "#1d3560",
            Gpu = "#00cfff", Cpu = "#3d8bff", Mem = "#00aaff", Net = "#66ccff", Disk = "#3399ff", Storage = "#8fb8e0",
        },
        ["sunset"] = new Theme
        {
            Name = "sunset", Bg = "#0f0505", Panel = "#1a0a08", Border = "#2e1210",
            Text = "#ffe0d0", Sub = "#b0705a", Dim = "#4d2418",
            Gpu = "#ff6b35", Cpu = "#ff3860", Mem = "#ff9500", Net = "#ff5577", Disk = "#ffaa55", Storage = "#d09080",
            Warn = "#ffd000", Hot = "#ff1f4b",
        },
        ["midnight"] = new Theme
        {
            Name = "midnight", Bg = "#07040f", Panel = "#0e0820", Border = "#1a1030",
            Text = "#e0d0ff", Sub = "#8a70cc", Dim = "#2c1d55",
            Gpu = "#b060ff", Cpu = "#ff40aa", Mem = "#8060ff", Net = "#c080ff", Disk = "#e070ff", Storage = "#a090d0",
        },
        ["light"] = new Theme
        {
            Name = "light", Bg = "#f4f4f8", Panel = "#ffffff", Border = "#dcdce8",
            Text = "#1a1a2e", Sub = "#5a5a78", Dim = "#b0b0c4",
            Gpu = "#0a8a3a", Cpu = "#cc2200", Mem = "#007a70", Net = "#6a2ccc", Disk = "#b86e00", Storage = "#556070",
            Warn = "#c77700", Hot = "#d0103a",
        },
    };

    public static Theme Get(string name) => Presets.TryGetValue(name ?? "", out var t) ? t : Presets["default"];
}
