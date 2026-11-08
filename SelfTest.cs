using System.Diagnostics;
using System.Text;
using PyDisplay.Sensors;
using PyDisplay.Tools;

namespace PyDisplay;

/// <summary>
/// `PyDisplay.exe --selftest &lt;file&gt; [--speed]` — runs the real tools once and writes what happened.
/// Never touches the clipboard, the real settings file or the startup registry key.
/// </summary>
internal static class SelfTest
{
    public static async Task RunAsync(SystemMonitor mon, string outFile, bool speed)
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); try { File.WriteAllText(outFile, sb.ToString()); } catch { } }
        int fails = 0;
        void Check(string what, bool ok, string detail = "") { if (!ok) fails++; W($"{(ok ? "PASS" : "FAIL")}  {what}  {detail}"); }

        W($"PyDisplay {App.ShortVersion} self-test  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  admin={MemoryCleaner.IsAdmin()}");
#pragma warning disable IL3000 // empty Location is exactly what proves the runtime is bundled in the exe
        bool bundled = string.IsNullOrEmpty(typeof(object).Assembly.Location);
#pragma warning restore IL3000
        W($"exe: {Environment.ProcessPath}");
        W($"runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, inside the exe={bundled}");
        W($"settings: {AppSettings.Dir}  portable={AppSettings.Portable}  first run={AppSettings.IsFirstRun}");

        // sensors
        await Task.Delay(3000);
        var s = mon.Last;
        Check("sensors produce snapshots", s != null);
        if (s != null)
        {
            Check("cpu load in range", s.CpuLoad is >= 0 and <= 100, $"{s.CpuLoad:0.0}%");
            Check("cpu cores", s.Cores.Length == Environment.ProcessorCount, $"{s.Cores.Length}");
            Check("ram", s.RamTotal > 1 && s.RamUsed > 0, $"{s.RamUsed:0.0}/{s.RamTotal:0.0} GB");
            Check("gpu", s.GpuOk, s.GpuName);
            Check("processes", s.Procs > 20 && s.Threads > s.Procs, $"{s.Procs} procs {s.Threads} threads, top cpu {s.TopCpuName}, top mem {s.TopMemName}");
            Check("drives", s.Drives.Count > 0, string.Join(",", s.Drives.Select(d => d.Name)));
            Check("cpu clock", s.CpuClock is > 0.3 and < 10, $"{s.CpuClock:0.00} GHz");
        }

        // memory cleaner (safe, no clipboard) — only with --test-clean: it trims every app, a running game included
        bool doClean = Environment.GetCommandLineArgs().Contains("--test-clean");
        if (!doClean) W("skip  memory clean (add --test-clean to run it for real)");
        else {
        var log = new List<string>();
        var sw = Stopwatch.StartNew();
        var r = await Task.Run(() => MemoryCleaner.Run(false, clearClipboard: false, log: log.Add));
        Check("memory clean ran", r != null, r == null ? "" : $"{r.BeforeGb:0.00} → {r.AfterGb:0.00} GB in {sw.ElapsedMilliseconds} ms, {r.Failed} failed steps");
        foreach (var l in log) W("        " + l);
        Check("busy guard released", !MemoryCleaner.Busy);

        // busy guard: two at once → second returns null
        var t1 = Task.Run(() => MemoryCleaner.Run(false, false, null));
        await Task.Delay(5);
        var second = MemoryCleaner.Run(false, false, null);
        var first = await t1;
        Check("two cleans can't overlap", first == null || second == null, $"first={(first != null)} second={(second != null)}");
        }
        Check("fullscreen-game detection runs", true, $"fullscreen app in front right now: {MemoryCleaner.FullscreenAppRunning()}");

        // upgrade from v1: settings + themes carried over (into a throwaway settings object)
        var up = new AppSettings();
        string? what = V1Import.ImportSettings(up);
        W($"      v1 import: {what ?? "no v1 install found"} → refresh {up.RefreshMs} ms, auto-clean {up.AutoCleanOn}/{up.AutoCleanMinutes}m/aggr {up.AutoCleanAggressive}, " +
          $"on top {up.Topmost}, hidden [{string.Join(",", up.Hidden)}], font {up.FontSize}, see-through {up.Opacity}, theme {up.Theme}, themes [{string.Join(", ", up.SavedThemes.Keys)}], pos {up.X},{up.Y}");
        Check("v1 settings import", what != null);

        // start with Windows pointing at a moved/old copy gets repointed (registry restored afterwards)
        if (Environment.GetCommandLineArgs().Contains("--test-startup"))
        {
            using var rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)!;
            object? orig = rk.GetValue("PyDisplay");
            rk.SetValue("PyDisplay", "\"C:\\old place\\PyDisplay.exe\"");
            Startup.RepairPath();
            string now = rk.GetValue("PyDisplay") as string ?? "";
            if (orig == null) rk.DeleteValue("PyDisplay", false); else rk.SetValue("PyDisplay", orig);
            Check("moved exe: startup entry repointed", now.Contains(Environment.ProcessPath!), now);
            Check("startup entry restored", Equals(rk.GetValue("PyDisplay"), orig), orig == null ? "(none, as before)" : orig.ToString()!);
        }

        // ip
        string? local = IpLookup.LocalIPv4();
        Check("local ip", local != null, local ?? "");
        string? pub = null;
        try { pub = await IpLookup.PublicAsync(CancellationToken.None); } catch { }
        Check("public ip", pub != null, pub != null ? "(found, not printed)" : "");

        // stats logger on a temp copy of settings, 5 s interval
        var tmpSettings = new AppSettings { LogIntervalMs = 5000 };
        long before = File.Exists(StatsLogger.FilePath) ? new FileInfo(StatsLogger.FilePath).Length : 0;
        var logger = new StatsLogger(mon, () => tmpSettings);
        logger.Start();
        await Task.Delay(6000);
        logger.Stop();
        var tail = File.ReadAllLines(StatsLogger.FilePath).Reverse().Take(8).Reverse().ToList();
        int lines = tail.Count(x => x.StartsWith('['));
        Check("stats log writes lines", lines >= 2, $"{lines} lines, file {new FileInfo(StatsLogger.FilePath).Length - before} bytes added");
        if (tail.FirstOrDefault(x => x.StartsWith('[')) is { } sample) W("        " + sample);

        // updates
        try
        {
            var u = await Updates.LatestAsync();
            Check("github latest release", u != null, $"{u?.Tag} newer={Updates.IsNewer(u?.Tag ?? "")}");
        }
        catch (Exception ex) { Check("github latest release", false, ex.Message); }
        Check("version compare", Updates.IsNewer("v1.1.3") && Updates.IsNewer("v1.2.0") && !Updates.IsNewer("v1.1.1") && !Updates.IsNewer("v1.1.2"));

        // v1 theme import into a throwaway settings object
        var imp = new AppSettings();
        int n = V1Import.ImportInto(imp);
        Check("v1 theme import", n >= 0, $"{n} imported: {string.Join(", ", imp.SavedThemes.Keys)}");
        int again = V1Import.ImportInto(imp);
        Check("v1 import doesn't duplicate", again == 0);

        // theme hex parsing
        Check("hex parse", Theme.NormalizeHex("4ade80") == "#4ade80" && Theme.NormalizeHex("#FFF") == "#ffffff"
                            && Theme.NormalizeHex("red") == null && Theme.NormalizeHex("#12345") == null);

        // settings sanitize
        var bad = AppSettings.FromJson("{\"Style\":\"weird\",\"FontSize\":999,\"Opacity\":-1,\"RefreshMs\":7,\"SectionOrder\":[\"gpu\",\"nope\"],\"Theme\":\"missing\"}");
        Check("bad settings repaired", bad.Style == "rows" && bad.FontSize == 24 && bad.Opacity == 0.3 && bad.RefreshMs == 1000
                                     && bad.SectionOrder.Count == 6 && bad.Theme == "default");

        // clean-up ("remove from this PC") — only exercised in a portable test folder, never on real AppData
        if (AppSettings.Portable && Environment.GetCommandLineArgs().Contains("--test-remove"))
        {
            foreach (var f in new[] { "settings.json", "error.log", "stats-log.txt" })
                File.WriteAllText(Path.Combine(AppSettings.Dir, f), "x");
            AppSettings.DeleteAll();
            bool gone = new[] { "settings.json", "error.log", "stats-log.txt" }.All(f => !File.Exists(Path.Combine(AppSettings.Dir, f)));
            Check("remove from this PC deletes settings + logs", gone);
            Check("…but leaves the exe alone", File.Exists(Environment.ProcessPath));
        }

        if (speed)
        {
            W("speed test (10 s down + 10 s up)…");
            var res = await SpeedTest.RunAsync(_ => { }, CancellationToken.None);
            Check("speed test", res.Error == null && res.DownMbps > 0, $"{res.Server}: ping {res.PingMs:0} ms, ↓ {res.DownMbps} ↑ {res.UpMbps} Mbps {res.Error}");
        }

        W(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
    }
}
