using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PyDisplay.Tools;

/// <summary>
/// RAM cleaner (ported from PyDisplay 1.1.1, with its two broken steps fixed).
/// Info-class numbers checked against the phnt headers:
///   SystemMemoryListInformation = 80, SystemCombinePhysicalMemoryInformation = 130,
///   SystemRegistryReconciliationInformation = 155 (v1 used 158 = energy-estimation config — a no-op).
/// v1's "low-memory event" step used class 74, which is obsolete in Windows, so it was dropped.
/// Most system-wide steps need admin; without it they're reported as skipped instead of silently failing.
/// </summary>
public static class MemoryCleaner
{
    private static readonly object _busyLock = new();
    private static bool _busy;
    public static bool Busy { get { lock (_busyLock) return _busy; } }

    public sealed record Result(double BeforeGb, double AfterGb, bool Admin, int Failed)
    {
        public double FreedGb => BeforeGb - AfterGb;
    }

    /// <summary>Runs the clean; returns null if another clean (manual or timed) is already running.</summary>
    public static Result? Run(bool aggressive, bool clearClipboard, Action<string>? log)
    {
        lock (_busyLock) { if (_busy) return null; _busy = true; }
        try { return RunCore(aggressive, clearClipboard, log ?? (_ => { })); }
        finally { lock (_busyLock) _busy = false; }
    }

    /// <summary>
    /// True while a fullscreen game or app is in front (exclusive or borderless fullscreen, presentation mode).
    /// Auto-clean waits then: trimming a running game's memory can make it hitch.
    /// </summary>
    public static bool FullscreenAppRunning()
    {
        try
        {
            if (SHQueryUserNotificationState(out int st) == 0 && st is 2 or 3 or 4) return true;   // busy / D3D fullscreen / presentation
            // borderless windowed games don't always set that state: compare the foreground window with its monitor
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
            GetWindowThreadProcessId(fg, out int pid);
            if (pid == Environment.ProcessId) return false;
            if (!GetWindowRect(fg, out RECT r)) return false;
            IntPtr mon = MonitorFromWindow(fg, 2);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;
            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);

    public static bool IsAdmin()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    /// <summary>Relaunches PyDisplay elevated (UAC prompt). Returns false if the user said no.</summary>
    public static bool RestartAsAdmin()
    {
        if (App.TestMode) return false;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--wait-mutex") { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch { return false; }   // UAC cancelled
    }

    private static Result RunCore(bool aggressive, bool clearClipboard, Action<string> log)
    {
        double before = UsedGb();
        bool admin = IsAdmin();
        int failed = 0;
        void Step(string what, Func<bool> act, bool needsAdmin = true)
        {
            if (needsAdmin && !admin) { log($"– {what}  (needs admin, skipped)"); return; }
            bool ok;
            try { ok = act(); } catch { ok = false; }
            if (!ok) failed++;
            log((ok ? "✓ " : "✗ ") + what);
        }

        foreach (var p in new[] { "SeDebugPrivilege", "SeIncreaseQuotaPrivilege", "SeProfileSingleProcessPrivilege" })
            EnablePrivilege(p);

        int trimmed = TrimAllWorkingSets();
        log($"✓ trimmed {trimmed} apps' working sets" + (admin ? "" : "  (your apps only — admin reaches all)"));
        Step("flushed system working set", () => MemList(MemoryEmptyWorkingSets));
        Step("flushed modified page list", () => MemList(MemoryFlushModifiedList));
        Step("cleared file cache", () => SetSystemFileCacheSize(unchecked((nuint)ulong.MaxValue), unchecked((nuint)ulong.MaxValue), 0));
        Step("flushed registry cache", () => NtSetSystemInformation(SystemRegistryReconciliationInformation, IntPtr.Zero, 0) == 0);
        Step("combined duplicate pages", () => Combine(log));
        Step("compacted heaps", () => { CompactHeaps(); return true; }, needsAdmin: false);
        Step("flushed DNS cache", () => DnsFlushResolverCache(), needsAdmin: false);
        if (clearClipboard)
            Step("cleared clipboard", () => { if (!OpenClipboard(IntPtr.Zero)) return false; EmptyClipboard(); CloseClipboard(); return true; }, needsAdmin: false);

        if (aggressive)
        {
            Step("purged low-priority standby list", () => MemList(MemoryPurgeLowPriorityStandbyList));
            Step("purged standby list", () => MemList(MemoryPurgeStandbyList));
            // purging standby faults pages back into apps — trim again to reclaim them
            log($"✓ re-trimmed {TrimAllWorkingSets()} apps after standby purge");
            Step("re-flushed modified pages", () => MemList(MemoryFlushModifiedList));
            Step("second combine pass", () => Combine(log));
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            Step("trimmed PyDisplay itself", () => { using var me = Process.GetCurrentProcess(); return EmptyWorkingSet(me.Handle); }, needsAdmin: false);
            Step("final heap compaction", () => { CompactHeaps(); return true; }, needsAdmin: false);
        }

        Thread.Sleep(300);   // let Windows settle before measuring
        return new Result(before, UsedGb(), admin, failed);
    }

    // ── helpers ──

    public static double UsedGb()
    {
        var m = new Sensors.Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Sensors.Native.MEMORYSTATUSEX>() };
        return Sensors.Native.GlobalMemoryStatusEx(ref m) ? (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0 : 0;
    }

    private static int TrimAllWorkingSets()
    {
        int ok = 0;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id is 0 or 4) continue;
                IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try { if (EmptyWorkingSet(h)) ok++; }
                finally { CloseHandle(h); }
            }
        }
        return ok;
    }

    private static bool MemList(int command)
    {
        IntPtr buf = Marshal.AllocHGlobal(4);
        try { Marshal.WriteInt32(buf, command); return NtSetSystemInformation(SystemMemoryListInformation, buf, 4) == 0; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static bool Combine(Action<string> log)
    {
        // MEMORY_COMBINE_INFORMATION_EX { HANDLE Handle; ULONG_PTR PagesCombined; ULONG Flags; }
        int size = IntPtr.Size * 2 + 8;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
            if (NtSetSystemInformation(SystemCombinePhysicalMemoryInformation, buf, size) != 0) return false;
            long pages = Marshal.ReadIntPtr(buf, IntPtr.Size).ToInt64();
            if (pages > 0) log($"    {pages:N0} pages combined ({pages * 4 / 1024} MB)");
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static void CompactHeaps()
    {
        var heaps = new IntPtr[256];
        int n = GetProcessHeaps(heaps.Length, heaps);
        for (int i = 0; i < Math.Min(n, heaps.Length); i++)
            if (heaps[i] != IntPtr.Zero) HeapCompact(heaps[i], 0);
    }

    private static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr tok)) return false;
        try
        {
            if (!LookupPrivilegeValueW(null, name, out long luid)) return false;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() == 0;   // ERROR_NOT_ALL_ASSIGNED = not held
        }
        finally { CloseHandle(tok); }
    }

    // ── native ──
    private const int SystemMemoryListInformation = 80, SystemCombinePhysicalMemoryInformation = 130,
                      SystemRegistryReconciliationInformation = 155;
    private const int MemoryEmptyWorkingSets = 2, MemoryFlushModifiedList = 3,
                      MemoryPurgeStandbyList = 4, MemoryPurgeLowPriorityStandbyList = 5;
    private const int PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_SET_QUOTA = 0x0100;
    private const int TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 2;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attributes; }

    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int cls, IntPtr info, int len);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetSystemFileCacheSize(nuint min, nuint max, int flags);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern int GetProcessHeaps(int n, [Out] IntPtr[] heaps);
    [DllImport("kernel32.dll")] private static extern nuint HeapCompact(IntPtr heap, int flags);
    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr h);
    [DllImport("dnsapi.dll")] private static extern bool DnsFlushResolverCache();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr proc, int access, out IntPtr tok);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValueW(string? sys, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr tok, bool disableAll, ref TOKEN_PRIVILEGES tp, int len, IntPtr prev, IntPtr retLen);
}
