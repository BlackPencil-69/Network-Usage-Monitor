using System.IO;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
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
    /// <summary>Некоректний параметр запиту - перетворюється на HTTP 400.</summary>
    private sealed class RequestValidationException : Exception
    {
        public RequestValidationException(string message) : base(message) { }
    }

    private const int AllAppsLimit = 100_000;

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
                if (token.IsCancellationRequested || !_listener.IsListening)
                {
                    return;
                }

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
        catch (RequestValidationException ex)
        {
            try
            {
                context.Response.StatusCode = 400;
                await WriteText(context.Response, ex.Message);
            }
            catch
            {
                // Client likely disconnected; nothing more we can do.
            }
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
            case "/api/countries":
                await HandleCountries(context.Response, query);
                return;
            case "/api/months":
                await WriteJson(context.Response, _repository.GetAvailableMonths());
                return;
            case "/api/compare":
                await HandleCompare(context.Response, query);
                return;
            case "/api/app-details":
                await HandleAppDetails(context.Response, query);
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

    private static string? NormalizeFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value.Trim();
    }

    /// <summary>Північ за локальним часом для заданої дати, переведена в UTC (DST враховується для самої дати).</summary>
    private static DateTimeOffset LocalMidnightUtc(int year, int month, int day) =>
        new DateTimeOffset(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Local)).ToUniversalTime();

    /// <summary>
    /// "YYYY-MM" -> [1-ше число 00:00:00; останній день 23:59:59] за локальним часом, у UTC.
    /// </summary>
    private static bool TryGetMonthRange(string? month, out DateTimeOffset from, out DateTimeOffset to)
    {
        from = default;
        to = default;

        if (string.IsNullOrWhiteSpace(month) ||
            !DateTime.TryParseExact(month.Trim(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        try
        {
            var firstLocal = new DateTime(parsed.Year, parsed.Month, 1, 0, 0, 0, DateTimeKind.Local);
            var nextFirstLocal = firstLocal.AddMonths(1);
            from = new DateTimeOffset(firstLocal).ToUniversalTime();
            to = new DateTimeOffset(nextFirstLocal.AddSeconds(-1)).ToUniversalTime();
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private (DateTimeOffset From, DateTimeOffset To, string? Session, string? Adapter, string? Country) ParseRange(
        NameValueCollection query)
    {
        var range = query["range"] ?? "day";
        var now = DateTimeOffset.UtcNow;
        var localNow = DateTimeOffset.Now;
        DateTimeOffset from;
        DateTimeOffset to = now;

        switch (range)
        {
            case "today":
                // "That Day": поточна календарна доба з 00:00 за локальним часом.
                from = LocalMidnightUtc(localNow.Year, localNow.Month, localNow.Day);
                break;
            case "week":
                from = now.AddDays(-7);
                break;
            case "this_week":
            {
                // "That Week": з понеділка 00:00 (локально). DayOfWeek: Sunday=0 -> зсув (d + 6) % 7 від понеділка.
                var monday = localNow.Date.AddDays(-(((int)localNow.DayOfWeek + 6) % 7));
                from = LocalMidnightUtc(monday.Year, monday.Month, monday.Day);
                break;
            }
            case "30d":
            case "month":
                from = now.AddDays(-30);
                break;
            case "calendar_month":
                // "This Month": з 1-го числа поточного місяця 00:00 (локально).
                from = LocalMidnightUtc(localNow.Year, localNow.Month, 1);
                break;
            case "year":
                from = now.AddDays(-365);
                break;
            case "this_year":
                // "That Year": з 1 січня поточного року 00:00 (локально).
                from = LocalMidnightUtc(localNow.Year, 1, 1);
                break;
            case "custom_month":
                if (!TryGetMonthRange(query["month"], out from, out to))
                {
                    throw new RequestValidationException("Parameter 'month' must be in YYYY-MM format (e.g. 2026-09).");
                }
                break;
            case "all":
                from = DateTimeOffset.UnixEpoch;
                break;
            default: // "day" - останні 24 години
                from = now.AddHours(-24);
                break;
        }

        // Явні from/to мають пріоритет над будь-яким range.
        if (query["from"] is { Length: > 0 } fromStr && DateTimeOffset.TryParse(fromStr, out var parsedFrom))
        {
            from = parsedFrom;
        }

        if (query["to"] is { Length: > 0 } toStr && DateTimeOffset.TryParse(toStr, out var parsedTo))
        {
            to = parsedTo;
        }

        var session = query["session"] == "true" ? _aggregator.SessionId : null;
        var adapter = NormalizeFilter(query["adapter"]);
        var country = NormalizeFilter(query["country"]);

        return (from, to, session, adapter, country);
    }

    private async Task HandleApps(HttpListenerResponse response, NameValueCollection query)
    {
        var (from, to, session, adapter, country) = ParseRange(query);
        var totals = _repository.GetAppTotals(from, to, session, adapter, countryCode: country);

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

    /// <summary>Розмір бакета: явний granularity має пріоритет, інакше підбираємо за довжиною діапазону.</summary>
    private static int PickBucketSeconds(NameValueCollection query, TimeSpan span) =>
        query["granularity"] switch
        {
            "hour" => 3600,
            "day" => 86400,
            _ when span <= TimeSpan.FromDays(2) => 300,     // day, today: 5 хв
            _ when span <= TimeSpan.FromDays(14) => 1800,   // week, this_week: 30 хв
            _ when span <= TimeSpan.FromDays(62) => 3600,   // 30d, calendar_month, custom_month: 1 год
            _ => 86400                                      // year, this_year, all: 1 доба
        };

    /// <summary>
    /// Zero-filling: суцільна сітка часових точок, порожні кроки = 0 байт.
    /// Правий край обрізається поточним моментом, щоб не малювати «майбутні» нулі
    /// (наприклад, для custom_month поточного місяця). Без даних повертає порожній список.
    /// </summary>
    private static List<object> BuildZeroFilledTimeline(
        IReadOnlyList<TimelinePoint> points, DateTimeOffset from, DateTimeOffset to, int bucketSeconds)
    {
        // Індексуємо наявні бакети за вирівняним unix-часом (суму додаємо на випадок дублікатів).
        var buckets = new Dictionary<long, (long Sent, long Received)>();
        foreach (var p in points)
        {
            var key = p.BucketStart.ToUnixTimeSeconds() / bucketSeconds * bucketSeconds;
            buckets.TryGetValue(key, out var current);
            buckets[key] = (current.Sent + p.BytesSent, current.Received + p.BytesReceived);
        }

        var result = new List<object>();
        if (buckets.Count == 0)
        {
            return result;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var effectiveTo = to > nowUtc ? nowUtc : to;

        // Для "all" (from = 1970) стартуємо з першого реального бакета, а не з епохи Unix.
        var effectiveFromUnix = from <= DateTimeOffset.UnixEpoch
            ? buckets.Keys.Min()
            : from.ToUnixTimeSeconds();

        var startBucket = effectiveFromUnix / bucketSeconds * bucketSeconds;
        var endBucket = effectiveTo.ToUnixTimeSeconds() / bucketSeconds * bucketSeconds;

        for (var t = startBucket; t <= endBucket; t += bucketSeconds)
        {
            buckets.TryGetValue(t, out var b);
            result.Add(new
            {
                timestamp = t,
                bytesSent = b.Sent,
                bytesReceived = b.Received
            });
        }

        return result;
    }

    private async Task HandleTimeline(HttpListenerResponse response, NameValueCollection query)
    {
        var (from, to, session, adapter, country) = ParseRange(query);
        var bucketSeconds = PickBucketSeconds(query, to - from);

        var points = _repository.GetTimeline(from, to, bucketSeconds, session, adapter, country);

        // Даних немає взагалі - віддаємо порожній масив, щоб UI показав "No data for this range yet".
        await WriteJson(response, BuildZeroFilledTimeline(points, from, to, bucketSeconds));
    }

    private async Task HandleHeatmap(HttpListenerResponse response, NameValueCollection query)
    {
        var (from, to, _, adapter, country) = ParseRange(query);
        var cells = _repository.GetHeatmap(from, to, adapter, country);
        await WriteJson(response, cells.Select(c => new { dayOfWeek = c.DayOfWeek, hour = c.Hour, totalBytes = c.TotalBytes }));
    }

    private static object ToCountryPayload(CountryStat c) => new
    {
        countryCode = c.CountryCode,
        bytesSent = c.BytesSent,
        bytesReceived = c.BytesReceived,
        totalBytes = c.BytesSent + c.BytesReceived,
        totalConnections = c.TotalConnections,
        connectionCount = c.TotalConnections
    };

    private async Task HandleCountries(HttpListenerResponse response, NameValueCollection query)
    {
        // Фільтр country тут свідомо ігнорується: ендпоінт сам і є розбивкою за країнами.
        var (from, to, session, adapter, _) = ParseRange(query);
        var processName = NormalizeFilter(query["name"]);

        int limit = int.TryParse(query["limit"], out var parsedLimit) ? Math.Clamp(parsedLimit, 1, 500) : 50;

        var countries = _repository.GetCountryTotals(from, to, session, adapter, processName)
            .Take(limit)
            .Select(ToCountryPayload);

        await WriteJson(response, countries);
    }

    /// <summary>Скільки календарних діб (локально) місяця вже минуло; для минулих місяців - усі, для майбутніх - 0.</summary>
    private static int CountElapsedDays(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var effectiveTo = toUtc < nowUtc ? toUtc : nowUtc;
        var days = (effectiveTo.ToLocalTime().Date - fromUtc.ToLocalTime().Date).Days + 1;
        return Math.Max(0, days);
    }

    private static object BuildMonthSummary(
        string month, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<AppTotal> apps)
    {
        long upload = apps.Sum(a => a.BytesSent);
        long download = apps.Sum(a => a.BytesReceived);
        long total = upload + download;
        int days = CountElapsedDays(from, to);

        return new
        {
            month,
            daysCounted = days,
            downloadBytes = download,
            uploadBytes = upload,
            totalBytes = total,
            avgDailyBytes = days > 0 ? total / days : 0L
        };
    }

    private static double? PercentChange(long baseline, long current) =>
        baseline > 0 ? Math.Round((current - baseline) * 100.0 / baseline, 1) : null;

    private async Task HandleCompare(HttpListenerResponse response, NameValueCollection query)
    {
        var monthA = query["monthA"]?.Trim();
        var monthB = query["monthB"]?.Trim();

        if (!TryGetMonthRange(monthA, out var fromA, out var toA) ||
            !TryGetMonthRange(monthB, out var fromB, out var toB))
        {
            response.StatusCode = 400;
            await WriteText(response, "Parameters 'monthA' and 'monthB' must be in YYYY-MM format (e.g. 2026-09).");
            return;
        }

        var adapter = NormalizeFilter(query["adapter"]);

        var appsA = _repository.GetAppTotals(fromA, toA, null, adapter, AllAppsLimit);
        var appsB = _repository.GetAppTotals(fromB, toB, null, adapter, AllAppsLimit);

        var summaryA = BuildMonthSummary(monthA!, fromA, toA, appsA);
        var summaryB = BuildMonthSummary(monthB!, fromB, toB, appsB);

        long totalA = appsA.Sum(a => a.BytesSent + a.BytesReceived);
        long totalB = appsB.Sum(a => a.BytesSent + a.BytesReceived);
        long downloadA = appsA.Sum(a => a.BytesReceived), downloadB = appsB.Sum(a => a.BytesReceived);
        long uploadA = appsA.Sum(a => a.BytesSent), uploadB = appsB.Sum(a => a.BytesSent);
        int daysA = CountElapsedDays(fromA, toA), daysB = CountElapsedDays(fromB, toB);
        long avgA = daysA > 0 ? totalA / daysA : 0, avgB = daysB > 0 ? totalB / daysB : 0;

        // Об'єднуємо застосунки обох місяців; monthA - базовий період, monthB - порівнюваний.
        var merged = new Dictionary<string, (long A, long B, string? Path)>(StringComparer.Ordinal);
        foreach (var app in appsA)
        {
            merged[app.ProcessName] = (app.BytesSent + app.BytesReceived, 0L, app.ExecPath);
        }

        foreach (var app in appsB)
        {
            long bytes = app.BytesSent + app.BytesReceived;
            merged[app.ProcessName] = merged.TryGetValue(app.ProcessName, out var cur)
                ? (cur.A, bytes, cur.Path ?? app.ExecPath)
                : (0L, bytes, app.ExecPath);
        }

        // Топ-10 за сумарним трафіком двох місяців, щоб потрапили і ті, хто «виріс», і ті, хто «зник».
        var topApps = merged
            .OrderByDescending(kv => kv.Value.A + kv.Value.B)
            .Take(10)
            .Select(kv => new
            {
                processName = kv.Key,
                executablePath = kv.Value.Path,
                iconDataUrl = ProcessIconExtractor.GetPngDataUrl(kv.Value.Path),
                bytesA = kv.Value.A,
                bytesB = kv.Value.B,
                deltaBytes = kv.Value.B - kv.Value.A,
                deltaPercent = PercentChange(kv.Value.A, kv.Value.B)
            })
            .ToList();

        await WriteJson(response, new
        {
            adapter,
            monthA = summaryA,
            monthB = summaryB,
            delta = new
            {
                downloadBytes = downloadB - downloadA,
                uploadBytes = uploadB - uploadA,
                totalBytes = totalB - totalA,
                avgDailyBytes = avgB - avgA,
                totalPercent = PercentChange(totalA, totalB)
            },
            topApps
        });
    }

    private async Task HandleAppDetails(HttpListenerResponse response, NameValueCollection query)
    {
        var name = query["name"];
        if (string.IsNullOrWhiteSpace(name))
        {
            response.StatusCode = 400;
            await WriteText(response, "Parameter 'name' is required.");
            return;
        }

        // country тут не застосовується: топ країн цього застосунку рахується окремо.
        var (from, to, _, adapter, _) = ParseRange(query);
        var bucketSeconds = PickBucketSeconds(query, to - from);

        var details = _repository.GetAppDetails(name, from, to, bucketSeconds, adapter);
        var timeline = BuildZeroFilledTimeline(details.Timeline, from, to, bucketSeconds);

        await WriteJson(response, new
        {
            processName = details.ProcessName,
            totalBytesSent = details.Timeline.Sum(p => p.BytesSent),
            totalBytesReceived = details.Timeline.Sum(p => p.BytesReceived),
            totalBytes = details.Timeline.Sum(p => p.BytesSent + p.BytesReceived),
            timeline,
            peakDay = details.PeakDayBytes > 0
                ? new { date = details.PeakDayDate, bytes = details.PeakDayBytes }
                : null,
            topCountries = details.TopCountries.Select(ToCountryPayload)
        });
    }

    private async Task HandleConnections(HttpListenerResponse response, NameValueCollection query)
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

    private async Task HandleExport(HttpListenerResponse response, NameValueCollection query)
    {
        var (from, to, _, _, _) = ParseRange(query);
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