using System.Text;
using System.Text.Json;

namespace Mazda.Application.Modules.Generations;

/// <summary>
/// Renders a failure-forensics bundle (see the execute harness's
/// <c>__mzCaptureForensics</c>) as compact plain text for an LLM or a human:
/// one <see cref="Report"/> that answers "what failed, where was the page,
/// which requests went wrong" in a single read, and <see cref="Artifact"/>
/// for paging into the big pieces (DOM, full accessibility tree, ...).
/// Pure functions over JSON text — no I/O, tolerant of missing fields.
/// </summary>
public static class ForensicsFormatter
{
    public const int ReportBudget = 14_000;
    public const int DefaultArtifactLimit = 20_000;

    public static readonly IReadOnlyList<string> ArtifactKinds =
        ["meta", "aria", "dom", "network", "console", "fields", "log", "screenshot"];

    // ── Report ───────────────────────────────────────────────────────

    /// <summary>
    /// Everything most debugging needs, within <see cref="ReportBudget"/>.
    /// Works from the bundle alone; the envelope and log only fill gaps
    /// (a run that failed before any page was available has no bundle).
    /// </summary>
    public static string Report(string? forensicsJson, string? envelopeJson = null, string? log = null)
    {
        var sb = new StringBuilder();

        using var bundle = Parse(forensicsJson);
        var root = bundle?.RootElement;

        if (root is { } b)
        {
            var stepId = Str(b, "stepId");
            var call = Str(b, "call");
            sb.AppendLine($"FAILED STEP: {stepId} -> {call}");
            if (b.TryGetProperty("args", out var args)) sb.AppendLine($"ARGS: {Clip(args.GetRawText(), 600)}");
            if (Str(b, "prevStepId") is { } prev) sb.AppendLine($"LAST SUCCESSFUL STEP: {prev}");
            if (b.TryGetProperty("error", out var err))
            {
                sb.AppendLine($"ERROR: {Clip(Str(err, "message"), 900)}");
                var stack = Str(err, "stack");
                if (!string.IsNullOrWhiteSpace(stack))
                    sb.AppendLine("STACK:\n" + Indent(string.Join('\n', stack.Split('\n').Take(8)), "  "));
            }
            sb.AppendLine();

            var page = new StringBuilder($"PAGE: {Str(b, "url")}");
            if (Str(b, "title") is { Length: > 0 } title) page.Append($" - \"{Clip(title, 120)}\"");
            sb.AppendLine(page.ToString());
            var facts = new List<string>();
            if (Str(b, "readyState") is { } rs) facts.Add($"readyState={rs}");
            if (b.TryGetProperty("viewport", out var vp)) facts.Add($"viewport={Num(vp, "w")}x{Num(vp, "h")}");
            if (b.TryGetProperty("scrollY", out var sy) && sy.ValueKind == JsonValueKind.Number) facts.Add($"scrollY={sy.GetInt32()}");
            if (Str(b, "activeElement") is { } ae) facts.Add($"focused={ae}");
            if (facts.Count > 0) sb.AppendLine("  " + string.Join(", ", facts));
            sb.AppendLine();

            AppendNetwork(sb, b, maxRows: 14);
            AppendList(sb, b, "inflight", "IN FLIGHT AT FAILURE", 10, s => Clip(s, 200));
            AppendConsole(sb, b);
            AppendList(sb, b, "pageErrors", "PAGE ERRORS (uncaught exceptions)", 5, s => Clip(s.Split('\n')[0], 300));
            AppendFields(sb, b);

            var aria = Str(b, "aria");
            if (!string.IsNullOrWhiteSpace(aria))
            {
                sb.AppendLine($"ACCESSIBILITY TREE ({aria.Length} chars{(aria.Length > 4000 ? ", first 4000 shown" : "")}):");
                sb.AppendLine(Clip(aria, 4000));
                sb.AppendLine();
            }
            sb.AppendLine("More: artifact kinds " + string.Join(", ", ArtifactKinds)
                + " (full DOM, complete tree, all request bodies, screenshot).");
        }
        else
        {
            // No page snapshot: say so plainly, then fall back to what exists.
            sb.AppendLine("No page snapshot was captured for this run (it failed before a step ran, or the page was unavailable).");
            AppendEnvelopeFailure(sb, envelopeJson);
            if (!string.IsNullOrWhiteSpace(log))
            {
                sb.AppendLine();
                sb.AppendLine("RUNNER LOG (tail):");
                sb.AppendLine(Tail(log, 3500));
            }
        }

        return Clip(Plain(sb.ToString().TrimEnd()), ReportBudget);
    }

    private static void AppendEnvelopeFailure(StringBuilder sb, string? envelopeJson)
    {
        using var env = Parse(envelopeJson);
        if (env is null || !env.RootElement.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Array) return;
        foreach (var s in steps.EnumerateArray())
        {
            if (Str(s, "status") != "failed") continue;
            sb.AppendLine($"FAILED STEP: {Str(s, "id")} -> {Str(s, "call")}");
            if (s.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                sb.AppendLine($"ERROR: {Str(e, "kind")}: {Clip(Str(e, "message"), 900)}");
                if (Str(e, "pageUrl") is { } u) sb.AppendLine($"PAGE: {u}");
                if (Str(e, "stack") is { } st)
                    sb.AppendLine("STACK:\n" + Indent(string.Join('\n', st.Split('\n').Take(8)), "  "));
            }
            return;
        }
    }

    private static void AppendNetwork(StringBuilder sb, JsonElement b, int maxRows)
    {
        if (!b.TryGetProperty("network", out var net) || net.ValueKind != JsonValueKind.Array) return;
        var all = net.EnumerateArray().ToList();
        var total = b.TryGetProperty("networkTotal", out var nt) && nt.ValueKind == JsonValueKind.Number ? nt.GetInt32() : all.Count;
        var bad = all.Where(IsBad).ToList();
        var ok = all.Where(r => !IsBad(r)).TakeLast(Math.Max(0, maxRows - Math.Min(bad.Count, maxRows))).ToList();

        sb.AppendLine($"NETWORK ({total} requests seen; {bad.Count} failed/4xx/5xx; showing problems first):");
        foreach (var r in bad.Take(maxRows)) sb.AppendLine("  " + RequestLine(r, withBodies: true));
        foreach (var r in ok) sb.AppendLine("  " + RequestLine(r, withBodies: false));
        if (all.Count == 0) sb.AppendLine("  (no XHR/fetch/document traffic captured)");
        sb.AppendLine();
    }

    private static void AppendConsole(StringBuilder sb, JsonElement b)
    {
        if (!b.TryGetProperty("console", out var c) || c.ValueKind != JsonValueKind.Array) return;
        var lines = c.EnumerateArray()
            .Where(m => Str(m, "type") is "error" or "warning" or "warn")
            .TakeLast(10)
            .Select(m => $"[{Str(m, "type")}] {Clip(Str(m, "text"), 300)}")
            .ToList();
        if (lines.Count == 0) return;
        sb.AppendLine("CONSOLE (errors/warnings, last 10):");
        foreach (var l in lines) sb.AppendLine("  " + l);
        sb.AppendLine();
    }

    private static void AppendFields(StringBuilder sb, JsonElement b)
    {
        if (!b.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Array) return;
        var rows = f.EnumerateArray()
            .Where(x => !string.IsNullOrEmpty(Str(x, "value")) || (x.TryGetProperty("checked", out var ch) && ch.ValueKind == JsonValueKind.True))
            .Take(15)
            .Select(x => $"{Str(x, "tag")}{(Str(x, "type") is { } t ? $"[{t}]" : "")} {Str(x, "name") ?? Str(x, "id") ?? "?"} = {Clip(Str(x, "value") ?? "", 80)}")
            .ToList();
        if (rows.Count == 0) return;
        sb.AppendLine("FORM FIELDS WITH VALUES:");
        foreach (var r in rows) sb.AppendLine("  " + r);
        sb.AppendLine();
    }

    private static void AppendList(StringBuilder sb, JsonElement b, string prop, string title, int max, Func<string, string> fmt)
    {
        if (!b.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        var items = arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(max).ToList();
        if (items.Count == 0) return;
        sb.AppendLine(title + ":");
        foreach (var i in items) sb.AppendLine("  " + fmt(i));
        sb.AppendLine();
    }

    // ── Artifacts ────────────────────────────────────────────────────

    /// <summary>One piece of the bundle, paged by character offset. Screenshot returns image data.</summary>
    public static ArtifactSlice Artifact(string forensicsJson, string kind, int offset = 0, int limit = DefaultArtifactLimit)
    {
        using var doc = Parse(forensicsJson);
        if (doc is null) return new ArtifactSlice("The stored forensics bundle could not be parsed.", null, false);
        var b = doc.RootElement;
        limit = Math.Clamp(limit <= 0 ? DefaultArtifactLimit : limit, 500, 60_000);
        offset = Math.Max(0, offset);

        switch (kind.ToLowerInvariant())
        {
            case "screenshot":
                var img = Str(b, "screenshotJpegBase64");
                return img is null
                    ? new ArtifactSlice("No screenshot was captured (page unavailable, or the image was too large).", null, false)
                    : new ArtifactSlice("Screenshot of the page at the moment the step failed.", img, true);
            case "meta":
                return Page(MetaText(b), offset, limit);
            case "aria":
                return Page(Str(b, "aria") ?? "(no accessibility tree captured)", offset, limit);
            case "dom":
                return Page(Str(b, "dom") ?? "(no DOM captured)", offset, limit);
            case "console":
                return Page(ConsoleText(b), offset, limit);
            case "fields":
                return Page(b.TryGetProperty("fields", out var f) ? f.GetRawText() : "(no form fields captured)", offset, limit);
            case "network":
                var sb = new StringBuilder();
                if (b.TryGetProperty("network", out var net) && net.ValueKind == JsonValueKind.Array)
                    foreach (var r in net.EnumerateArray()) sb.AppendLine(RequestLine(r, withBodies: true));
                return Page(sb.Length == 0 ? "(no network traffic captured)" : sb.ToString(), offset, limit);
            default:
                return new ArtifactSlice($"Unknown artifact kind '{kind}'. Valid kinds: {string.Join(", ", ArtifactKinds)}.", null, false);
        }
    }

    public sealed record ArtifactSlice(string Text, string? ImageBase64, bool Found);

    private static ArtifactSlice Page(string text, int offset, int limit)
    {
        text = Plain(text);
        if (offset >= text.Length && text.Length > 0)
            return new ArtifactSlice($"(offset {offset} is past the end; total length {text.Length})", null, true);
        var slice = text.Substring(offset, Math.Min(limit, text.Length - offset));
        var end = offset + slice.Length;
        var footer = end < text.Length
            ? $"\n\n[chars {offset}-{end} of {text.Length}; call again with offset={end} for more]"
            : $"\n\n[chars {offset}-{end} of {text.Length}; end]";
        return new ArtifactSlice(slice + footer, null, true);
    }

    private static string MetaText(JsonElement b)
    {
        var sb = new StringBuilder();
        foreach (var k in new[] { "capturedAt", "stepId", "call", "prevStepId", "url", "title", "readyState", "activeElement", "scrollY", "networkTotal" })
            if (b.TryGetProperty(k, out var v)) sb.AppendLine($"{k}: {(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())}");
        if (b.TryGetProperty("viewport", out var vp)) sb.AppendLine($"viewport: {vp.GetRawText()}");
        if (b.TryGetProperty("args", out var a)) sb.AppendLine($"args: {a.GetRawText()}");
        if (b.TryGetProperty("error", out var e)) sb.AppendLine($"error: {e.GetRawText()}");
        return sb.ToString();
    }

    private static string ConsoleText(JsonElement b)
    {
        var sb = new StringBuilder();
        if (b.TryGetProperty("console", out var c) && c.ValueKind == JsonValueKind.Array)
            foreach (var m in c.EnumerateArray()) sb.AppendLine($"[{Str(m, "type")}] {Str(m, "text")}");
        if (b.TryGetProperty("pageErrors", out var pe) && pe.ValueKind == JsonValueKind.Array)
            foreach (var e in pe.EnumerateArray()) sb.AppendLine($"[pageerror] {(e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())}");
        return sb.Length == 0 ? "(no console output captured)" : sb.ToString();
    }

    // ── Request rows (shared with the MCP network search) ────────────

    public static bool IsBad(JsonElement r)
    {
        if (r.TryGetProperty("failure", out var f) && f.ValueKind == JsonValueKind.String) return true;
        return r.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number && s.GetInt32() >= 400;
    }

    public static string RequestLine(JsonElement r, bool withBodies)
    {
        var sb = new StringBuilder();
        sb.Append($"[{Str(r, "step") ?? "-"}] {Str(r, "method")} {Clip(Str(r, "url"), 220)} -> ");
        if (Str(r, "failure") is { } fail) sb.Append($"FAILED {fail}");
        else if (r.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number) sb.Append(s.GetInt32());
        else sb.Append("(no response)");
        if (r.TryGetProperty("ms", out var ms) && ms.ValueKind == JsonValueKind.Number) sb.Append($" ({ms.GetInt32()}ms)");
        if (withBodies)
        {
            if (Str(r, "reqBody") is { Length: > 0 } rq) sb.Append($"\n      request: {Clip(rq, 400)}");
            if (Str(r, "body") is { Length: > 0 } rs) sb.Append($"\n      response: {Clip(rs, 600)}");
        }
        return sb.ToString();
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static JsonDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json); } catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "?";

    private static string Clip(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + $"… [+{s.Length - max} chars]";

    // Playwright colours its call log with ANSI escapes; they are noise to an LLM.
    private static readonly System.Text.RegularExpressions.Regex Ansi =
        new(@"\x1B\[[0-9;]*[A-Za-z]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string Plain(string s) => Ansi.Replace(s, "");

    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];

    private static string Indent(string s, string pad) => string.Join('\n', s.Split('\n').Select(l => pad + l));
}
