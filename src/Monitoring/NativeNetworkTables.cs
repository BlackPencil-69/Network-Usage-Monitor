using System.Net;
using System.Runtime.InteropServices;

using NetworkUsageMonitor.Models;

namespace NetworkUsageMonitor.Monitoring;

public static class NativeNetworkTables
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable, ref int size, bool sort, int ipVersion, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr udpTable, ref int size, bool sort, int ipVersion, int tableClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    public static IReadOnlyList<(int Pid, LiveConnectionInfo Connection)> GetTcpConnectionsByPid(
        IReadOnlySet<int> pidFilter)
    {
        var results = new List<(int, LiveConnectionInfo)>();
        int bufferSize = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AfInet, TcpTableOwnerPidAll, 0);
        if (bufferSize <= 0)
        {
            return results;
        }

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            var ret = GetExtendedTcpTable(buffer, ref bufferSize, false, AfInet, TcpTableOwnerPidAll, 0);
            if (ret != 0)
            {
                return results;
            }

            int rowCount = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, 4);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();

            for (int i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(rowPtr, i * rowSize));
                var pid = (int)row.OwningPid;
                if (pidFilter.Count > 0 && !pidFilter.Contains(pid))
                {
                    continue;
                }

                results.Add((pid, new LiveConnectionInfo
                {
                    Protocol = "TCP",
                    RemoteAddress = new IPAddress(row.RemoteAddr).ToString(),
                    RemotePort = SwapPort(row.RemotePort),
                    LocalPort = SwapPort(row.LocalPort)
                }));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return results;
    }

    public static IReadOnlyList<(int Pid, LiveConnectionInfo Connection)> GetUdpEndpointsByPid(
        IReadOnlySet<int> pidFilter)
    {
        var results = new List<(int, LiveConnectionInfo)>();
        int bufferSize = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref bufferSize, false, AfInet, UdpTableOwnerPid, 0);
        if (bufferSize <= 0)
        {
            return results;
        }

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            var ret = GetExtendedUdpTable(buffer, ref bufferSize, false, AfInet, UdpTableOwnerPid, 0);
            if (ret != 0)
            {
                return results;
            }

            int rowCount = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, 4);
            int rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();

            for (int i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(IntPtr.Add(rowPtr, i * rowSize));
                var pid = (int)row.OwningPid;
                if (pidFilter.Count > 0 && !pidFilter.Contains(pid))
                {
                    continue;
                }

                results.Add((pid, new LiveConnectionInfo
                {
                    Protocol = "UDP",
                    RemoteAddress = "*",
                    RemotePort = 0,
                    LocalPort = SwapPort(row.LocalPort)
                }));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return results;
    }

    private static int SwapPort(uint rawPort)
    {
        return ((int)rawPort >> 8 & 0xFF) | ((int)rawPort & 0xFF) << 8;
    }
}
