using System.Text.Json;

namespace Mazda.Infrastructure.Runner;

/// <summary>
/// Shared parsing for the fenced payloads harnesses print to stdout.
///
/// Every harness shape that returns structured data does it the same way:
/// print a begin sentinel, one line of JSON, an end sentinel. Keeping stdout
/// as the transport means the payload coexists with whatever Playwright's
/// reporter writes, at the cost of having to find it again on this side.
/// </summary>
internal static class HarnessOutput
{
    /// <summary>
    /// Grep captured stdout for a sentinel pair and return the JSON payload
    /// between them. Returns null when the fence is absent — which the caller
    /// should treat as "the harness died before it could report", not as an
    /// empty result.
    ///
    /// The interior is NOT simply everything between the sentinels. Other
    /// output races into the fence: Node's <c>process.emitWarning</c> is
    /// deferred to a later tick, so a warning raised earlier can surface
    /// between the harness's two <c>console.log</c> calls. That is not
    /// hypothetical — Playwright spawns its workers with <c>FORCE_COLOR=1</c>
    /// (runner/workerHost.js), and a conflicting <c>NO_COLOR</c> in the
    /// inherited environment made Node print a warning straight into the
    /// middle of a state payload. Blind concatenation glued the warning text
    /// onto the front of the JSON and the parse failed.
    ///
    /// Harnesses emit the payload as one <c>console.log(JSON.stringify(...))</c>,
    /// so the payload is exactly one line: pick the interior line that parses.
    /// </summary>
    public static string? ExtractFenced(string fullLog, string beginSentinel, string endSentinel)
    {
        var lines = fullLog.Split('\n');
        int beginIdx = -1, endIdx = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (beginIdx < 0 && line == beginSentinel) beginIdx = i;
            else if (beginIdx >= 0 && line == endSentinel) { endIdx = i; break; }
        }
        if (beginIdx < 0 || endIdx <= beginIdx) return null;

        var interior = lines.Skip(beginIdx + 1).Take(endIdx - beginIdx - 1)
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        foreach (var line in interior)
        {
            var t = line.Trim();
            if (t.Length == 0 || (t[0] != '{' && t[0] != '[')) continue;
            if (IsWellFormedJson(t)) return t;
        }

        // Nothing stood alone. Try the whole interior as one document — covers
        // a payload pretty-printed across lines, which no harness emits today
        // but the previous behaviour allowed for.
        var joined = string.Concat(interior).Trim();
        if (joined.Length > 0 && IsWellFormedJson(joined)) return joined;

        // Last resort: noise sharing a line with the payload. Take the widest
        // brace/bracket span and see whether that parses.
        var start = joined.IndexOfAny(new[] { '{', '[' });
        var end = joined.LastIndexOfAny(new[] { '}', ']' });
        if (start >= 0 && end > start)
        {
            var span = joined[start..(end + 1)];
            if (IsWellFormedJson(span)) return span;
        }

        // Hand back whatever was in the fence so the caller's error message can
        // show it. Returning null here would report "no payload" when the real
        // problem is "payload was corrupted", which sends the reader looking in
        // the wrong place.
        return joined.Length == 0 ? null : joined;
    }

    private static bool IsWellFormedJson(string s)
    {
        try
        {
            using var _ = JsonDocument.Parse(s);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Last <paramref name="chars"/> characters — the end of a log is where the failure is.</summary>
    public static string Tail(string s, int chars)
        => string.IsNullOrEmpty(s) || s.Length <= chars ? s ?? string.Empty : s[^chars..];
}
