namespace NetworkUsageMonitor.Monitoring;

public readonly record struct RawTrafficEvent(int Pid, long Bytes, bool IsSend, DateTimeOffset Timestamp, string AdapterName);

public interface INetworkMonitor : IDisposable
{
    event Action<RawTrafficEvent>? TrafficObserved;

    void Start();
    void Stop();
}
