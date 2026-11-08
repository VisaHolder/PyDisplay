using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PyDisplay.Sensors;

internal interface IGpuReader
{
    string Name { get; }
    void Read(Snapshot s);
}

/// <summary>NVIDIA cards, through nvml.dll (installed with every NVIDIA driver).</summary>
internal sealed class NvmlGpu : IGpuReader
{
    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCount_v2(out uint n);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr d);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetName(IntPtr d, byte[] name, uint len);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr d, out Util u);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr d, out Mem m);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr d, int sensor, out uint t);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr d, out uint mw);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr d, out uint mw);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetClockInfo(IntPtr d, int type, out uint mhz);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetFanSpeed(IntPtr d, out uint pct);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetEncoderUtilization(IntPtr d, out uint util, out uint period);

    [StructLayout(LayoutKind.Sequential)] private struct Util { public uint gpu, memory; }
    [StructLayout(LayoutKind.Sequential)] private struct Mem { public ulong total, free, used; }

    private readonly IntPtr _dev;
    public string Name { get; }

    private NvmlGpu(IntPtr dev, string name) { _dev = dev; Name = name; }

    public static NvmlGpu? TryCreate(int index)
    {
        try
        {
            if (nvmlInit_v2() != 0) return null;
            if (nvmlDeviceGetCount_v2(out uint n) != 0 || n == 0) return null;
            uint i = (uint)Math.Clamp(index, 0, (int)n - 1);
            if (nvmlDeviceGetHandleByIndex_v2(i, out var dev) != 0) return null;
            var buf = new byte[96];
            string name = nvmlDeviceGetName(dev, buf, (uint)buf.Length) == 0
                ? Encoding.ASCII.GetString(buf).TrimEnd('\0') : "NVIDIA GPU";
            return new NvmlGpu(dev, name);
        }
        catch { return null; }   // no nvml.dll (no NVIDIA driver) — fine, fall back
    }

    /// <summary>Names of every NVIDIA GPU, in NVML index order (empty when there's no NVIDIA driver).</summary>
    public static List<string> ListNames()
    {
        var list = new List<string>();
        try
        {
            if (nvmlInit_v2() != 0 || nvmlDeviceGetCount_v2(out uint n) != 0) return list;
            for (uint i = 0; i < n; i++)
            {
                var buf = new byte[96];
                list.Add(nvmlDeviceGetHandleByIndex_v2(i, out var d) == 0 && nvmlDeviceGetName(d, buf, (uint)buf.Length) == 0
                    ? Encoding.ASCII.GetString(buf).TrimEnd('\0') : $"GPU {i}");
            }
        }
        catch { }
        return list;
    }

    public void Read(Snapshot s)
    {
        s.GpuOk = true;
        s.GpuName = Name;
        if (nvmlDeviceGetUtilizationRates(_dev, out var u) == 0) s.GpuLoad = u.gpu;
        if (nvmlDeviceGetMemoryInfo(_dev, out var m) == 0 && m.total > 0)
        {
            s.VramUsed = m.used / 1073741824.0;
            s.VramTotal = m.total / 1073741824.0;
        }
        if (nvmlDeviceGetTemperature(_dev, 0, out uint t) == 0 && t > 0) s.GpuTemp = t;
        if (nvmlDeviceGetPowerUsage(_dev, out uint mw) == 0) s.GpuPower = mw / 1000.0;
        if (nvmlDeviceGetEnforcedPowerLimit(_dev, out uint lim) == 0 && lim > 0) s.GpuPowerLimit = lim / 1000.0;
        if (nvmlDeviceGetClockInfo(_dev, 0, out uint gc) == 0) s.GpuClock = gc;
        if (nvmlDeviceGetClockInfo(_dev, 2, out uint mc) == 0) s.GpuMemClock = mc;
        if (nvmlDeviceGetFanSpeed(_dev, out uint fan) == 0) s.GpuFan = fan;
        if (nvmlDeviceGetEncoderUtilization(_dev, out uint enc, out _) == 0) s.GpuEnc = enc;
    }
}

/// <summary>
/// Any GPU (AMD, Intel, NVIDIA without nvml) through the Windows GPU counters —
/// the same numbers Task Manager shows. Load + VRAM only.
/// </summary>
internal sealed class WindowsGpu : IGpuReader
{
    private readonly Pdh _pdh = new();
    private readonly double? _totalGb;
    public string Name { get; }

    private WindowsGpu(string name, double? totalGb)
    {
        Name = name;
        _totalGb = totalGb;
        _pdh.Add("eng", @"\GPU Engine(*)\Utilization Percentage");
        _pdh.Add("mem", @"\GPU Adapter Memory(*)\Dedicated Usage");
        _pdh.Collect();
    }

    public static WindowsGpu? TryCreate()
    {
        try
        {
            var (name, gb) = DetectFromRegistry();
            var g = new WindowsGpu(name ?? "GPU", gb);
            return g._pdh.Has("eng") ? g : null;
        }
        catch { return null; }
    }

    public void Read(Snapshot s)
    {
        _pdh.Collect();
        // Sum the 3D engine use per adapter (luid), keep the busiest adapter.
        var perLuid = new Dictionary<string, double>();
        foreach (var (inst, v) in _pdh.GetArray("eng"))
        {
            if (!inst.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
            string luid = Luid(inst);
            perLuid[luid] = perLuid.GetValueOrDefault(luid) + v;
        }
        double vram = 0;
        foreach (var (_, v) in _pdh.GetArray("mem")) vram = Math.Max(vram, v);

        s.GpuOk = true;
        s.GpuName = Name;
        s.GpuLoad = perLuid.Count > 0 ? Math.Min(100, perLuid.Values.Max()) : 0;
        if (vram > 0) s.VramUsed = vram / 1073741824.0;
        if (_totalGb is > 0) s.VramTotal = _totalGb;
    }

    private static string Luid(string inst)
    {
        int a = inst.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (a < 0) return "";
        int b = inst.IndexOf("_phys", a, StringComparison.OrdinalIgnoreCase);
        return b > a ? inst[a..b] : inst[a..];
    }

    /// <summary>Picks the display adapter with the most memory (the real GPU, not a virtual one).</summary>
    private static (string? name, double? gb) DetectFromRegistry()
    {
        string? best = null; double bestGb = 0;
        using var cls = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls == null) return (null, null);
        foreach (var sub in cls.GetSubKeyNames())
        {
            if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
            try
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is not string desc) continue;
                if (desc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("Parsec", StringComparison.OrdinalIgnoreCase)) continue;
                double gb = 0;
                object? q = k.GetValue("HardwareInformation.qwMemorySize");
                object? d = k.GetValue("HardwareInformation.MemorySize");
                if (q is long ql) gb = ql / 1073741824.0;
                else if (d is int di) gb = (uint)di / 1073741824.0;
                else if (d is byte[] db && db.Length >= 4) gb = BitConverter.ToUInt32(db, 0) / 1073741824.0;
                if (best == null || gb > bestGb) { best = desc; bestGb = gb; }
            }
            catch { /* some subkeys are locked — skip */ }
        }
        return (best, bestGb > 0 ? bestGb : null);
    }
}
