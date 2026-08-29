using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Infrastructure.Llm;

/// <summary>
/// Runtime LLM. Given the signature's action-function catalogue, a fresh
/// state snapshot, and the user's natural-language prompt, produces a
/// <see cref="Plan"/>: a flat ordered list of function calls with literal
/// args. No Playwright. No control flow. Just "which functions, in what
/// order, with what values."
///
/// Sits between <see cref="IStateGatherer"/> and <see cref="IPlanInterpreter"/>
/// in the runtime pipeline. Uses the generator model — the plan is short
/// but it needs decent instruction-following to pick the right functions
/// and ground args in observed state; a weak model regresses to picking
/// plausible-looking functions with hallucinated args.
///
/// Only ACTION functions are presented to the LLM. State functions are the
/// planner's INPUT (they produced the snapshot), not compositional building
/// blocks — showing them would tempt the model to write a plan that "reads"
/// state mid-execution, which v1 doesn't support.
/// </summary>
public class LlmPlanGenerator(IOptions<LlmOptions> options) : IPlanGenerator
{
    private static readonly string SystemPrompt = """
        You are Mazda's runtime PLANNER. You receive:
          1. A catalogue of ACTION functions available on the current signature
             (name, description, JSON-schema parameters, preconditions).
          2. A STATE snapshot — the JSON returned by every state function on
             the signature, captured seconds ago against the live UI.
          3. A user PROMPT describing what they want done.

        Your job: compose an ordered plan of action calls that fulfils the
        prompt, with args grounded in the state snapshot. That's it. You do
        not write Playwright. You do not read state — the snapshot is the
        only reading you get.

        ── OUTPUT FORMAT ──
        Output JSON ONLY. No prose. No markdown. No code fences.

        Schema:
        {
          "reasoning": "1-3 short sentences of English explaining the plan (optional but encouraged)",
          "steps": [
            {
              "id":   "sN — small stable id you generate (s1, s2, s3, ...)",
              "call": "the qualified function name from the catalogue",
              "args": { <object matching that function's parameters schema> }
            }
          ]
        }

        ── HARD RULES ──
          1. Every `call` MUST be a function present in the catalogue's
             `actions` list, spelled EXACTLY (including any `Namespace.`
             prefix for dependency-owned functions). A misspelled name is
             a broken plan.
          2. Every `args` object MUST validate against the referenced
             function's `parameters` schema. Missing required fields, wrong
             types, or enum values outside the allowed set will be rejected
             by the interpreter and the run will fail.
          3. Args are LITERAL. No variables, no references, no `${...}`
             interpolation, no expressions. If the value comes from the
             state snapshot, copy the literal in (e.g. `"index": 2`, not
             `"index": "state.GetTasks[0].index"`).
          4. No control flow. No conditionals. No loops. If the user's
             intent requires acting on multiple items, emit one step per
             item — the interpreter runs steps in order.
          5. Order matters. Steps execute top-to-bottom. On the first
             failure the interpreter STOPS — do not compose "cleanup" steps
             assuming earlier steps succeeded unless you'd want them to
             fire on partial state.
          6. Keep it short. A well-composed plan is typically 1–5 steps.
             If you find yourself writing 10+ steps for a single prompt,
             the catalogue is probably missing a higher-level function —
             emit what you can and stop.
          7. Use the state snapshot to translate user references into args.
             The user says "mark the groceries task done" → find the task
             named "groceries" in `state.GetTasks`, read its `index`, emit
             `{ "call": "DoneTask", "args": { "index": <that index> } }`.
             If no such task exists, emit an empty `steps` array with a
             reasoning line explaining why.
          8. When a required arg has no answer in the state snapshot AND
             cannot be inferred from the prompt, do NOT invent one. Emit
             an empty `steps` array with a reasoning line explaining what
             information you'd have needed.

        ── STATE SNAPSHOT SHAPE ──
        The state JSON is a flat object keyed by state-function name (or
        qualified name for dependencies): each value is whatever that
        function returned. Some keys may be `{"__mz_error": "..."}` — that
        function threw during gather. Treat those keys as "unobserved":
        don't assume the underlying UI is missing, but also don't fabricate
        data to fill them.

        ── WORKED MINI-EXAMPLES ──

        Catalogue:
          - GetTasks (state)   → Task[]
          - AddTask (action)   name: string, priority: "low"|"med"|"high"
          - RemoveTask (action) index: integer
          - DoneTask (action)  index: integer
          - ChangePriority (action) index: integer, priority: "low"|"med"|"high"

        State snapshot:
          {
            "GetTasks": [
              { "index": 0, "name": "buy milk",  "priority": "med",  "done": false },
              { "index": 1, "name": "call mom",  "priority": "low",  "done": true  },
              { "index": 2, "name": "gym",       "priority": "high", "done": false }
            ]
          }

        Prompt: "mark buy-milk done and add 'buy bread' as high priority"
        Good plan:
          {
            "reasoning": "buy milk is index 0 (not done). Mark it, then append the new task.",
            "steps": [
              { "id": "s1", "call": "DoneTask", "args": { "index": 0 } },
              { "id": "s2", "call": "AddTask",  "args": { "name": "buy bread", "priority": "high" } }
            ]
          }

        Prompt: "delete every done task"
        Good plan:
          {
            "reasoning": "One done task at index 1 (call mom). Removing it.",
            "steps": [
              { "id": "s1", "call": "RemoveTask", "args": { "index": 1 } }
            ]
          }
        Note: emitting one RemoveTask per done task IS the loop equivalent
        in v1. If there were three done tasks, emit three steps. Order
        matters — remove from HIGHEST index downward so earlier removals
        don't shift the indices of later ones.

        Prompt: "mark the task about groceries done"
        Good plan (no such task):
          {
            "reasoning": "No task named 'groceries' or containing that word in the current snapshot. Nothing to do.",
            "steps": []
          }

        ── ANTI-PATTERNS ──
          ✗ Referencing a call name not in the catalogue.
          ✗ Args that don't match the parameters schema (missing required,
            wrong type, enum out of range).
          ✗ Emitting a plan when the required grounding isn't in the
            snapshot — return an empty plan with a reasoning line instead.
          ✗ Long plans (>10 steps) — that's a signal the catalogue is missing
            a compound action; emit what you can and stop.
          ✗ Anything that looks like control flow: "if", "while", "for", or
            a "call": "if" with args. There's no such action.

        ── FINAL CHECKS ──
          • Every `call` in the catalogue? Every `args` type-matches its
            schema? Steps ordered so failures don't leave dangling state
            you'd have cleaned up in a later step? Reasoning explains WHY
            these steps? Then emit.
        """;

    public async Task<PlanGenerationResult> GeneratePlanAsync(
        Signature signature,
        IReadOnlyList<Signature>? dependencies,
        GatheredState state,
        string userPrompt,
        CancellationToken ct = default)
    {
        var opts = options.Value;
        // Runtime path uses the generator model, same tier as the catalogue
        // generator. The plan is short but grounding args in observed state
        // benefits from a strong instruction-follower.
        var chat = ChatClientFactory.ForModel(opts, opts.GeneratorModel);

        var actionCatalogue = BuildActionCatalogue(signature, dependencies);
        // Empty catalogue is an actionable failure: no functions → no plan
        // is possible. Surface a clean exception so the caller returns a
        // validation error rather than making a pointless LLM round-trip.
        if (actionCatalogue.Count == 0)
        {
            throw new InvalidOperationException(
                "Signature has no ACTION functions. Generate a catalogue first, " +
                "or add at least one action function by hand before running.");
        }

        var payload = JsonSerializer.Serialize(new
        {
            signature = new
            {
                name = signature.Name,
                type = signature.Type,
                humanDescription = signature.HumanDescription,
            },
            state = TryParse(state.StateJson) ?? JsonDocument.Parse("{}").RootElement,
            actions = actionCatalogue,
        }, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        var userMsg =
            $"Compose a plan that fulfils this prompt.\n\n" +
            $"User prompt:\n{userPrompt.Trim()}\n\n" +
            $"Context:\n{payload}";

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

        Plan plan;
        try
        {
            plan = ParseAndValidate(raw, actionCatalogue);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Plan generator returned non-JSON content. First 200 chars: " +
                $"{raw[..Math.Min(200, raw.Length)]}", ex);
        }

        return new PlanGenerationResult(plan, tokens);
    }

    /// <summary>
    /// Build the action-only view of the catalogue that the LLM sees.
    /// Namespaced qualified names for dependency-owned functions mirror
    /// the runtime <see cref="FunctionHarnessBuilder"/> convention exactly
    /// — a plan step's `call` MUST use the same string.
    /// </summary>
    private static IReadOnlyList<object> BuildActionCatalogue(
        Signature signature,
        IReadOnlyList<Signature>? dependencies)
    {
        var list = new List<object>();
        foreach (var fn in signature.Functions.Where(f => f.Kind == FunctionKind.Action).OrderBy(f => f.Order))
        {
            list.Add(new
            {
                name = fn.Name,
                description = fn.Description,
                preconditions = string.IsNullOrWhiteSpace(fn.Preconditions) ? null : fn.Preconditions,
                parameters = TryParse(fn.ParametersJsonSchema),
            });
        }
        if (dependencies is not null)
        {
            foreach (var dep in dependencies)
            {
                foreach (var fn in dep.Functions.Where(f => f.Kind == FunctionKind.Action).OrderBy(f => f.Order))
                {
                    list.Add(new
                    {
                        name = $"{Namespace(dep.Name)}.{fn.Name}",
                        description = fn.Description,
                        preconditions = string.IsNullOrWhiteSpace(fn.Preconditions) ? null : fn.Preconditions,
                        parameters = TryParse(fn.ParametersJsonSchema),
                    });
                }
            }
        }
        return list;
    }

    /// <summary>
    /// Validate the LLM output shape and cross-check that every step's
    /// `call` is present in the catalogue we handed it. Type/enum
    /// validation against parameters schema is deferred to the interpreter
    /// — doing it here would duplicate the runtime check and require a
    /// JSON-Schema library on the app-server side.
    /// </summary>
    private static Plan ParseAndValidate(string raw, IReadOnlyList<object> actionCatalogue)
    {
        // Build a set of valid call names from the catalogue view we sent.
        var validCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in actionCatalogue)
        {
            // We built these anonymous objects ourselves so we know the
            // shape; reflect into `name`.
            var nameProp = a.GetType().GetProperty("name");
            var nameStr = nameProp?.GetValue(a) as string;
            if (!string.IsNullOrWhiteSpace(nameStr)) validCalls.Add(nameStr);
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                "Plan generator returned a JSON value that isn't an object.");

        string? reasoning = null;
        if (root.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String)
            reasoning = r.GetString();

        if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                "Plan generator returned JSON without the required `steps` array.");

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var parsed = new List<PlanStep>();

        var stepIndex = 0;
        foreach (var step in steps.EnumerateArray())
        {
            stepIndex++;
            if (step.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Step #{stepIndex} is not a JSON object.");

            var id = step.TryGetProperty("id", out var idElem) && idElem.ValueKind == JsonValueKind.String
                ? idElem.GetString() : null;
            var call = step.TryGetProperty("call", out var callElem) && callElem.ValueKind == JsonValueKind.String
                ? callElem.GetString() : null;

            // Auto-generate a step id when the LLM forgot — better than
            // failing the whole plan for a cosmetic omission. Uniqueness is
            // still enforced against IDs the LLM did emit.
            if (string.IsNullOrWhiteSpace(id)) id = $"s{stepIndex}";
            if (!seenIds.Add(id!))
                throw new InvalidOperationException(
                    $"Duplicate step id '{id}'. Step ids must be unique within a plan.");

            if (string.IsNullOrWhiteSpace(call))
                throw new InvalidOperationException($"Step '{id}' is missing `call`.");
            if (!validCalls.Contains(call!))
                throw new InvalidOperationException(
                    $"Step '{id}' references unknown function '{call}'. " +
                    $"Valid actions: {string.Join(", ", validCalls.OrderBy(s => s, StringComparer.Ordinal))}");

            // args is optional (some functions take none). Serialize back to
            // raw JSON so the interpreter can pass it verbatim into the
            // harness's plan literal without an extra round-trip.
            string argsJson = "{}";
            if (step.TryGetProperty("args", out var argsElem))
            {
                if (argsElem.ValueKind != JsonValueKind.Object && argsElem.ValueKind != JsonValueKind.Null)
                    throw new InvalidOperationException(
                        $"Step '{id}' has non-object `args`. Expected a JSON object matching the function's parameters schema.");
                if (argsElem.ValueKind == JsonValueKind.Object) argsJson = argsElem.GetRawText();
            }

            parsed.Add(new PlanStep(Id: id!, Call: call!, ArgsJson: argsJson));
        }

        return new Plan(Reasoning: reasoning, Steps: parsed);
    }

    private static JsonElement? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sanitize a dependency signature's name for use as a plan-side
    /// namespace prefix. MUST produce the same string as
    /// <c>FunctionHarnessBuilder.SanitizeNamespace</c> so the plan the LLM
    /// composes matches the identifiers the harness emits. Kept as a small
    /// local copy rather than cross-project reference so the LLM layer and
    /// runner layer stay independently testable.
    /// </summary>
    private static string Namespace(string name)
    {
        var trimmed = new string(name.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var sb = new System.Text.StringBuilder(trimmed.Length);
        foreach (var c in trimmed) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        var slug = sb.ToString();
        return string.IsNullOrEmpty(slug) ? "dep" : slug;
    }
}
