using System.Diagnostics;
using System.Net;

using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Win32;

namespace NetworkUsageMonitor.Monitoring;

public sealed class EtwNetworkMonitor : INetworkMonitor
{
    private const string SessionName = "NetworkUsageMonitor-Kernel";

    private readonly NetworkAdapterResolver _adapterResolver;
    private readonly object _syncRoot = new();

    private TraceEventSession? _session;
    private Task? _processingTask;
    private volatile bool _disposed;

    private CancellationTokenSource? _resumeCts;

    public event Action<RawTrafficEvent>? TrafficObserved;

    public EtwNetworkMonitor(NetworkAdapterResolver adapterResolver)
    {
        _adapterResolver = adapterResolver;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public static bool IsElevated() => TraceEventSession.IsElevated() ?? false;

    public void Start()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            StartCore();
        }
    }

    public void Restart()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            StopAndCleanupCore();
            StartCore();
        }
    }

    private void StartCore()
    {
        if (!IsElevated())
        {
            throw new InvalidOperationException(
                "Reading per-process network traffic requires the app to run as Administrator.");
        }

        try
        {
            using var oldSession = new TraceEventSession(SessionName);
            oldSession.Stop(true);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[EtwNetworkMonitor] Failed to stop previous session '{SessionName}': {ex.Message}");
        }

        var session = new TraceEventSession(SessionName) { StopOnDispose = true };
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

        var kernel = session.Source.Kernel;

        // NOTE ON ADAPTER ATTRIBUTION:
        // The Kernel-Network provider does not stamp these events with an adapter
        // IfIndex, so we identify the adapter from the *local* endpoint of the
        // connection instead: on a Send, the local machine is the source ("saddr");
        // on a Recv, the local machine is the destination ("daddr"). That address is
        // handed to NetworkAdapterResolver, which maps it back to a friendly adapter
        // name using the live unicast-address table for every NIC on the box.
        //
        // NOTE ON REMOTE ADDRESS:
        // The remote endpoint is simply the opposite side: on a Send it is "daddr",
        // on a Recv it is "saddr". It is forwarded in RawTrafficEvent so the
        // aggregator can attribute traffic to a country.
        kernel.TcpIpSend += data => Emit(data.ProcessID, data.size, isSend: true, data.TimeStamp, data.saddr, data.daddr);
        kernel.TcpIpRecv += data => Emit(data.ProcessID, data.size, isSend: false, data.TimeStamp, data.daddr, data.saddr);
        kernel.TcpIpSendIPV6 += data => Emit(data.ProcessID, data.size, isSend: true, data.TimeStamp, data.saddr, data.daddr);
        kernel.TcpIpRecvIPV6 += data => Emit(data.ProcessID, data.size, isSend: false, data.TimeStamp, data.daddr, data.saddr);

        kernel.UdpIpSend += data => Emit(data.ProcessID, data.size, isSend: true, data.TimeStamp, data.saddr, data.daddr);
        kernel.UdpIpRecv += data => Emit(data.ProcessID, data.size, isSend: false, data.TimeStamp, data.daddr, data.saddr);
        kernel.UdpIpSendIPV6 += data => Emit(data.ProcessID, data.size, isSend: true, data.TimeStamp, data.saddr, data.daddr);
        kernel.UdpIpRecvIPV6 += data => Emit(data.ProcessID, data.size, isSend: false, data.TimeStamp, data.daddr, data.saddr);

        _session = session;
        _processingTask = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    session.Source.Process();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[EtwNetworkMonitor] ETW Process() loop terminated with error: {ex}");
                }
            },
            TaskCreationOptions.LongRunning);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || _disposed)
        {
            return;
        }

        CancellationToken token;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _resumeCts?.Cancel();
            _resumeCts?.Dispose();
            _resumeCts = new CancellationTokenSource();
            token = _resumeCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2500, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested && !_disposed)
                {
                    Restart();
                }
            }
            catch (OperationCanceledException)
            {
                // Перезапуск скасовано через нову подію PowerModes.Resume або закриття монітора.
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[EtwNetworkMonitor] Failed to restart ETW session after sleep resume: {ex}");
            }
        });
    }

    private void Emit(
        int pid,
        long bytes,
        bool isSend,
        DateTime timestamp,
        IPAddress? localAddress,
        IPAddress? remoteAddress)
    {
        if (bytes <= 0 || pid <= 0)
        {
            return;
        }

        var adapterName = _adapterResolver.ResolveAdapterName(localAddress);
        TrafficObserved?.Invoke(new RawTrafficEvent(pid, bytes, isSend, timestamp, adapterName, remoteAddress));
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            try
            {
                _session?.Stop();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[EtwNetworkMonitor] Error stopping session: {ex.Message}");
            }
        }
    }

    private void StopAndCleanupCore()
    {
        var sessionToDispose = _session;
        var taskToWait = _processingTask;

        _session = null;
        _processingTask = null;

        try
        {
            sessionToDispose?.Stop();
        }
        catch
        {
            // Ignore errors when stopping a potentially broken session.
        }

        try
        {
            sessionToDispose?.Dispose();
        }
        catch
        {
            // Ignore disposal exceptions on broken kernel handles.
        }

        if (taskToWait is not null && !taskToWait.IsCompleted)
        {
            try
            {
                taskToWait.Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // Best-effort wait before re-creating the session.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        lock (_syncRoot)
        {
            _resumeCts?.Cancel();
            _resumeCts?.Dispose();
            _resumeCts = null;

            StopAndCleanupCore();
        }
    }
}