namespace PyDisplay.Sensors;

public sealed record DriveSample(string Name, double UsedGb, double TotalGb);

/// <summary>Everything the overlay shows, read once per tick. Null = not available on this PC.</summary>
public sealed class Snapshot
{
    // GPU
    public bool GpuOk;
    public string GpuName = "";
    public double? GpuLoad, VramUsed, VramTotal, GpuTemp, GpuTempMin, GpuTempMax,
                   GpuPower, GpuPowerLimit, GpuClock, GpuMemClock, GpuFan, GpuEnc;

    // CPU
    public string CpuName = "";
    public double CpuLoad;
    public double[] Cores = [];
    public double? CpuClock, CpuClockMin, CpuClockMax;
    public int Procs, Threads, Handles;
    public string? TopCpuName;
    public double TopCpuPct;

    // Memory
    public double RamUsed, RamTotal, RamPct, RamPeak, CommitUsed, CommitLimit;
    public string? TopMemName;
    public double TopMemGb;

    // Network
    public double NetDown, NetUp, NetPeakDown, NetPeakUp;   // bytes/s
    public long SessDown, SessUp;                           // bytes since launch
    public double? PingMs;

    // Disk
    public double? DiskRead, DiskWrite;                     // bytes/s
    public double DiskPeakRead, DiskPeakWrite;
    public List<DriveSample> Drives = [];

    public TimeSpan Uptime;

    // 60-second histories for the graphs
    public double[] HGpu = [], HCpu = [], HDown = [], HUp = [], HDisk = [];
}

internal sealed class Ring(int cap)
{
    private readonly double[] _b = new double[cap];
    private int _n, _i;
    public void Push(double v) { _b[_i] = v; _i = (_i + 1) % _b.Length; if (_n < _b.Length) _n++; }
    public double[] ToArray()
    {
        var a = new double[_n];
        for (int k = 0; k < _n; k++) a[k] = _b[(_i - _n + k + _b.Length) % _b.Length];
        return a;
    }
}
