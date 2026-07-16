using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetworkUsageMonitor.Monitoring;

/// <summary>
/// Resolves the network adapter a packet travelled over into a clear,
/// human-readable name ("Wi-Fi", "Ethernet", "Cellular", ...).
///
/// The Kernel-Network ETW provider does not stamp TCP/UDP send/receive events
/// with an adapter interface index, so attribution is done by matching each
/// packet's *local* address (the machine's own side of the connection, i.e.
/// `saddr` on a send and `daddr` on a receive) against the current unicast
/// address list of every network adapter on the box. The map is rebuilt
/// whenever Windows reports a network configuration change (Wi-Fi roam,
/// cable plugged in, VPN connect/disconnect, etc.) and on a slow safety-net
/// timer in case a change notification is missed.
/// </summary>
public sealed class NetworkAdapterResolver : IDisposable
{
    private const string UnknownAdapterName = "Unknown";
    private const string LoopbackAdapterName = "Loopback";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private readonly Timer _refreshTimer;
    private Dictionary<IPAddress, string> _addressToAdapter = new();

    public NetworkAdapterResolver()
    {
        Refresh();
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _refreshTimer = new Timer(_ => Refresh(), null, RefreshInterval, RefreshInterval);
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var map = new Dictionary<IPAddress, string>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                var friendlyName = DisplayNameFor(nic);

                IPInterfaceProperties props;
                try
                {
                    props = nic.GetIPProperties();
                }
                catch
                {
                    continue;
                }

                foreach (var unicast in props.UnicastAddresses)
                {
                    var address = unicast.Address;
                    map[address] = friendlyName;

                    // ETW sometimes reports the IPv4-mapped-to-IPv6 form of an address;
                    // register both representations so lookups succeed either way.
                    if (address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        map[address.MapToIPv6()] = friendlyName;
                    }
                }
            }
        }
        catch
        {
            // Best-effort: if enumeration fails, keep serving the previous snapshot
            // rather than clearing everything back to "Unknown".
            return;
        }

        lock (_lock)
        {
            _addressToAdapter = map;
        }
    }

    private static string DisplayNameFor(NetworkInterface nic)
    {
        return nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx => "Ethernet",
            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Cellular",
            _ => nic.Name
        };
    }

    /// <summary>
    /// Resolves the adapter that owns the given local address. Returns "Unknown"
    /// if the address can't be matched to any currently known adapter (e.g. the
    /// interface list hasn't refreshed yet after a very recent network change).
    /// </summary>
    public string ResolveAdapterName(IPAddress? localAddress)
    {
        if (localAddress is null)
        {
            return UnknownAdapterName;
        }

        if (IPAddress.IsLoopback(localAddress))
        {
            return LoopbackAdapterName;
        }

        Dictionary<IPAddress, string> snapshot;
        lock (_lock)
        {
            snapshot = _addressToAdapter;
        }

        if (snapshot.TryGetValue(localAddress, out var name))
        {
            return name;
        }

        if (localAddress.IsIPv4MappedToIPv6 && snapshot.TryGetValue(localAddress.MapToIPv4(), out var mapped))
        {
            return mapped;
        }

        return UnknownAdapterName;
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _refreshTimer.Dispose();
    }
}
