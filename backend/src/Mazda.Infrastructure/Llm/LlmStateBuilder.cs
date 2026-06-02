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

        ── PRINCIPLES ──
        1. Use the signature's descriptions and logic to NAME each piece of state.
           Don't keep the raw selector strings as keys; use human-meaningful names.
           For repeating containers (elementType "list" / "listitem") synthesise an
           array of structured objects, not an array of raw strings, when the
           signature's logic tells you what each text represents (a name, a status,
           a count, …).
        2. Coerce primitive values:
             • Strings that look like numbers → numbers.
             • "true"/"false" / "yes"/"no" / "active"/"inactive" → booleans, when
               the signature's logic indicates the field is binary.
             • Status / badge texts that have a finite vocabulary (per the
               signature's logic) → keep as strings but include both the raw
               value and a normalised lower-case form when useful.
        3. GROUP related fields. If the signature describes a list of cards each
           with name + type + status, emit:
             "cards": [{ "name": "...", "type": "...", "status": "..." }, ...]
           NOT three flat arrays.
        4. Cardinality goes in the object:
             "cards": { "count": 3, "items": [...] }
           is preferred when the consumer would care about the count separately,
           e.g. for any list / repeating container.
        5. SKIP fields whose raw extraction was empty / null / errored unless the
           emptiness itself is meaningful (e.g. "No items" is informative). In
           that case include the field with the appropriate empty representation
           (`null`, `0`, `[]`).
        6. The output JSON is consumed by another LLM as test-generation context.
           Optimise for clarity over completeness — fewer well-named keys beat
           many raw keys.

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

        ── EXAMPLE ──
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
        """;

    public async Task<StateBuildResult> BuildStateAsync(Signature signature, string rawExtractionJson, CancellationToken ct = default)
    {
        var opts = options.Value;
        var chat = ChatClientFactory.ForModel(opts, opts.AnalyzerModel);

        var sigJson = JsonSerializer.Serialize(new
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
