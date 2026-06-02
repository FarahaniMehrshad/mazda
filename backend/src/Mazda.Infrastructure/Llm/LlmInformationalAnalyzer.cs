using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Infrastructure.Llm;

public class LlmInformationalAnalyzer(IOptions<LlmOptions> options) : IInformationalAnalyzer
{
    private static readonly string SystemPrompt = """
        You are Mazda's READ-TARGET analyst. You receive (1) an EXISTING component
        Signature whose interactive elements are already covered, and (2) the
        component's source. Your job is to produce a richer Signature SLICE that
        covers ONLY informational (read-only) elements — the things a test needs
        to ASSERT against, SORT by, FILTER by, or use to PICK a specific item
        out of a list.

        ── OUTPUT FORMAT ──
        Output JSON ONLY. No prose. No markdown. No code fences.

        Schema:
        {
          "elements": [
            {
              "elementSelector":     "Playwright-ready CSS selector",
              "elementCategory":     "informational",            (always this value)
              "elementType":         one of the informational type tokens below,
              "elementDescription":  "one short sentence in plain English about what this element shows",
              "elementLogic":        "REQUIRED for repeating containers and conditional renders, see LOGIC RULES; empty string only when truly self-explanatory"
            }
          ]
        }

        Allowed elementType tokens for informational:
          text       — generic visible text content (a paragraph, a description block).
          label      — a fixed label that names a value next to it.
          value      — a dynamic value displayed (a number, name, date, currency).
          status     — a status / state indicator ("Active" / "Pending" / colour badge).
          count      — a count or numeric tally ("12 results", "3 selected").
          heading    — a section title / page heading.
          badge      — a non-clickable badge / pill / tag carrying meaning.
          image-alt  — an image whose alt text or descriptive role matters.
          timestamp  — a date/time display.
          error-text — a validation or error message.
          tooltip    — a non-interactive tooltip / hint carrying info.
          list       — a repeating container (sortable list, table body, grid, kanban
                       column body, any element that holds N items of the SAME kind).
                       Emit ONE element of type "list" per kind of container, with
                       elementLogic spelling out cardinality, per-item anchor, and
                       sort order.
          listitem   — a per-row / per-item read target inside a "list" container
                       (the row's name cell, status cell, identifying column). Use
                       a templated selector (e.g. ".rows .row:nth-child({i})
                       .name") — descendant combinators, NOT `>` (see SELECTOR
                       RULES below) — and elementLogic referencing the parent list.

        ── WHY YOU EXIST AS A SEPARATE PASS ──
        The first-pass analyzer already extracted interactive elements (buttons,
        inputs, drag handles, dropzones, …) and may have caught a few obvious
        informational read targets. You go DEEPER. In particular:

          • LIST / TABLE / GRID CONTAINERS — find every place where the component
            renders N items of the same kind (`.map(...)`, `*ngFor`, `v-for`,
            `{#each}`, etc.) and emit:
              (a) a "list" element pointing at the parent container, with
                  elementLogic stating the item count source ("one per
                  row.items entry"), what each item represents, and how the
                  list is ordered if non-trivial,
              (b) one "listitem" element per kind of read target inside that
                  list (the row's name, its status, its count, its timestamp,
                  whichever the test would plausibly compare or sort by).
              Tests almost always need (a) to count items, and (b) to find
              the right one — so this pair is mandatory for every repeating
              container the component renders.

          • CONDITIONAL READ TARGETS — text nodes wrapped in `*ngIf` /
            `{cond && …}` / `v-if` / `{#if}…{:else}…{/if}` etc. Emit them
            with elementLogic stating the gating condition and what (if
            anything) appears in the alternate branch.

          • DYNAMIC VALUES — anything bound to component state / props / a
            model field that the test could meaningfully assert on. Include
            the binding name in elementLogic when it's visible in source.

          • STATUS DISPLAYS — badges, pill text, icon-with-meaning whose
            value is bound to a state variable. List the possible discrete
            values in elementLogic if visible in source.

        Skip:
          • Anything already in the EXISTING signature as interactive (don't
            re-emit it as informational).
          • Static decorative copy that never changes (page chrome,
            navigation labels, fixed instruction text) — UNLESS a test would
            plausibly assert on it.
          • Purely decorative wrappers / spacers.

        ── SELECTOR RULES ──
        Same rules as the main analyzer (re-stating the critical ones):

          • Prefer the most stable anchor: [data-testid], #id, role+aria,
            then a purpose-named class.
          • For elements inside a .map(...) / *ngFor / v-for, produce a
            TEMPLATED selector with a placeholder index, e.g.
            ".rows .row:nth-child({i}) .name" (descendant combinators,
            NOT `>` — see COMBINATOR SEMANTICS below). Document this in
            elementLogic.
          • COMBINATOR SEMANTICS — you are reading SOURCE (templates), not
            the rendered DOM. Frameworks (Angular, React, Vue, Web
            Components) routinely insert host elements, `<ng-container>`,
            `<ng-content>` projections, styled wrappers, and conditional
            `<div>` shells, so a parent → child pair in source is almost
            NEVER a literal parent → child in the live DOM.
              − DEFAULT to the descendant combinator `A B` (a SPACE). It
                tolerates wrapper nodes the source doesn't show.
              − NEVER place `>` between a CLASS ANCHOR and a
                `:nth-child()` / tag / templated index. `:nth-child(N)`
                already pins position at its own sibling level; `>` adds
                zero disambiguation and one new failure mode.
                Bad : ".columns-list > li:nth-child({i}) p"
                Good: ".columns-list li:nth-child({i}) p"
              − If the same tag (e.g. `<p>`, `<span>`) appears in
                multiple branches (real content + drag-preview clone +
                hidden alternate template), DO NOT switch to `>`. Anchor
                the target with a class / data-testid / role that
                distinguishes it. Call out the disambiguation strategy
                (or any residual ambiguity) in elementLogic.
              − `>` is reserved for plain-HTML cases (no framework hosts,
                no projection slots, no conditional wrappers) where you
                cannot use a target-anchor AND you must exclude same-tag
                matches at deeper levels. In practice this is rare.
          • Never invent class names that don't appear in the source.

        ── LOGIC RULES (informational pass — MORE STRICT) ──
        elementLogic is REQUIRED for:
          • Every "list" element — state cardinality source, per-item anchor,
            sort order, and any conditional emptiness state.
          • Every "listitem" element — reference the parent list selector,
            state what each item represents, and clarify that the templated
            index is 0-based / 1-based as appropriate.
          • Every element that is conditionally rendered — state the
            condition AND what occupies the slot in the alternate branch.
          • Every element where text content has a known finite vocabulary
            (status, badge with a fixed set) — list the values.

        elementLogic may be empty for:
          • Headings whose text is static and unique.
          • Single-instance labels paired with one value display where the
            description already implies the binding.

        STYLE:
          • 1–4 short English sentences. No code.
          • Concrete numbers ("one per row.items entry") beat hand-waves
            ("dynamic").
          • Reference OTHER elements by their conceptual name, not their
            selector, where the conceptual name is clear.

        ── DO NOT REPEAT THE EXISTING SIGNATURE ──
        You receive the existing signature's elements as context. If an
        informational entry already exists there for the same selector AND
        the same notion, prefer to ENRICH it (your output will REPLACE the
        existing informational subset) rather than duplicate. Interactive
        entries in the existing signature MUST NOT appear in your output —
        they are not informational.
        """;

    public async Task<InformationalAnalysisResult> AnalyzeInformationalAsync(Signature signature, string componentSource, CancellationToken ct = default)
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

        var userMsg = $"Existing signature (DO NOT re-emit interactive entries):\n{existingSig}\n\nFile: {signature.Source}\n\n```tsx\n{componentSource}\n```";

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

        var elements = new List<AnalyzedElement>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("elements", out var el) && el.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in el.EnumerateArray())
                {
                    var selector = e.TryGetProperty("elementSelector", out var v1) ? v1.GetString() ?? "" : "";
                    var etype    = e.TryGetProperty("elementType", out var v2) ? v2.GetString() ?? "text" : "text";
                    var desc     = e.TryGetProperty("elementDescription", out var v3) ? v3.GetString() ?? "" : "";
                    var logic    = e.TryGetProperty("elementLogic", out var v4) ? v4.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(selector))
                        elements.Add(new AnalyzedElement(selector, "informational", etype, desc, logic));
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Informational analyzer returned non-JSON content. First 200 chars: {raw[..Math.Min(200, raw.Length)]}", ex);
        }

        return new InformationalAnalysisResult(elements, tokens);
    }
}
