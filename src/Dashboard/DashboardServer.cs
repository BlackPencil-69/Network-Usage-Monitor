using System.IO;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

using NetworkUsageMonitor.Monitoring;
using NetworkUsageMonitor.Persistence;
using NetworkUsageMonitor.Settings;

namespace NetworkUsageMonitor.Dashboard;

public sealed class DashboardServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly SqliteUsageRepository _repository;
    private readonly UsageAggregator _aggregator;
    private readonly SettingsStore _settingsStore;
    private readonly int _port;
    private readonly object _liveLock = new();
    private Dictionary<string, List<int>> _liveGroupPids = new();
    private CancellationTokenSource? _cts;
    private readonly GeoIpResolver _geoIpResolver;

    public DashboardServer(
        SqliteUsageRepository repository,
        UsageAggregator aggregator,
        SettingsStore settingsStore,
        GeoIpResolver geoIpResolver,
        int port)
    {
        _repository = repository;
        _aggregator = aggregator;
        _settingsStore = settingsStore;
        _geoIpResolver = geoIpResolver;
        _port = port;

        _aggregator.LiveSampleReady += OnLiveSample;
    }

    private void OnLiveSample(IReadOnlyList<LiveProcessSpeed> speeds)
    {
        var map = new Dictionary<string, List<int>>();
        foreach (var speed in speeds)
        {
            if (!map.TryGetValue(speed.Process.GroupKey, out var list))
            {
                list = new List<int>();
                map[speed.Process.GroupKey] = list;
            }
            list.Add(speed.Process.Pid);
        }

        lock (_liveLock)
        {
            _liveGroupPids = map;
        }
    }

    public void OpenInBrowser()
    {
        Start();
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{_port}/") { UseShellExecute = true });
    }

    public void Start()
    {
        if (_listener.IsListening)
        {
            return;
        }

        _listener.Prefixes.Clear();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoop(_cts.Token));
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                continue;
            }

            _ = Task.Run(() => HandleSafely(context));
        }
    }

    private async Task HandleSafely(HttpListenerContext context)
    {
        try
        {
            await Route(context);
        }
        catch (Exception ex)
        {
            try
            {
                context.Response.StatusCode = 500;
                await WriteText(context.Response, ex.Message);
            }
            catch
            {
                // Client likely disconnected; nothing more we can do.
            }
        }
    }

    private async Task Route(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var query = context.Request.QueryString;

        switch (path)
        {
            case "/":
                await WriteEmbedded(context.Response, "index.html", "text/html; charset=utf-8");
                return;
            case "/css/dashboard.css":
                await WriteEmbedded(context.Response, "css.dashboard.css", "text/css; charset=utf-8");
                return;
            case "/js/dashboard.js":
                await WriteEmbedded(context.Response, "js.dashboard.js", "application/javascript; charset=utf-8");
                return;
            case "/api/apps":
                await HandleApps(context.Response, query);
                return;
            case "/api/timeline":
                await HandleTimeline(context.Response, query);
                return;
            case "/api/heatmap":
                await HandleHeatmap(context.Response, query);
                return;
            case "/api/adapters":
                await WriteJson(context.Response, _repository.GetKnownAdapters());
                return;
            case "/api/connections":
                await HandleConnections(context.Response, query);
                return;
            case "/api/reveal" when context.Request.HttpMethod == "POST":
                await HandleReveal(context);
                return;
            case "/api/export":
                await HandleExport(context.Response, query);
                return;
            default:
                context.Response.StatusCode = 404;
                await WriteText(context.Response, "Not found");
                return;
        }
    }

    private static (DateTimeOffset From, DateTimeOffset To, string? Session, string? Adapter) ParseRange(
        System.Collections.Specialized.NameValueCollection query)
    {
        var range = query["range"] ?? "day";
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset from;

        if (query["from"] is { Length: > 0 } fromStr && DateTimeOffset.TryParse(fromStr, out var parsedFrom))
        {
            from = parsedFrom;
        }
        else
        {
            from = range switch
            {
                "week" => now.AddDays(-7),
                "month" => now.AddMonths(-1),
                "year" => now.AddYears(-1),
                "all" => DateTimeOffset.UnixEpoch,
                _ => now.AddDays(-1)
            };
        }

        DateTimeOffset to = query["to"] is { Length: > 0 } toStr && DateTimeOffset.TryParse(toStr, out var parsedTo)
            ? parsedTo
            : now;

        var session = query["session"] == "true" ? ComputeCurrentSessionId() : null;

        var adapter = query["adapter"];
        if (string.IsNullOrWhiteSpace(adapter) || string.Equals(adapter, "all", StringComparison.OrdinalIgnoreCase))
        {
            adapter = null;
        }

        return (from, to, session, adapter);
    }

    private static string ComputeCurrentSessionId()
    {
        var approximateBootTime = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return approximateBootTime.ToString("yyyyMMddHHmm");
    }

    private async Task HandleApps(HttpListenerResponse response, System.Collections.Specialized.NameValueCollection query)
    {
        var (from, to, session, adapter) = ParseRange(query);
        var totals = _repository.GetAppTotals(from, to, session, adapter);

        var payload = totals.Select(t => new
        {
            processName = t.ProcessName,
            executablePath = t.ExecPath,
            bytesSent = t.BytesSent,
            bytesReceived = t.BytesReceived,
            totalBytes = t.BytesSent + t.BytesReceived,
            iconDataUrl = ProcessIconExtractor.GetPngDataUrl(t.ExecPath)
        });

        await WriteJson(response, payload);
    }

    private async Task HandleTimeline(HttpListenerResponse response, System.Collections.Specialized.NameValueCollection query)
    {
        var (from, to, session, adapter) = ParseRange(query);
        var bucketSeconds = query["granularity"] switch
        {
            "hour" => 3600,
            "day" => 86400,
            _ => 300
        };

        var points = _repository.GetTimeline(from, to, bucketSeconds, session, adapter);
        var payload = points.Select(p => new
        {
            timestamp = p.BucketStart.ToUnixTimeSeconds(),
            bytesSent = p.BytesSent,
            bytesReceived = p.BytesReceived
        });

        await WriteJson(response, payload);
    }

    private async Task HandleHeatmap(HttpListenerResponse response, System.Collections.Specialized.NameValueCollection query)
    {
        var (from, to, _, adapter) = ParseRange(query);
        var cells = _repository.GetHeatmap(from, to, adapter);
        await WriteJson(response, cells.Select(c => new { dayOfWeek = c.DayOfWeek, hour = c.Hour, totalBytes = c.TotalBytes }));
    }

    private async Task HandleConnections(HttpListenerResponse response, System.Collections.Specialized.NameValueCollection query)
    {
        var group = query["group"];
        List<int> pids;
        lock (_liveLock)
        {
            pids = group is not null && _liveGroupPids.TryGetValue(group, out var list)
                ? new List<int>(list)
                : new List<int>();
        }

        var pidSet = new HashSet<int>(pids);

        // --- О heroe, рятуємо наш монітор від торент-сміття! ---
        if (pidSet.Count == 0)
        {
            // Якщо додаток зараз не запущений, миттєво віддаємо порожній масив
            await WriteJson(response, Array.Empty<object>());
            return;
        }

        var tcp = NativeNetworkTables.GetTcpConnectionsByPid(pidSet);
        var udp = NativeNetworkTables.GetUdpEndpointsByPid(pidSet);
        var connections = tcp.Concat(udp).ToList();

        // Отримуємо країни миттєво (з кешу або запускаємо фоновий запит)
        var payload = connections.Select(c => new
        {
            protocol = c.Connection.Protocol,
            remoteAddress = c.Connection.RemoteAddress,
            remotePort = c.Connection.RemotePort,
            localPort = c.Connection.LocalPort,
            countryCode = _geoIpResolver.ResolveCountryCode(c.Connection.RemoteAddress)
        });

        await WriteJson(response, payload);
    }

    private async Task HandleReveal(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream);
        var body = await reader.ReadToEndAsync();

        string? execPath;
        try
        {
            var doc = JsonDocument.Parse(body);
            execPath = doc.RootElement.GetProperty("executablePath").GetString();
        }
        catch (JsonException)
        {
            context.Response.StatusCode = 400;
            await WriteText(context.Response, "Malformed request body");
            return;
        }

        if (string.IsNullOrWhiteSpace(execPath) || !Path.IsPathRooted(execPath) || !File.Exists(execPath))
        {
            context.Response.StatusCode = 400;
            await WriteText(context.Response, "executablePath must be an absolute path to an existing file");
            return;
        }

        // Explorer has its own quirky command-line parser: "/select," and the
        // (auto-quoted) path need to be separate ArgumentList entries, which
        // avoids any shell/string-concatenation injection risk entirely.
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add("/select,");
        psi.ArgumentList.Add(execPath);
        Process.Start(psi);

        await WriteJson(context.Response, new { ok = true });
    }

    private async Task HandleExport(HttpListenerResponse response, System.Collections.Specialized.NameValueCollection query)
    {
        var (from, to, _, _) = ParseRange(query);
        var format = query["format"] ?? "json";
        var rows = _repository.ExportRaw(from, to).ToList();

        if (format == "csv")
        {
            var sb = new StringBuilder();
            sb.AppendLine("timestamp,process_name,bytes_sent,bytes_received,adapter_name");
            foreach (var row in rows)
            {
                sb.AppendLine($"{row.Timestamp},{EscapeCsv(row.ProcessName)},{row.Sent},{row.Received},{EscapeCsv(row.AdapterName)}");
            }

            response.ContentType = "text/csv; charset=utf-8";
            response.AddHeader("Content-Disposition", "attachment; filename=\"network-usage-export.csv\"");
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.OutputStream.Close();
            return;
        }

        await WriteJson(response, rows.Select(r => new
        {
            timestamp = r.Timestamp,
            processName = r.ProcessName,
            bytesSent = r.Sent,
            bytesReceived = r.Received,
            adapterName = r.AdapterName
        }));
    }

    private static string EscapeCsv(string value) =>
        value.Contains(',') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    private static async Task WriteEmbedded(HttpListenerResponse response, string relativeResourceName, string contentType)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"NetworkUsageMonitor.Dashboard.wwwroot.{relativeResourceName}";
        await using var stream = assembly.GetManifestResourceStream(resourceName);

        if (stream is null)
        {
            response.StatusCode = 404;
            await WriteText(response, $"Missing embedded resource: {resourceName}");
            return;
        }

        response.ContentType = contentType;
        response.ContentLength64 = stream.Length;
        await stream.CopyToAsync(response.OutputStream);
        response.OutputStream.Close();
    }

    private static async Task WriteJson<T>(HttpListenerResponse response, T payload)
    {
        var json = JsonSerializer.Serialize(payload);
        response.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.OutputStream.Close();
    }

    private static async Task WriteText(HttpListenerResponse response, string text)
    {
        response.ContentType = "text/plain; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.OutputStream.Close();
    }

    public void Dispose()
    {
        _aggregator.LiveSampleReady -= OnLiveSample;
        _cts?.Cancel();
        if (_listener.IsListening)
        {
            _listener.Stop();
        }
        _listener.Close();
    }
}
