using System.Text;
using System.Windows;
using PyDisplay.Sensors;
using PyDisplay.UI;

namespace PyDisplay;

/// <summary>
/// `PyDisplay.exe --sizetest &lt;file&gt;` — feeds extreme readings to every style/layout and checks the
/// overlay's size never changes (the overlay must stay put where the user placed it).
/// </summary>
internal static class SizeTest
{
    public static void Run(AppSettings real, string outFile)
    {
        var sb = new StringBuilder();
        int fails = 0;
        foreach (var style in new[] { "rows", "graphs", "compact" })
            foreach (var dir in new[] { "vertical", "horizontal" })
                foreach (var font in new[] { "Cascadia Mono", "Consolas", "Segoe UI" })
                {
                    if (!Fonts.IsInstalled(font)) continue;
                    var s = real.Clone();
                    s.Style = style; s.Direction = dir; s.FontFamily = font; s.Collapsed.Clear(); s.Hidden.Clear();
                    foreach (var k in AppSettings.AllExtras) s.Extras[k.Key] = true;
                    var v = new OverlayView(Theme.For(s), s, null);
                    var sizes = new List<Size>();
                    foreach (var snap in Snaps())
                    {
                        v.Update(snap);
                        v.Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        v.Root.Arrange(new Rect(v.Root.DesiredSize));
                        v.Root.UpdateLayout();
                        sizes.Add(v.Root.DesiredSize);
                    }
                    // first reading is the warm-up (everything appears once); after that nothing may move
                    var after = sizes.Skip(1).Select(z => $"{z.Width:0}x{z.Height:0}").Distinct().ToList();
                    bool ok = after.Count == 1;
                    if (!ok) fails++;
                    sb.AppendLine($"{(ok ? "ok   " : "FAIL ")} {style,-8}{dir,-11}{font,-14} {string.Join(" | ", sizes.Select(z => $"{z.Width:0}x{z.Height:0}"))}");
                }
        sb.AppendLine(fails == 0 ? "ALL PASSED — size never changes" : $"{fails} FAILED");
        File.WriteAllText(outFile, sb.ToString());
    }

    private static Snapshot Make(double load, double rate, string? top, double? ping, double ramUsed)
    {
        double[] hist = Enumerable.Range(0, 60).Select(i => load * (i % 7) / 7).ToArray();
        return new Snapshot
        {
            GpuOk = true, GpuName = "NVIDIA GeForce RTX 5070 Ti", GpuLoad = load, VramUsed = 15.9 * load / 100, VramTotal = 16,
            GpuTemp = 30 + load * 0.69, GpuTempMin = 30, GpuTempMax = 99, GpuPower = 5 + load * 3.4, GpuPowerLimit = 348,
            GpuClock = 210 + load * 28, GpuMemClock = 14000, GpuFan = load, GpuEnc = load,
            CpuName = "AMD Ryzen 7 9800X3D 8-Core Processor", CpuLoad = load, Cores = Enumerable.Repeat(load, 16).ToArray(),
            CpuClock = 0.5 + load * 0.055, CpuClockMin = 0.5, CpuClockMax = 5.99, Procs = (int)(99 + load * 99), Threads = 9999, Handles = 999999,
            TopCpuName = top, TopCpuPct = load, RamUsed = ramUsed, RamTotal = 31.1, RamPct = ramUsed / 31.1 * 100, RamPeak = 99,
            CommitUsed = 50, CommitLimit = 53, TopMemName = top, TopMemGb = 9.9,
            NetDown = rate, NetUp = rate / 3, NetPeakDown = 999e6, NetPeakUp = 999e6, SessDown = (long)(rate * 1000), SessUp = (long)rate,
            PingMs = ping, DiskRead = rate * 2, DiskWrite = rate * 3, DiskPeakRead = 3e9, DiskPeakWrite = 3e9,
            Drives = [new("C:", 1403, 1862)], Uptime = TimeSpan.FromHours(load),
            HGpu = hist, HCpu = hist, HDown = hist, HUp = hist, HDisk = hist,
        };
    }

    private static IEnumerable<Snapshot> Snaps()
    {
        yield return Make(50, 5e6, "chrome", 40, 12.3);                          // warm-up: everything present
        yield return Make(0, 0, null, null, 0.5);                                // all low, no top app, no ping
        yield return Make(100, 999.9 * 1048576, "NocatFarm.Tests.Something", 999, 31.0);   // all max, long name
        yield return Make(5, 900, "a", 3, 9.9);
        yield return Make(100, 12000.0 * 1048576, "SteelSeriesSonar", 12, 30.9); // > 10 GB/s
        yield return Make(37, 1023 * 1024, "audiodg", 999, 15.5);
    }
}
