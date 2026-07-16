using System.Net;

using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetworkUsageMonitor.Monitoring;

public sealed class EtwNetworkMonitor : INetworkMonitor
{
    private const string SessionName = "NetworkUsageMonitor-Kernel";

    private readonly NetworkAdapterResolver _adapterResolver;

    private TraceEventSession? _session;
    private Task? _processingTask;
    private volatile bool _disposed;

    public event Action<RawTrafficEvent>? TrafficObserved;

    public EtwNetworkMonitor(NetworkAdapterResolver adapterResolver)
    {
        _adapterResolver = adapterResolver;
    }

    public static bool IsElevated() => TraceEventSession.IsElevated() ?? false;

    public void Start()
    {
        if (!IsElevated())
        {
            throw new InvalidOperationException(
                "Reading per-process network traffic requires the app to run as Administrator.");
        }

        using (var oldSession = new TraceEventSession(SessionName))
        {
            oldSession.Stop(true);
        }

        _session = new TraceEventSession(SessionName) { StopOnDispose = true };
        _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

        var kernel = _session.Source.Kernel;

        // NOTE ON ADAPTER ATTRIBUTION:
        // The Kernel-Network provider does not stamp these events with an adapter
        // IfIndex, so we identify the adapter from the *local* endpoint of the
        // connection instead: on a Send, the local machine is the source ("saddr");
        // on a Recv, the local machine is the destination ("daddr"). That address is
        // handed to NetworkAdapterResolver, which maps it back to a friendly adapter
        // name using the live unicast-address table for every NIC on the box.
        kernel.TcpIpSend += data => Emit(data.ProcessID, data.size, isSend: true, data.TimeStamp, data.saddr);
        kernel.TcpIpRecv += data => Emit(data.ProcessID, data.size, isSend: false, data.TimeStamp, data.daddr);
        kernel.TcpIpSendIPV6 += data => Emit(data.ProcessID, data.size, isSend: true, data.TimeStamp, data.saddr);
        kernel.TcpIpRecvIPV6 += data => Emit(data.ProcessID, data.size, isSend: false, data.TimeStamp, data.daddr);

        kernel.UdpIpSend += data => Emit(data.ProcessID, data.size, isSend: true, data.TimeStamp, data.saddr);
        kernel.UdpIpRecv += data => Emit(data.ProcessID, data.size, isSend: false, data.TimeStamp, data.daddr);
        kernel.UdpIpSendIPV6 += data => Emit(data.ProcessID, data.size, isSend: true, data.TimeStamp, data.saddr);
        kernel.UdpIpRecvIPV6 += data => Emit(data.ProcessID, data.size, isSend: false, data.TimeStamp, data.daddr);

        _processingTask = Task.Factory.StartNew(
            () => _session.Source.Process(),
            TaskCreationOptions.LongRunning);
    }

    private void Emit(int pid, long bytes, bool isSend, DateTime timestamp, IPAddress? localAddress)
    {
        if (bytes <= 0 || pid <= 0)
        {
            return;
        }

        var adapterName = _adapterResolver.ResolveAdapterName(localAddress);
        TrafficObserved?.Invoke(new RawTrafficEvent(pid, bytes, isSend, timestamp, adapterName));
    }

    public void Stop()
    {
        _session?.Stop();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session?.Dispose();

        try
        {
            _processingTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort shutdown: the process is exiting regardless.
        }
    }
}
