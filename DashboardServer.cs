using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProcLens;

internal sealed class DashboardServer : IDisposable
{
    private readonly string _dataDirectory;
    private readonly int _port;
    private readonly string _token;
    private readonly TcpListener _listener;
    private readonly SemaphoreSlim _connections = new(8, 8);
    private CancellationTokenRegistration _stopRegistration;
    private Task? _loop;

    public DashboardServer(string dataDirectory, int port, string token)
    {
        _dataDirectory = dataDirectory;
        _port = port;
        _token = token;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start(CancellationToken cancellationToken)
    {
        _listener.Start();
        _stopRegistration = cancellationToken.Register(() => _listener.Stop());
        _loop = AcceptLoopAsync(cancellationToken);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                if (!await _connections.WaitAsync(0, cancellationToken))
                {
                    client.Dispose();
                    continue;
                }
                _ = HandleAsync(client, cancellationToken).ContinueWith(_ => _connections.Release(), TaskScheduler.Default);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { /* Keep the collector alive if a dashboard request is malformed. */ }
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var headerText = await ReadHeaderAsync(stream, timeout.Token);
                if (headerText is null) return;
                var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var requestLine = lines.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(requestLine)) return;

                var parts = requestLine.Split(' ', 3);
                if (parts.Length < 2 || parts[0] != "GET")
                {
                    await RespondAsync(stream, 405, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("GET only"), cancellationToken);
                    return;
                }

                var host = lines.Skip(1).FirstOrDefault(x => x.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim();
                if (host is null || !(host.Equals($"127.0.0.1:{_port}", StringComparison.OrdinalIgnoreCase) ||
                                      host.Equals($"localhost:{_port}", StringComparison.OrdinalIgnoreCase)))
                {
                    await RespondAsync(stream, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Invalid local host."), cancellationToken);
                    return;
                }

                var uri = new Uri("http://127.0.0.1" + parts[1]);
                if (uri.AbsolutePath == "/health")
                {
                    await RespondAsync(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"status\":\"ok\"}"), cancellationToken);
                    return;
                }

                if (uri.AbsolutePath == "/api/dashboard")
                {
                    if (!TokenMatches(uri.Query))
                    {
                        await RespondAsync(stream, 403, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"error\":\"Open ProcLens from its tray icon.\"}"), cancellationToken);
                        return;
                    }
                    var minutes = ParseMinutes(uri.Query);
                    var json = DashboardData.Build(_dataDirectory, minutes);
                    await RespondAsync(stream, 200, "application/json; charset=utf-8", json, cancellationToken);
                    return;
                }

                var asset = uri.AbsolutePath switch
                {
                    "/" or "/index.html" => ("index.html", "text/html; charset=utf-8"),
                    "/app.js" => ("app.js", "text/javascript; charset=utf-8"),
                    "/styles.css" => ("styles.css", "text/css; charset=utf-8"),
                    _ => ((string?)null, "text/plain; charset=utf-8")
                };

                if (asset.Item1 is null)
                {
                    await RespondAsync(stream, 404, asset.Item2, Encoding.UTF8.GetBytes("Not found"), cancellationToken);
                    return;
                }

                var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", asset.Item1);
                if (!File.Exists(path))
                {
                    await RespondAsync(stream, 500, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Dashboard assets are missing."), cancellationToken);
                    return;
                }
                await RespondAsync(stream, 200, asset.Item2, await File.ReadAllBytesAsync(path, cancellationToken), cancellationToken);
            }
            catch { /* Browsers routinely cancel requests during refresh/navigation. */ }
        }
    }

    private bool TokenMatches(string query)
    {
        var supplied = ParseQueryValue(query, "token");
        if (supplied is null) return false;
        var expectedBytes = Encoding.UTF8.GetBytes(_token);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static async Task<string?> ReadHeaderAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        const int maximum = 16 * 1024;
        var buffer = new byte[1024];
        using var memory = new MemoryStream();
        while (memory.Length < maximum)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return null;
            memory.Write(buffer, 0, read);
            var bytes = memory.GetBuffer();
            var length = (int)memory.Length;
            for (var i = Math.Max(3, length - read - 3); i < length; i++)
            {
                if (i >= 3 && bytes[i - 3] == 13 && bytes[i - 2] == 10 && bytes[i - 1] == 13 && bytes[i] == 10)
                    return Encoding.ASCII.GetString(bytes, 0, i + 1);
            }
        }
        return null;
    }

    private static int ParseMinutes(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "minutes" && int.TryParse(parts[1], out var value))
                return Math.Clamp(value, 5, 43200);
        }
        return 60;
    }

    private static string? ParseQueryValue(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals(name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(parts[1]);
        }
        return null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string contentType, byte[] body, CancellationToken cancellationToken)
    {
        var reason = status switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Internal Server Error" };
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n" +
            "Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'\r\n" +
            "Cross-Origin-Resource-Policy: same-origin\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    public void Dispose()
    {
        _stopRegistration.Dispose();
        _listener.Stop();
        _connections.Dispose();
        if (_loop?.IsCompleted == true) _loop.Dispose();
    }
}

internal static class DashboardData
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static byte[] Build(string dataDirectory, int minutes)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-minutes);
        var systems = new List<SystemPoint>();
        var processes = new List<ProcessPoint>();
        var lifecycle = new List<LifecycleEvent>();
        var runs = new List<RunEvent>();

        if (Directory.Exists(dataDirectory))
        {
            foreach (var line in Program.ReadDashboardHistory(dataDirectory, cutoff))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!TryDate(root, "timeUtc", out var time) || time < cutoff) continue;
                    var type = Text(root, "type");
                    if (type == "system_sample")
                    {
                        systems.Add(new SystemPoint(time, Number(root, "cpuPercent"), Number(root, "commitGb"),
                            Number(root, "commitLimitGb"), Number(root, "physicalTotalGb"), Number(root, "physicalAvailableGb"),
                            Integer(root, "processCount"), Integer(root, "threadCount"), Integer(root, "handleCount")));
                    }
                    else if (type == "process_sample")
                    {
                        processes.Add(new ProcessPoint(time, Integer(root, "pid"), Text(root, "name"), Text(root, "category"),
                            Text(root, "owner"), Flag(root, "unresolvedOwner") || Flag(root, "orphan"), Number(root, "privateMb"), Number(root, "workingSetMb"),
                            Number(root, "cpuPercent"), Number(root, "readMbPerSec"), Number(root, "writeMbPerSec"), Number(root, "pageFaultsPerSec")));
                    }
                    else if (type is "process_start" or "process_stop" or "process_existing")
                    {
                        lifecycle.Add(new LifecycleEvent(time, type, Integer(root, "pid"), Text(root, "name"), Text(root, "category"),
                            Text(root, "owner"), Flag(root, "unresolvedOwner") || Flag(root, "orphan")));
                    }
                    else if (type == "collector_start")
                    {
                        TryDate(root, "bootUtc", out var boot);
                        runs.Add(new RunEvent(time, boot, Text(root, "runId")));
                    }
                }
                catch { /* Ignore partial writes and older incompatible lines. */ }
            }
        }

        var latestProcessTime = processes.Count == 0 ? DateTime.MinValue : processes.Max(x => x.TimeUtc);
        var current = processes.Where(x => x.TimeUtc == latestProcessTime).ToList();
        var latestSystem = systems.LastOrDefault();

        var categoryTotals = processes.GroupBy(x => new { x.TimeUtc, x.Category })
            .Select(g => new AggregatePoint(g.Key.TimeUtc, g.Key.Category, g.Sum(x => x.PrivateMb), g.Sum(x => x.CpuPercent), g.Any(x => x.Unresolved)))
            .ToList();
        var categories = categoryTotals.GroupBy(x => x.Label)
            .Select(g => new
            {
                name = g.Key,
                currentMb = g.FirstOrDefault(x => x.TimeUtc == latestProcessTime)?.MemoryMb ?? 0,
                peakMb = g.Max(x => x.MemoryMb),
                averageMb = Math.Round(g.Average(x => x.MemoryMb), 1),
                currentCpu = Math.Round(g.FirstOrDefault(x => x.TimeUtc == latestProcessTime)?.CpuPercent ?? 0, 1),
                unresolved = g.Any(x => x.Unresolved),
                series = Downsample(g.OrderBy(x => x.TimeUtc).Select(x => new { t = x.TimeUtc, v = Math.Round(x.MemoryMb, 1) }).ToList(), 90)
            })
            .OrderByDescending(x => x.currentMb).ThenByDescending(x => x.peakMb).Take(16).ToList();

        var ownerTotals = processes.GroupBy(x => new { x.TimeUtc, x.Owner })
            .Select(g => new AggregatePoint(g.Key.TimeUtc, g.Key.Owner, g.Sum(x => x.PrivateMb), g.Sum(x => x.CpuPercent), g.Any(x => x.Unresolved)))
            .ToList();
        var owners = ownerTotals.GroupBy(x => x.Label)
            .Select(g => new
            {
                name = g.Key,
                currentMb = g.FirstOrDefault(x => x.TimeUtc == latestProcessTime)?.MemoryMb ?? 0,
                peakMb = g.Max(x => x.MemoryMb),
                currentCpu = Math.Round(g.FirstOrDefault(x => x.TimeUtc == latestProcessTime)?.CpuPercent ?? 0, 1),
                unresolved = g.Any(x => x.Unresolved),
                processCount = current.Count(x => x.Owner == g.Key)
            })
            .OrderByDescending(x => x.currentMb).ThenByDescending(x => x.peakMb).Take(18).ToList();

        var unresolved = current.Where(x => x.Unresolved)
            .GroupBy(x => new { x.Pid, x.Name, x.Category, x.Owner })
            .Select(g => new { g.Key.Pid, g.Key.Name, g.Key.Category, g.Key.Owner, privateMb = g.Sum(x => x.PrivateMb), cpu = g.Sum(x => x.CpuPercent) })
            .OrderByDescending(x => x.privateMb).Take(12).ToList();

        var starts = lifecycle.Count(x => x.Type == "process_start");
        var stops = lifecycle.Count(x => x.Type == "process_stop");
        var commitPercent = latestSystem is null || latestSystem.CommitLimitGb == 0 ? 0 : latestSystem.CommitGb / latestSystem.CommitLimitGb * 100;
        var status = latestSystem is null ? "unknown" : latestSystem.AvailableGb < 1 || commitPercent >= 90 ? "critical" : latestSystem.AvailableGb < 2 || commitPercent >= 78 ? "pressure" : "stable";

        var result = new
        {
            generatedUtc = DateTime.UtcNow,
            windowMinutes = minutes,
            status,
            latestSampleUtc = latestProcessTime == DateTime.MinValue ? (DateTime?)null : latestProcessTime,
            summary = new
            {
                cpuPercent = latestSystem?.CpuPercent ?? 0,
                commitGb = latestSystem?.CommitGb ?? 0,
                commitLimitGb = latestSystem?.CommitLimitGb ?? 0,
                commitPercent = Math.Round(commitPercent, 1),
                physicalTotalGb = latestSystem?.PhysicalTotalGb ?? 0,
                availableGb = latestSystem?.AvailableGb ?? 0,
                trackedPrivateGb = Math.Round(current.Sum(x => x.PrivateMb) / 1024, 2),
                processCount = latestSystem?.ProcessCount ?? 0,
                trackedCount = current.Count,
                unresolvedCount = current.Count(x => x.Unresolved),
                starts,
                stops
            },
            systemSeries = Downsample(systems.Select(x => new
            {
                t = x.TimeUtc,
                cpu = x.CpuPercent,
                commit = x.CommitGb,
                available = x.AvailableGb,
                processes = x.ProcessCount
            }).ToList(), 480),
            categories,
            owners,
            unresolved,
            lifecycle = lifecycle.Where(x => x.Type != "process_existing").OrderByDescending(x => x.TimeUtc).Take(100),
            runs = runs.OrderByDescending(x => x.TimeUtc).Take(12)
        };
        return JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
    }

    private static List<T> Downsample<T>(List<T> source, int maximum)
    {
        if (source.Count <= maximum) return source;
        var result = new List<T>(maximum);
        var step = source.Count / (double)maximum;
        for (var i = 0; i < maximum; i++) result.Add(source[Math.Min(source.Count - 1, (int)Math.Floor(i * step))]);
        return result;
    }

    private static string Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
    private static double Number(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : 0;
    private static int Integer(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
    private static bool Flag(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;
    private static bool TryDate(JsonElement root, string name, out DateTime value)
    {
        value = default;
        return root.TryGetProperty(name, out var node) && node.TryGetDateTime(out value);
    }
}

internal sealed record SystemPoint(DateTime TimeUtc, double CpuPercent, double CommitGb, double CommitLimitGb,
    double PhysicalTotalGb, double AvailableGb, int ProcessCount, int ThreadCount, int HandleCount);
internal sealed record ProcessPoint(DateTime TimeUtc, int Pid, string Name, string Category, string Owner, bool Unresolved,
    double PrivateMb, double WorkingSetMb, double CpuPercent, double ReadMbPerSec, double WriteMbPerSec, double PageFaultsPerSec);
internal sealed record LifecycleEvent(DateTime TimeUtc, string Type, int Pid, string Name, string Category, string Owner, bool Unresolved);
internal sealed record RunEvent(DateTime TimeUtc, DateTime BootUtc, string RunId);
internal sealed record AggregatePoint(DateTime TimeUtc, string Label, double MemoryMb, double CpuPercent, bool Unresolved);
