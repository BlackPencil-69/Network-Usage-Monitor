namespace NetworkUsageMonitor.Models;

public sealed class ProcessCounters
{
    public int Pid { get; init; }
    public long BytesSent;
    public long BytesReceived;
}

public sealed class UsageBucket
{
    public required DateTimeOffset Timestamp { get; init; }
    public int Pid { get; init; }
    public required string ProcessName { get; init; }
    public string? ExecutablePath { get; init; }
    public long BytesSent { get; init; }
    public long BytesReceived { get; init; }

    /// <summary>
    /// Human-readable name of the network adapter this traffic was observed on
    /// (e.g. "Wi-Fi", "Ethernet", "Cellular"). Replaces the old cost-based
    /// NetworkInterfaceCategory classification entirely.
    /// </summary>
    public required string AdapterName { get; init; }

    public required string SessionId { get; init; }
}

/// <summary>
/// Трафік одного процесу з однією країною за один інтервал збереження (15 с).
/// </summary>
public sealed class CountryUsageBucket
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string ProcessName { get; init; }

    /// <summary>ISO-код країни в нижньому регістрі ("ua", "us", "de") або "unk".</summary>
    public required string CountryCode { get; init; }

    public long BytesSent { get; init; }
    public long BytesReceived { get; init; }

    /// <summary>Кількість унікальних віддалених IP за інтервал.</summary>
    public int ConnectionCount { get; init; }

    public required string AdapterName { get; init; }
    public required string SessionId { get; init; }
}