using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mazda.Application.Modules.Recordings;

/// <summary>
/// Turns concrete URLs into route templates so episodes generalise
/// (<c>/datasets/8f2c…/rows/17</c> → <c>/datasets/{id}/rows/{id}</c>) and
/// strips values of sensitive query parameters.
/// </summary>
public static class UrlTemplater
{
    private static readonly Regex Uuid = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Numeric = new(@"^\d+$", RegexOptions.Compiled);
    private static readonly Regex LongHex = new("^[0-9a-f]{12,}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Long opaque tokens: base64-ish ids, slugs with embedded hashes, JWTs.
    private static readonly Regex OpaqueToken = new(@"^(?=.*\d)[A-Za-z0-9_\-.]{20,}$", RegexOptions.Compiled);

    internal static readonly Regex SensitiveKey = new(
        @"pass(word)?|passwd|secret|token|auth|api[-_]?key|session|cookie|signature|^sig$|^code$|otp|ssn|card|cvv|cvc|^pin$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsIdLike(string seg)
        => Numeric.IsMatch(seg) || Uuid.IsMatch(seg) || LongHex.IsMatch(seg) || OpaqueToken.IsMatch(seg);

    /// <summary>Path + templated query, without scheme/host. Returns the input on parse failure.</summary>
    public static string Template(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!TrySplit(url, out var path, out var query)) return url;

        var segs = path.Split('/').Select(s => s.Length > 0 && IsIdLike(Uri.UnescapeDataString(s)) ? "{id}" : s);
        var templatedPath = string.Join('/', segs);
        if (string.IsNullOrEmpty(query)) return templatedPath;

        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(kv =>
        {
            var eq = kv.IndexOf('=');
            var key = eq < 0 ? kv : kv[..eq];
            var val = eq < 0 ? "" : kv[(eq + 1)..];
            if (SensitiveKey.IsMatch(Uri.UnescapeDataString(key))) return $"{key}=***";
            if (IsIdLike(Uri.UnescapeDataString(val))) return $"{key}={{id}}";
            return val.Length > 40 ? $"{key}={val[..40]}…" : kv;
        });
        return templatedPath + "?" + string.Join('&', parts);
    }

    /// <summary>Full URL with sensitive query values replaced by <c>***</c>. Keeps everything else verbatim.</summary>
    public static string RedactQuery(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url ?? string.Empty;
        var q = url.IndexOf('?');
        if (q < 0) return url;
        var hash = url.IndexOf('#', q);
        var query = hash < 0 ? url[(q + 1)..] : url[(q + 1)..hash];
        var tail = hash < 0 ? "" : url[hash..];
        var parts = query.Split('&').Select(kv =>
        {
            var eq = kv.IndexOf('=');
            if (eq < 0) return kv;
            var key = kv[..eq];
            return SensitiveKey.IsMatch(Uri.UnescapeDataString(key)) ? $"{key}=***" : kv;
        });
        return url[..(q + 1)] + string.Join('&', parts) + tail;
    }

    public static string? Origin(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")
            ? u.GetLeftPart(UriPartial.Authority)
            : null;
    }

    private static bool TrySplit(string url, out string path, out string query)
    {
        path = url; query = string.Empty;
        string rest;
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
            rest = abs.PathAndQuery;
        else if (url.StartsWith('/'))
            rest = url;
        else
            return false;
        var hash = rest.IndexOf('#');
        if (hash >= 0) rest = rest[..hash];
        var q = rest.IndexOf('?');
        path = q < 0 ? rest : rest[..q];
        query = q < 0 ? string.Empty : rest[(q + 1)..];
        return true;
    }
}

/// <summary>
/// Server-side redaction applied to every recorder event before it is
/// persisted. Defence in depth: the page script already masks sensitive
/// inputs, but request/response bodies and URLs come from the network layer
/// and can carry tokens the page never "typed".
/// </summary>
public static class PayloadSanitizer
{
    private static readonly Regex SensitiveKey = new(
        @"pass(word)?|passwd|secret|token|authorization|api[-_]?key|cookie|session|ssn|card(number)?|cvv|cvc|otp|^pin$|iban",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Redact sensitive fields of an event in place (bodies + URLs).</summary>
    public static void SanitizeEvent(JsonObject evt)
    {
        if (evt["url"] is JsonValue u && u.TryGetValue<string>(out var url))
            evt["url"] = UrlTemplater.RedactQuery(url);

        if (evt["data"] is not JsonObject data) return;
        foreach (var key in new[] { "reqBody", "resBody" })
        {
            if (data[key] is JsonValue v && v.TryGetValue<string>(out var body))
                data[key] = RedactBody(body);
        }
        if (data["url"] is JsonValue du && du.TryGetValue<string>(out var dataUrl))
            data["url"] = UrlTemplater.RedactQuery(dataUrl);
    }

    /// <summary>Redacts JSON or form-encoded bodies by key. Anything else is returned unchanged.</summary>
    public static string RedactBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return body;
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                var node = JsonNode.Parse(body);
                if (node is not null)
                {
                    Redact(node);
                    return node.ToJsonString();
                }
            }
            catch (JsonException)
            {
                // Truncated JSON (the recorder clips bodies) won't parse. Fall
                // back to a regex pass over "key":"value" pairs.
                return Regex.Replace(body,
                    "\"([^\"]{1,60})\"\\s*:\\s*\"[^\"]*\"",
                    m => SensitiveKey.IsMatch(m.Groups[1].Value) ? $"\"{m.Groups[1].Value}\":\"***\"" : m.Value);
            }
        }
        if (body.Contains('=') && !body.Contains(' ') && !body.Contains('\n'))
        {
            return string.Join('&', body.Split('&').Select(kv =>
            {
                var eq = kv.IndexOf('=');
                if (eq < 0) return kv;
                var key = Uri.UnescapeDataString(kv[..eq]);
                return SensitiveKey.IsMatch(key) ? $"{kv[..eq]}=***" : kv;
            }));
        }
        return body;
    }

    private static void Redact(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (SensitiveKey.IsMatch(key) && obj[key] is not JsonObject and not JsonArray)
                        obj[key] = "***";
                    else if (obj[key] is { } child)
                        Redact(child);
                }
                break;
            case JsonArray arr:
                foreach (var child in arr)
                    if (child is not null) Redact(child);
                break;
        }
    }
}

/// <summary>Picks which recorded episode documents go into the catalogue generator's prompt.</summary>
public static class RecordingGrounding
{
    public const int BudgetChars = 80_000;

    /// <summary>
    /// One grounding document per recording: its title, the user's stated
    /// intent, what the analyzer understood, and the episodes — so function
    /// names and descriptions can follow the user's own wording.
    /// </summary>
    public static IReadOnlyList<string> Compose(IReadOnlyList<Mazda.Application.Common.Abstractions.GroundingRecording> newestFirst, int budget = BudgetChars)
        => Select(newestFirst.Select(r =>
            "{\"title\":" + System.Text.Json.JsonSerializer.Serialize(r.Title) +
            (string.IsNullOrWhiteSpace(r.Intent) ? "" : ",\"intent\":" + System.Text.Json.JsonSerializer.Serialize(r.Intent)) +
            (string.IsNullOrWhiteSpace(r.Understanding) ? "" : ",\"understanding\":" + System.Text.Json.JsonSerializer.Serialize(r.Understanding)) +
            ",\"episodes\":" + r.EpisodesJson + "}").ToList(), budget);

    /// <summary>Newest first; the newest is always included, older ones while they fit.</summary>
    public static IReadOnlyList<string> Select(IReadOnlyList<string> newestFirst, int budget = BudgetChars)
    {
        var picked = new List<string>();
        var total = 0;
        foreach (var doc in newestFirst)
        {
            if (string.IsNullOrWhiteSpace(doc)) continue;
            if (picked.Count > 0 && total + doc.Length > budget) continue;
            picked.Add(doc);
            total += doc.Length;
        }
        return picked;
    }
}
