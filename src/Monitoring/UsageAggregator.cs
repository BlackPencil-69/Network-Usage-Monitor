using System.Collections.Concurrent;

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

    private readonly INetworkMonitor _monitor;
    private readonly ProcessInfoResolver _resolver;
    private readonly SqliteUsageRepository _repository;
    private readonly string _sessionId;

    // Live counters drive the tray flyout's real-time per-second speeds and are
    // not split by adapter (nothing in the UI needs that breakdown live).
    private ConcurrentDictionary<int, ProcessCounters> _liveCounters = new();

    // Persisted counters ARE split by adapter, since the dashboard's history,
    // chart, heatmap, and app totals all need to be filterable per network.
    private ConcurrentDictionary<(int Pid, string AdapterName), ProcessCounters> _persistCounters = new();

    private Timer? _liveTimer;
    private Timer? _persistTimer;

    public event Action<IReadOnlyList<LiveProcessSpeed>>? LiveSampleReady;

    public string SessionId => _sessionId;

    public UsageAggregator(
        INetworkMonitor monitor,
        ProcessInfoResolver resolver,
        SqliteUsageRepository repository)
    {
        _monitor = monitor;
        _resolver = resolver;
        _repository = repository;
        _sessionId = ComputeSessionId();

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

    private void FlushPersist()
    {
        var snapshot = Interlocked.Exchange(
            ref _persistCounters,
            new ConcurrentDictionary<(int Pid, string AdapterName), ProcessCounters>());

        if (snapshot.IsEmpty)
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var buckets = new List<UsageBucket>(snapshot.Count);

        foreach (var (key, counters) in snapshot)
        {
            var info = _resolver.Resolve(key.Pid);
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
        _resolver.EvictStale(StaleProcessEviction);
    }

    private static string ComputeSessionId()
    {
        var approximateBootTime = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return approximateBootTime.ToString("yyyyMMddHHmm");
    }

    public void Dispose()
    {
        _liveTimer?.Dispose();
        _persistTimer?.Dispose();
        _monitor.TrafficObserved -= OnTraffic;
        FlushPersist();
        _monitor.Dispose();
    }
}
