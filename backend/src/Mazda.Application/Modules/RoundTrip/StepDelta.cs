using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mazda.Application.Modules.Recordings;

namespace Mazda.Application.Modules.RoundTrip;

/// <summary>One request a step caused, reduced to what can be compared across runs.</summary>
/// <param name="Key">"METHOD /templated/path" (ids in the path replaced).</param>
/// <param name="Fields">Body flattened to path → scalar text (JSON carried inside strings opened up).</param>
/// <param name="BodyComplete">False when the captured body was clipped: missing fields then prove nothing.</param>
public sealed record RequestObservation(string Key, IReadOnlyDictionary<string, string> Fields, bool BodyComplete);

/// <summary>
/// What one step changed, in application-agnostic terms. The same shape is
/// built for a demonstrated span of the recording and for a replayed step,
/// so the two can be compared.
/// </summary>
/// <param name="Requests">Mutating requests (POST/PUT/PATCH/DELETE) in the order they were sent.</param>
/// <param name="Dom">DOM change signatures: "+tag|name" appeared, "-tag|text" disappeared,
/// "~text" text that became visible, "@attr=value" a meaningful attribute change.
/// CSS-class / style churn and positions are not part of a signature.</param>
/// <param name="Texts">Normalized texts that appeared or changed: used to find where a chosen value shows up.</param>
public sealed record StepDelta(
    IReadOnlyList<RequestObservation> Requests,
    IReadOnlyList<string> Dom,
    IReadOnlySet<string> Texts)
{
    public static readonly StepDelta Empty = new(Array.Empty<RequestObservation>(), Array.Empty<string>(), new HashSet<string>());

    public bool HasDomChange => Dom.Count > 0;
}

/// <summary>
/// Builds a <see cref="StepDelta"/> from raw recorder output: the "mutations"
/// summaries the page-side recorder emits and the network records. Used for
/// both sides, demonstration and replay, so they are reduced identically.
/// </summary>
public static class DeltaBuilder
{
    private static readonly HashSet<string> Mutating = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    // Attributes whose changes are styling / framework bookkeeping, not meaning.
    private static readonly Regex IgnoredAttr = new(@"^(class|style|data-v-.*|_ngcontent.*|_nghost.*|ng-reflect-.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsMutating(string? method) => method is not null && Mutating.Contains(method);

    /// <param name="mutations">The <c>data</c> objects of "mutations" events.</param>
    /// <param name="network">Network records: { method, url, reqBody?, status? }.</param>
    public static StepDelta Build(IEnumerable<JsonObject> mutations, IEnumerable<JsonObject> network)
    {
        var dom = new List<string>();
        var texts = new HashSet<string>(StringComparer.Ordinal);

        foreach (var m in mutations)
        {
            foreach (var a in Arr(m["appeared"]))
            {
                var name = Norm(Str(a["name"]) ?? Str(a["text"]) ?? "");
                dom.Add($"+{Str(a["role"]) ?? Str(a["tag"]) ?? "?"}|{name}");
                if (name.Length > 0) texts.Add(name);
                foreach (var o in Arr(a["outline"]))
                    if (Norm(Str(o["text"]) ?? "") is { Length: > 0 } ot) texts.Add(ot);
            }
            foreach (var d in Arr(m["disappeared"]))
                dom.Add($"-{Str(d["tag"]) ?? "?"}|{Norm(Str(d["text"]) ?? "")}");
            foreach (var t in Arr(m["text"]))
            {
                var to = Norm(Str(t["to"]) ?? "");
                if (to.Length == 0) continue;
                dom.Add("~" + to);
                texts.Add(to);
            }
            foreach (var c in Arr(m["changed"]))
            {
                var attr = Str(c["attr"]);
                if (attr is null || IgnoredAttr.IsMatch(attr)) continue;
                var value = Str(c["to"])
                    ?? string.Join(" ", (c["added"] as JsonArray)?.Select(x => x?.ToString() ?? "") ?? Enumerable.Empty<string>());
                dom.Add($"@{attr}={Norm(value)}");
            }
        }

        var requests = new List<RequestObservation>();
        foreach (var n in network)
        {
            var method = Str(n["method"]) ?? "GET";
            if (!IsMutating(method)) continue;
            var key = $"{method.ToUpperInvariant()} {UrlTemplater.Template(Str(n["url"]) ?? Str(n["path"]))}";
            var body = Str(n["reqBody"]) ?? Str(n["req"]);
            var (fields, complete) = Flatten(body);
            requests.Add(new RequestObservation(key, fields, complete));
        }
        return new StepDelta(requests, dom, texts);
    }

    // ── Body flattening ──────────────────────────────────────────────

    private static readonly Regex Pair = new(
        "\"([^\"\\\\]{1,80})\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\"|-?\\d+(?:\\.\\d+)?|true|false|null)",
        RegexOptions.Compiled);

    /// <summary>Body → field path → scalar text. Strict JSON or form when parseable; a tolerant pair scan for a clipped body.</summary>
    public static (IReadOnlyDictionary<string, string> Fields, bool Complete) Flatten(string? body)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(body)) return (map, true);
        var t = body.TrimStart();
        if (t.StartsWith('{') || t.StartsWith('['))
        {
            try
            {
                Walk(JsonNode.Parse(body), "", map, 0);
                return (map, true);
            }
            catch (JsonException)
            {
                var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (Match m in Pair.Matches(body))
                {
                    var key = m.Groups[1].Value;
                    var n = seen[key] = seen.GetValueOrDefault(key) + 1;
                    var val = m.Groups[2].Value;
                    if (val.Length >= 2 && val[0] == '"')
                    {
                        try { val = JsonSerializer.Deserialize<string>(val) ?? ""; }
                        catch (JsonException) { val = val[1..^1]; }
                    }
                    map[n == 1 ? key : $"{key}#{n}"] = val;
                    if (map.Count > 600) break;
                }
                return (map, false);
            }
        }
        if (body.Contains('=') && !body.Contains('\n'))
        {
            foreach (var kv in body.Split('&'))
            {
                var eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                string Dec(string x) { try { return Uri.UnescapeDataString(x.Replace('+', ' ')); } catch (UriFormatException) { return x; } }
                map.TryAdd(Dec(kv[..eq]), Dec(kv[(eq + 1)..]));
            }
            return (map, true);
        }
        map["$body"] = body.Length > 300 ? body[..300] : body;
        return (map, true);
    }

    private static void Walk(JsonNode? node, string path, Dictionary<string, string> map, int depth)
    {
        if (map.Count > 600 || depth > 12) return;
        switch (node)
        {
            case JsonObject o:
                foreach (var kv in o) Walk(kv.Value, path.Length == 0 ? kv.Key : $"{path}.{kv.Key}", map, depth + 1);
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]", map, depth + 1);
                break;
            case JsonValue v:
                if (v.TryGetValue<string>(out var s))
                {
                    var ts = s.TrimStart();
                    if (s.Length > 1 && (ts.StartsWith('{') || ts.StartsWith('[')))
                    {
                        try { Walk(JsonNode.Parse(s), path, map, depth + 1); break; }
                        catch (JsonException) { /* an ordinary string that starts with a brace */ }
                    }
                    map[path] = s;
                }
                else map[path] = v.ToJsonString();
                break;
            case null:
                map[path] = "null";
                break;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────

    public static string Norm(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 80 ? s[..80] : s;
    }

    private static IEnumerable<JsonObject> Arr(JsonNode? n) => (n as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
