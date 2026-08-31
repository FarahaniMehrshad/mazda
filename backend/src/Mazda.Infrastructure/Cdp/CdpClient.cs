using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Mazda.Application.Common.Abstractions;
using Microsoft.Extensions.Options;

namespace Mazda.Infrastructure.Cdp;

public class CdpOptions
{
    /// <summary>
    /// Host the API container uses to reach the user's Chrome. Not a per-user
    /// setting — it is a deployment fact. On Docker Desktop the default lets
    /// the Linux container reach the Windows/macOS host.
    /// </summary>
    public string Host { get; set; } = "host.docker.internal";

    /// <summary>How long to wait before calling the browser unreachable.</summary>
    public int ProbeTimeoutSeconds { get; set; } = 5;
}

/// <summary>
/// Talks to Chrome's HTTP debugging endpoints (<c>/json/version</c>,
/// <c>/json/list</c>) so the app can report whether a browser is attachable
/// and enumerate its tabs.
///
/// The one thing that makes this non-trivial: <b>Chrome v111+ rejects any
/// <c>Host:</c> header other than localhost on these endpoints.</b> We reach
/// Chrome at <c>host.docker.internal</c> from inside the container, so the
/// header has to be spoofed back to <c>localhost:&lt;port&gt;</c> or every
/// request 403s and it looks indistinguishable from "no browser running".
/// This is the same workaround the injected <c>__mzConnectViaHost</c> helper
/// performs in TypeScript for the WebSocket side — see the three-problem stack
/// in MAZDA.md.
/// </summary>
public class CdpClient(IHttpClientFactory httpFactory, IOptions<CdpOptions> options) : ICdpClient
{
    public async Task<CdpStatus> ProbeAsync(int port, CancellationToken ct = default)
    {
        var opts = options.Value;
        try
        {
            using var res = await SendAsync(opts.Host, port, "/json/version", opts.ProbeTimeoutSeconds, ct);
            if (!res.IsSuccessStatusCode)
            {
                return CdpStatus.Unavailable(
                    $"Chrome answered on port {port} with HTTP {(int)res.StatusCode}. " +
                    "If this is 403, the browser is refusing the Host header — start Chrome with " +
                    "--remote-debugging-address=0.0.0.0 as well as --remote-debugging-port.");
            }

            var doc = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var browser = doc.TryGetProperty("Browser", out var b) ? b.GetString() : null;
            return CdpStatus.Reachable(browser ?? "unknown");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return CdpStatus.Unavailable(
                $"No response from {opts.Host}:{port} within {opts.ProbeTimeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            // The overwhelmingly common case: Chrome isn't running, or is
            // running without the remote-debugging flags.
            return CdpStatus.Unavailable(
                $"Could not reach a debuggable Chrome at {opts.Host}:{port} ({ex.Message}). " +
                "Start Chrome with --remote-debugging-port=" + port +
                " --remote-debugging-address=0.0.0.0");
        }
    }

    public async Task<IReadOnlyList<CdpTab>> ListTabsAsync(int port, CancellationToken ct = default)
    {
        var opts = options.Value;
        using var res = await SendAsync(opts.Host, port, "/json/list", opts.ProbeTimeoutSeconds, ct);
        res.EnsureSuccessStatusCode();

        var doc = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (doc.ValueKind != JsonValueKind.Array) return Array.Empty<CdpTab>();

        var tabs = new List<CdpTab>();
        foreach (var t in doc.EnumerateArray())
        {
            var type = Str(t, "type");
            // /json/list also reports service workers, iframes, extension
            // backgrounds and the DevTools UI itself. None of those are
            // something a function body can be driven against.
            if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase)) continue;

            var url = Str(t, "url") ?? string.Empty;
            if (url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase)) continue;
            if (url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) continue;

            var id = Str(t, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;

            tabs.Add(new CdpTab(id!, Str(t, "title") ?? string.Empty, url));
        }
        return tabs;
    }

    private async Task<HttpResponseMessage> SendAsync(
        string host, int port, string path, int timeoutSeconds, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("cdp");
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));

        var req = new HttpRequestMessage(HttpMethod.Get, $"http://{host}:{port}{path}");
        // THE critical line — see the class remarks. Without it Chrome 111+
        // rejects the request outright and the UI reports "no browser".
        req.Headers.Host = $"localhost:{port}";
        return await client.SendAsync(req, ct);
    }

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
