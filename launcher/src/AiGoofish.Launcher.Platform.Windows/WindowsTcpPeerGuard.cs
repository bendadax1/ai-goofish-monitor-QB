using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AiGoofish.Launcher.Platform.Windows;

internal sealed record WindowsTcpConnectionOwnerRow(
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress RemoteAddress,
    int RemotePort,
    uint ProcessId);

internal interface IWindowsTcpConnectionOwnerTable
{
    IReadOnlyList<WindowsTcpConnectionOwnerRow> GetEstablishedIpv4Connections();
}

internal sealed class WindowsTcpConnectionOwnerTable : IWindowsTcpConnectionOwnerTable
{
    private const uint ErrorInsufficientBuffer = 122;
    private const int AddressFamilyIpv4 = 2;
    private const int TcpTableOwnerPidConnections = 4;
    private const uint TcpStateEstablished = 5;
    private const int TcpRowSize = 24;

    public IReadOnlyList<WindowsTcpConnectionOwnerRow> GetEstablishedIpv4Connections()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint size = 0;
            var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AddressFamilyIpv4, TcpTableOwnerPidConnections, 0);
            if (result is not ErrorInsufficientBuffer || size < sizeof(uint))
            {
                throw new IOException("无法读取 Windows IPv4 TCP 连接所有者表。");
            }

            var table = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                result = GetExtendedTcpTable(table, ref size, false, AddressFamilyIpv4, TcpTableOwnerPidConnections, 0);
                if (result is ErrorInsufficientBuffer)
                {
                    continue;
                }
                if (result != 0 || size < sizeof(uint))
                {
                    throw new IOException("无法读取 Windows IPv4 TCP 连接所有者表。");
                }

                var count = unchecked((uint)Marshal.ReadInt32(table));
                if (count > (size - sizeof(uint)) / TcpRowSize)
                {
                    throw new IOException("Windows IPv4 TCP 连接所有者表长度无效。");
                }

                var rows = new List<WindowsTcpConnectionOwnerRow>();
                for (var index = 0U; index < count; index++)
                {
                    var rowPointer = IntPtr.Add(table, checked(sizeof(uint) + (int)index * TcpRowSize));
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPointer);
                    if (row.State != TcpStateEstablished)
                    {
                        continue;
                    }

                    rows.Add(new WindowsTcpConnectionOwnerRow(
                        AddressFromNative(row.LocalAddress),
                        PortFromNetworkOrder(row.LocalPort),
                        AddressFromNative(row.RemoteAddress),
                        PortFromNetworkOrder(row.RemotePort),
                        row.ProcessId));
                }

                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }
        }

        throw new IOException("Windows IPv4 TCP 连接表持续变化，无法安全读取。");
    }

    private static IPAddress AddressFromNative(uint address) => new(BitConverter.GetBytes(address));

    private static int PortFromNetworkOrder(uint port) =>
        unchecked((ushort)IPAddress.NetworkToHostOrder(unchecked((short)(port & 0xffff))));

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint ProcessId;
    }
}

internal sealed class WindowsLoopbackPeerVerifier
{
    private static readonly IPAddress Loopback = IPAddress.Loopback;
    private readonly IWindowsTcpConnectionOwnerTable _ownerTable;
    private readonly Func<uint, OwnedProcessIdentity, bool> _processIdentityMatches;

    public WindowsLoopbackPeerVerifier(
        IWindowsTcpConnectionOwnerTable? ownerTable = null,
        Func<uint, OwnedProcessIdentity, bool>? processIdentityMatches = null)
    {
        _ownerTable = ownerTable ?? new WindowsTcpConnectionOwnerTable();
        _processIdentityMatches = processIdentityMatches ?? ProcessIdentityMatches;
    }

    public void Verify(Socket connectedSocket, int expectedServerPort, OwnedProcessIdentity expectedIdentity)
    {
        if (connectedSocket.AddressFamily is not AddressFamily.InterNetwork ||
            connectedSocket.LocalEndPoint is not IPEndPoint local ||
            connectedSocket.RemoteEndPoint is not IPEndPoint remote ||
            !local.Address.Equals(Loopback) ||
            !remote.Address.Equals(Loopback) ||
            remote.Port != expectedServerPort ||
            expectedIdentity.ProcessId <= 0 ||
            expectedIdentity.InstanceId == Guid.Empty ||
            expectedIdentity.RunId == Guid.Empty)
        {
            throw new IOException("Python 控制连接不是预期的 IPv4 loopback 会话。");
        }

        IReadOnlyList<WindowsTcpConnectionOwnerRow> rows;
        try
        {
            rows = _ownerTable.GetEstablishedIpv4Connections();
        }
        catch (Exception exception)
        {
            throw new IOException("无法证明 Python 控制连接的服务端进程身份。", exception);
        }

        var matches = rows.Where(row =>
            row.LocalAddress.Equals(remote.Address) &&
            row.LocalPort == remote.Port &&
            row.RemoteAddress.Equals(local.Address) &&
            row.RemotePort == local.Port).ToArray();
        if (matches.Length != 1 || matches[0].ProcessId == 0 ||
            matches[0].ProcessId != (uint)expectedIdentity.ProcessId ||
            !_processIdentityMatches(matches[0].ProcessId, expectedIdentity))
        {
            throw new IOException("Python 控制连接服务端不属于本次受管进程，已在发送凭据前中止。");
        }
    }

    private static bool ProcessIdentityMatches(uint processId, OwnedProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            if (process.HasExited || process.Id != identity.ProcessId)
            {
                return false;
            }

            var startTime = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var executablePath = process.MainModule?.FileName;
            return startTime.UtcTicks == identity.StartedAtUtc.UtcTicks &&
                !string.IsNullOrWhiteSpace(executablePath) &&
                string.Equals(Path.GetFullPath(executablePath), Path.GetFullPath(identity.ExecutablePath), StringComparison.OrdinalIgnoreCase) &&
                !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
