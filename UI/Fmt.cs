using System.Globalization;
using System.Text.RegularExpressions;

namespace PyDisplay.UI;

/// <summary>Number formatting shared by every style. Invariant culture so "5.22" never becomes "5,22" on some PCs mid-layout.</summary>
public static partial class Fmt
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Pct(double? v) => v is { } x ? x.ToString("0", C) + "%" : "–";
    public static string Num(double v, string f) => v.ToString(f, C);

    /// <summary>Bytes/s → "320 KB/s", "25.3 MB/s".</summary>
    public static string Rate(double? bps)
    {
        if (bps is not { } b) return "–";
        if (b < 1024 * 1024) return (b / 1024).ToString(b < 10 * 1024 ? "0.0" : "0", C) + " KB/s";
        double mb = b / 1048576;
        return mb.ToString(mb < 10 ? "0.00" : mb < 100 ? "0.0" : "0", C) + " MB/s";
    }

    /// <summary>Same as Rate but just the number + short unit, for tight spots: "320K", "25.3M".</summary>
    public static string RateShort(double? bps)
    {
        if (bps is not { } b) return "–";
        if (b < 1024 * 1024) return (b / 1024).ToString("0", C) + "K";
        double mb = b / 1048576;
        return mb.ToString(mb < 10 ? "0.00" : mb < 100 ? "0.0" : "0", C) + "M";
    }

    /// <summary>Total bytes → "210M", "1.4G".</summary>
    public static string Bytes(long b)
    {
        if (b < 1024L * 1024) return (b / 1024.0).ToString("0", C) + "K";
        if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0", C) + "M";
        return (b / 1073741824.0).ToString("0.0", C) + "G";
    }

    public static string Gb(double? v, string f = "0.0") => v is { } x ? x.ToString(f, C) : "–";

    public static string Temp(double? c, bool f) =>
        c is not { } x ? "–" : f ? (x * 9 / 5 + 32).ToString("0", C) + "°F" : x.ToString("0", C) + "°C";

    public static string TempN(double? c, bool f) =>
        c is not { } x ? "–" : (f ? x * 9 / 5 + 32 : x).ToString("0", C);

    public static string Ghz(double? v) => v is { } x ? x.ToString("0.00", C) : "–";
    public static string Mhz(double? v) => v is { } x ? (x / 1000).ToString(x >= 10000 ? "0.0" : "0.00", C) : "–";

    public static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" :
        t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m" : $"{t.Minutes}m";

    /// <summary>"NVIDIA GeForce RTX 5070 Ti" → "RTX 5070 Ti"; "AMD Ryzen 7 9800X3D 8-Core Processor" → "Ryzen 7 9800X3D".</summary>
    public static string ShortName(string n)
    {
        if (string.IsNullOrWhiteSpace(n)) return "";
        n = Junk().Replace(n, " ");
        n = Cores().Replace(n, " ");
        return Spaces().Replace(n, " ").Trim();
    }

    [GeneratedRegex(@"\(R\)|\(TM\)|\bNVIDIA\b|\bGeForce\b|\bAMD\b|\bIntel\b|\bCore\b(?=\s+(i\d|Ultra))|\bProcessor\b|\bCPU\b|with Radeon.*$|@.*$|\bGraphics\b(?=.+)", RegexOptions.IgnoreCase)]
    private static partial Regex Junk();
    [GeneratedRegex(@"\b\d+-Core\b", RegexOptions.IgnoreCase)]
    private static partial Regex Cores();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
