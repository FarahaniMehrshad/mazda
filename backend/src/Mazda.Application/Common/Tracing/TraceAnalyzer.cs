namespace Mazda.Application.Common.Tracing;

/// <param name="Category">A <see cref="TraceCategory"/> value or one of the summary buckets (timeout, js, backend).</param>
/// <param name="Normal">Expected time (network), shown but not a bottleneck.</param>
public sealed record TraceBucket(string Category, long Ms, int Count, bool Normal);

/// <param name="Ms">Time spent in calls made from this line (helpers count at the line that called them).</param>
/// <param name="Category">The category that took most of that time.</param>
public sealed record TraceLine(string Fn, string Body, int Line, string Source, long Ms, int Calls, int TimedOut, string Category);

/// <param name="BetweenCallsMs">The function's own time outside Playwright calls (JavaScript, timers).</param>
public sealed record TraceFunction(string Fn, int Calls, long Ms, long BetweenCallsMs);

/// <summary>All LLM calls in a trace. <c>CostUsd</c> sums the calls whose cost is known.</summary>
public sealed record TraceLlmTotals(
    int Calls, long Input, long CacheRead, long CacheWrite, long Output, long Reasoning, long Total,
    decimal CostUsd, int UnknownCostCalls);

public sealed record TraceSummary(long TotalMs, List<TraceBucket> Buckets, List<TraceLine> Lines, List<TraceFunction> Functions)
{
    public TraceLlmTotals? Llm { get; init; }
}

/// <summary>
/// Where the time went. Every node contributes only its own time (its
/// duration minus what its children cover), so nothing is counted twice and
/// the buckets add up to the trace's length. Pure.
/// </summary>
public static class TraceAnalyzer
{
    public static TraceSummary Summarize(TraceDoc doc)
    {
        var children = doc.Nodes.Where(n => n.Parent is not null).GroupBy(n => n.Parent!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var buckets = new Dictionary<string, (long Ms, int Count)>(StringComparer.Ordinal);
        void Add(string k, long ms, int count = 1)
        {
            if (ms <= 0 && count == 0) return;
            var cur = buckets.GetValueOrDefault(k);
            buckets[k] = (cur.Ms + Math.Max(0, ms), cur.Count + count);
        }

        foreach (var n in doc.Nodes)
        {
            var own = SelfMs(n, children.GetValueOrDefault(n.Id));
            Add(Bucket(n), own);
        }

        // Time no node covers at all (between backend phases).
        var covered = Union(doc.Nodes.Select(r => (r.Start, r.Start + r.Ms)), doc.Start, doc.Start + doc.Ms);
        if (doc.Ms - covered > 0) Add(TraceCategory.Backend, doc.Ms - covered, 0);

        var lines = new List<TraceLine>();
        var byId = doc.Nodes.ToDictionary(n => n.Id);
        bool TopAtLine(TraceNode n) =>
            n.Parent is not { } p || !byId.TryGetValue(p, out var parent) || parent.Body != n.Body || parent.Line != n.Line;
        foreach (var g in doc.Nodes.Where(n => n.Body is not null && n.Line is not null && n.Category != TraceCategory.Function && TopAtLine(n))
                     .GroupBy(n => (n.Body!, n.Line!.Value)))
        {
            var body = doc.Bodies.GetValueOrDefault(g.Key.Item1);
            var source = body is not null && g.Key.Value >= 1 && g.Key.Value <= body.Lines.Count ? body.Lines[g.Key.Value - 1].Trim() : "";
            var main = g.GroupBy(Bucket).OrderByDescending(x => x.Sum(n => n.Ms)).First().Key;
            lines.Add(new TraceLine(body?.Fn ?? g.First().Fn ?? "?", g.Key.Item1, g.Key.Value, source,
                g.Sum(n => n.Ms), g.Count(), g.Count(n => n.TimedOut), main));
        }

        var functions = doc.Nodes.Where(n => n.Category == TraceCategory.Function && n.Fn is not null)
            .GroupBy(n => n.Fn!)
            .Select(g => new TraceFunction(g.Key, g.Count(), g.Sum(n => n.Ms), g.Sum(n => SelfMs(n, children.GetValueOrDefault(n.Id)))))
            .OrderByDescending(f => f.Ms).ToList();

        var llm = doc.Nodes.Where(n => n.Llm is not null).Select(n => n.Llm!).ToList();
        var llmTotals = llm.Count == 0 ? null : new TraceLlmTotals(
            llm.Count, llm.Sum(u => u.Input), llm.Sum(u => u.CacheRead), llm.Sum(u => u.CacheWrite),
            llm.Sum(u => u.Output), llm.Sum(u => u.Reasoning),
            llm.Sum(u => u.Input + u.CacheRead + u.CacheWrite + u.Output),
            llm.Sum(u => u.CostUsd ?? 0m),
            llm.Count(u => u.CostUsd is null && u.Input + u.CacheRead + u.CacheWrite + u.Output > 0));

        return new TraceSummary(doc.Ms,
            buckets.Where(b => b.Value.Ms > 0).Select(b => new TraceBucket(b.Key, b.Value.Ms, b.Value.Count, TraceCategory.IsNormal(b.Key)))
                .OrderByDescending(b => b.Ms).ToList(),
            lines.OrderByDescending(l => l.Ms).ToList(),
            functions) { Llm = llmTotals };
    }

    /// <summary>The summary bucket a node's own time goes to.</summary>
    public static string Bucket(TraceNode n) =>
        n.TimedOut ? TraceCategory.Timeout
        : n.Category == TraceCategory.Function ? TraceCategory.Js
        : n.Category == TraceCategory.Operation ? TraceCategory.Backend
        : n.Category;

    public static long SelfMs(TraceNode n, List<TraceNode>? kids)
    {
        if (kids is null || kids.Count == 0) return n.Ms;
        var covered = Union(kids.Select(k => (k.Start, k.Start + k.Ms)), n.Start, n.Start + n.Ms);
        return Math.Max(0, n.Ms - covered);
    }

    /// <summary>Length of the union of intervals, clipped to [from, to].</summary>
    public static long Union(IEnumerable<(long Start, long End)> intervals, long from, long to)
    {
        long total = 0, curS = 0, curE = -1;
        var started = false;
        foreach (var (s0, e0) in intervals.Select(i => (Math.Max(i.Start, from), Math.Min(i.End, to))).Where(i => i.Item2 > i.Item1).OrderBy(i => i.Item1))
        {
            if (!started) { curS = s0; curE = e0; started = true; continue; }
            if (s0 <= curE) { curE = Math.Max(curE, e0); continue; }
            total += curE - curS; curS = s0; curE = e0;
        }
        if (started) total += curE - curS;
        return total;
    }
}
