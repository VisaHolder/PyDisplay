using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace PyDisplay.Tools;

/// <summary>
/// Download / upload / ping test, same method as v1: nearest speedtest.net server, 8 parallel streams, 10 s each.
/// Falls back to Cloudflare's speed endpoints if no speedtest.net server answers.
/// </summary>
public static class SpeedTest
{
    public sealed record Progress(string Phase, string Server, double? PingMs, double? Down, double? Up);
    public sealed record Result(string Server, double? PingMs, double? DownMbps, double? UpMbps, string? Error = null);

    private const int Streams = 8;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);

    private static HttpClient NewClient()
    {
        var c = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = Streams * 2, PooledConnectionLifetime = TimeSpan.FromMinutes(1) })
        { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PyDisplay/" + App.ShortVersion);
        c.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        return c;
    }

    public static async Task<Result> RunAsync(Action<Progress> report, CancellationToken ct)
    {
        using var http = NewClient();
        try
        {
            report(new("finding the nearest server…", "", null, null, null));
            var server = await NearestOoklaAsync(http, ct);
            string name; Func<Uri> down; Uri up; string host; int port;
            if (server is { } s)
            {
                name = s.Name; host = s.Host; port = s.Port;
                int i = 0; int[] sizes = [2000, 2500, 3000, 3500, 4000];
                down = () => { int sz = sizes[(Interlocked.Increment(ref i) & int.MaxValue) % sizes.Length]; return new Uri($"{s.Base}/random{sz}x{sz}.jpg?x={Guid.NewGuid():N}"); };
                up = new Uri($"{s.Base}/upload.php");
            }
            else
            {
                name = "Cloudflare"; host = "speed.cloudflare.com"; port = 443;
                down = () => new Uri("https://speed.cloudflare.com/__down?bytes=25000000");
                up = new Uri("https://speed.cloudflare.com/__up");
            }

            report(new("measuring ping…", name, null, null, null));
            double? ping = await PingAsync(host, port, ct);

            double? dl = await MeasureAsync(http, ct, (c, t) => DownloadWorker(c, down, t),
                mbps => report(new("download…", name, ping, mbps, null)));
            double? ul = await MeasureAsync(http, ct, (c, t) => UploadWorker(c, up, t),
                mbps => report(new("upload…", name, ping, dl, mbps)));

            return new Result(name, ping, dl, ul);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new Result("", null, null, null, ex.Message); }
    }

    private sealed record Server(string Name, string Base, string Host, int Port);

    private static async Task<Server?> NearestOoklaAsync(HttpClient http, CancellationToken ct)
    {
        List<Server> list = [];
        try
        {
            var json = await http.GetStringAsync("https://www.speedtest.net/api/js/servers?engine=js&limit=10&https_functional=true", ct);
            foreach (var e in JsonDocument.Parse(json).RootElement.EnumerateArray())
            {
                string url = e.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                string hostPort = e.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "";
                if (url.Length == 0 || hostPort.Length == 0) continue;
                string sponsor = e.TryGetProperty("sponsor", out var sp) ? sp.GetString() ?? "" : "";
                string city = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var parts = hostPort.Split(':');
                int port = parts.Length > 1 && int.TryParse(parts[1], out var pp) ? pp : 8080;
                list.Add(new Server(string.Join(" · ", new[] { sponsor, city }.Where(x => x.Length > 0)), url[..url.LastIndexOf('/')], parts[0], port));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }

        // fastest TCP connect wins (all probed at once)
        var probes = list.Select(async s => (s, ms: await PingAsync(s.Host, s.Port, ct, samples: 1))).ToList();
        var results = await Task.WhenAll(probes);
        return results.Where(r => r.ms != null).OrderBy(r => r.ms).Select(r => r.s).FirstOrDefault();
    }

    private static async Task<double?> PingAsync(string host, int port, CancellationToken ct, int samples = 5)
    {
        var times = new List<double>();
        for (int i = 0; i < samples; i++)
        {
            try
            {
                using var sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(3000);
                var sw = Stopwatch.StartNew();
                await sock.ConnectAsync(host, port, cts.Token);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
            if (samples > 1) await Task.Delay(50, ct);
        }
        return times.Count > 0 ? times.Min() : null;   // best of N = the line's real latency
    }

    private static async Task<double?> MeasureAsync(HttpClient http, CancellationToken ct,
        Func<HttpClient, StreamCounter, Task> worker, Action<double> progress)
    {
        var counter = new StreamCounter { End = DateTime.UtcNow + Duration, Ct = ct };
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, Streams).Select(_ => Task.Run(() => worker(http, counter), ct)).ToList();
        while (DateTime.UtcNow < counter.End)
        {
            await Task.Delay(250, ct);
            progress(Mbps(Interlocked.Read(ref counter.Bytes), sw.Elapsed.TotalSeconds));
        }
        try { await Task.WhenAll(tasks); } catch { }
        long b = Interlocked.Read(ref counter.Bytes);
        return b > 0 ? Math.Round(Mbps(b, sw.Elapsed.TotalSeconds), 1) : null;
    }

    private static double Mbps(long bytes, double secs) => secs > 0 ? bytes * 8 / secs / 1_000_000 : 0;

    private sealed class StreamCounter
    {
        public long Bytes;
        public DateTime End;
        public CancellationToken Ct;
        public bool Done => DateTime.UtcNow >= End || Ct.IsCancellationRequested;
    }

    private static async Task DownloadWorker(HttpClient http, Func<Uri> next, StreamCounter c)
    {
        var buf = new byte[65536];
        while (!c.Done)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(c.Ct);
                cts.CancelAfter(c.End - DateTime.UtcNow + TimeSpan.FromMilliseconds(200));
                using var resp = await http.GetAsync(next(), HttpCompletionOption.ResponseHeadersRead, cts.Token);
                await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
                int n;
                while (!c.Done && (n = await s.ReadAsync(buf, cts.Token)) > 0)
                    Interlocked.Add(ref c.Bytes, n);
            }
            catch { if (!c.Done) await Task.Delay(100); }
        }
    }

    private static async Task UploadWorker(HttpClient http, Uri up, StreamCounter c)
    {
        var payload = new byte[512 * 1024];
        Random.Shared.NextBytes(payload);
        while (!c.Done)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(c.Ct);
                cts.CancelAfter(TimeSpan.FromSeconds(12));
                using var content = new ByteArrayContent(payload);
                content.Headers.ContentType = new("application/octet-stream");
                using var resp = await http.PostAsync(up, content, cts.Token);
                if (resp.IsSuccessStatusCode && DateTime.UtcNow <= c.End.AddSeconds(1))
                    Interlocked.Add(ref c.Bytes, payload.Length);
            }
            catch { if (!c.Done) await Task.Delay(100); }
        }
    }
}
