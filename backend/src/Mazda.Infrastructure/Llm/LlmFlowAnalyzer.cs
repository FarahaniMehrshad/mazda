using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Infrastructure.Llm;

public class LlmFlowAnalyzer(IOptions<LlmOptions> options) : IFlowAnalyzer
{
    private static readonly string SystemPrompt = """
        You are Mazda's flow analyst. Given a UI Signature (a list of CSS-selector-
        anchored interactive elements) — and optionally a Cypress integration test
        file that exercises the same UI — you produce a DIRECTED GRAPH of all
        plausible user flows through that UI.

        Output JSON ONLY — no prose, no markdown, no code fences.

        ── SCHEMA ──
        {
          "nodes": [
            {
              "id":          "lowerCamel.dot.path",  // unique, stable
              "label":       "Short noun phrase the user would recognize as a UI state",
              "description": "One sentence describing what is true of the UI here"
            }
          ],
          "edges": [
            {
              "id":               "lowerCamel.dot.path",        // unique
              "from":             "<node id>",
              "to":               "<node id>",
              "action":           "click | fill | check | uncheck | selectOption | press | hover | upload | drag",
              "elementSelector":  "exact selector from the Signature (verbatim)",
              "label":            "Short verb phrase, e.g. \"Click \\\"Add column\\\"\"",
              "description":      "One sentence: what the user does and why",
              "value":            "Optional — required for fill/selectOption/upload"
            }
          ]
        }

        ── NODES (UI states) ──
        - Always include an `initial` node with label "Initial state".
        - Add nodes for each *distinguishable* state the UI can be in after one or
          more interactions: an empty form, a form with one row added, an edit
          modal open, a confirmation visible, a validation error showing, etc.
        - Do NOT explode the state space combinatorially. Coalesce equivalent states.
        - Use stable dotted-camel ids: `state.initial`, `state.rowAdded`,
          `state.editModalOpen`, `state.errorVisible`, etc.

        ── EDGES (actions) ──
        - Each edge corresponds to ONE primitive interaction on ONE Signature element.
        - `elementSelector` MUST be a verbatim copy of a selector from the Signature
          list — never invent one. If a flow you'd like to draw requires an element
          that is not in the Signature, OMIT that flow.
        - Match `action` to the element's nature (click for buttons, fill for text
          inputs, selectOption for select/combobox, etc.).
        - For repeated actions across rows/items, model ONE representative edge —
          do not enumerate every row.
        - Use stable dotted-camel ids: `edge.clickAddColumn`, `edge.fillOutputName`,
          `edge.openEditModal`, `edge.confirmDelete`, etc.

        ── COVERAGE ──
        - Walk the Signature and identify the principal happy-path flow first
          (e.g. add → fill → save). Add it as a chain of nodes + edges.
        - Then add reasonable variations: cancel, error, edge cases, secondary
          actions (delete, edit existing, toggle a switch).
        - Aim for between 5 and 25 nodes and 8 and 40 edges for a non-trivial
          component. Smaller for a simple form.
        - The graph should be CONNECTED — every non-initial node must be reachable
          from `state.initial`.

        ── USING A CYPRESS TEST AS GROUNDING ──
        If the user provides Cypress integration test source, use it as ground
        truth for which flows are real and important. Extract:
          • Which elements get clicked/filled and in what order.
          • Which states the test asserts (visible / not visible / contains text).
          • Branches (e.g. retries on failure).
        Map each `cy.get/contains/click/type/select` to an edge in your graph,
        using the Signature's selector for the same element. Use the test's
        `describe/it` structure to name the principal flow chains.

        ── HARD RULES ──
        - JSON only. No fences, no prose.
        - Every `from` and `to` must reference a node id you defined.
        - Every edge `elementSelector` must appear verbatim in the Signature.
        - All ids must be unique within their array.
        - The graph must be valid (acyclic is preferred but not required —
          loops are fine for "edit again" patterns).
        """;

    public async Task<FlowAnalysisResult> InferFlowsAsync(Signature signature, string? cypressSource, CancellationToken ct = default)
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
                elementType = e.ElementType,
                description = e.ElementDescription,
            }),
        }, new JsonSerializerOptions { WriteIndented = true });

        var cypressBlock = string.IsNullOrWhiteSpace(cypressSource)
            ? string.Empty
            : $"\n\nCypress integration test (use as grounding — map every cy.* call to an edge):\n```ts\n{cypressSource}\n```";

        var userMsg = $"Signature:\n{sigJson}{cypressBlock}";

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

        // Defensive parse — at minimum ensure it's valid JSON. Don't shape it.
        try { using var _ = JsonDocument.Parse(raw); }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Flow analyzer returned non-JSON content. First 200 chars: {raw[..Math.Min(200, raw.Length)]}", ex);
        }

        return new FlowAnalysisResult(raw, tokens);
    }
}
