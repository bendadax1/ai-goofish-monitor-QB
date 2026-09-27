using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AiGoofish.Launcher.Platform.Windows;

internal static class LoopbackPeerGuardAcceptance
{
    private sealed class OwnerTableWithPidOverride(int port, uint? ownerPid) : IWindowsTcpConnectionOwnerTable
    {
        private readonly WindowsTcpConnectionOwnerTable _inner = new();
        public bool MatchedConnection { get; private set; }

        public IReadOnlyList<WindowsTcpConnectionOwnerRow> GetEstablishedIpv4Connections()
        {
            var rows = _inner.GetEstablishedIpv4Connections();
            if (ownerPid is null)
            {
                return rows;
            }

            return rows.Select(row =>
            {
                if (row.LocalPort != port || !row.LocalAddress.Equals(IPAddress.Loopback) || !row.RemoteAddress.Equals(IPAddress.Loopback))
                {
                    return row;
                }

                MatchedConnection = true;
                return row with { ProcessId = ownerPid.Value };
            }).ToArray();
        }
    }

    private sealed class FixedOwnerTable(IReadOnlyList<WindowsTcpConnectionOwnerRow>? rows, bool throws = false) : IWindowsTcpConnectionOwnerTable
    {
        public IReadOnlyList<WindowsTcpConnectionOwnerRow> GetEstablishedIpv4Connections() =>
            throws ? throw new IOException("synthetic TCP table failure") : rows ?? [];
    }

    public static async Task<int> RunAsync()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var instanceId = Guid.NewGuid();
            var identity = new OwnedProcessIdentity(
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
                process.MainModule?.FileName ?? throw new IOException("无法读取 loopback guard 测试进程路径。"),
                Path.GetTempPath(),
                instanceId,
                Guid.NewGuid());

            await VerifyTupleAndFailureCasesAsync(identity);

            var (blockedBytes, wrongOwnerMatched) = await SendAgainstSyntheticListenerAsync(identity, instanceId, wrongOwner: true);
            Assert(wrongOwnerMatched, "错误PID测试必须确认 owner 表已命中本次 accepted tuple。");
            Assert(blockedBytes.Length == 0, "错误PID的合成监听器不应收到任何 HTTP 字节或控制令牌。");
            Console.WriteLine("PASS wrong owner PID is rejected before HTTP bytes");

            var (acceptedBytes, _) = await SendAgainstSyntheticListenerAsync(identity, instanceId, wrongOwner: false);
            Assert(Encoding.ASCII.GetString(acceptedBytes).Contains("Authorization: Bearer ", StringComparison.Ordinal),
                "正确 PID 的本机合成服务应收到 Authorization Bearer 请求头。");
            Console.WriteLine("PASS exact current-process owner can receive control request");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"LOOPBACK_PEER_GUARD_FAILED={error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static async Task VerifyTupleAndFailureCasesAsync(OwnedProcessIdentity identity)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var accepted = await listener.AcceptSocketAsync();
        var local = (IPEndPoint)client.LocalEndPoint!;
        var remote = (IPEndPoint)client.RemoteEndPoint!;
        var row = new WindowsTcpConnectionOwnerRow(remote.Address, remote.Port, local.Address, local.Port, checked((uint)identity.ProcessId));

        new WindowsLoopbackPeerVerifier(new FixedOwnerTable([row]), (_, _) => true).Verify(client, port, identity);
        VerifyRejected("missing row", []);
        VerifyRejected("duplicate rows", [row, row]);
        VerifyRejected("zero PID", [row with { ProcessId = 0 }]);
        VerifyRejected("wildcard listener tuple", [row with { LocalAddress = IPAddress.Any }]);
        VerifyRejected("non-loopback peer tuple", [row with { RemoteAddress = IPAddress.Parse("192.0.2.1") }]);
        VerifyRejected("PID reuse identity mismatch", [row], (_, _) => false);
        VerifyRejected("owner table API error", null, throws: true);

        using var ipv6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        AssertThrows<IOException>(() => new WindowsLoopbackPeerVerifier(new FixedOwnerTable([row]), (_, _) => true).Verify(ipv6, port, identity),
            "IPv6 control socket must be rejected.");
        Console.WriteLine("PASS tuple ambiguity, wildcard, non-loopback, PID reuse, API error, and IPv6 fail closed");

        void VerifyRejected(
            string name,
            IReadOnlyList<WindowsTcpConnectionOwnerRow>? rows,
            Func<uint, OwnedProcessIdentity, bool>? processMatches = null,
            bool throws = false)
        {
            AssertThrows<IOException>(() => new WindowsLoopbackPeerVerifier(new FixedOwnerTable(rows, throws), processMatches ?? ((_, _) => true))
                .Verify(client, port, identity), $"{name}必须拒绝。");
        }
    }

    private static async Task<(byte[] Bytes, bool OwnerTupleMatched)> SendAgainstSyntheticListenerAsync(
        OwnedProcessIdentity identity,
        Guid instanceId,
        bool wrongOwner)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var receiveTask = ReceiveAsync(listener, deadline.Token);
        var fakeTable = new OwnerTableWithPidOverride(port, wrongOwner ? checked((uint)identity.ProcessId + 1) : null);
        var verifier = new WindowsLoopbackPeerVerifier(fakeTable);
        using var client = new PythonLoopbackControlClient(port, TimeSpan.FromSeconds(2), verifier);
        var response = await client.CheckReadinessAsync(
            identity,
            instanceId,
            new string('G', 48),
            "fixture",
            "maintenance",
            1,
            1,
            deadline.Token);
        var bytes = await receiveTask;
        if (wrongOwner)
        {
            Assert(response.State is PythonReadinessState.Unreachable, "错误服务端 PID 必须让就绪请求 fail closed。");
        }
        else
        {
            Assert(response.State is PythonReadinessState.Rejected, "合成 401 响应应证明正确 PID 的请求抵达服务端。");
        }

        return (bytes, fakeTable.MatchedConnection);
    }

    private static async Task<byte[]> ReceiveAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var peer = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = peer.GetStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
            if (buffer.Length >= 4)
            {
                var text = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (text.StartsWith("GET ", StringComparison.Ordinal))
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 401 Unauthorized\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"), cancellationToken);
                    }
                    break;
                }
            }
        }

        return buffer.ToArray();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
