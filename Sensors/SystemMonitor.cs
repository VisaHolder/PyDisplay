using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PyDisplay.Sensors;

/// <summary>Reads every sensor on a background thread and raises <see cref="Sampled"/> once per tick.</summary>
public sealed class SystemMonitor : IDisposable
{
    public event Action<Snapshot>? Sampled;
    public Snapshot? Last { get; private set; }
    public int IntervalMs { get; set; }
    public bool ForceWindowsGpu { get; init; }   // testing: skip nvml, use the AMD/Intel path

    private int _gpuIndex;
    private volatile int _wantGpu = -1;
    public IReadOnlyList<string> GpuNames { get; private set; } = [];
    private Thread? _thread;
    private volatile bool _stop;
    private IGpuReader? _gpu;
    private readonly Pdh _pdh = new();
    private double _baseMhz;
    private readonly string _cpuName;
    private readonly int _cores = Environment.ProcessorCount;

    // deltas between ticks
    private long[] _prevIdle = [], _prevTotal = [];
    private readonly Dictionary<long, long> _prevProcTime = new();
    private long _prevStamp;
    private long _prevRx = -1, _prevTx = -1;
    private List<NetworkInterface> _nics = [];
    private long _nicsAt;
    private int _tick;
    private List<DriveSample> _drives = [];

    // running min/max/peaks
    private double _tMin = double.MaxValue, _tMax = double.MinValue;
    private double _cMin = double.MaxValue, _cMax = double.MinValue;
    private double _ramPeak, _pkDown, _pkUp, _pkRead, _pkWrite;
    private long _sessDown, _sessUp;
    private double _ping = double.NaN;

    private readonly Ring _hGpu = new(60), _hCpu = new(60), _hDown = new(60), _hUp = new(60), _hDisk = new(60);

    public SystemMonitor(int intervalMs, int gpuIndex)
    {
        IntervalMs = intervalMs;
        _gpuIndex = gpuIndex;
        _cpuName = ReadCpuName();
    }

    public void Start()
    {
        _pdh.Add("perf", @"\Processor Information(_Total)\% Processor Performance");
        _pdh.Add("dr", @"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _pdh.Add("dw", @"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        _pdh.Collect();
        _baseMhz = ReadBaseMhz();
        _gpu = (ForceWindowsGpu ? null : NvmlGpu.TryCreate(_gpuIndex)) ?? (IGpuReader?)WindowsGpu.TryCreate();
        GpuNames = ForceWindowsGpu ? [] : NvmlGpu.ListNames();

        _thread = new Thread(Loop) { IsBackground = true, Name = "sensors" };
        _thread.Start();
        new Thread(PingLoop) { IsBackground = true, Name = "ping" }.Start();
    }

    public void Dispose() { _stop = true; _pdh.Dispose(); }

    /// <summary>Switch GPU (multi-GPU PCs). Applied on the sensor thread at the next tick.</summary>
    public void SelectGpu(int index) { if (index != _gpuIndex) _wantGpu = index; }

    private void Loop()
    {
        while (!_stop)
        {
            long t0 = Environment.TickCount64;
            try
            {
                var s = Sample();
                Last = s;
                Sampled?.Invoke(s);
            }
            catch (Exception ex) { Log.Error("sample", ex); }
            int wait = IntervalMs - (int)(Environment.TickCount64 - t0);
            Thread.Sleep(Math.Max(50, wait));
        }
    }

    private void PingLoop()
    {
        using var p = new Ping();
        string[] hosts = ["1.1.1.1", "8.8.8.8"];
        while (!_stop)
        {
            double r = double.NaN;
            foreach (var h in hosts)
            {
                try
                {
                    var rep = p.Send(h, 1000);
                    if (rep.Status == IPStatus.Success) { r = rep.RoundtripTime; break; }
                }
                catch { }
            }
            _ping = r;
            Thread.Sleep(2000);
        }
    }

    private Snapshot Sample()
    {
        var s = new Snapshot();
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double secs = _prevStamp == 0 ? 1 : (now - _prevStamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        _prevStamp = now;
        _pdh.Collect();

        // ── GPU ──
        if (_wantGpu >= 0 && _wantGpu != _gpuIndex)
        {
            _gpuIndex = _wantGpu; _wantGpu = -1;
            if (NvmlGpu.TryCreate(_gpuIndex) is { } g) { _gpu = g; _tMin = double.MaxValue; _tMax = double.MinValue; }
        }
        try { _gpu?.Read(s); } catch (Exception ex) { Log.Error("gpu", ex); }
        if (s.GpuTemp is > 0 and var t)
        {
            _tMin = Math.Min(_tMin, t); _tMax = Math.Max(_tMax, t);
            s.GpuTempMin = _tMin; s.GpuTempMax = _tMax;
        }
        _hGpu.Push(s.GpuLoad ?? 0);

        // ── CPU ──
        s.CpuName = _cpuName;
        ReadCpuLoad(s);
        if (_pdh.Get("perf") is > 0 and var perf && _baseMhz > 0)
        {
            double ghz = perf * _baseMhz / 100.0 / 1000.0;
            s.CpuClock = ghz;
            _cMin = Math.Min(_cMin, ghz); _cMax = Math.Max(_cMax, ghz);
            s.CpuClockMin = _cMin; s.CpuClockMax = _cMax;
        }
        ReadProcesses(s, secs);
        _hCpu.Push(s.CpuLoad);

        // ── Memory ──
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref m))
        {
            s.RamTotal = m.ullTotalPhys / 1073741824.0;
            s.RamUsed = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
            s.RamPct = s.RamTotal > 0 ? s.RamUsed / s.RamTotal * 100 : 0;
            _ramPeak = Math.Max(_ramPeak, s.RamPct);
            s.RamPeak = _ramPeak;
            s.CommitLimit = m.ullTotalPageFile / 1073741824.0;
            s.CommitUsed = (m.ullTotalPageFile - m.ullAvailPageFile) / 1073741824.0;
        }

        // ── Network ──
        ReadNetwork(s, secs);
        s.PingMs = double.IsNaN(_ping) ? null : _ping;

        // ── Disk ──
        s.DiskRead = _pdh.Get("dr");
        s.DiskWrite = _pdh.Get("dw");
        _pkRead = Math.Max(_pkRead, s.DiskRead ?? 0); _pkWrite = Math.Max(_pkWrite, s.DiskWrite ?? 0);
        s.DiskPeakRead = _pkRead; s.DiskPeakWrite = _pkWrite;
        _hDisk.Push((s.DiskRead ?? 0) + (s.DiskWrite ?? 0));
        if (_tick % 10 == 0) _drives = ReadDrives();
        s.Drives = _drives;

        s.Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        s.HGpu = _hGpu.ToArray(); s.HCpu = _hCpu.ToArray();
        s.HDown = _hDown.ToArray(); s.HUp = _hUp.ToArray(); s.HDisk = _hDisk.ToArray();
        _tick++;
        return s;
    }

    // ── CPU load per core (same source Task Manager uses) ──
    private void ReadCpuLoad(Snapshot s)
    {
        const int sz = 48; // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
        int n = _cores;
        IntPtr buf = Marshal.AllocHGlobal(sz * n);
        try
        {
            if (Native.NtQuerySystemInformation(Native.SystemProcessorPerformanceInformation, buf, sz * n, out int ret) != 0)
                return;
            n = ret / sz;
            if (_prevIdle.Length != n) { _prevIdle = new long[n]; _prevTotal = new long[n]; }
            var cores = new double[n];
            long dIdleAll = 0, dTotAll = 0;
            for (int i = 0; i < n; i++)
            {
                long idle = Marshal.ReadInt64(buf, i * sz);
                long kern = Marshal.ReadInt64(buf, i * sz + 8);   // includes idle
                long user = Marshal.ReadInt64(buf, i * sz + 16);
                long tot = kern + user;
                long dI = idle - _prevIdle[i], dT = tot - _prevTotal[i];
                bool first = _prevTotal[i] == 0;
                _prevIdle[i] = idle; _prevTotal[i] = tot;
                if (first || dT <= 0) continue;
                cores[i] = Math.Clamp(100.0 * (dT - dI) / dT, 0, 100);
                dIdleAll += dI; dTotAll += dT;
            }
            s.Cores = cores;
            s.CpuLoad = dTotAll > 0 ? Math.Clamp(100.0 * (dTotAll - dIdleAll) / dTotAll, 0, 100) : 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ── Processes: count, threads, handles, top CPU app, top RAM app (no admin needed) ──
    private int _procBuf = 1 << 20;
    private void ReadProcesses(Snapshot s, double secs)
    {
        IntPtr buf = IntPtr.Zero;
        try
        {
            int status;
            while (true)
            {
                buf = Marshal.AllocHGlobal(_procBuf);
                status = Native.NtQuerySystemInformation(Native.SystemProcessInformation, buf, _procBuf, out int need);
                if (status != Native.STATUS_INFO_LENGTH_MISMATCH) break;
                Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                _procBuf = Math.Max(_procBuf * 2, need + 65536);
                if (_procBuf > 64 << 20) return;
            }
            if (status != 0) return;

            var cpuByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var memByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<long>();
            int procs = 0, threads = 0, handles = 0, off = 0;
            while (true)
            {
                IntPtr p = buf + off;
                int next = Marshal.ReadInt32(p, 0);
                int nThreads = Marshal.ReadInt32(p, 4);
                long wsPrivate = Marshal.ReadInt64(p, 8);
                long user = Marshal.ReadInt64(p, 40), kern = Marshal.ReadInt64(p, 48);
                int nameLen = (ushort)Marshal.ReadInt16(p, 56);
                IntPtr namePtr = Marshal.ReadIntPtr(p, 64);
                long pid = Marshal.ReadIntPtr(p, 80).ToInt64();
                int nHandles = Marshal.ReadInt32(p, 96);

                if (pid != 0)
                {
                    procs++; threads += nThreads; handles += nHandles;
                    string name = namePtr == IntPtr.Zero ? "System" : Marshal.PtrToStringUni(namePtr, nameLen / 2);
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                    long time = user + kern;
                    seen.Add(pid);
                    if (_prevProcTime.TryGetValue(pid, out long prev) && time >= prev)
                        cpuByName[name] = cpuByName.GetValueOrDefault(name) + (time - prev);
                    _prevProcTime[pid] = time;
                    if (name is not ("Memory Compression" or "System" or "Registry"))
                        memByName[name] = memByName.GetValueOrDefault(name) + wsPrivate;
                }
                if (next == 0) break;
                off += next;
            }
            foreach (var dead in _prevProcTime.Keys.Where(k => !seen.Contains(k)).ToList())
                _prevProcTime.Remove(dead);

            s.Procs = procs; s.Threads = threads; s.Handles = handles;
            if (cpuByName.Count > 0)
            {
                var top = cpuByName.MaxBy(kv => kv.Value);
                s.TopCpuName = top.Key;
                s.TopCpuPct = Math.Clamp(top.Value / (secs * 1e7 * _cores) * 100, 0, 100);
            }
            if (memByName.Count > 0)
            {
                var top = memByName.MaxBy(kv => kv.Value);
                s.TopMemName = top.Key;
                s.TopMemGb = top.Value / 1073741824.0;
            }
        }
        catch (Exception ex) { Log.Error("procs", ex); }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
    }

    // ── Network: only adapters that route to the internet (no double-counting virtual switches) ──
    private void ReadNetwork(Snapshot s, double secs)
    {
        if (Environment.TickCount64 - _nicsAt > 30000 || _nics.Count == 0)
        {
            _nicsAt = Environment.TickCount64;
            try
            {
                _nics = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                             && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                             && n.GetIPProperties().GatewayAddresses.Count > 0)
                    .ToList();
            }
            catch { _nics = []; }
        }
        long rx = 0, tx = 0;
        foreach (var n in _nics)
        {
            try { var st = n.GetIPStatistics(); rx += st.BytesReceived; tx += st.BytesSent; }
            catch { }
        }
        if (_prevRx >= 0 && rx >= _prevRx && tx >= _prevTx)
        {
            long dRx = rx - _prevRx, dTx = tx - _prevTx;
            s.NetDown = dRx / secs; s.NetUp = dTx / secs;
            _sessDown += dRx; _sessUp += dTx;
        }
        _prevRx = rx; _prevTx = tx;
        _pkDown = Math.Max(_pkDown, s.NetDown); _pkUp = Math.Max(_pkUp, s.NetUp);
        s.NetPeakDown = _pkDown; s.NetPeakUp = _pkUp;
        s.SessDown = _sessDown; s.SessUp = _sessUp;
        _hDown.Push(s.NetDown); _hUp.Push(s.NetUp);
    }

    private static List<DriveSample> ReadDrives()
    {
        var list = new List<DriveSample>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize <= 0) continue;
                    double tot = d.TotalSize / 1073741824.0;
                    list.Add(new DriveSample(d.Name.TrimEnd('\\'), tot - d.TotalFreeSpace / 1073741824.0, tot));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    private static string ReadCpuName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch { return "CPU"; }
    }

    private static double ReadBaseMhz()
    {
        try
        {
            int n = Environment.ProcessorCount;
            var arr = new Native.PROCESSOR_POWER_INFORMATION[n];
            uint r = Native.CallNtPowerInformation(11, IntPtr.Zero, 0, arr,
                Marshal.SizeOf<Native.PROCESSOR_POWER_INFORMATION>() * n);
            return r == 0 ? arr[0].MaxMhz : 0;
        }
        catch { return 0; }
    }
}
