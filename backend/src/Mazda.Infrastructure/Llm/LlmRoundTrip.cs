using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Mazda.Application.Modules.RoundTrip;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Infrastructure.Llm;

// The three model calls of a round trip. Each returns a proposal or a
// verdict; the caller validates and decides. Nothing here is app-specific.

/// <summary>Rebuilds, from a recording, the function calls that reproduce what the user did.</summary>
public class LlmReversePlanner(ILlmGateway llm) : IReversePlanner
{
    private static readonly string SystemPrompt = """
        You are Mazda's REVERSE PLANNER. A user recorded themselves doing a task
        in a web app. Functions were written that each perform one high-level
        operation. Your job: write the sequence of function calls that, run in
        order on a fresh page, reproduces EXACTLY what the user did.

        You receive `functions` (name, description, parameters JSON schema, body)
        and `recording`: the user's title / intent and `episodes`, one entry per
        interaction with its index `i`, the `action`, its `target`, a typed
        `value`, the `effects` on the page and the `network` requests it caused.

        ── OUTPUT ── JSON ONLY:
        {
          "steps": [
            { "call": "FunctionName", "args": { }, "episodes": [3, 4],
              "contrastArgs": { } | null, "why": "one short sentence" }
          ],
          "notes": "anything the author should know (interactions no function covers, ambiguities)"
        }

        ── RULES ──
        • One step per function invocation the user performed, in the order they
          performed them. If they did the same operation several times with
          different choices, that is several steps.
        • `episodes`: the `i` values this call reproduces — every interaction the
          function's body performs for it (opening a menu AND choosing in it).
          Each episode belongs to at most one step. Incidental interactions
          (hovering to read a tooltip, a click that changed nothing, scrolling)
          belong to no step: leave them out.
        • `args` must satisfy the parameters schema and repeat the user's exact
          choice: what they typed, the item they picked. When an item can be
          given by name, give the name the user saw (from the target's name,
          a tooltip or text that appeared). Never invent a value.
        • `contrastArgs`: a DIFFERENT valid choice for the same call, used once
          to learn which values in the app's requests carry the user's choice.
          Take it from what the recording shows was available (another option
          visible in a list, another column, another item). Must satisfy the
          schema. Use null when the function takes no choice, or when the
          recording shows no alternative.
        • If interactions are not covered by any function, do not force them into
          a step; say so in `notes`.
        """;

    public async Task<ReversePlanResult> PlanAsync(
        Signature signature,
        IReadOnlyList<SignatureFunction> actions,
        string recordingTitle,
        string recordingIntent,
        string episodesJson,
        CancellationToken ct = default)
    {
        var chat = await llm.ChatAsync(LlmTier.Generator, "rebuild the plan", ct);

        var functions = JsonSerializer.Serialize(actions.OrderBy(f => f.Order).Select(f => new
        {
            name = f.Name,
            description = f.Description,
            parameters = Parse(f.ParametersJsonSchema),
            preconditions = string.IsNullOrWhiteSpace(f.Preconditions) ? null : f.Preconditions,
            body = f.Body,
        }), new JsonSerializerOptions { WriteIndented = true });

        var recording = "{\"title\":" + JsonSerializer.Serialize(recordingTitle) +
                        (string.IsNullOrWhiteSpace(recordingIntent) ? "" : ",\"intent\":" + JsonSerializer.Serialize(recordingIntent)) +
                        ",\"episodes\":" + episodesJson + "}";

        var completion = await chat.CompleteChatSafeAsync(
            new List<ChatMessage>
            {
                new SystemChatMessage(SystemPrompt),
                new UserChatMessage($"Component: {signature.Name}\n\nfunctions:\n{functions}\n\nrecording:\n```json\n{recording}\n```"),
            },
            new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() },
            ct);

        var raw = LlmComponentAnalyzer.StripCodeFences(completion.TextOr("{}"));
        var tokens = completion.Tokens;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var steps = new List<PlanStep>();
            if (doc.RootElement.TryGetProperty("steps", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in arr.EnumerateArray())
                {
                    var call = Str(s, "call");
                    if (string.IsNullOrWhiteSpace(call)) continue;
                    var eps = new List<int>();
                    if (s.TryGetProperty("episodes", out var e) && e.ValueKind == JsonValueKind.Array)
                        foreach (var x in e.EnumerateArray())
                            if (x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var i)) eps.Add(i);
                    // Compact: the author edits these as one-line JSON.
                    var args = s.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? Compact(a) : "{}";
                    var contrast = s.TryGetProperty("contrastArgs", out var c) && c.ValueKind == JsonValueKind.Object ? Compact(c) : null;
                    steps.Add(new PlanStep(call!, args, eps, contrast, Str(s, "why") ?? ""));
                }
            }
            return new ReversePlanResult(steps, Str(doc.RootElement, "notes") ?? "", tokens);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The reverse planner returned non-JSON content. First 200 chars: " + raw[..Math.Min(200, raw.Length)], ex);
        }
    }

    private static string Compact(JsonElement e)
        => JsonSerializer.Serialize(e, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    internal static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); } catch (JsonException) { return null; }
    }

    internal static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Judges, from pictures and the listed differences, whether a replayed step had the same result as the demonstration.</summary>
public class LlmEffectJudge(ILlmGateway llm) : IEffectJudge
{
    private static readonly string SystemPrompt = """
        You compare two runs of the same user operation in a web app: the user's
        own DEMONSTRATION and a REPLAY of it by an automated function. You get
        before/after screenshots of both and a list of differences an automatic
        comparison could not explain on its own.

        Decide whether the replay had the SAME RESULT as the demonstration, from
        the user's point of view: the same thing was created / changed / chosen,
        with the same content. Ignore what naturally differs between two runs:
        ids, timestamps, counters, ordering, data that was already on the page,
        positions, animations, a tooltip or hover state that is still showing.

        Answer JSON ONLY: { "verdict": "same" | "different" | "unsure",
                            "reason": "one or two sentences a non-developer understands" }
        Say "unsure" when the pictures do not show the relevant part of the page.
        """;

    public async Task<JudgeVerdict> JudgeAsync(
        string stepDescription, string differences,
        string? demoBefore, string? demoAfter, string? replayBefore, string? replayAfter,
        CancellationToken ct = default)
    {
        var chat = await llm.ChatAsync(LlmTier.Generator, "judge picture", ct);
        var parts = new List<ChatMessageContentPart>
        {
            ChatMessageContentPart.CreateTextPart($"Operation: {stepDescription}\n\nDifferences found automatically:\n{differences}"),
        };
        void Img(string label, string? b64)
        {
            if (string.IsNullOrEmpty(b64)) { parts.Add(ChatMessageContentPart.CreateTextPart($"{label}: (no picture)")); return; }
            parts.Add(ChatMessageContentPart.CreateTextPart(label + ":"));
            parts.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(Convert.FromBase64String(b64)), "image/jpeg", ChatImageDetailLevel.Low));
        }
        Img("DEMONSTRATION before", demoBefore);
        Img("DEMONSTRATION after", demoAfter);
        Img("REPLAY before", replayBefore);
        Img("REPLAY after", replayAfter);

        var completion = await chat.CompleteChatSafeAsync(
            new List<ChatMessage> { new SystemChatMessage(SystemPrompt), new UserChatMessage(parts) },
            new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() },
            ct);
        var raw = LlmComponentAnalyzer.StripCodeFences(completion.TextOr("{}"));
        var tokens = completion.Tokens;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var v = LlmReversePlanner.Str(doc.RootElement, "verdict")?.Trim().ToLowerInvariant();
            if (v is not ("same" or "different" or "unsure")) v = "unsure";
            return new JudgeVerdict(v, LlmReversePlanner.Str(doc.RootElement, "reason") ?? "", tokens);
        }
        catch (JsonException) { return new JudgeVerdict("unsure", "The judge's answer could not be read.", tokens); }
    }
}

/// <summary>Diagnoses why a replayed step did not reproduce the demonstration and proposes one change.</summary>
public class LlmRoundTripFixer(ILlmGateway llm) : IRoundTripFixer
{
    private static readonly string SystemPrompt = """
        You are Mazda's ROUND-TRIP REPAIR engineer. A user demonstrated a task in
        a web app. A plan of high-level function calls was built to reproduce it
        and replayed step by step on a fresh page. One step did NOT reproduce what
        the user did. You get the evidence: what the user's interactions were and
        what requests they caused, what the replay did instead, errors, console,
        the page's accessibility tree, the earlier steps' outcomes, and
        after-pictures of the demonstration and the replay.

        The demonstration is the definition of correct.

        First decide where the fault is:
          • "function"       the function's body does the wrong thing, or waits
                             for something the app does not do every time.
          • "plan"           the step calls the wrong function, or passes the
                             wrong arguments for what the user did.
          • "earlier-step"   an earlier step left the page in the wrong state.
          • "nondeterminism" the app behaves differently between runs; nothing to fix.
        Choose "plan" ONLY when the step's call or its arguments contradict the
        demonstration (the user picked "X", the plan passes "Y"; the user did
        something no call in the plan does). When the arguments match what the
        user saw or typed and the function still does something else, the fault
        is the FUNCTION, even if other arguments would happen to work. Never work
        around a function bug by changing the arguments (passing a position
        instead of the name the user saw, picking another item): that leaves the
        function broken for every other caller.
        If a previous attempt is listed, it did not work: look for a different cause.
        Then propose ONE change.

        ── OUTPUT ── JSON ONLY:
        { "fault": "function" | "plan" | "earlier-step" | "nondeterminism",
          "explanation": "1-2 sentences: the concrete cause",
          "body": "the corrected TypeScript body (only for fault=function)",
          "planCall": "function name (only for fault=plan)",
          "planArgs": { } }

        ── BODY RULES (fault=function) ──
        • Body only: never change the function's name or parameters. Minimal edit.
        • The body must stay general: it receives the user's choice as arguments.
          NEVER hard-code the demonstrated value (a chart type, a column name, a
          text) — use the parameter.
        • No assertions or post-condition checks: no expect, waitForTimeout,
          waitForFunction; after the last interaction wait only for network
          responses registered before it (page.waitForResponse). Wait only for a
          request the app sends EVERY time; a request the evidence shows does not
          always fire must not be awaited, and never make a request wait optional
          with .catch (it costs its full timeout whenever the request is missing).
        • State functions read the page as it is now and never wait: no
          .waitFor before reading, no textContent/innerText/getAttribute on an
          element that may be absent unless guarded by `count() > 0`.
        • Reading a value off the page (e.g. a tooltip to resolve a name): make
          sure the text read belongs to the element just hovered, not a previous
          one still on screen. Such a read is short and tolerant
          (`.waitFor({ timeout: 1500 }).catch(() => {})`, `.textContent({ timeout: 1000 }).catch(() => '')`):
          one popup that is slow or does not show must leave that one label
          empty, not fail the whole function.
        • Use the helpers already used by the body (__mzClick, __mzPointerDrag, …).
        • No console.log, no try/catch that swallows failures.
        """;

    public async Task<RoundTripFix> DiagnoseAsync(
        Signature signature,
        SignatureFunction function,
        IReadOnlyList<SignatureFunction> actions,
        RoundTripEvidence ev,
        CancellationToken ct = default)
    {
        var chat = await llm.ChatAsync(LlmTier.Generator, "diagnose step", ct);

        var payload = JsonSerializer.Serialize(new
        {
            step = ev.StepDescription,
            function = new
            {
                name = function.Name,
                description = function.Description,
                parameters = LlmReversePlanner.Parse(function.ParametersJsonSchema),
                body = function.Body,
            },
            argsUsed = LlmReversePlanner.Parse(ev.ArgsJson),
            demonstration = new { interactions = ev.DemoSteps, requests = ev.DemoRequests },
            replay = new
            {
                requests = ev.ReplayRequests,
                error = ev.Error,
                stack = Trim(ev.ErrorStack, 1500),
                console = ev.Console.Take(20),
                accessibilityTree = Trim(ev.Aria, 6000),
            },
            differences = ev.Differences,
            earlierSteps = ev.EarlierSteps,
            previousAttempts = ev.PreviousAttempts,
            otherFunctions = actions.Where(a => a.Id != function.Id).Select(a => new
            {
                name = a.Name,
                description = a.Description,
                parameters = LlmReversePlanner.Parse(a.ParametersJsonSchema),
            }),
            selectors = signature.Elements.OrderBy(e => e.Order).Take(80).Select(e => new { selector = e.ElementSelector, description = e.ElementDescription }),
        }, new JsonSerializerOptions { WriteIndented = true });

        var parts = new List<ChatMessageContentPart> { ChatMessageContentPart.CreateTextPart(payload) };
        foreach (var (label, b64) in new[] { ("DEMONSTRATION after the step", ev.DemoAfter), ("REPLAY after the step", ev.ReplayAfter) })
        {
            if (string.IsNullOrEmpty(b64)) continue;
            parts.Add(ChatMessageContentPart.CreateTextPart(label + ":"));
            parts.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(Convert.FromBase64String(b64)), "image/jpeg", ChatImageDetailLevel.Low));
        }

        var completion = await chat.CompleteChatSafeAsync(
            new List<ChatMessage> { new SystemChatMessage(SystemPrompt), new UserChatMessage(parts) },
            new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() },
            ct);
        var raw = LlmComponentAnalyzer.StripCodeFences(completion.TextOr("{}"));
        var tokens = completion.Tokens;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            var fault = LlmReversePlanner.Str(r, "fault")?.Trim().ToLowerInvariant();
            if (fault is not ("function" or "plan" or "earlier-step" or "nondeterminism")) fault = "function";
            var body = LlmReversePlanner.Str(r, "body");
            if (body is not null) body = LlmFunctionFixer.StripFunctionWrapper(body);
            var planArgs = r.TryGetProperty("planArgs", out var pa) && pa.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Serialize(pa, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
                : null;
            return new RoundTripFix(fault, body, LlmReversePlanner.Str(r, "planCall"), planArgs,
                LlmReversePlanner.Str(r, "explanation") ?? "", tokens);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The repair model returned non-JSON content. First 200 chars: " + raw[..Math.Min(200, raw.Length)], ex);
        }
    }

    private static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max] + "…";
}
