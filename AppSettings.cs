using System.Text.Json;
using System.Windows.Media;

namespace PyDisplay;

public sealed class AppSettings
{
    public static readonly string[] AllSections = ["gpu", "cpu", "mem", "net", "disk", "storage"];

    /// <summary>Extra-info rows the user can switch on/off.</summary>
    public static readonly (string Key, string Label)[] AllExtras =
    [
        ("gpuclock", "gpu clock + fan"),
        ("powerlimit", "gpu power limit"),
        ("encoder", "encoder (obs)"),
        ("topapp", "top app"),
        ("pagefile", "pagefile"),
        ("ping", "ping"),
        ("session", "data used"),
        ("uptime", "uptime"),
        ("clock", "clock"),
        ("date", "date"),
    ];

    /// <summary>Extras that start switched off.</summary>
    private static readonly HashSet<string> OffByDefault = ["date", "pagefile"];

    public string Style { get; set; } = "rows";           // rows | graphs | compact
    public string Direction { get; set; } = "vertical";   // vertical | horizontal
    public string FontFamily { get; set; } = "";          // "" = best installed mono font
    public double FontSize { get; set; } = 12;
    public double Opacity { get; set; } = 0.94;
    public string Theme { get; set; } = "default";
    public List<string> SectionOrder { get; set; } = [.. AllSections];
    public List<string> Hidden { get; set; } = [];
    public Dictionary<string, bool> Extras { get; set; } = new();
    public double? X { get; set; }
    public double? Y { get; set; }
    public bool ClickThrough { get; set; } = true;
    public int RefreshMs { get; set; } = 1000;
    public int GpuIndex { get; set; } = 0;
    public bool Fahrenheit { get; set; }
    public bool Topmost { get; set; } = true;
    public int LogIntervalMs { get; set; } = 15000;        // 5000 / 15000 / 30000 / 60000
    public bool AutoCleanOn { get; set; }
    public int AutoCleanMinutes { get; set; } = 30;        // 5 / 15 / 30 / 60
    public bool AutoCleanAggressive { get; set; }
    public bool CleanAggressive { get; set; }              // mode picked for manual cleans
    public bool LockPosition { get; set; }
    public bool TrayMode { get; set; }                     // × hides to the tray instead of quitting
    public bool TrayShowGpu { get; set; } = true;          // tray tooltip shows live GPU %
    public List<string> Collapsed { get; set; } = [];      // sections folded to just their header
    public Dictionary<string, string> CustomColors { get; set; } = new();  // unsaved edits: field name → #rrggbb
    public Dictionary<string, Dictionary<string, string>> SavedThemes { get; set; } = new();  // name → colours

    public bool Extra(string key) => Extras.TryGetValue(key, out bool v) ? v : !OffByDefault.Contains(key);

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;

    // ── storage ──
    // Portable mode (same rule as v1): if the exe sits in a folder called "PyDisplay", keep settings there.
    public static readonly string Dir = ResolveDir();
    private static string Path => System.IO.Path.Combine(Dir, "settings.json");

    public static bool Portable { get; private set; }

    private static string ResolveDir()
    {
        string exeDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        // portable only if we can actually write there (a "PyDisplay" folder under Program Files can't be written)
        if (string.Equals(System.IO.Path.GetFileName(exeDir), "PyDisplay", StringComparison.OrdinalIgnoreCase) && CanWrite(exeDir))
        {
            Portable = true;
            return exeDir;
        }
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PyDisplay", "v2");
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            string probe = System.IO.Path.Combine(dir, ".pydisplay-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>True when there was no settings file yet (brand-new install) — triggers the v1 import + welcome.</summary>
    public static bool IsFirstRun { get; private set; }

    public static AppSettings Load()
    {
        IsFirstRun = !File.Exists(Path);
        try
        {
            if (!IsFirstRun)
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path));
                if (s != null) return s.Sanitize();
            }
        }
        catch (Exception ex)
        {
            // unreadable file: keep a copy instead of silently overwriting it with defaults
            Log.Error("settings-load", ex);
            try { File.Copy(Path, Path + ".bad", true); } catch { }
        }
        return new AppSettings().Sanitize();
    }

    /// <summary>Deletes PyDisplay 2's own files from this PC (never v1's, never the exe).</summary>
    public static void DeleteAll()
    {
        NoSave = true;
        if (Portable)
        {
            foreach (var f in new[] { "settings.json", "settings.json.tmp", "settings.json.bad", "error.log", "stats-log.txt" })
                try { File.Delete(System.IO.Path.Combine(Dir, f)); } catch { }
        }
        else
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    /// <summary>Tests run on a throwaway copy and must never write the real file.</summary>
    public static bool NoSave;

    public void Save()
    {
        if (NoSave) return;
        try
        {
            Directory.CreateDirectory(Dir);
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, Path, true);   // atomic: a crash mid-write can't wipe settings
        }
        catch (Exception ex) { Log.Error("settings-save", ex); }
    }

    /// <summary>Parses + repairs settings JSON (import). Throws if it isn't settings at all.</summary>
    public static AppSettings FromJson(string json) =>
        (JsonSerializer.Deserialize<AppSettings>(json) ?? throw new InvalidDataException()).Sanitize();

    /// <summary>Re-checks every value (after importing from v1).</summary>
    public void Repair() => Sanitize();

    /// <summary>Repairs anything a hand-edited or old file could break.</summary>
    private AppSettings Sanitize()
    {
        if (Style is not ("rows" or "graphs" or "compact")) Style = "rows";
        if (Direction is not ("vertical" or "horizontal")) Direction = "vertical";
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, 9, 24) : 12;
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.3, 1) : 0.94;
        RefreshMs = RefreshMs is 500 or 1000 or 2000 or 5000 ? RefreshMs : 1000;
        LogIntervalMs = LogIntervalMs is 5000 or 15000 or 30000 or 60000 ? LogIntervalMs : 15000;
        AutoCleanMinutes = AutoCleanMinutes is 5 or 15 or 30 or 60 ? AutoCleanMinutes : 30;
        Collapsed = (Collapsed ?? []).Where(AllSections.Contains).Distinct().ToList();
        SectionOrder = (SectionOrder ?? []).Where(AllSections.Contains).Distinct().ToList();
        foreach (var k in AllSections) if (!SectionOrder.Contains(k)) SectionOrder.Add(k);
        Hidden = (Hidden ?? []).Where(AllSections.Contains).Distinct().ToList();
        Extras ??= new();
        CustomColors ??= new();
        SavedThemes = (SavedThemes ?? new())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null && !PyDisplay.Theme.Presets.ContainsKey(kv.Key) && kv.Key != "custom")
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        Theme ??= "default";
        bool known = PyDisplay.Theme.Presets.ContainsKey(Theme) || SavedThemes.ContainsKey(Theme) || (Theme == "custom" && CustomColors.Count > 0);
        if (!known) Theme = "default";
        if (GpuIndex < 0) GpuIndex = 0;
        if (!Fonts.IsInstalled(FontFamily)) FontFamily = "";
        return this;
    }
}

public static class Fonts
{
    private static readonly string[] Candidates =
        ["Cascadia Mono", "Consolas", "JetBrains Mono", "Fira Code", "Lucida Console", "Courier New", "Segoe UI"];

    private static HashSet<string>? _installed;
    private static HashSet<string> Installed => _installed ??=
        System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsInstalled(string? name) => !string.IsNullOrEmpty(name) && Installed.Contains(name);

    /// <summary>Only fonts that are actually on this PC — nothing can break on Windows 10.</summary>
    public static List<string> Available() => Candidates.Where(IsInstalled).ToList();

    public static FontFamily Resolve(string name)
    {
        if (IsInstalled(name)) return new FontFamily(name);
        foreach (var c in Candidates) if (IsInstalled(c)) return new FontFamily(c);
        return new FontFamily("Courier New");
    }
}
