using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Infrastructure.Llm;

/// <summary>
/// "Learn by watching", step 2: reads the action → effect episodes of one or
/// more recorded sessions of a component and proposes ONE consolidated set of
/// signature elements (with OBSERVED selectors) plus user-flow additions.
/// Applies the same signature rules as source analysis
/// (<see cref="SignatureRules"/>), with observation as the evidence.
/// Output is a proposal — the application layer validates it against what was
/// actually observed and the user reviews it before anything is merged.
/// </summary>
public class LlmRecordingAnalyzer(IOptions<LlmOptions> options) : IRecordingAnalyzer
{
    private static readonly string SystemPrompt = """
        You are MAZDA's behaviour analyst. A user operated a web UI while MAZDA
        watched through the Chrome DevTools Protocol, usually over several short
        sessions, each demonstrating one task. Your job: produce the component's
        SIGNATURE — every element a script must act on or read — from what was
        observed, exactly as a source-code analyst would from the component's code.

        ── INPUT ──
          • `signature` — what MAZDA already knows (elements, user flows). Often
            empty: these components ship without usable source, which is why they
            were recorded.
          • `recordings[]` — each { index, label, intent?, episodes }.
              intent  OPTIONAL — the user's own statement of what they were
                      demonstrating in that session ("archive a task, not
                      delete it", "the error case: save with an empty title").
                      Use it to interpret ambiguous actions and to name that
                      session's flow — but the episodes are the truth: if the
                      intent and what was recorded disagree, say so in that
                      session's `understanding` and follow the episodes.
              label   a display name; usually a timestamp — ignore it unless it
                      reads like a description.
            An episode is one user action
            and what the page did in response:
              action      click | fill | select | key | drag | scroll | navigate
              target      the element acted on:
                            best        a selector unique on the page at that moment
                            candidates  observed selectors, with `count` = number of
                                        elements it matched then (1 = unique,
                                        >1 = it matches several, e.g. one per row)
                            role, name, tag, text (DOM text), data (data-* attributes),
                            textTransform (CSS display transform of the text)
                            collection  present when the target sits in a REPEATED
                                        set: { item: selector matching EVERY item,
                                        count, index (0-based), inner: path from the
                                        item to the target, itemOutline: the item's
                                        structure }
              value / key what was typed / chosen / pressed ("•••" = masked)
              effects     appeared     elements that mounted — each with
                                       collection (if it is a list item) and
                                       outline: its meaningful descendants as
                                       { sel (path inside it), tag, role, name,
                                         text, data, checked, value, options,
                                         textTransform }
                          rerendered   on an appeared item: it was re-mounted with
                                       no change (lists that rebuild on every update)
                          itemChanges  what ACTUALLY changed inside list items
                                       across a re-render: { item, index, sel,
                                       field, from, to } (field = text | checked |
                                       value | present | data-*)
                          disappeared, changed (attribute flips), text (text changes)
              network     requests the action triggered (method, templated path, status)
              urlBefore / urlAfter, settleMs, count (repeated N times)

        """ + SignatureRules.TypeVocabulary + "\n" + SignatureRules.ListRules + "\n" +
        SignatureRules.InformationalRules + "\n" + SignatureRules.LogicRules + """

        ── SELECTOR RULES (hard — selectors that weren't observed are discarded) ──
          • Use selectors observed in the recordings: `best`, `candidates[].selector`,
            `collection.item`, `collection.item` + " " + `collection.inner`, or an
            outline path anchored under its item: `collection.item` + " " + `sel`
            (e.g. "#list > li h3.task-title"). You may join two observed pieces with
            a space. Never invent class names, attributes or structure.
          • Prefer, for ONE-OF-A-KIND elements: unique test-id, unique role=…[name=…]
            (count 1), unique #id, unique stable attribute, then classes.
          • For anything that exists ONCE PER ITEM of a collection (row controls,
            row fields): build the selector from `collection.item` + inner/outline
            path so it matches that thing in every item. Never use a candidate whose
            name/text contains one item's data (`role=checkbox[name="Mark \"x\" as
            done"]`) or a positional path pinned to one item.
          • A candidate with count > 1 is NOT unique; only use it as a per-item selector.
          • Read state from where it lives: data-* attributes, checked, value, a
            dedicated element's text — not by parsing a whole row's text.
          • If an element is the same control as one in `signature.elements`, set
            `matchesExisting` to that selector.

        ── CONSOLIDATE ──
          The recordings show the SAME component from different tasks. Emit each
          element ONCE (merge what every recording taught about it); `evidence` lists
          "<recording index>:<episode i>" references. Expect a compact set: one
          entry per distinct control or read target, plus the list/listitem pairs.

        ── FLOWS ──
          One chain of states/transitions per demonstrated task (add, edit, delete,
          toggle…), sharing states across tasks: `state.initial`, `state.editing`…
          Reuse existing node ids from `signature.userFlows` when the state is the
          same. Edges reference element selectors from your list or the signature.
          Never copy masked values ("•••") into an edge `value`.

        ── OUTPUT (JSON only, no prose, no fences) ──
        {
          "elements": [
            {
              "selector":        "<grounded selector>",
              "category":        "interactive" | "informational",
              "elementType":     "<token from the vocabulary>",
              "description":     "one sentence, user terms",
              "logic":           "per LOGIC NOTES; may be empty",
              "matchesExisting": "<existing selector>" | null,
              "evidence":        ["0:3", "2:1"]
            }
          ],
          "flows": {
            "nodes": [ { "id": "state.x", "label": "...", "description": "..." } ],
            "edges": [ { "id": "edge.x", "from": "state.a", "to": "state.b",
                         "action": "click | fill | check | uncheck | selectoption | press | hover | drag",
                         "elementselector": "<selector>", "label": "...", "description": "...",
                         "value": "optional" } ]
          },
          "sessions": [
            { "index": <recording index>,
              "title": "3–7 word name of what this session demonstrates, e.g. \"Add three tasks with priorities\"",
              "understanding": "1–3 sentences, plain language, addressed to the user: what you understood the user did and what the UI did in response, including anything you could NOT tell or that contradicts their intent" }
          ],
          "notes": "short: anything surprising (re-render behaviour, timing, masked fields)"
        }

        ── SESSIONS ──
          Exactly one entry per recording. The user reads `understanding` to
          check you got it right and refines their intent if not — be concrete
          ("You added 'homework' with High priority; the list re-rendered and
          the new row appeared last"), never generic ("The user interacted
          with the UI").

        ── FINAL CHECK ──
          • Every repeated container has its list + listitem pair, and per-item
            controls match every item? • No selector bound to one item's text or
            position? • Types from the vocabulary? • Each element once?
        """;

    public async Task<RecordingAnalysisResult> AnalyzeAsync(
        Signature signature,
        IReadOnlyList<RecordedSessionInput> sessions,
        bool ignoreLearned = false,
        CancellationToken ct = default)
    {
        var opts = options.Value;
        // GeneratorModel, not AnalyzerModel: one call per analysis, and
        // interpreting behaviour (which candidate generalises, which effect a
        // script must wait on) is where small models slip — and every
        // function generated later inherits the result.
        var chat = ChatClientFactory.ForModel(opts, opts.GeneratorModel);

        var recordings = new JsonArray();
        foreach (var s in sessions)
        {
            JsonNode? episodes;
            try { episodes = JsonNode.Parse(s.EpisodesJson); }
            catch (JsonException ex) { throw new InvalidOperationException($"Stored episodes of '{s.Label}' are not valid JSON.", ex); }
            var rec = new JsonObject { ["index"] = recordings.Count, ["label"] = s.Label };
            if (!string.IsNullOrWhiteSpace(s.Intent)) rec["intent"] = s.Intent;
            rec["episodes"] = episodes;
            recordings.Add(rec);
        }

        var known = signature.Elements
            .Where(e => !ignoreLearned || e.Origin != ElementOrigin.Recorded)
            .OrderBy(e => e.Order);
        var payload = new JsonObject
        {
            ["signature"] = new JsonObject
            {
                ["name"] = signature.Name,
                ["type"] = signature.Type,
                ["humanDescription"] = signature.HumanDescription,
                ["elements"] = new JsonArray(known.Select(e => (JsonNode?)new JsonObject
                {
                    ["selector"] = e.ElementSelector,
                    ["category"] = e.Category.ToString().ToLowerInvariant(),
                    ["elementType"] = e.ElementType,
                    ["description"] = e.ElementDescription,
                    ["logic"] = string.IsNullOrWhiteSpace(e.ElementLogic) ? null : e.ElementLogic,
                }).ToArray()),
                ["userFlows"] = ignoreLearned ? null : TryParse(signature.UserFlowsJson),
            },
            ["recordings"] = recordings,
        };

        var completion = await chat.CompleteChatSafeAsync(
            new List<ChatMessage>
            {
                new SystemChatMessage(SystemPrompt),
                new UserChatMessage(
                    $"Analyze these {sessions.Count} recorded session(s) of the component and produce its signature elements + flows.\n\n" +
                    payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false })),
            },
            new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() },
            ct);

        var raw = LlmComponentAnalyzer.StripCodeFences(completion.Value.Content[0].Text ?? "{}");
        var tokens = completion.Value.Usage?.TotalTokenCount ?? 0;

        JsonObject root;
        try { root = JsonNode.Parse(raw) as JsonObject ?? throw new JsonException("not an object"); }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Recording analyzer returned non-JSON content. First 200 chars: {raw[..Math.Min(200, raw.Length)]}", ex);
        }

        var elements = new List<ProposedElement>();
        foreach (var e in (root["elements"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var selector = Str(e["selector"])?.Trim();
            if (string.IsNullOrWhiteSpace(selector)) continue;
            elements.Add(new ProposedElement(
                selector,
                Str(e["category"]) ?? "interactive",
                (Str(e["elementType"]) ?? "text").Trim().ToLowerInvariant(),
                Str(e["description"]) ?? string.Empty,
                Str(e["logic"]) ?? string.Empty,
                string.IsNullOrWhiteSpace(Str(e["matchesExisting"])) ? null : Str(e["matchesExisting"])!.Trim(),
                ParseEvidence(e["evidence"])));
        }

        var flows = root["flows"] as JsonObject ?? new JsonObject { ["nodes"] = new JsonArray(), ["edges"] = new JsonArray() };

        var understood = new List<SessionUnderstanding>();
        foreach (var s in (root["sessions"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var idx = s["index"] is JsonValue iv && iv.TryGetValue<int>(out var ix) ? ix
                    : s["index"] is JsonValue sv && sv.TryGetValue<string>(out var ixs) && int.TryParse(ixs, out var ixp) ? ixp : -1;
            if (idx < 0 || idx >= sessions.Count || understood.Any(u => u.SessionId == sessions[idx].Id)) continue;
            understood.Add(new SessionUnderstanding(sessions[idx].Id, Str(s["title"]) ?? string.Empty, Str(s["understanding"]) ?? string.Empty));
        }

        return new RecordingAnalysisResult(elements, flows.ToJsonString(), Str(root["notes"]) ?? string.Empty, tokens,
            Sessions: understood);
    }

    /// <summary>
    /// Evidence arrives as "r:i" strings (joint analysis) or bare episode
    /// numbers. Stored as episode numbers (the recording index is dropped:
    /// the UI shows "seen in episodes …").
    /// </summary>
    private static List<int> ParseEvidence(JsonNode? node)
    {
        var list = new List<int>();
        foreach (var n in node as JsonArray ?? new JsonArray())
        {
            if (n is not JsonValue v) continue;
            if (v.TryGetValue<int>(out var i)) list.Add(i);
            else if (v.TryGetValue<string>(out var s))
            {
                var part = s.Contains(':') ? s[(s.LastIndexOf(':') + 1)..] : s;
                if (int.TryParse(part, out var j)) list.Add(j);
            }
        }
        return list.Where(x => x >= 0).Distinct().ToList();
    }

    private static JsonNode? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); } catch (JsonException) { return null; }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
