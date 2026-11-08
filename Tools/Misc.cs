using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;
using PyDisplay.Sensors;
using PyDisplay.UI;

namespace PyDisplay.Tools;

/// <summary>Local + public IP (same sources as v1: ipify, falling back to ifconfig.me).</summary>
public static class IpLookup
{
    public static string? LocalIPv4()
    {
        try
        {
            // no packet is sent: connecting a UDP socket just asks Windows which adapter it would use
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 80);
            return (s.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString();
        }
        catch { return null; }
    }

    public static async Task<string?> PublicAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PyDisplay/" + App.ShortVersion);
        foreach (var url in new[] { "https://api.ipify.org", "https://ifconfig.me/ip" })
        {
            try
            {
                var ip = (await http.GetStringAsync(url, ct)).Trim();
                if (System.Net.IPAddress.TryParse(ip, out _)) return ip;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }
        return null;
    }
}

/// <summary>Writes one line of stats every N seconds to stats-log.txt (same format as v1's PyDisplay_log.txt).</summary>
public sealed class StatsLogger
{
    private readonly SystemMonitor _mon;
    private readonly Func<AppSettings> _settings;
    private Timer? _timer;
    public bool Active => _timer != null;
    public int IntervalMs { get; private set; }
    public static string FilePath => Path.Combine(AppSettings.Dir, "stats-log.txt");

    public StatsLogger(SystemMonitor mon, Func<AppSettings> settings) { _mon = mon; _settings = settings; }

    public void Start()
    {
        if (_timer != null) return;
        Marker("▶ logging started");
        int ms = _settings().LogIntervalMs;
        IntervalMs = ms;
        _timer = new Timer(_ => WriteLine(), null, 0, ms);
    }

    public void Stop()
    {
        if (_timer == null) return;
        _timer.Dispose();
        _timer = null;
        Marker("■ logging stopped");
    }

    /// <summary>Interval changed while logging — keep going at the new rate.</summary>
    public void Restart()
    {
        if (_timer == null || _settings().LogIntervalMs == IntervalMs) return;   // unchanged: don't write an extra line
        IntervalMs = _settings().LogIntervalMs;
        _timer.Change(IntervalMs, IntervalMs);
    }

    private static void Append(string text)
    {
        try { Directory.CreateDirectory(AppSettings.Dir); File.AppendAllText(FilePath, text); }
        catch (Exception ex) { Log.Error("stats-log", ex); }
    }

    private static void Marker(string what)
    {
        string sep = new('═', 48);
        Append($"{Environment.NewLine}{sep}{Environment.NewLine}  {what}  —  {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{sep}{Environment.NewLine}");
    }

    private void WriteLine()
    {
        var s = _mon.Last;
        if (s == null) return;
        bool f = _settings().Fahrenheit;
        var parts = new List<string>
        {
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]",
            $"GPU {Fmt.Pct(s.GpuLoad)} | {Fmt.Gb(s.VramUsed)}/{Fmt.Gb(s.VramTotal, "0")}GB VRAM | {Fmt.Temp(s.GpuTemp, f)} | {(s.GpuPower is { } w ? Fmt.Num(w, "0") + "W" : "–")}"
                + (s.GpuClock is { } gc ? $" | {Fmt.Mhz(gc)}GHz" : "") + (s.GpuFan is { } fan ? $" | fan {Fmt.Num(fan, "0")}%" : ""),
            $"CPU {Fmt.Pct(s.CpuLoad)} | {Fmt.Ghz(s.CpuClock)}GHz | {s.Procs}P {s.Threads}T {s.Handles}H" + (s.TopCpuName != null ? $" | top {s.TopCpuName} {Fmt.Pct(s.TopCpuPct)}" : ""),
            $"MEM {Fmt.Gb(s.RamUsed)}/{Fmt.Gb(s.RamTotal, "0")}GB ({Fmt.Pct(s.RamPct)})" + (s.TopMemName != null ? $" | top {s.TopMemName} {Fmt.Gb(s.TopMemGb)}GB" : ""),
            $"NET ↓{Fmt.Rate(s.NetDown)} ↑{Fmt.Rate(s.NetUp)}" + (s.PingMs is { } p ? $" | {Fmt.Num(p, "0")}ms" : ""),
            $"DISK R:{Fmt.Rate(s.DiskRead)} W:{Fmt.Rate(s.DiskWrite)}",
        };
        if (s.Drives.Count > 0)
            parts.Add("STR " + string.Join("  ", s.Drives.Select(d => $"{d.Name} {Fmt.Gb(d.UsedGb, "0")}/{Fmt.Gb(d.TotalGb, "0")}GB ({Fmt.Pct(d.UsedGb / d.TotalGb * 100)})")));
        Append(string.Join("  |  ", parts) + Environment.NewLine);
    }
}

/// <summary>"Start with Windows" — HKCU Run key, no admin needed.</summary>
public static class Startup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "PyDisplay";

    /// <summary>
    /// If "start with Windows" is on but points at an old copy (the exe was moved), point it at this one —
    /// otherwise Windows would silently fail to start PyDisplay.
    /// </summary>
    public static void RepairPath()
    {
        if (App.TestMode) return;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            if (k?.GetValue(Name) is not string v) return;
            string want = $"\"{Environment.ProcessPath}\"";
            if (!string.Equals(v, want, StringComparison.OrdinalIgnoreCase)) k.SetValue(Name, want);
        }
        catch (Exception ex) { Log.Error("startup-repair", ex); }
    }

    public static bool Enabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                return k?.GetValue(Name) is string v && v.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        set
        {
            if (App.TestMode) return;
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(Key);
                if (value) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
                else k.DeleteValue(Name, false);
            }
            catch (Exception ex) { Log.Error("startup", ex); }
        }
    }
}

/// <summary>Checks GitHub for a newer release (opens the release page; never downloads by itself).</summary>
public static class Updates
{
    public const string Repo = "VisaHolder/PyDisplay";

    public static async Task<(string Tag, string Url)?> LatestAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PyDisplay/" + App.ShortVersion);
        var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        var r = JsonDocument.Parse(json).RootElement;
        return (r.GetProperty("tag_name").GetString() ?? "", r.GetProperty("html_url").GetString() ?? "");
    }

    public static bool IsNewer(string tag)
    {
        static Version? V(string s) => Version.TryParse(new string(s.TrimStart('v', 'V').TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()), out var v) ? v : null;
        return V(tag) is { } a && V(App.ShortVersion) is { } b && a > b;
    }
}

/// <summary>Brings v1 theme files (PyDisplay_theme_*.json) into the saved themes.</summary>
public static class V1Import
{
    private static readonly (string V1, string V2)[] Map =
    [
        ("BG", "Bg"), ("PANEL", "Panel"), ("BORDER", "Border"), ("TEXT", "Text"), ("SUBTEXT", "Sub"), ("DIM", "Dim"),
        ("gpu", "Gpu"), ("cpu", "Cpu"), ("mem", "Mem"), ("net", "Net"), ("disk", "Disk"), ("storage", "Storage"),
    ];

    public static IEnumerable<string> Folders()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PyDisplay");
        yield return AppContext.BaseDirectory;   // portable v1 kept themes next to the exe
    }

    /// <summary>
    /// First run only: carry over PyDisplay 1.x settings (update speed, auto-clean, hidden sections, position, …)
    /// and its themes. Keeps the new default look unless the user had picked a custom v1 theme.
    /// Returns a short description of what came over, or null if there was no v1 install.
    /// </summary>
    public static string? ImportSettings(AppSettings s)
    {
        string? cfg = Folders().Select(d => Path.Combine(d, "PyDisplay_pos.json")).FirstOrDefault(f => { try { return File.Exists(f); } catch { return false; } });
        int themes = ImportInto(s);
        if (cfg == null) return themes > 0 ? $"{themes} theme(s) from PyDisplay 1.x" : null;
        try
        {
            var r = JsonDocument.Parse(File.ReadAllText(cfg)).RootElement;
            int? I(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
            bool? B(string k) => r.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
            string? Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            List<string>? L(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList() : null;

            if (I("refresh_ms") is { } rm) s.RefreshMs = rm;
            if (I("log_interval_ms") is { } lm) s.LogIntervalMs = lm;
            if (B("autoclean_on") is { } ao) s.AutoCleanOn = ao;
            if (I("autoclean_interval_min") is { } am) s.AutoCleanMinutes = am;
            if (B("autoclean_aggressive") is { } aa) { s.AutoCleanAggressive = aa; s.CleanAggressive = aa; }
            if (B("always_on_top") is { } top) s.Topmost = top;
            if (Str("temp_unit") is { } tu) s.Fahrenheit = tu == "F";
            if (B("click_through") is { } ct) s.ClickThrough = ct;
            if (B("position_locked") is { } pl) s.LockPosition = pl;
            if (B("minimize_to_tray") is { } mt) s.TrayMode = mt;
            if (B("tray_show_gpu") is { } tg) s.TrayShowGpu = tg;
            if (I("gpu_device_index") is { } gi) s.GpuIndex = gi;
            if (Str("layout_mode") is { } lay) s.Direction = lay;
            if (L("hidden_sections") is { } hs) s.Hidden = hs;
            if (L("section_order") is { } so) s.SectionOrder = so;
            if (L("collapsed") is { } co) s.Collapsed = co;
            if (I("x") is { } x && I("y") is { } y) { s.X = x; s.Y = y; }

            // the v1 theme file also held font size + see-through
            if (Str("active_theme") is { } at && File.Exists(at))
            {
                var t = JsonDocument.Parse(File.ReadAllText(at)).RootElement;
                if (t.TryGetProperty("font_size", out var fs) && fs.ValueKind == JsonValueKind.Number)
                    s.FontSize = Math.Round(fs.GetDouble() * 4 / 3);   // v1 used points at 9 = v2's 12 px
                if (t.TryGetProperty("opacity", out var op) && op.ValueKind == JsonValueKind.Number)
                    s.Opacity = op.GetDouble();
                string name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                // a theme the user made themselves stays selected; v1's stock "Default" makes way for the new look
                if (name.Length > 0 && !name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                    && s.SavedThemes.ContainsKey("v1 " + name.Trim().ToLowerInvariant()))
                    s.Theme = "v1 " + name.Trim().ToLowerInvariant();
            }
            s.Repair();
            return "settings" + (themes > 0 ? $" and {themes} theme(s)" : "") + " from PyDisplay 1.x";
        }
        catch (Exception ex) { Log.Error("v1-settings", ex); return themes > 0 ? $"{themes} theme(s) from PyDisplay 1.x" : null; }
    }

    /// <summary>Returns how many themes were added.</summary>
    public static int ImportInto(AppSettings s)
    {
        int added = 0;
        foreach (var dir in Folders().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string[] files;
            try { files = Directory.Exists(dir) ? Directory.GetFiles(dir, "PyDisplay_theme_*.json") : []; }
            catch (Exception ex) { Log.Error("v1-import-dir", ex); continue; }   // a folder we can't list: skip it, never crash
            foreach (var file in files)
            {
                try
                {
                    var root = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
                    string raw = root.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } nm
                        ? nm : Path.GetFileNameWithoutExtension(file)["PyDisplay_theme_".Length..];
                    string name = "v1 " + raw.Trim().ToLowerInvariant();
                    if (s.SavedThemes.ContainsKey(name)) continue;
                    var colors = new Dictionary<string, string>();
                    foreach (var (v1, v2) in Map)
                        if (root.TryGetProperty(v1, out var c) && Theme.NormalizeHex(c.GetString()) is { } hex) colors[v2] = hex;
                    if (colors.Count == 0) continue;
                    s.SavedThemes[name] = colors;
                    added++;
                }
                catch (Exception ex) { Log.Error("v1-import", ex); }
            }
        }
        return added;
    }
}
