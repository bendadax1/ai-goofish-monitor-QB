using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using AiGoofish.Launcher.Platform.Windows;

internal static class HttpFaultCases
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await CheckAsync("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n{", stall: true);
            Console.WriteLine("PASS HTTP response body deadline");
            await CheckAsync("HTTP/1.1 200 OK\r\nContent-Length: 70000\r\n\r\n", stall: false);
            Console.WriteLine("PASS oversized HTTP body rejected");
            await CheckAsync("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/should-not-follow\r\nContent-Length: 2\r\n\r\n{}", stall: false);
            Console.WriteLine("PASS redirect cannot establish readiness");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("HTTP_CONTRACT_FAILED=" + error.GetType().Name);
            return 1;
        }
    }

    private static async Task CheckAsync(string response, bool stall)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var server = ServeAsync(listener, response, stall, lifetime.Token);
        using var client = new PythonLoopbackControlClient(port, TimeSpan.FromMilliseconds(250));
        using var process = Process.GetCurrentProcess();
        var instanceId = Guid.NewGuid();
        var identity = new OwnedProcessIdentity(
            process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
            process.MainModule?.FileName ?? throw new InvalidOperationException("无法读取Python HTTP测试进程路径。"),
            Path.GetTempPath(),
            instanceId,
            Guid.NewGuid());
        try
        {
            var operation = client.CheckReadinessAsync(identity, instanceId, new string('T', 44), "fixture", "maintenance", 1, 1, CancellationToken.None);
            var winner = await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(1.5)));
            if (winner != operation) throw new InvalidOperationException("response timeout was not bounded");
            try
            {
                var result = await operation;
                if (result.State == PythonReadinessState.Ready) throw new InvalidOperationException("untrusted HTTP response accepted");
            }
            catch (OperationCanceledException) when (stall) { }
            catch (PythonControlException) { }
        }
        finally
        {
            lifetime.Cancel();
            listener.Stop();
            try { await server; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    private static async Task ServeAsync(TcpListener listener, string response, bool stall, CancellationToken cancellationToken)
    {
        using var peer = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = peer.GetStream();
        var buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer, cancellationToken);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        if (stall) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
