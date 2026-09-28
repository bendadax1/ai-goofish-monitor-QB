using System.Net;
using AiGoofish.Launcher.Platform.Windows;

internal static class SetupBrowserCases
{
    public static async Task RunAsync(ProcessTestWorkspace _)
    {
        var ticket = new string('t', 43);
        using var handler = new StubHandler();
        using var client = new HttpClient(handler) { MaxResponseContentBufferSize = 4096 };
        handler.Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ticket\":\"" + ticket + "\"}") };
        var url = await PortableSetupBrowserClient.CreateUrlAsync(client, "http://127.0.0.1:18080/", "fixture", "setup-secret");
        Assert(url == "http://127.0.0.1:18080/setup#ticket=" + ticket, "应仅将短时票据放入片段");
        Assert(handler.LastTarget == "http://127.0.0.1:18080/internal/setup-ticket" && handler.LastToken == "setup-secret" && handler.LastInstance == "fixture", "授权必须在请求头中发送给准确实例");
        Assert(!url.Contains("setup-secret"), "设置凭据不得进入浏览器地址");
        foreach (var body in new[] { "{}", "{\"ticket\":null}", "{\"ticket\":\"http://evil.invalid/\"}", "[1]", "broken" })
        {
            handler.Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            await Reject(() => PortableSetupBrowserClient.CreateUrlAsync(client, "http://127.0.0.1:18080/", "fixture", "setup-secret"));
        }
        handler.Reply = _ => new HttpResponseMessage(HttpStatusCode.Conflict);
        Assert(await PortableSetupBrowserClient.CreateUrlAsync(client, "http://127.0.0.1:18080/", "fixture", "setup-secret") == "http://127.0.0.1:18080/", "已初始化应返回正常管理页");
        handler.Reply = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        await Reject(() => PortableSetupBrowserClient.CreateUrlAsync(client, "http://127.0.0.1:18080/", "fixture", "setup-secret"));
        await Reject(() => PortableSetupBrowserClient.CreateUrlAsync(client, "http://evil.invalid/", "fixture", "setup-secret"));
    }

    private static async Task Reject(Func<Task<string>> run)
    {
        try { await run(); }
        catch (Exception) { return; }
        throw new InvalidOperationException("不安全的首次设置响应必须被拒绝");
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Reply { get; set; } = _ => new(HttpStatusCode.OK);
        public string? LastTarget { get; private set; }
        public string? LastToken { get; private set; }
        public string? LastInstance { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastTarget = request.RequestUri?.AbsoluteUri;
            LastToken = request.Headers.Authorization?.Parameter;
            LastInstance = request.Headers.GetValues("X-Goofish-Instance-Id").Single();
            return Task.FromResult(Reply(request));
        }
    }
}
