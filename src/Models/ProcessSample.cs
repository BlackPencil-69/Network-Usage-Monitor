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
