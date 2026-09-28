using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiGoofish.Launcher.Platform.Windows;

internal static class PortableSetupBrowserClient
{
    internal static async Task<string> CreateUrlAsync(
        HttpClient client, string managementUrl, string instanceId, string setupToken,
        CancellationToken cancellationToken = default)
    {
        var origin = new Uri(managementUrl, UriKind.Absolute);
        if (origin.Scheme != "http" || origin.Host != "127.0.0.1" || origin.AbsolutePath != "/" ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
        {
            throw new InvalidOperationException("首次设置仅支持本机管理地址。");
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "internal/setup-ticket"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setupToken);
        request.Headers.Add("X-Goofish-Instance-Id", instanceId);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict) return managementUrl;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ticket", out var value) ||
            value.ValueKind != JsonValueKind.String || value.GetString() is not { } ticket ||
            !Regex.IsMatch(ticket, "^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException("首次设置授权响应无效。");
        }
        // Only an expiring one-time ticket reaches the browser; never the setup/control token.
        return new Uri(origin, "setup#ticket=" + ticket).AbsoluteUri;
    }
}
