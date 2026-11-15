using System.Text.Json.Nodes;
using Mazda.Application.Modules.Recordings;

namespace Mazda.Infrastructure.Runner;

/// <summary>
/// Scrubs a failure-forensics bundle before it leaves the runner: sensitive
/// query values in URLs and sensitive keys in request / response bodies get
/// the same treatment recordings get. Headers are never captured at all.
/// If the bundle can't be parsed or redacted, the network section is dropped
/// rather than stored unscrubbed.
/// </summary>
internal static class ForensicsRedactor
{
    public static string Redact(string json)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null) return json;

            try
            {
                Url(root, "url");
                if (root["network"] is JsonArray net)
                {
                    foreach (var item in net.OfType<JsonObject>())
                    {
                        Url(item, "url");
                        Body(item, "reqBody");
                        // HTML documents can carry tokens in inline scripts or
                        // hidden inputs, and key-based redaction only understands
                        // JSON / form bodies. The page itself is in the DOM
                        // artifact (password values blanked), so drop the body.
                        if (item["type"] is JsonValue t && t.TryGetValue<string>(out var type) && type == "document")
                            item.Remove("body");
                        else
                            Body(item, "body");
                    }
                }
                if (root["inflight"] is JsonArray inflight)
                {
                    for (var i = 0; i < inflight.Count; i++)
                    {
                        var s = inflight[i]?.GetValue<string>();
                        if (s is null) continue;
                        var sp = s.IndexOf(' ');
                        inflight[i] = sp < 0 ? s : s[..(sp + 1)] + UrlTemplater.RedactQuery(s[(sp + 1)..]);
                    }
                }
            }
            catch
            {
                root.Remove("network");
                root.Remove("inflight");
                root["redactionError"] = "network section dropped: redaction failed";
            }
            return root.ToJsonString();
        }
        catch
        {
            return "{\"redactionError\":\"forensics dropped: bundle could not be parsed\"}";
        }
    }

    private static void Url(JsonObject o, string key)
    {
        if (o[key] is JsonValue v && v.TryGetValue<string>(out var s)) o[key] = UrlTemplater.RedactQuery(s);
    }

    private static void Body(JsonObject o, string key)
    {
        if (o[key] is JsonValue v && v.TryGetValue<string>(out var s)) o[key] = PayloadSanitizer.RedactBody(s);
    }
}
