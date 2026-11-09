using System.Text.Json;
using System.Text.Json.Nodes;
using Mazda.Application.Common.Abstractions;
using Mazda.Application.Modules.Signatures;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Application.Modules.Recordings;

public sealed record MergeResult(
    IReadOnlyList<SignatureElement> Elements,
    /// Null when flows were not touched.
    string? FlowsJson,
    int AddedElements,
    int EnrichedElements,
    int AddedNodes,
    int AddedEdges,
    /// Previously learned elements dropped by a replace-learned apply.
    int RemovedElements = 0);

/// <summary>
/// Deterministic merge of an (accepted subset of an) analyzer proposal into a
/// signature. Purely additive by construction: existing elements keep their
/// selector, position and text (enrichment only appends to their logic), so
/// <see cref="ElementChangePolicy.InvalidateOnlyIfExistingSelectorsChanged"/>
/// keeps the function catalogue and relationship graph intact.
/// </summary>
public static class RecordingMerge
{
    // Column limits of signature_elements (MazdaDbContext). LLM output is
    // clipped to them here: one over-long value would otherwise fail the
    // whole apply at SaveChanges.
    public const int MaxSelector = 500;
    public const int MaxType = 40;
    public const int MaxDescription = 1000;
    public const int MaxLogic = 8000;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public static MergeResult Merge(
        Signature signature,
        IReadOnlyList<ProposedElement> proposal,
        string? proposalFlowsJson,
        IReadOnlySet<string> acceptedSelectors,
        bool enrichExisting,
        bool applyFlows,
        bool replaceLearned = false)
    {
        // Replace-learned: elements earlier recordings taught are superseded by
        // this (joint, fresh) proposal; source-derived and manual ones stay.
        var all = signature.Elements.OrderBy(e => e.Order).ToList();
        var existing = replaceLearned ? all.Where(e => e.Origin != ElementOrigin.Recorded).ToList() : all;
        var removed = all.Count - existing.Count;
        var bySelector = existing.ToDictionary(e => e.ElementSelector, StringComparer.Ordinal);
        var extraLogic = new Dictionary<string, string>(StringComparer.Ordinal);
        var added = new List<ProposedElement>();
        var addedSelectors = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in proposal)
        {
            if (!acceptedSelectors.Contains(p.Selector)) continue;

            var matchKey = bySelector.ContainsKey(p.Selector) ? p.Selector
                : p.MatchesExisting is { } m && bySelector.ContainsKey(m) ? m
                : null;

            if (matchKey is not null)
            {
                if (!enrichExisting) continue;
                var note = string.IsNullOrWhiteSpace(p.Logic) ? p.Description : p.Logic;
                if (string.IsNullOrWhiteSpace(note)) continue;
                var current = bySelector[matchKey].ElementLogic;
                if (current.Contains(note, StringComparison.Ordinal)) continue;
                extraLogic[matchKey] = Clip(note.Trim(), MaxLogic);
                continue;
            }

            // A selector longer than the column can't be stored, and one that
            // long is not a selector a function should depend on anyway.
            if (p.Selector.Length > MaxSelector) continue;
            if (addedSelectors.Add(p.Selector)) added.Add(p);
        }

        var order = 0;
        var elements = new List<SignatureElement>();
        foreach (var e in existing)
        {
            var logic = e.ElementLogic;
            if (extraLogic.TryGetValue(e.ElementSelector, out var obs))
                logic = string.IsNullOrWhiteSpace(logic) ? $"Observed: {obs}" : $"{logic}\nObserved: {obs}";
            elements.Add(SignatureElement.Create(
                e.ElementSelector, e.Category, e.ElementType, e.ElementDescription, order++, logic, e.Origin));
        }
        foreach (var p in added)
        {
            elements.Add(SignatureElement.Create(
                p.Selector,
                SignatureMapper.ParseCategory(p.Category),
                Clip(string.IsNullOrWhiteSpace(p.ElementType) ? "element" : p.ElementType.Trim(), MaxType),
                Clip(p.Description ?? string.Empty, MaxDescription),
                order++,
                Clip(p.Logic ?? string.Empty, MaxLogic),
                ElementOrigin.Recorded));
        }

        string? flows = null;
        int addedNodes = 0, addedEdges = 0;
        var selectors = elements.Select(e => e.ElementSelector).ToHashSet(StringComparer.Ordinal);
        // Replace-learned drops only what recordings taught: edges marked
        // `source: recorded` and anything pointing at a removed selector.
        // Source-derived and hand-edited flows survive. The base is computed
        // even when flows aren't applied — removing selectors makes the
        // repository wipe UserFlowsJson, and the caller re-sets it from here.
        var baseFlows = replaceLearned
            ? StripLearnedFlows(signature.UserFlowsJson, selectors)
            : signature.UserFlowsJson;
        if (applyFlows && !string.IsNullOrWhiteSpace(proposalFlowsJson))
            (flows, addedNodes, addedEdges) = MergeFlows(baseFlows, proposalFlowsJson, selectors);
        else if (replaceLearned)
            flows = baseFlows;

        return new MergeResult(elements, flows, added.Count, extraLogic.Count, addedNodes, addedEdges, removed);
    }

    /// <summary>
    /// Graph union keyed by id. Existing nodes/edges win; a recorded node whose
    /// id already exists is the same UI state (the analyzer is told to reuse
    /// ids) and is skipped; a recorded edge whose id collides is re-keyed
    /// <c>&lt;id&gt;.rec</c>. Edges whose endpoints or selector don't resolve are dropped.
    /// </summary>
    public static (string Json, int AddedNodes, int AddedEdges) MergeFlows(
        string? existingJson, string proposedJson, IReadOnlySet<string> knownSelectors)
    {
        var existing = ParseGraph(existingJson);
        var proposed = ParseGraph(proposedJson);

        var nodes = (JsonArray)existing["nodes"]!;
        var edges = (JsonArray)existing["edges"]!;
        var nodeIds = nodes.OfType<JsonObject>().Select(Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var edgeIds = edges.OfType<JsonObject>().Select(Id).OfType<string>().ToHashSet(StringComparer.Ordinal);

        var addedNodes = 0;
        foreach (var n in ((JsonArray)proposed["nodes"]!).OfType<JsonObject>())
        {
            var id = Id(n);
            if (id is null || nodeIds.Contains(id)) continue;
            var nc = (JsonObject)n.DeepClone();
            nc["source"] = "recorded";
            nodes.Add(nc);
            nodeIds.Add(id);
            addedNodes++;
        }

        var addedEdges = 0;
        foreach (var e in ((JsonArray)proposed["edges"]!).OfType<JsonObject>())
        {
            var id = Id(e);
            var from = EpisodeBuilder.Str(e["from"]);
            var to = EpisodeBuilder.Str(e["to"]);
            var sel = EpisodeBuilder.Str(e["elementselector"]);
            if (id is null || from is null || to is null) continue;
            if (!nodeIds.Contains(from) || !nodeIds.Contains(to)) continue;
            if (sel is not null && !knownSelectors.Contains(sel)) continue;
            if (edgeIds.Contains(id))
            {
                // Same id + same transition = the edge we already have (e.g.
                // the same proposal applied twice). Only a genuinely different
                // edge under a colliding id is kept, re-keyed.
                var same = edges.OfType<JsonObject>().Any(x =>
                    Id(x) == id &&
                    EpisodeBuilder.Str(x["from"]) == from &&
                    EpisodeBuilder.Str(x["to"]) == to &&
                    EpisodeBuilder.Str(x["elementselector"]) == sel);
                if (same) continue;
                id += ".rec";
                if (edgeIds.Contains(id)) continue;
            }
            var copy = (JsonObject)e.DeepClone();
            copy["id"] = id;
            copy["source"] = "recorded";
            edges.Add(copy);
            edgeIds.Add(id);
            addedEdges++;
        }

        return (existing.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), addedNodes, addedEdges);
    }

    /// <summary>
    /// The flow graph minus what recordings contributed: edges tagged
    /// <c>source: recorded</c>, edges whose selector is no longer an element,
    /// and recorded nodes no remaining edge touches. Untagged (source-derived
    /// or hand-written) nodes and edges are kept as they are.
    /// </summary>
    public static string StripLearnedFlows(string? json, IReadOnlySet<string>? keptSelectors = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        var g = ParseGraph(json);
        var edges = (JsonArray)g["edges"]!;
        foreach (var e in edges.OfType<JsonObject>().ToList())
        {
            var sel = EpisodeBuilder.Str(e["elementselector"]);
            if (IsRecorded(e) || (keptSelectors is not null && sel is not null && !keptSelectors.Contains(sel)))
                edges.Remove(e);
        }
        var used = edges.OfType<JsonObject>()
            .SelectMany(e => new[] { EpisodeBuilder.Str(e["from"]), EpisodeBuilder.Str(e["to"]) })
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        var nodes = (JsonArray)g["nodes"]!;
        foreach (var n in nodes.OfType<JsonObject>().ToList())
            if (IsRecorded(n) && (Id(n) is not { } nid || !used.Contains(nid))) nodes.Remove(n);
        return g.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static bool IsRecorded(JsonObject o) => EpisodeBuilder.Str(o["source"]) == "recorded";

    private static JsonObject ParseGraph(string? json)
    {
        JsonObject? o = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try { o = JsonNode.Parse(json) as JsonObject; } catch (JsonException) { }
        }
        o ??= new JsonObject();
        if (o["nodes"] is not JsonArray) o["nodes"] = new JsonArray();
        if (o["edges"] is not JsonArray) o["edges"] = new JsonArray();
        return o;
    }

    private static string? Id(JsonObject o) => EpisodeBuilder.Str(o["id"]);
}
