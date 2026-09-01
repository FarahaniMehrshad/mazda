using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Infrastructure.Llm;

public class LlmFlowAnalyzer(IOptions<LlmOptions> options) : IFlowAnalyzer
{
    private static readonly string SystemPrompt = """
        you are mazda's flow analyst. given a ui signature (a list of css-selector-
        anchored interactive elements) you produce a directed graph of all
        plausible user flows through that ui.

        output json only — no prose, no markdown, no code fences.

        ── schema ──
        {
          "nodes": [
            {
              "id":          "lowercamel.dot.path",  // unique, stable
              "label":       "short noun phrase the user would recognize as a ui state",
              "description": "one sentence describing what is true of the ui here"
            }
          ],
          "edges": [
            {
              "id":               "lowercamel.dot.path",        // unique
              "from":             "<node id>",
              "to":               "<node id>",
              "action":           "click | fill | check | uncheck | selectoption | press | hover | upload | drag",
              "elementselector":  "exact selector from the signature (verbatim)",
              "label":            "short verb phrase, e.g. \"click \\\"add column\\\"\"",
              "description":      "one sentence: what the user does and why",
              "value":            "optional — required for fill/selectoption/upload"
            }
          ]
        }

        ── nodes (ui states) ──
        - always include an `initial` node with label "initial state".
        - add nodes for each *distinguishable* state the ui can be in after one or
          more interactions: an empty form, a form with one row added, an edit
          modal open, a confirmation visible, a validation error showing, etc.
        - do not explode the state space combinatorially. coalesce equivalent states.
        - use stable dotted-camel ids: `state.initial`, `state.rowadded`,
          `state.editmodalopen`, `state.errorvisible`, etc.

        ── edges (actions) ──
        - each edge corresponds to one primitive interaction on one signature element.
        - `elementselector` must be a verbatim copy of a selector from the signature
          list — never invent one. if a flow you'd like to draw requires an element
          that is not in the signature, omit that flow.
        - match `action` to the element's nature (click for buttons, fill for text
          inputs, selectoption for select/combobox, etc.).
        - for repeated actions across rows/items, model one representative edge —
          do not enumerate every row.
        - use stable dotted-camel ids: `edge.clickaddcolumn`, `edge.filloutputname`,
          `edge.openeditmodal`, `edge.confirmdelete`, etc.

        ── coverage ──
        - walk the signature and identify the principal happy-path flow first
          (e.g. add → fill → save). add it as a chain of nodes + edges.
        - then add reasonable variations: cancel, error, edge cases, secondary
          actions (delete, edit existing, toggle a switch).
        - aim for between 5 and 25 nodes and 8 and 40 edges for a non-trivial
          component. smaller for a simple form.
        - the graph should be connected — every non-initial node must be reachable
          from `state.initial`.

        ── hard rules ──
        - json only. no fences, no prose.
        - every `from` and `to` must reference a node id you defined.
        - every edge `elementselector` must appear verbatim in the signature.
        - all ids must be unique within their array.
        - the graph must be valid (acyclic is preferred but not required —
          loops are fine for "edit again" patterns).
        """;

    public async Task<FlowAnalysisResult> InferFlowsAsync(Signature signature, CancellationToken ct = default)
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

        var userMsg = $"Signature:\n{sigJson}";

        var completion = await chat.CompleteChatSafeAsync(
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
