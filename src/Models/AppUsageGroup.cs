namespace NetworkUsageMonitor.Models;

public sealed class ResolvedProcessInfo
{
    public int Pid { get; init; }
    public required string ProcessName { get; init; }
    public required string GroupKey { get; init; }
    public required string DisplayName { get; init; }
    public string? ExecutablePath { get; init; }
}

public sealed class LiveConnectionInfo
{
    public required string Protocol { get; init; }
    public required string RemoteAddress { get; init; }
    public int RemotePort { get; init; }
    public int LocalPort { get; init; }
    public string? ResolvedHost { get; init; }
    public string? OrgHint { get; init; }
}
