using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;

using NetworkUsageMonitor.Models;
using NetworkUsageMonitor.Persistence;

namespace NetworkUsageMonitor.Monitoring;

public sealed class LiveProcessSpeed
{
    public required ResolvedProcessInfo Process { get; init; }
    public long BytesSentPerSecond { get; init; }
    public long BytesReceivedPerSecond { get; init; }
}

public sealed class UsageAggregator : IDisposable
{
    private static readonly TimeSpan LiveInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StaleProcessEviction = TimeSpan.FromMinutes(10);

    // Єдине стабільне значення SessionId на весь час роботи процесу,
    // обчислене як округлений до хвилини Unix-час завантаження системи.
    private static readonly string ProcessSessionId =
        ((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Environment.TickCount64 / 1000) / 60)
        .ToString(CultureInfo.InvariantCulture);

    private static readonly object LogLock = new();

    private readonly INetworkMonitor _monitor;
    private readonly ProcessInfoResolver _resolver;
    private readonly SqliteUsageRepository _repository;
    private readonly GeoIpResolver _geoIpResolver;
    private readonly string _sessionId;
    private readonly string _crashLogPath;

    // Live counters drive the tray flyout's real-time per-second speeds and are
    // not split by adapter (nothing in the UI needs that breakdown live).
    private ConcurrentDictionary<int, ProcessCounters> _liveCounters = new();

    // Persisted counters ARE split by adapter, since the dashboard's history,
    // chart, heatmap, and app totals all need to be filterable per network.
    private ConcurrentDictionary<(int Pid, string AdapterName), ProcessCounters> _persistCounters = new();

    // Країнова статистика: ключ (Pid, адаптер, країна). Накопичується паралельно з _persistCounters
    // і скидається в БД тим самим 15-секундним таймером.
    private ConcurrentDictionary<(int Pid, string AdapterName, string CountryCode), CountryCounters> _countryCounters = new();

    private Timer? _liveTimer;
    private Timer? _persistTimer;
    private int _isFlushingPersist;

    public event Action<IReadOnlyList<LiveProcessSpeed>>? LiveSampleReady;

    public string SessionId => _sessionId;

    public UsageAggregator(
        INetworkMonitor monitor,
        ProcessInfoResolver resolver,
        SqliteUsageRepository repository,
        GeoIpResolver geoIpResolver,
        string? dataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(geoIpResolver);

        _monitor = monitor;
        _resolver = resolver;
        _repository = repository;
        _geoIpResolver = geoIpResolver;
        _sessionId = ComputeSessionId();

        var baseDir = !string.IsNullOrWhiteSpace(dataDirectory)
            ? dataDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkUsageMonitor");

        _crashLogPath = Path.Combine(baseDir, "crash.log");

        _monitor.TrafficObserved += OnTraffic;
    }

    public void Start()
    {
        _monitor.Start();
        _liveTimer = new Timer(_ => FlushLive(), null, LiveInterval, LiveInterval);
        _persistTimer = new Timer(_ => FlushPersist(), null, PersistInterval, PersistInterval);
    }

    private void OnTraffic(RawTrafficEvent e)
    {
        AddToLive(e);
        AddToPersist(e);
        AddToCountry(e);
    }

    private void AddToLive(RawTrafficEvent e)
    {
        var counters = _liveCounters.GetOrAdd(e.Pid, pid => new ProcessCounters { Pid = pid });
        Accumulate(counters, e);
    }

    private void AddToPersist(RawTrafficEvent e)
    {
        var key = (e.Pid, e.AdapterName);
        var counters = _persistCounters.GetOrAdd(key, k => new ProcessCounters { Pid = k.Pid });
        Accumulate(counters, e);
    }

    private void AddToCountry(RawTrafficEvent e)
    {
        var remote = e.RemoteAddress;
        if (remote is null)
        {
            return;
        }

        // Бінарний пошук по масивах у пам'яті — безпечно викликати прямо з потоку ETW.
        var country = _geoIpResolver.ResolveCountryCode(remote);

        // Приватні / локальні адреси в країнову статистику не потрапляють.
        if (country is null || country == GeoIpResolver.LocalCode)
        {
            return;
        }

        var key = (e.Pid, e.AdapterName, country);
        var counters = _countryCounters.GetOrAdd(key, static _ => new CountryCounters());

        if (e.IsSend)
        {
            Interlocked.Add(ref counters.BytesSent, e.Bytes);
        }
        else
        {
            Interlocked.Add(ref counters.BytesReceived, e.Bytes);
        }

        // HashSet не потокобезпечний, а FlushPersist читає його з потоку таймера.
        lock (counters.SyncRoot)
        {
            counters.RemoteAddresses.Add(ToAddressKey(remote));
        }
    }

    /// <summary>
    /// Ключ віддаленої адреси для підрахунку унікальних з'єднань.
    /// IPv4 (і IPv4-mapped IPv6) — це просто uint, нативний IPv6 — повні 128 біт,
    /// тож один і той самий хост не рахується двічі, а IPv6 не губиться.
    /// </summary>
    private static UInt128 ToAddressKey(IPAddress ip)
    {
        Span<byte> buffer = stackalloc byte[16];
        if (!ip.TryWriteBytes(buffer, out var written))
        {
            return default;
        }

        if (written == 4)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(buffer);
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(12, 4));
        }

        return BinaryPrimitives.ReadUInt128BigEndian(buffer);
    }

    private static void Accumulate(ProcessCounters counters, RawTrafficEvent e)
    {
        if (e.IsSend)
        {
            Interlocked.Add(ref counters.BytesSent, e.Bytes);
        }
        else
        {
            Interlocked.Add(ref counters.BytesReceived, e.Bytes);
        }
    }

    private void FlushLive()
    {
        try
        {
            var snapshot = Interlocked.Exchange(ref _liveCounters, new ConcurrentDictionary<int, ProcessCounters>());
            if (snapshot.IsEmpty)
            {
                LiveSampleReady?.Invoke(Array.Empty<LiveProcessSpeed>());
                return;
            }

            var speeds = new List<LiveProcessSpeed>(snapshot.Count);
            foreach (var (pid, counters) in snapshot)
            {
                speeds.Add(new LiveProcessSpeed
                {
                    Process = _resolver.Resolve(pid),
                    BytesSentPerSecond = counters.BytesSent,
                    BytesReceivedPerSecond = counters.BytesReceived
                });
            }

            LiveSampleReady?.Invoke(speeds);
        }
        catch (Exception ex)
        {
            LogCrash("FlushLive", ex);
        }
    }

    private void FlushPersist()
    {
        if (Interlocked.CompareExchange(ref _isFlushingPersist, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var snapshot = Interlocked.Exchange(
                ref _persistCounters,
                new ConcurrentDictionary<(int Pid, string AdapterName), ProcessCounters>());

            var countrySnapshot = Interlocked.Exchange(
                ref _countryCounters,
                new ConcurrentDictionary<(int Pid, string AdapterName, string CountryCode), CountryCounters>());

            if (snapshot.IsEmpty && countrySnapshot.IsEmpty)
            {
                return;
            }

            var timestamp = DateTimeOffset.UtcNow;

            // Один pid може зустрічатися в обох знімках і з кількома країнами — резолвимо його лише раз за флаш.
            var resolved = new Dictionary<int, ResolvedProcessInfo>();

            // Збій запису однієї таблиці не повинен губити дані іншої — тому окремі try/catch.
            try
            {
                FlushUsage(snapshot, timestamp, resolved);
            }
            catch (Exception ex)
            {
                LogCrash("FlushPersist.Usage", ex);
            }

            try
            {
                FlushCountries(countrySnapshot, timestamp, resolved);
            }
            catch (Exception ex)
            {
                LogCrash("FlushPersist.Country", ex);
            }

            _resolver.EvictStale(StaleProcessEviction);
        }
        catch (Exception ex)
        {
            LogCrash("FlushPersist", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _isFlushingPersist, 0);
        }
    }

    private void FlushUsage(
        ConcurrentDictionary<(int Pid, string AdapterName), ProcessCounters> snapshot,
        DateTimeOffset timestamp,
        Dictionary<int, ResolvedProcessInfo> resolved)
    {
        if (snapshot.IsEmpty)
        {
            return;
        }

        var buckets = new List<UsageBucket>(snapshot.Count);

        foreach (var (key, counters) in snapshot)
        {
            var info = ResolveCached(resolved, key.Pid);
            buckets.Add(new UsageBucket
            {
                Timestamp = timestamp,
                Pid = key.Pid,
                ProcessName = info.DisplayName,
                ExecutablePath = info.ExecutablePath,
                BytesSent = counters.BytesSent,
                BytesReceived = counters.BytesReceived,
                AdapterName = key.AdapterName,
                SessionId = _sessionId
            });
        }

        _repository.InsertBatch(buckets);
    }

    private void FlushCountries(
        ConcurrentDictionary<(int Pid, string AdapterName, string CountryCode), CountryCounters> countrySnapshot,
        DateTimeOffset timestamp,
        Dictionary<int, ResolvedProcessInfo> resolved)
    {
        if (countrySnapshot.IsEmpty)
        {
            return;
        }

        // CountryUsageBucket не містить Pid, тож зливаємо різні pid з однаковим ім'ям процесу
        // (кілька вікон/воркерів одного застосунку) в один рядок. Підсумок унікальних IP
        // по pid не перетинається між процесами, тому ConnectionCount просто додається.
        var merged = new Dictionary<(string Process, string Adapter, string Country), (long Sent, long Received, int Connections)>();

        foreach (var (key, counters) in countrySnapshot)
        {
            var info = ResolveCached(resolved, key.Pid);

            int connections;
            lock (counters.SyncRoot)
            {
                connections = counters.RemoteAddresses.Count;
            }

            var mergeKey = (info.DisplayName, key.AdapterName, key.CountryCode);
            merged.TryGetValue(mergeKey, out var acc);

            merged[mergeKey] = (
                acc.Sent + Interlocked.Read(ref counters.BytesSent),
                acc.Received + Interlocked.Read(ref counters.BytesReceived),
                acc.Connections + connections);
        }

        var countryBuckets = new List<CountryUsageBucket>(merged.Count);
        foreach (var (key, acc) in merged)
        {
            countryBuckets.Add(new CountryUsageBucket
            {
                Timestamp = timestamp,
                ProcessName = key.Process,
                CountryCode = key.Country,
                BytesSent = acc.Sent,
                BytesReceived = acc.Received,
                ConnectionCount = acc.Connections,
                AdapterName = key.Adapter,
                SessionId = _sessionId
            });
        }

        _repository.InsertCountryBatch(countryBuckets);
    }

    private ResolvedProcessInfo ResolveCached(Dictionary<int, ResolvedProcessInfo> cache, int pid)
    {
        if (!cache.TryGetValue(pid, out var info))
        {
            info = _resolver.Resolve(pid);
            cache[pid] = info;
        }

        return info;
    }

    public static string ComputeSessionId() => ProcessSessionId;

    private void LogCrash(string operation, Exception ex)
    {
        var message = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [UsageAggregator.{operation}] {ex}{Environment.NewLine}";
        Trace.WriteLine(message);

        try
        {
            lock (LogLock)
            {
                var dir = Path.GetDirectoryName(_crashLogPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(_crashLogPath, message);
            }
        }
        catch
        {
            // Ignore logging I/O failures so the timer thread never crashes.
        }
    }

    public void Dispose()
    {
        _liveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _liveTimer?.Dispose();
        _liveTimer = null;

        _persistTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _persistTimer?.Dispose();
        _persistTimer = null;

        _monitor.TrafficObserved -= OnTraffic;

        SpinWait.SpinUntil(() => Volatile.Read(ref _isFlushingPersist) == 0, TimeSpan.FromSeconds(5));

        FlushPersist();
        _monitor.Dispose();
    }

    /// <summary>
    /// Лічильники одного ключа (Pid, адаптер, країна) за поточний 15-секундний інтервал.
    /// </summary>
    private sealed class CountryCounters
    {
        public long BytesSent;
        public long BytesReceived;

        public readonly object SyncRoot = new();
        public readonly HashSet<UInt128> RemoteAddresses = new();
    }
}