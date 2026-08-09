using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Infrastructure.Llm;

/// <summary>
/// Step 4 of the signature pipeline. Builds a SEMANTIC RELATIONSHIP GRAPH
/// over the signature's elements: which container labels which children,
/// which control affects which field, which read target identifies which
/// row, which pair is mutually exclusive, etc. Downstream test-gen uses
/// this graph to resolve relational phrasing in user prompts ("click the
/// delete button next to the row whose name is X").
///
/// Mirrors LlmInformationalAnalyzer's wiring: receives the existing
/// Signature + raw source, asks the LLM for JSON, returns it verbatim
/// (the use case persists it onto the signature).
/// </summary>
public class LlmRelationshipAnalyzer(IOptions<LlmOptions> options) : IRelationshipAnalyzer
{
    private static readonly string SystemPrompt = """
        You are Mazda's RELATIONSHIP analyst. You receive (1) an EXISTING
        component Signature whose interactive AND informational elements are
        already extracted, and (2) the component's source. Your job is to
        produce a GRAPH that captures the SEMANTIC RELATIONS between those
        elements — the connections that are obvious to a human looking at the
        rendered UI but hidden in the DOM tree / template structure.

        A test-generation LLM downstream consumes your graph to resolve
        relational phrasing in user prompts:
          • "select the column whose name is 'price'"            — refers-to / identifies
          • "click the delete button next to row X"               — sibling-of / in-row
          • "tick the checkbox under 'Sorting columns'"           — grouped-under
          • "this button submits the form on the right"           — controls / submits
          • "either show the value or show the editor"            — mutually-exclusive
          • "the count next to 'Selected' shows N items"          — label-value-pair
          • "the title above the grid says 'Available columns'"   — container-labeled-by

        ── OUTPUT FORMAT ──
        Output JSON ONLY. No prose. No markdown. No code fences.

        Schema:
        {
          "nodes": [
            {
              "id":          "stable string id; usually equal to elementSelector",
              "selector":    "the Playwright CSS selector from the signature, EXACT match",
              "label":       "1-6 word human-readable label for graph display",
              "role":        "interactive" | "informational" | "container" | "section",
              "description": "optional 1-sentence note shown in the viewer hover (≤140 chars)"
            }
          ],
          "edges": [
            {
              "id":          "stable string id, unique within the graph",
              "from":        "source node id",
              "to":          "target node id",
              "relation":    one of the RELATION TYPES below,
              "label":       "1-4 word edge label for graph display (e.g. 'labels', 'controls')",
              "description": "1-2 sentence explanation of what the relation means in THIS UI"
            }
          ]
        }

        ── NODE RULES ──
          1. Use the signature's EXISTING elements as the source of truth.
             EVERY node's `selector` MUST appear in the input signature
             unless it is a pure SECTION / CONTAINER you discovered in source
             that isn't in the signature (see rule 3).
          2. `id` should usually equal `selector`. When a selector is too long
             or contains characters that hurt graph rendering, you may use a
             short slug — but `selector` must still be exact.
          3. You MAY introduce additional `container` / `section` nodes that
             aren't in the signature when the source clearly defines them and
             they serve as grouping pivots. Example: a `<fieldset>` whose
             `<legend>` says "Grouping columns" — the fieldset itself isn't an
             element a test would click, but it's the pivot every "X under
             grouping columns" edge lands on. Give it a stable selector you
             can derive (a class anchor, an aria-labelledby, a header
             text, …). Mark `role: "section"` or `role: "container"`.
          4. DO NOT duplicate. One node per distinct selector. If two
             different relations connect the same pair of nodes, emit two
             edges, not two node copies.
          5. Aim for a graph the user can read at a glance: typically 8–40
             nodes for a real component. Over-emitting tiny leaf nodes is
             noise; under-emitting connectors hides the structure.

        ── EDGE RULES ──
        Allowed `relation` values (use the most specific one that fits):

          • "labels"             — Node A's text content NAMES node B
                                   (a heading above a section, a label
                                   element htmlFor a field, an aria-labelledby
                                   target, the text in a card header that
                                   tells you what the card is about).
          • "grouped-under"      — Node A is a member of the group / section
                                   represented by node B. Use this for items
                                   inside a labeled fieldset, options under a
                                   header, rows inside a card. Direction is
                                   ALWAYS member → group.
          • "controls"           — Node A's interaction affects node B's
                                   state (submit button → form, toggle →
                                   field, tab → panel, sort header → table).
          • "submits"            — Specific case of controls where A submits
                                   the form / dialog containing B.
          • "opens"              — A's interaction reveals / mounts B (a
                                   button that opens a dropdown panel, a
                                   trigger that mounts a popover).
          • "closes"             — A's interaction dismisses B (a cancel /
                                   close / × button).
          • "mutually-exclusive" — A and B occupy the same slot but cannot
                                   both be visible (display vs. inline-edit
                                   input, "loading" vs. "ready" badge).
          • "depends-on"         — A only appears / is enabled when B has a
                                   value or particular state. Use for inputs
                                   that unlock other inputs.
          • "refers-to"          — A's visible text IDENTIFIES the same
                                   entity that B operates on. The row's
                                   name cell `refers-to` the row's delete
                                   button (same row). Tests use this edge
                                   for "click the delete next to row X".
          • "sibling-of"         — A and B are co-children of the same
                                   logical row / card / item. Use this
                                   when neither labels nor controls the
                                   other, but they share a row-level
                                   identity. Tests use this for "click
                                   the X next to Y".
          • "list-item-of"       — A is a single repeated item (the row /
                                   card template) inside the list container
                                   B. Direction is item → container.
          • "value-of"           — A is the dynamic value display whose
                                   meaning is fixed by the label B.
                                   Pair with a "labels" edge B→A for the
                                   classic label↔value pair.
          • "related"            — Catch-all for genuinely-related elements
                                   that don't fit the categories above.
                                   Use sparingly — prefer a specific
                                   relation when one applies.

        Direction matters. For "labels", the LABELING node is `from`, the
        labeled node is `to`. For "controls", the CONTROL is `from`, the
        affected node is `to`. For "grouped-under" and "list-item-of", the
        MEMBER is `from`, the GROUP/CONTAINER is `to`. Inverting these
        breaks downstream prompt resolution.

        ── HOW TO DISCOVER RELATIONS ──
        Walk the source TREE, not the rendered DOM. The signals you're
        looking for:

          1. SECTION HEADINGS / FIELDSET LEGENDS / CARD TITLES.
             Anywhere the source has a heading or title element followed by
             a region with multiple interactive / informational elements,
             emit:
               (a) a `section` node for the region (or reuse a container
                   anchor selector if there's one),
               (b) a `labels` edge from heading → section,
               (c) a `grouped-under` edge from EACH child element → section.
             This is the single most common relation a user prompt
             implicitly refers to. ALWAYS look for this.

          2. LABEL ↔ FIELD PAIRS.
             `<label htmlFor="id">…</label>` + `<input id="id">`, or any
             label element rendered adjacent to a single input/select. Emit
             a `labels` edge label → field. If the source binds a separate
             read-display next to the same label (a value display rather
             than an editable field), also emit `value-of` field → label
             when the same label is reused for both.

          3. AFFECTED REGIONS.
             A submit button at the bottom of a form: `submits` edge
             button → form-root. A toggle that disables a fieldset:
             `controls` toggle → fieldset. A tab that swaps the visible
             panel: `controls` tab → panel.

          4. ROW-LEVEL GROUPING.
             A `.map(...)` / `*ngFor` / `v-for` body containing a name cell
             + a delete button + a status badge: emit `sibling-of` edges
             between them (or a `refers-to` edge from the name cell to
             every action in the same row, plus `list-item-of` edges from
             the row template to the list container).

          5. CONDITIONAL TWINS.
             `{cond ? <A/> : <B/>}` / `*ngIf … *ngIf else` patterns where A
             and B occupy the same slot: emit `mutually-exclusive` A ↔ B.

          6. ENABLEMENT DEPENDENCIES.
             A field whose `disabled` binding references another field's
             value, or a button whose `disabled` references a form's
             validity: `depends-on` dependent → dependency.

          7. POPUP / DIALOG TRIGGERS.
             A button whose onClick mounts a modal / popover / dropdown:
             `opens` button → popup. The popup's close button: `closes`
             button → popup.

        ── SELECTOR DISCIPLINE ──
        Every node's `selector` MUST be EXACTLY a string that appears (a)
        in the existing signature's `elementSelector` list, OR (b) is a
        new section/container selector you derived from source using the
        SAME selector rules the signature pipeline uses (data-testid,
        purpose-named class, role+aria, descendant combinator NOT `>`).
        Do not paraphrase a signature selector; preserve it byte-for-byte
        (Playwright cares about exact strings).

        ── DON'T HALLUCINATE ──
          • Do NOT invent elements that aren't in the signature AND
            aren't in the source. If you can't point to either, drop the
            node.
          • Do NOT manufacture relations the source doesn't justify.
            "These two elements look related" is not enough — there has
            to be a textual / structural / behavioural anchor.
          • Empty graph is acceptable if the component is a single
            standalone control with no relations to other elements.

        ── COVERAGE EXPECTATIONS ──
        A real form / wizard / grid component typically yields 10–40
        edges. If your output has 0–2 edges on a multi-section component,
        you are missing the obvious section-labels / grouped-under
        structure. If your output has 100+ edges, you are emitting noisy
        "related" edges between everything — trim back to the meaningful
        ones.
        """;

    public async Task<RelationshipAnalysisResult> AnalyzeRelationshipsAsync(
        Signature signature, string componentSource, CancellationToken ct = default)
    {
        var opts = options.Value;
        var chat = ChatClientFactory.ForModel(opts, opts.AnalyzerModel);

        var existingSig = JsonSerializer.Serialize(new
        {
            name = signature.Name,
            type = signature.Type,
            humanDescription = signature.HumanDescription,
            elements = signature.Elements.OrderBy(e => e.Order).Select(e => new
            {
                selector = e.ElementSelector,
                category = e.Category.ToString().ToLowerInvariant(),
                elementType = e.ElementType,
                description = e.ElementDescription,
                logic = string.IsNullOrWhiteSpace(e.ElementLogic) ? null : e.ElementLogic,
            }),
        }, new JsonSerializerOptions { WriteIndented = true });

        var userMsg =
            $"Existing signature (your nodes MUST reuse these selectors verbatim, " +
            $"except for new `section` / `container` nodes you derive from source):\n" +
            $"{existingSig}\n\nFile: {signature.Source}\n\n```tsx\n{componentSource}\n```";

        var completion = await chat.CompleteChatAsync(
            new List<ChatMessage>
            {
                new SystemChatMessage(SystemPrompt),
                new UserChatMessage(userMsg),
            },
            new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
            },
            ct);

        var raw = LlmComponentAnalyzer.StripCodeFences(completion.Value.Content[0].Text ?? "{}");
        var tokens = completion.Value.Usage?.TotalTokenCount ?? 0;

        // Validate the JSON shape lightly here so a malformed payload is caught
        // at write time rather than at frontend render time. We accept the
        // bytes verbatim — no normalization — so the round-trip through the
        // DB is lossless.
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("nodes", out var nodes)
                || nodes.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("edges", out var edges)
                || edges.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "Relationship analyzer returned JSON without the required " +
                    "{ nodes: [...], edges: [...] } shape.");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Relationship analyzer returned non-JSON content. First 200 chars: " +
                $"{raw[..Math.Min(200, raw.Length)]}", ex);
        }

        return new RelationshipAnalysisResult(raw, tokens);
    }
}
