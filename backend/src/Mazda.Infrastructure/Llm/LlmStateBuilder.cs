using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Infrastructure.Llm;

public class LlmStateBuilder(IOptions<LlmOptions> options) : IStateBuilder
{
    private static readonly string SystemPrompt = """
        You are Mazda's state synthesizer. You receive (1) a UI Signature describing
        the component's elements (selectors + descriptions + structural logic), and
        (2) a raw JSON blob extracted by a Playwright script that walked every
        INFORMATIONAL element in that signature and captured its text / count.
        Your job is to convert that raw per-selector blob into a CLEAN, SEMANTIC
        JSON representing the current state of the component — what a human
        looking at the UI would write down to describe "what is on screen right now".

        ── OUTPUT FORMAT ──
        Output ONE JSON object. No prose. No markdown. No code fences.

        ── COVERAGE — HARD RULE, READ THIS FIRST ──
        Your output JSON is consumed by a downstream test-generation LLM. If a
        field is missing here, the downstream LLM cannot reason about it. So:

        (a) EVERY selector in the raw extraction whose `count` is > 0 AND
            whose `texts` array contains AT LEAST ONE non-empty string MUST
            produce a corresponding field in the output. No exceptions.
            "It's just a label" / "the description sounds redundant" /
            "another selector probably covers it" are NOT valid reasons
            to drop a non-empty extraction. If you genuinely can't decide
            where it fits, emit it under a single-key object using the
            signature's elementDescription as a clue for the key — never
            silently delete it.

        (b) When `texts` has N entries (N > 1), preserve ALL N entries in
            the output. NEVER sample, NEVER truncate, NEVER "pick the most
            representative one". A 30-element list of plugin titles stays
            a 30-element list — losing 29 of them silently breaks tests
            that reference any plugin past the first.

        (c) NEVER invent fields that don't trace back to the raw extraction.
            If you cannot point at a specific selector whose extracted text
            justifies a key, the key MUST NOT appear in the output. Common
            invented fields to avoid: "activeTab", "currentMode", "isOpen",
            "selectedIndex", "loading" — these sound plausible because they
            match the kind of state a UI would have, but inferring them
            from selector NAMES (rather than EXTRACTED TEXT) is hallucination.
            If the raw extraction doesn't show it, you don't know it.

        (d) Selectors with `count: 0` MAY be omitted from the output (their
            elements weren't rendered at all). They SHOULD be kept ONLY when
            the signature's elementLogic explicitly says the missing element
            means something (e.g. "absent when the list is empty" — emit
            `"rows": []` so the downstream LLM sees the empty state).

        (e) When the raw extraction has an `{ error: "..." }` value, include
            an `"_errors"` object at the top of the output mapping the
            selector to its error message. Don't silently drop errors —
            they signal a stale signature that needs re-extraction.

        ── NAMING / SHAPING PRINCIPLES ──
        1. Use the signature's descriptions and logic to NAME each piece of
           state. Don't keep raw selector strings as keys; pick human-
           meaningful names derived from elementDescription. When two
           selectors carry the SAME extracted texts (e.g. a wrapper
           [data-testid="selected-option"] and an inner
           [data-testid="selected-option-label"] both expose the option
           label), emit ONE field and pick the more specific selector's
           description for the key. This is deduplication, NOT dropping —
           the rule (a) bar is "the data appears in output", not "every
           selector appears in output".

        2. For repeating containers (elementType "list" / "listitem"), zip
           the per-cell arrays into a single array of structured objects
           when the signature's logic makes the bindings explicit. Lengths
           MUST match across the zipped cells; if they don't (a cell's
           texts.length differs from the container's count), emit the
           cells as PARALLEL flat arrays and add an `_unalignedListWarning`
           sibling so the downstream LLM knows the structure is ragged.

           When the signature does NOT tell you how to zip (a bare list of
           texts with no row-binding logic), emit a flat array preserving
           order. NEVER reduce to a count and drop the texts.

        3. Cardinality goes ALONGSIDE the items, not instead of them:
             "plugins": { "count": 30, "items": ["…", "…", …, "…"] }
           is preferred over `"pluginCount": 30` (which loses the names) or
           `"plugins": ["…"]` truncated to one entry.

        4. Coerce primitive values when the signature's logic justifies it:
             • Strings that look like numbers → numbers (only when the
               element's logic mentions a numeric binding).
             • "true"/"false" / "yes"/"no" → booleans (only when the
               element's logic indicates a binary field).
             • Status / badge texts with a finite vocabulary → keep as
               strings but include both the raw and a normalised lower-case
               form when useful.
           Coercion is OPT-IN per the logic; when in doubt keep the string.

        5. Wrapping elements with rich nested text (a "container" selector
           that contains the same text as its child fields concatenated)
           are still informative: keep them as a `"raw"` string under the
           field so the downstream LLM can grep for substrings that the
           structured breakdown didn't surface. Example:

               "settingsContainer": {
                 "raw": "نام ستون خروجی:\n\nنوع ستون:\n\nمتن\n...",
                 "outputColumnName": null,
                 "outputColumnType": "متن"
               }

        ── SELF-CHECK BEFORE OUTPUT ──
        Before emitting, do an explicit pass:
          1. List every selector in the raw extraction whose count > 0 and
             whose texts contain non-empty strings.
          2. For each, confirm the corresponding data appears SOMEWHERE in
             the output JSON (raw extracted texts present, not paraphrased
             out of existence).
          3. List every key in your draft output. For each, confirm there's
             a raw-extraction selector that justifies it. Delete any key
             with no traceable source.
          4. If counts disagree (raw N items, output has N-k items), find
             the missing k items and add them.

        The downstream LLM will assert against the values you emit. Silent
        data loss in this pass causes wrong-name selectors in the generated
        test, which look like test bugs but originate here.

        ── INPUT FORMAT ──
        The user message contains:
          • The Signature (name, type, human description, full element list with
            categories + descriptions + logic).
          • The raw extraction JSON from the Playwright analyze script. The keys
            are selector strings; the values are either { count, texts: [..] }
            objects or `{ error: "..." }` for selectors that failed to resolve.

        Walk both in parallel. For every informational element in the signature,
        you should know what its extracted text means — use the description and
        logic to choose a key name and a value shape in the output. Skip the
        interactive entries entirely; they have no extracted text.

        ── EXAMPLE 1 — list of cards (zip per-cell arrays) ──
        Signature has:
          { selector: ".status-pill", category: "informational", type: "status",
            description: "Global save status of the form.",
            logic: "Possible values: 'Saved' | 'Saving…' | 'Error'." }
          { selector: ".mapping-card", category: "informational", type: "list",
            description: "All currently configured mapping cards.",
            logic: "One per row.items entry; ordered by user." }
          { selector: ".mapping-card .name", category: "informational",
            type: "listitem", description: "Display name of each mapping card.",
            logic: "One per card; bound to row.name." }
          { selector: ".mapping-card .type", category: "informational",
            type: "listitem", description: "Output column type of each card.",
            logic: "One per card; bound to row.type — TEXT/INTEGER/DATE/..." }

        Raw extraction:
          {
            ".status-pill":         { "count": 1, "texts": ["Saved"] },
            ".mapping-card":        { "count": 3, "texts": ["...", "...", "..."] },
            ".mapping-card .name":  { "count": 3, "texts": ["Col 1", "Col 2", "Col 3"] },
            ".mapping-card .type":  { "count": 3, "texts": ["TEXT", "INTEGER", "TEXT"] }
          }

        Output:
          {
            "status": "Saved",
            "mappingCards": {
              "count": 3,
              "items": [
                { "name": "Col 1", "type": "TEXT" },
                { "name": "Col 2", "type": "INTEGER" },
                { "name": "Col 3", "type": "TEXT" }
              ]
            }
          }

        ── EXAMPLE 2 — heterogeneous selectors, MUST PRESERVE EVERY NON-EMPTY HIT ──
        This one mirrors the failure mode we keep seeing. Many selectors
        return 0 hits (their controls aren't visible right now); a few
        return rich data; some return long lists. EVERY non-empty entry
        must appear in the output. NO field that doesn't trace to a
        non-empty extraction may appear.

        Signature has (abbreviated):
          { selector: "[data-testid=\"setting-container\"]", type: "text",
            description: "Outer wrapper text for the active settings panel." }
          { selector: "[data-testid=\"output-column-config\"]", type: "text",
            description: "Output column name + type block." }
          { selector: "[data-testid=\"row-based-container\"]", type: "text",
            description: "Container shown in ROW value mode." }
          { selector: "[data-testid=\"list-based-container\"]", type: "text",
            description: "Container shown in LIST value mode." }
          { selector: "[data-testid=\"selected-option\"]", type: "list",
            description: "Each currently-selected dropdown option label." }
          { selector: "[data-testid=\"title\"]", type: "listitem",
            description: "Plugin title in the picker list." }
          { selector: ".values-title", type: "label",
            description: "Section heading for the values panel." }
          { selector: "[data-testid=\"empty-container\"]", type: "text",
            description: "Empty-state copy + add CTA inside the values panel." }

        Raw extraction (abbreviated):
          {
            "[data-testid=\"setting-container\"]":      { "count": 1, "texts": ["نام ستون خروجی:\n\nنوع ستون:\n\nمتن\n..."] },
            "[data-testid=\"output-column-config\"]":   { "count": 1, "texts": ["نام ستون خروجی:\n\nنوع ستون:\n\nمتن"] },
            "[data-testid=\"row-based-container\"]":    { "count": 1, "texts": ["مقادیر\n\nمقداری وجود ندارد.\nافزودن مقدار"] },
            "[data-testid=\"list-based-container\"]":   { "count": 0, "texts": [] },
            "[data-testid=\"selected-option\"]":        { "count": 3, "texts": ["مجری پیش‌فرض","پردازشگر پیش‌فرض","متن"] },
            "[data-testid=\"title\"]":                  { "count": 30, "texts": ["خواندن از پایگاه داده","به‌روزرسانی پایگاه داده", /* 28 more */] },
            ".values-title":                            { "count": 1, "texts": ["مقادیر"] },
            "[data-testid=\"empty-container\"]":        { "count": 1, "texts": ["مقداری وجود ندارد.\nافزودن مقدار"] }
          }

        Output (correct — every non-empty extraction kept, NO invented fields):
          {
            "settingContainer": {
              "raw": "نام ستون خروجی:\n\nنوع ستون:\n\nمتن\n..."
            },
            "outputColumnConfig": {
              "raw": "نام ستون خروجی:\n\nنوع ستون:\n\nمتن"
            },
            "rowBasedContainer": {
              "raw": "مقادیر\n\nمقداری وجود ندارد.\nافزودن مقدار"
            },
            "selectedOptions": ["مجری پیش‌فرض","پردازشگر پیش‌فرض","متن"],
            "pluginTitles": {
              "count": 30,
              "items": ["خواندن از پایگاه داده","به‌روزرسانی پایگاه داده", /* all 28 more, in order */]
            },
            "valuesTitle": "مقادیر",
            "emptyContainer": "مقداری وجود ندارد.\nافزودن مقدار"
          }

        WRONG output (mirrors the actual regression you'd produce without
        the COVERAGE rule):
          {
            "activeTab": "Settings",          // ✗ INVENTED — no selector named "tab"
            "valueMode": "ROW_BASED",         // ✗ INVENTED — derived from selector NAME, not extracted text
            "outputColumn": { "type": { "label": "متن" } },   // ✗ buried & loses the row container, selected options, plugin titles
            "values": { "count": 0, "items": [] }             // ✗ "items" lies — 30 plugin titles got dropped
          }

        Things the wrong output gets wrong:
          • Dropped 30 plugin titles from `[data-testid="title"]`.
          • Kept only 1 of the 3 `selected-option` entries.
          • Dropped the verbatim `settingContainer` / `outputColumnConfig`
            text — downstream tests can't grep them.
          • Invented `activeTab` and `valueMode` from selector NAMES
            without any extracted text justifying those values.
        """;

    public async Task<StateBuildResult> BuildStateAsync(Signature signature, string rawExtractionJson, IReadOnlyList<Signature>? dependencies = null, CancellationToken ct = default)
    {
        var opts = options.Value;
        var chat = ChatClientFactory.ForModel(opts, opts.AnalyzerModel);

        // Union of root + dependency informational elements. The analyze
        // script captured every informational selector in the union, so the
        // state builder needs to know about ALL of them to name fields. The
        // raw extraction JSON keys ARE the selectors — we just need the
        // descriptions / logic next to each. Deduplicated by selector
        // (root's element wins on collision because we list it first).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var allElements = new List<(SignatureElement E, string SourceName)>();
        foreach (var e in signature.Elements.OrderBy(x => x.Order))
        {
            if (seen.Add(e.ElementSelector)) allElements.Add((e, signature.Name));
        }
        if (dependencies is not null)
        {
            foreach (var dep in dependencies)
            {
                if (dep.Id == signature.Id) continue;
                foreach (var e in dep.Elements.OrderBy(x => x.Order))
                {
                    if (seen.Add(e.ElementSelector)) allElements.Add((e, dep.Name));
                }
            }
        }

        var sigJson = JsonSerializer.Serialize(new
        {
            name = signature.Name,
            type = signature.Type,
            humanDescription = signature.HumanDescription,
            elements = allElements.Select(t => new
            {
                selector = t.E.ElementSelector,
                category = t.E.Category.ToString().ToLowerInvariant(),
                elementType = t.E.ElementType,
                description = t.E.ElementDescription,
                logic = string.IsNullOrWhiteSpace(t.E.ElementLogic) ? null : t.E.ElementLogic,
                // Which component owns this element. Helps the state builder
                // disambiguate when grouping fields ("connections" might come
                // from the primary signature; "userMenu" from a dep).
                fromComponent = t.SourceName,
            }),
        }, new JsonSerializerOptions { WriteIndented = true });

        var userMsg = $"Signature:\n{sigJson}\n\nRaw extraction JSON:\n{rawExtractionJson}";

        var completion = await chat.CompleteChatAsync(
            new List<ChatMessage>
            {
                new SystemChatMessage(SystemPrompt),
                new UserChatMessage(userMsg),
            },
            new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() },
            ct);

        var raw = LlmComponentAnalyzer.StripCodeFences(completion.Value.Content[0].Text ?? "{}");
        var tokens = completion.Value.Usage?.TotalTokenCount ?? 0;

        // Validate the response is valid JSON; if not, throw clearly.
        try { using var _ = JsonDocument.Parse(raw); }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"State builder returned non-JSON. First 200 chars: {raw[..Math.Min(200, raw.Length)]}", ex);
        }

        return new StateBuildResult(raw, tokens);
    }
}
