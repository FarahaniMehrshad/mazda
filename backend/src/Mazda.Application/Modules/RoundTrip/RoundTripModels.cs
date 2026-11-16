using System.Text.Json;
using System.Text.Json.Nodes;
using Mazda.Application.Common;
using Mazda.Application.Modules.Recordings;
using Mazda.Domain.Modules.Recordings;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Application.Modules.RoundTrip;

// ── Stored JSON shapes ───────────────────────────────────────────────

public sealed record PlanStepModel(string Call, string ArgsJson, List<int> Episodes, string? ContrastArgsJson, string Why);
public sealed record PlanModel(List<PlanStepModel> Steps, string Notes);

/// <param name="Step">Plan step index the change applies to.</param>
public sealed record PlanChangeModel(int Step, string Call, string ArgsJson, string Explanation);

public sealed record JudgeModel(string Verdict, string Reason);

/// <param name="Verdict">identical | equivalent | different | question | not-run</param>
/// <param name="Run">Which replay the pictures come from.</param>
public sealed record StepResultModel(
    int Index,
    string Call,
    string Verdict,
    List<Finding> Findings,
    JudgeModel? Judge,
    string? Question,
    string? Error,
    int DurationMs,
    int Run,
    List<string> DemoRequests,
    List<string> ReplayRequests,
    List<string> Console);

public sealed record ResultModel(List<StepResultModel> Steps, int Replays, string Summary);

/// <param name="Kind">info | replay | verdict | fix | revert | plan | question | error</param>
public sealed record LogEntry(DateTime At, string Kind, string Message, string? Function = null);

public static class RoundTripJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Options); } catch (JsonException) { return null; }
    }
}

/// <summary>Plan validation, shared by the planner output, the author's edits and the fix loop's plan changes.</summary>
public static class PlanRules
{
    /// <summary>Null when the plan can run; otherwise the first problem, phrased for the author.</summary>
    public static string? Problem(PlanModel plan, IReadOnlyList<SignatureFunction> actions, JsonObject? episodesDoc)
    {
        if (plan.Steps.Count == 0) return "The plan has no steps.";
        var seen = new HashSet<int>();
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var s = plan.Steps[i];
            var fn = actions.FirstOrDefault(a => a.Name == s.Call);
            if (fn is null) return $"Step {i + 1}: there is no action function named “{s.Call}”.";
            if (FunctionArgs.Validate(fn.ParametersJsonSchema, string.IsNullOrWhiteSpace(s.ArgsJson) ? "{}" : s.ArgsJson) is { } argErr)
                return $"Step {i + 1} ({s.Call}): {argErr}";
            if (!string.IsNullOrWhiteSpace(s.ContrastArgsJson)
                && FunctionArgs.Validate(fn.ParametersJsonSchema, s.ContrastArgsJson!) is { } cErr)
                return $"Step {i + 1} ({s.Call}), contrast choice: {cErr}";
            if (s.Episodes.Count == 0) return $"Step {i + 1} ({s.Call}) is not linked to any demonstrated interaction.";
            foreach (var e in s.Episodes)
            {
                if (episodesDoc is not null && RoundTripDemo.FindEpisode(episodesDoc, e) is null)
                    return $"Step {i + 1} ({s.Call}): the recording has no interaction number {e}.";
                if (!seen.Add(e)) return $"Interaction {e} is used by more than one step.";
            }
        }
        return null;
    }
}

/// <summary>The demonstration side of a round trip: per plan step, what the user's interactions changed, and pictures.</summary>
public sealed class RoundTripDemo
{
    public required JsonObject Episodes { get; init; }
    public required IReadOnlyList<StepDelta> Deltas { get; init; }
    public required IReadOnlyList<string> Descriptions { get; init; }
    public required IReadOnlyList<List<string>> RequestLines { get; init; }
    /// <summary>Action ids whose "after" picture is the step's before / after; "_start" for the recording's first picture.</summary>
    public required IReadOnlyList<(string? Before, string? After)> ShotKeys { get; init; }
    public required string? StartUrl { get; init; }

    public static JsonObject? FindEpisode(JsonObject doc, int index)
        => (doc["episodes"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(e => e["i"] is JsonValue v && v.TryGetValue<int>(out var i) && i == index);

    /// <summary>Episodes rebuilt from raw events with every action id they cover, and no size budget.</summary>
    public static JsonObject Rebuild(IReadOnlyList<RecordingEvent> events)
        => JsonNode.Parse(EpisodeBuilder.Build(events, int.MaxValue, keepActionIds: true).Json) as JsonObject ?? new JsonObject();

    public static RoundTripDemo Build(PlanModel plan, IReadOnlyList<RecordingEvent> events)
    {
        var doc = Rebuild(events);
        var actionSeq = events.Where(e => e.Kind == "action" && e.ActionId is not null)
            .GroupBy(e => e.ActionId!).ToDictionary(g => g.Key, g => g.Min(e => e.Seq));
        var orderedActions = actionSeq.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();

        var deltas = new List<StepDelta>();
        var descriptions = new List<string>();
        var requestLines = new List<List<string>>();
        var shots = new List<(string?, string?)>();
        string? startUrl = null;
        foreach (var e in events.Where(e => e.Kind == "ready"))
        {
            try { startUrl = (JsonNode.Parse(e.PayloadJson)?["data"]?["url"] as JsonValue)?.GetValue<string>(); } catch { }
            break;
        }

        foreach (var step in plan.Steps)
        {
            var aids = new List<string>();
            var lines = new List<string>();
            foreach (var i in step.Episodes)
            {
                if (FindEpisode(doc, i) is not { } ep) continue;
                foreach (var a in (ep["aids"] as JsonArray) ?? new JsonArray())
                    if (a?.GetValue<string>() is { } s) aids.Add(s);
                lines.Add($"{i}. {Describe(ep)}");
            }
            var aidSet = aids.ToHashSet(StringComparer.Ordinal);
            var mutations = new List<JsonObject>();
            var network = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var n = 0;
            foreach (var e in events.Where(e => e.ActionId is not null && aidSet.Contains(e.ActionId)))
            {
                JsonObject? data;
                try { data = JsonNode.Parse(e.PayloadJson)?["data"] as JsonObject; } catch (JsonException) { continue; }
                if (data is null) continue;
                if (e.Kind == "mutations") mutations.Add(data);
                else if (e.Kind == "network")
                {
                    // A request reported "still pending" and later completed: keep the last report.
                    var rid = (data["rid"] as JsonValue)?.GetValue<string>() ?? $"n{n++}";
                    network[rid] = data;
                }
            }
            var delta = DeltaBuilder.Build(mutations, network.Values);
            deltas.Add(delta);
            descriptions.Add(string.Join("\n", lines));
            requestLines.Add(Lines(delta));

            // Pictures: before = after the interaction preceding the step (or the start); after = the step's last interaction.
            var inStep = aids.Where(actionSeq.ContainsKey).OrderBy(a => actionSeq[a]).ToList();
            string? before = "_start", after = null;
            if (inStep.Count > 0)
            {
                var first = orderedActions.IndexOf(inStep[0]);
                before = first > 0 ? orderedActions[first - 1] : "_start";
                after = inStep[^1];
            }
            shots.Add((before, after));
        }
        return new RoundTripDemo
        {
            Episodes = doc, Deltas = deltas, Descriptions = descriptions, RequestLines = requestLines,
            ShotKeys = shots, StartUrl = startUrl,
        };
    }

    /// <summary>"click “Save”", "drag “Birth_Date” onto “#listValue”": one line a non-developer recognizes.</summary>
    public static string Describe(JsonObject ep)
    {
        static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        static string Clip(string s, int m) => s.Length <= m ? s : s[..m] + "…";
        var target = ep["target"] as JsonObject;
        var text = S(ep["action"]) ?? "interaction";
        var name = S(target?["name"]) ?? S(target?["best"]);
        if (!string.IsNullOrEmpty(name)) text += $" “{Clip(name, 60)}”";
        if (target?["collection"] is JsonObject col && col["index"] is JsonValue iv && iv.TryGetValue<int>(out var idx))
            text += $" (item {idx + 1})";
        if (S(ep["value"]) is { Length: > 0 } value) text += $" with “{Clip(value, 50)}”";
        if (ep["dest"] is JsonObject dest && (S(dest["name"]) ?? S(dest["best"])) is { } dn) text += $" onto “{Clip(dn, 50)}”";
        return text;
    }

    public static List<string> Lines(StepDelta d)
        => d.Requests.Select(r =>
        {
            var fields = string.Join(", ", r.Fields.Take(8).Select(kv => $"{kv.Key}={(kv.Value.Length > 40 ? kv.Value[..40] + "…" : kv.Value)}"));
            return fields.Length == 0 ? r.Key : $"{r.Key}  {fields}{(r.Fields.Count > 8 ? ", …" : "")}";
        }).ToList();
}
