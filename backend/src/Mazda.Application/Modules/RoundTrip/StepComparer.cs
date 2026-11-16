using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mazda.Application.Modules.RoundTrip;

/// <summary>What one replay of one step showed.</summary>
/// <param name="PageBefore">Accessibility text of the page before the step (null when unknown).</param>
/// <param name="PageAfter">Accessibility text of the page after the step (null when unknown).</param>
public sealed record StepObservation(StepDelta Delta, bool Ran, bool Threw, string? Error,
    string? PageBefore = null, string? PageAfter = null);

/// <summary>Everything known about one plan step when judging it.</summary>
/// <param name="Demo">What the demonstrated span changed.</param>
/// <param name="Replay">The replay being judged.</param>
/// <param name="Twin">A second replay with the same arguments from the same fresh start: what differs between the two is noise.</param>
/// <param name="Contrast">A replay with different arguments (<see cref="ContrastArgsJson"/>): what differs from <see cref="Replay"/> carries the choice.</param>
/// <param name="Repetitions">Other demonstrated calls of the same function (different arguments) in the same recording.</param>
/// <param name="Accepted">Difference keys the author said are fine ("same result").</param>
/// <param name="Rejected">Difference keys the author said are NOT the same result.</param>
/// <param name="Roles">Learned once per step from the first replays (noise, intent): a property of the app, reused for later replays.</param>
/// <param name="Siblings">Every other demonstrated call of the same function, whatever its arguments: what they all send is the step's main request.</param>
public sealed record StepContext(
    string Call,
    string ArgsJson,
    StepDelta Demo,
    StepObservation Replay,
    StepObservation? Twin,
    IReadOnlyList<StepDelta> Repetitions,
    IReadOnlySet<string> Accepted,
    IReadOnlySet<string> Rejected,
    LearnedRoles Roles,
    IReadOnlyList<StepDelta>? Siblings = null);

/// <summary>
/// Which values are noise and which carry the user's choice, for one step.
/// Keys: <c>field:{request key}:{field}</c> and <c>dom:{signature}</c>.
/// </summary>
public sealed record LearnedRoles(IReadOnlySet<string> Noise, IReadOnlySet<string> Intent)
{
    public static readonly LearnedRoles None = new(new HashSet<string>(), new HashSet<string>());

    /// <summary>
    /// Noise = what differs between two replays with the same arguments from the same fresh start.
    /// Intent = what differs between a replay and a replay where only this step's arguments changed.
    /// </summary>
    public static LearnedRoles Learn(StepDelta a, StepDelta? twin, StepDelta? contrast)
    {
        var noise = new HashSet<string>(StringComparer.Ordinal);
        var intent = new HashSet<string>(StringComparer.Ordinal);
        if (twin is not null)
        {
            foreach (var (key, field) in DifferingFields(a, twin)) noise.Add($"field:{key}:{field}");
            var x = a.Dom.ToHashSet(); var y = twin.Dom.ToHashSet();
            foreach (var sig in x.Except(y).Concat(y.Except(x))) noise.Add("dom:" + sig);
        }
        if (contrast is not null)
            foreach (var (key, field) in DifferingFields(a, contrast))
                if (!noise.Contains($"field:{key}:{field}")) intent.Add($"field:{key}:{field}");
        return new LearnedRoles(noise, intent);
    }

    /// <summary>
    /// A step's roles plus what the other steps of the same function learned: a field that carries the
    /// choice in one call carries it in every call (a step that did not run while learning knows nothing
    /// on its own). The step's own observations win where they disagree.
    /// </summary>
    public static LearnedRoles Merge(LearnedRoles own, IEnumerable<LearnedRoles> sameFunction)
    {
        var others = sameFunction.ToList();
        var noise = new HashSet<string>(own.Noise, StringComparer.Ordinal);
        var intent = new HashSet<string>(own.Intent, StringComparer.Ordinal);
        foreach (var k in others.SelectMany(o => o.Intent)) if (!own.Noise.Contains(k)) intent.Add(k);
        foreach (var k in others.SelectMany(o => o.Noise)) if (!intent.Contains(k)) noise.Add(k);
        return new LearnedRoles(noise, intent);
    }

    private static IEnumerable<(string Key, string Field)> DifferingFields(StepDelta a, StepDelta b)
    {
        foreach (var group in a.Requests.GroupBy(r => r.Key))
        {
            var others = b.Requests.Where(r => r.Key == group.Key).ToList();
            var n = 0;
            foreach (var ra in group)
            {
                var rb = n < others.Count ? others[n] : null;
                n++;
                if (rb is null) continue;
                foreach (var (f, v) in ra.Fields)
                    if (rb.Fields.TryGetValue(f, out var w) && w != v) yield return (group.Key, f);
            }
        }
    }
}

/// <param name="Severity">blocking (the step did something else) | unexplained (needs a judge or the author) | note</param>
/// <param name="Key">Stable id of this kind of difference, used to remember the author's answer.</param>
public sealed record Finding(string Severity, string Kind, string Key, string Summary, string? Demo = null, string? Replay = null);

/// <param name="Verdict">identical | equivalent | different | unexplained | not-run.
/// "unexplained" means the comparison could not decide on its own: the caller asks the judge, then the author.</param>
public sealed record StepJudgement(string Verdict, IReadOnlyList<Finding> Findings)
{
    public IEnumerable<Finding> Blocking => Findings.Where(f => f.Severity == "blocking");
    public IEnumerable<Finding> Unexplained => Findings.Where(f => f.Severity == "unexplained");
}

/// <summary>
/// Decides whether a replayed step did what the demonstration did. Pure.
///
/// A value's role is learned from how it varies, never guessed from its
/// shape (an id-looking value can be the user's choice; a small number can
/// depend on what was already on the page):
///   • differs between two replays with the same arguments  → noise, ignored
///   • differs when only the arguments change (contrast run), or equals a
///     chosen value                                         → intent, must match the demo
///   • same everywhere                                      → structure, must match the demo
///   • otherwise                                            → unexplained: the judge / the author decide
/// </summary>
public static class StepComparer
{
    public static StepJudgement Judge(StepContext c)
    {
        var f = new List<Finding>();
        void Add(Finding x)
        {
            if (x.Severity != "note" && c.Accepted.Contains(x.Key))
                f.Add(x with { Severity = "note", Summary = "Accepted as the same result: " + x.Summary });
            else if (x.Severity == "unexplained" && c.Rejected.Contains(x.Key))
                f.Add(x with { Severity = "blocking", Summary = x.Summary + " (you said this is not the same result)" });
            else f.Add(x);
        }

        if (!c.Replay.Ran) return new StepJudgement("not-run", new[] { new Finding("note", "not-run", "not-run",
            "Not run: an earlier step failed, so the page was not prepared for this one.") });

        if (c.Replay.Threw)
            Add(new Finding("blocking", "error", "error", $"The function failed: {c.Replay.Error}"));

        var chosen = ChosenValues(c.ArgsJson);
        CompareRequests(c, chosen, Add);
        CompareDom(c, chosen, Add);

        var verdict =
            f.Any(x => x.Severity == "blocking") ? "different" :
            f.Any(x => x.Severity == "unexplained") ? "unexplained" :
            f.Count == 0 ? "identical" : "equivalent";
        return new StepJudgement(verdict, f);
    }

    // ── Requests ─────────────────────────────────────────────────────

    private static void CompareRequests(StepContext c, IReadOnlyList<string> chosen, Action<Finding> add)
    {
        var demo = c.Demo.Requests;
        var used = new HashSet<RequestObservation>();
        var primary = PrimaryIndex(c);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < demo.Count; i++)
        {
            var d = demo[i];
            var occurrence = demo.Take(i).Count(x => x.Key == d.Key);
            var a = Nth(c.Replay.Delta.Requests, d.Key, occurrence);
            var b = c.Twin is null ? null : Nth(c.Twin.Delta.Requests, d.Key, occurrence);

            if (a is null)
            {
                if (i == primary && !c.Replay.Threw)
                    add(new Finding("blocking", "request-missing", $"missing:{d.Key}",
                        $"Your demonstration sent {d.Key}; the function did not.", d.Key, null));
                else if (i != primary && reported.Add(d.Key))
                    add(new Finding("note", "request-sometimes", $"missing:{d.Key}",
                        b is null
                            ? $"Your demonstration also sent {d.Key}; the replay did not. It may not fire every time."
                            : $"{d.Key} was sent in one replay and not in the other: it does not fire every time."));
                continue;
            }
            used.Add(a);

            foreach (var (field, dv) in d.Fields)
            {
                if (!a.Fields.TryGetValue(field, out var av))
                {
                    if (d.BodyComplete && a.BodyComplete && !(b is not null && !b.Fields.ContainsKey(field)))
                        add(new Finding("unexplained", "field-missing", $"field:{d.Key}:{field}",
                            $"{d.Key}: your demonstration sent {field} = {Show(dv)}; the function did not send it.", dv, null));
                    continue;
                }
                if (dv == av) continue;

                // Varies between two identical replays: noise.
                var roleKey = $"field:{d.Key}:{field}";
                if (c.Roles.Noise.Contains(roleKey)) continue;
                if (b is not null && b.Fields.TryGetValue(field, out var bv) && bv != av) continue;

                var isChosen = chosen.Any(v => Same(v, dv) || Same(v, av));
                var reactsToChoice = c.Roles.Intent.Contains(roleKey);
                if (isChosen || reactsToChoice)
                {
                    add(new Finding("blocking", "intent-differs", $"field:{d.Key}:{field}",
                        $"{d.Key}: your demonstration sent {field} = {Show(dv)}; the function sent {field} = {Show(av)}." +
                        (reactsToChoice ? " This field changes with the chosen option, so it carries your choice." : ""),
                        dv, av));
                    continue;
                }

                var variesAcrossCalls = c.Repetitions.Any(r => r.Requests.Any(x => x.Key == d.Key
                    && x.Fields.TryGetValue(field, out var rv) && rv != dv));
                add(new Finding("unexplained", variesAcrossCalls ? "varies-across-calls" : "context-differs", $"field:{d.Key}:{field}",
                    $"{d.Key}: your demonstration sent {field} = {Show(dv)}; the function sent {Show(av)}" +
                    (variesAcrossCalls ? " (this field also differs between your own demonstrated calls)." : "."),
                    dv, av));
            }
        }

        var extra = c.Replay.Delta.Requests.Where(r => !used.Contains(r)).Select(r => r.Key).Distinct().ToList();
        if (extra.Count > 0)
            add(new Finding("note", "request-extra", "extra",
                $"The replay also sent {string.Join(", ", extra.Take(5))}{(extra.Count > 5 ? ", …" : "")}, which your demonstration did not."));
    }

    /// <summary>
    /// The request the step exists for. Requests that only fire in some situations (a lookup the app
    /// makes because something was already selected) come and go between calls; the one every
    /// demonstrated call of this function sent is the main one. Without other calls: the first.
    /// </summary>
    private static int PrimaryIndex(StepContext c)
    {
        var demo = c.Demo.Requests;
        if (demo.Count == 0 || c.Siblings is not { Count: > 0 } sib) return 0;
        for (var i = 0; i < demo.Count; i++)
            if (sib.All(s => s.Requests.Any(r => r.Key == demo[i].Key))) return i;
        return 0;
    }

    // ── DOM ──────────────────────────────────────────────────────────

    private static void CompareDom(StepContext c, IReadOnlyList<string> chosen, Action<Finding> add)
    {
        var demo = c.Demo;
        var rep = c.Replay.Delta;

        // Nothing happened at all, while the demonstration changed something.
        if ((demo.HasDomChange || demo.Requests.Count > 0) && !rep.HasDomChange && rep.Requests.Count == 0 && !c.Replay.Threw)
        {
            add(new Finding("blocking", "no-effect", "no-effect",
                "Your demonstration changed the page; the replay ran without any visible effect or request."));
            return;
        }

        // The chosen value showed up on the page in the demonstration: it must show up in the replay too.
        foreach (var v in chosen)
        {
            if (!demo.Texts.Any(t => t.Contains(v, StringComparison.OrdinalIgnoreCase))) continue;
            if (rep.Texts.Any(t => t.Contains(v, StringComparison.OrdinalIgnoreCase))) continue;
            // The change log can miss a re-rendered label; the page itself after the step is the fallback:
            // the value is visible now and was not before.
            if (Shows(c.Replay.PageAfter, v) && !Shows(c.Replay.PageBefore, v)) continue;
            add(new Finding("blocking", "echo-missing", $"echo:{v}",
                $"After your demonstration “{v}” appeared on the page; after the replay it did not.", v, null));
        }

        // Without requests, the page change is the only evidence: compare its shape, minus replay-to-replay noise.
        if (demo.Requests.Count > 0) return;
        var noise = new HashSet<string>(c.Roles.Noise.Where(k => k.StartsWith("dom:")).Select(k => k[4..]), StringComparer.Ordinal);
        if (c.Twin is not null)
        {
            var a = rep.Dom.ToHashSet(); var b = c.Twin.Delta.Dom.ToHashSet();
            noise.UnionWith(a.Except(b)); noise.UnionWith(b.Except(a));
        }
        var ds = demo.Dom.Where(x => !noise.Contains(x)).ToHashSet();
        var rs = rep.Dom.Where(x => !noise.Contains(x)).ToHashSet();
        if (ds.Count == 0 && rs.Count == 0) return;
        var union = ds.Union(rs).Count();
        var sim = union == 0 ? 1.0 : (double)ds.Intersect(rs).Count() / union;
        if (sim < 0.5)
            add(new Finding("unexplained", "dom-differs", "dom",
                $"The page changed differently than in your demonstration (about {Math.Round(sim * 100)}% of the changes match).",
                string.Join(", ", ds.Except(rs).Take(6)), string.Join(", ", rs.Except(ds).Take(6))));
    }

    // ── helpers ──────────────────────────────────────────────────────

    /// <summary>String values among the step's arguments: what the user chose or typed.</summary>
    public static IReadOnlyList<string> ChosenValues(string? argsJson)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(argsJson)) return list;
        try
        {
            void Walk(JsonNode? n)
            {
                switch (n)
                {
                    case JsonObject o: foreach (var kv in o) Walk(kv.Value); break;
                    case JsonArray a: foreach (var x in a) Walk(x); break;
                    case JsonValue v when v.TryGetValue<string>(out var s) && DeltaBuilder.Norm(s).Length >= 2:
                        list.Add(DeltaBuilder.Norm(s)); break;
                }
            }
            Walk(JsonNode.Parse(argsJson));
        }
        catch (JsonException) { }
        return list;
    }

    private static bool Shows(string? page, string v)
        => !string.IsNullOrEmpty(page)
           && System.Text.RegularExpressions.Regex.Replace(page, @"\s+", " ").Contains(v, StringComparison.OrdinalIgnoreCase);

    private static RequestObservation? Nth(IReadOnlyList<RequestObservation> list, string key, int n)
        => list.Where(r => r.Key == key).Skip(n).FirstOrDefault();

    private static bool Same(string a, string b)
        => string.Equals(DeltaBuilder.Norm(a), DeltaBuilder.Norm(b), StringComparison.OrdinalIgnoreCase);

    private static string Show(string v) => v.Length <= 80 ? $"\"{v}\"" : $"\"{v[..80]}…\"";
}
