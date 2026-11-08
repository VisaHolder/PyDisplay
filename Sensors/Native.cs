using System.Runtime.InteropServices;

namespace PyDisplay.Sensors;

/// <summary>Win32 / NT / PDH calls. Every one of these exists on Windows 10 1809+.</summary>
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);

    public const int SystemProcessInformation = 5;
    public const int SystemProcessorPerformanceInformation = 8;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESSOR_POWER_INFORMATION
    {
        public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState;
    }

    [DllImport("powrprof.dll")]
    public static extern uint CallNtPowerInformation(int level, IntPtr inBuf, int inLen,
        [Out] PROCESSOR_POWER_INFORMATION[] outBuf, int outLen);

    // ── PDH (English counter names, so it works on every Windows language) ──
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQueryW(string? src, IntPtr user, out IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounterW(IntPtr q, string path, IntPtr user, out IntPtr c);
    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr q);
    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(IntPtr q);

    [StructLayout(LayoutKind.Explicit)]
    public struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
    }

    [DllImport("pdh.dll")]
    public static extern uint PdhGetFormattedCounterValue(IntPtr c, uint fmt, IntPtr type, out PDH_FMT_COUNTERVALUE v);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhGetFormattedCounterArrayW(IntPtr c, uint fmt, ref uint bufSize, out uint count, IntPtr buf);

    public const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
    public const uint PDH_MORE_DATA = 0x800007D2;

    // ── window styles / keyboard ──
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000;
    public const int VK_CONTROL = 0x11;
}

/// <summary>Small wrapper around one PDH query.</summary>
internal sealed class Pdh : IDisposable
{
    private IntPtr _q;
    private readonly Dictionary<string, IntPtr> _c = new();
    public bool Ok { get; }

    public Pdh() => Ok = Native.PdhOpenQueryW(null, IntPtr.Zero, out _q) == 0;

    public bool Add(string key, string path)
    {
        if (!Ok) return false;
        if (Native.PdhAddEnglishCounterW(_q, path, IntPtr.Zero, out var c) != 0) return false;
        _c[key] = c;
        return true;
    }

    public bool Has(string key) => _c.ContainsKey(key);

    public void Collect() { if (Ok) Native.PdhCollectQueryData(_q); }

    public double? Get(string key)
    {
        if (!_c.TryGetValue(key, out var c)) return null;
        if (Native.PdhGetFormattedCounterValue(c, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, IntPtr.Zero, out var v) != 0)
            return null;
        if (v.CStatus > 1 || double.IsNaN(v.doubleValue)) return null;
        return v.doubleValue;
    }

    /// <summary>Values for a wildcard counter, as (instance name, value).</summary>
    public List<(string Name, double Value)> GetArray(string key)
    {
        var list = new List<(string, double)>();
        if (!_c.TryGetValue(key, out var c)) return list;
        uint size = 0;
        uint r = Native.PdhGetFormattedCounterArrayW(c, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
        if (r != Native.PDH_MORE_DATA || size == 0) return list;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.PdhGetFormattedCounterArrayW(c, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref size, out uint count, buf) != 0)
                return list;
            int item = IntPtr.Size + 16; // name pointer + PDH_FMT_COUNTERVALUE
            for (int i = 0; i < count; i++)
            {
                IntPtr p = buf + i * item;
                string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p)) ?? "";
                uint status = (uint)Marshal.ReadInt32(p, IntPtr.Size);
                if (status > 1) continue;
                double val = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, IntPtr.Size + 8));
                list.Add((name, val));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    public void Dispose()
    {
        if (_q != IntPtr.Zero) { Native.PdhCloseQuery(_q); _q = IntPtr.Zero; }
    }
}
