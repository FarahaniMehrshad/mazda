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
    /// Grep captured stdout for a sentinel pair and return everything between
    /// them. Both sentinels must appear on their own lines; the interior is
    /// concatenated with no separator because harnesses emit the payload in a
    /// single <c>console.log</c>. Returns null when the fence is absent or
    /// malformed — which the caller should treat as "the harness died before
    /// it could report", not as an empty result.
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

        var interior = string.Concat(lines.Skip(beginIdx + 1).Take(endIdx - beginIdx - 1)
            .Select(l => l.TrimEnd('\r')));
        return interior.Trim();
    }

    /// <summary>Last <paramref name="chars"/> characters — the end of a log is where the failure is.</summary>
    public static string Tail(string s, int chars)
        => string.IsNullOrEmpty(s) || s.Length <= chars ? s ?? string.Empty : s[^chars..];
}
