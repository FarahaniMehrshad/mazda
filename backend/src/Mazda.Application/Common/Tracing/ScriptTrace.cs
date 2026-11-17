using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mazda.Application.Common.Tracing;

/// <summary>Timestamps the runner took around one Playwright process (Unix ms).</summary>
public sealed record RunnerTimings(long PrepareStart, long PrepareEnd, long Spawn, long Exit);

public sealed record ScriptTraceResult(List<TraceNode> Nodes, Dictionary<string, TraceBody> Bodies);

/// <summary>
/// Turns what the trace reporter wrote for one Playwright process into trace
/// nodes: start-up, the test, every step and API call, each API call mapped
/// to the function body line that made it. Pure.
///
/// Body lines are found through the markers the harness writes around every
/// body (<see cref="BodyStartMarker"/> … <see cref="BodyEndMarker"/>), read from
/// the final spec, so whatever the runner injected above them does not matter.
/// </summary>
public static class ScriptTrace
{
    public const string BodyStartMarker = "// __MZ_BODY__ ";
    public const string BodyEndMarker = "// __MZ_BODY_END__";
    public const string FunctionStepPrefix = "fn ";
    public const string PhaseStepPrefix = "mz: ";
    public const string SpecFileName = "mazda.spec.ts";

    private static readonly Regex Method = new(@"^(?<recv>[A-Za-z_$][\w$]*)\.(?<m>[A-Za-z_$][\w$]*)", RegexOptions.Compiled);

    private static readonly HashSet<string> NetworkMethods = new(StringComparer.Ordinal)
        { "waitForResponse", "waitForRequest", "waitForLoadState", "waitForURL", "waitForNavigation", "waitForEvent", "goto", "reload", "goBack", "goForward" };
    private static readonly HashSet<string> ActionMethods = new(StringComparer.Ordinal)
        { "click", "dblclick", "fill", "press", "pressSequentially", "type", "check", "uncheck", "setChecked", "selectOption", "hover", "dragTo",
          "setInputFiles", "tap", "focus", "blur", "dispatchEvent", "clear", "scrollIntoViewIfNeeded", "selectText", "down", "up", "move", "wheel", "insertText" };
    private static readonly HashSet<string> WaitMethods = new(StringComparer.Ordinal) { "waitFor", "waitForSelector", "waitForFunction" };
    private static readonly HashSet<string> CaptureMethods = new(StringComparer.Ordinal) { "screenshot", "ariaSnapshot", "snapshot", "content" };
    private static readonly HashSet<string> StartupMethods = new(StringComparer.Ordinal)
        { "connectOverCDP", "connect", "newPage", "newContext", "close", "launch", "addInitScript", "exposeBinding", "exposeFunction", "newCDPSession", "route" };

    /// <summary>Category of a Playwright API call from its step title ("locator.click(#b)").</summary>
    public static string CategoryOfCall(string title)
    {
        var m = Method.Match(title);
        if (!m.Success) return TraceCategory.Read;
        var recv = m.Groups["recv"].Value;
        var name = m.Groups["m"].Value;
        if (recv is "keyboard" or "mouse" or "touchscreen") return TraceCategory.Action;
        if (name == "waitForTimeout") return TraceCategory.Sleep;
        if (NetworkMethods.Contains(name)) return TraceCategory.Network;
        if (ActionMethods.Contains(name)) return TraceCategory.Action;
        if (WaitMethods.Contains(name)) return TraceCategory.Wait;
        if (CaptureMethods.Contains(name)) return TraceCategory.Capture;
        if (StartupMethods.Contains(name)) return TraceCategory.Startup;
        return TraceCategory.Read;
    }

    /// <summary>Category of a harness phase step ("mz: settle").</summary>
    public static string CategoryOfPhase(string phase) => phase switch
    {
        _ when phase.StartsWith("pause", StringComparison.Ordinal) => TraceCategory.Sleep,
        _ when phase.StartsWith("screenshot", StringComparison.Ordinal) || phase.StartsWith("aria", StringComparison.Ordinal) => TraceCategory.Capture,
        _ when phase.StartsWith("connect", StringComparison.Ordinal) || phase.StartsWith("open", StringComparison.Ordinal) => TraceCategory.Startup,
        _ => TraceCategory.Harness,
    };

    /// <summary>Function bodies between the markers: name → (first spec line, lines).</summary>
    public static Dictionary<string, (int FirstLine, List<string> Lines)> Bodies(string spec)
    {
        var result = new Dictionary<string, (int, List<string>)>(StringComparer.Ordinal);
        var lines = spec.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (!t.StartsWith(BodyStartMarker, StringComparison.Ordinal)) continue;
            var name = t[BodyStartMarker.Length..].Trim();
            var body = new List<string>();
            var j = i + 1;
            for (; j < lines.Length && lines[j].Trim() != BodyEndMarker; j++)
                body.Add(Dedent(lines[j].TrimEnd('\r')));
            result[name] = (i + 2, body);   // spec line numbers are 1-based; the body starts on the line after the marker
            i = j;
        }
        return result;
    }

    private static string Dedent(string s) => s.StartsWith("    ", StringComparison.Ordinal) ? s[4..] : s;

    public static string BodyKey(string fn, IEnumerable<string> lines)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return fn + "@" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    public static ScriptTraceResult Build(string reporterJson, string spec, RunnerTimings t)
    {
        var nodes = new List<TraceNode>();
        var bodies = new Dictionary<string, TraceBody>(StringComparer.Ordinal);
        var ranges = Bodies(spec);
        var keyOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, (_, lines)) in ranges)
        {
            var key = BodyKey(name, lines);
            keyOf[name] = key;
            bodies[key] = new TraceBody(name, lines);
        }

        JsonElement root;
        try { root = JsonDocument.Parse(reporterJson).RootElement; }
        catch (JsonException) { root = default; }
        var title = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("title", out var tt) ? tt.GetString() ?? "script" : "script";
        long L(string p, long fallback) => root.ValueKind == JsonValueKind.Object && Long(root, p) is { } x ? x : fallback;
        var testStart = L("testStart", t.Spawn);
        var testEnd = L("testEnd", t.Exit);

        nodes.Add(new TraceNode(1, null, "script: " + title, TraceCategory.Startup, t.PrepareStart, Math.Max(0, t.Exit - t.PrepareStart)));
        nodes.Add(new TraceNode(2, 1, "prepare the run directory", TraceCategory.Startup, t.PrepareStart, Math.Max(0, t.PrepareEnd - t.PrepareStart)));
        nodes.Add(new TraceNode(3, 1, "start Playwright (TypeScript, worker)", TraceCategory.Startup, t.Spawn, Math.Max(0, testStart - t.Spawn)));
        nodes.Add(new TraceNode(4, 1, "test", TraceCategory.Harness, testStart, Math.Max(0, testEnd - testStart)));
        nodes.Add(new TraceNode(5, 1, "shut down", TraceCategory.Startup, testEnd, Math.Max(0, t.Exit - testEnd)));

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            return new ScriptTraceResult(nodes, bodies);

        const int offset = 100;
        var raw = new List<TraceNode>();
        foreach (var s in steps.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            var id = (int)(Long(s, "id") ?? 0);
            int? parent = Long(s, "parent") is { } pi ? (int)pi + offset : 4;
            var cat = Str(s, "cat") ?? "";
            var stepTitle = Str(s, "title") ?? "";
            var start = Long(s, "start") ?? testStart;
            var ms = Long(s, "ms") ?? 0;
            var file = Str(s, "file");
            int? line = Long(s, "line") is { } li ? (int)li : null;
            var err = Str(s, "err");

            string category; string? fn = null; string name = stepTitle;
            if (cat == "test.step" && stepTitle.StartsWith(FunctionStepPrefix, StringComparison.Ordinal))
            {
                category = TraceCategory.Function;
                fn = stepTitle[FunctionStepPrefix.Length..].Trim();
            }
            else if (cat == "test.step" && stepTitle.StartsWith(PhaseStepPrefix, StringComparison.Ordinal))
                category = CategoryOfPhase(stepTitle[PhaseStepPrefix.Length..]);
            else if (cat == "test.step") category = TraceCategory.Action;   // a helper (__mzClick…) boxed at its call site
            else if (cat == "expect") category = TraceCategory.Wait;
            else category = CategoryOfCall(stepTitle);

            string? body = null; int? bodyLine = null;
            if (fn is not null && keyOf.TryGetValue(fn, out var fk)) body = fk;
            if (line is { } l && (file is null || file.EndsWith(SpecFileName, StringComparison.Ordinal)))
                foreach (var (bn, (first, lines)) in ranges)
                    if (l >= first && l < first + lines.Count)
                    {
                        fn ??= bn; body = keyOf[bn]; bodyLine = l - first + 1;
                        break;
                    }

            raw.Add(new TraceNode(id + offset, parent, Shorten(name, 160), category, start, Math.Max(0, ms))
            {
                Fn = fn, Body = body, Line = bodyLine,
                Error = err is null ? null : Shorten(err, 300),
                TimedOut = err is not null && err.Contains("Timeout", StringComparison.Ordinal) && err.Contains("exceeded", StringComparison.Ordinal),
            });
        }

        // Calls inside a helper report the helper's own lines: they belong to the body line that called the helper.
        var byId = raw.ToDictionary(n => n.Id);
        TraceNode Resolve(TraceNode n, int depth = 0)
        {
            if ((n.Body is not null && n.Line is not null) || depth > 50 || n.Parent is not { } p || !byId.TryGetValue(p, out var parent)) return n;
            var r = Resolve(parent, depth + 1);
            return n with { Fn = n.Fn ?? r.Fn, Body = n.Body ?? r.Body, Line = n.Line ?? r.Line };
        }
        foreach (var n in raw) nodes.Add(Resolve(n));
        return new ScriptTraceResult(nodes, bodies);
    }

    private static long? Long(JsonElement o, string p)
        => o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x
         : o.TryGetProperty(p, out v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : null;

    private static string? Str(JsonElement o, string p)
        => o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Shorten(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace("\u001b", "");
        s = Regex.Replace(s, @"\[\d+m", "");
        return s.Length <= max ? s : s[..max] + "…";
    }
}
