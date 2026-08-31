using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Infrastructure.Llm;

/// <summary>
/// Repairs one function body that failed against the live page.
///
/// The catalogue generator writes bodies blind — it has the signature but has
/// never watched the code run. This one gets the thing the generator lacked:
/// what actually happened. That makes it a materially easier problem, so the
/// prompt pushes hard toward a minimal, targeted edit rather than a rewrite.
///
/// Body only. Schemas are out of scope (see <see cref="IFunctionFixer"/>).
/// </summary>
public class LlmFunctionFixer(IOptions<LlmOptions> options) : IFunctionFixer
{
    private static readonly string SystemPrompt = """
        You are Mazda's function REPAIR engineer. A single high-level function
        from a component's catalogue was executed against a real browser page
        and it failed. You receive the function, the signature it belongs to,
        and exactly what went wrong. You return a corrected body.

        ── OUTPUT FORMAT ──
        JSON ONLY. No prose outside the JSON. No markdown fences.

        {
          "explanation": "1-2 sentences: what was actually wrong. Name the
                          concrete cause, not a restatement of the error.",
          "body":        "the corrected TypeScript body, complete"
        }

        ── WHAT YOU MAY CHANGE ──
        The BODY, and only the body. You must NOT change the function's name,
        kind, parameters or return schema — the author's arguments and the
        runtime planner's contract are both built on those. If the only real
        fix would require a new parameter, do not invent one: return the body
        unchanged and say so in `explanation`.

        ── THE BODY CONTRACT ──
        The body executes as `async (page, args) => { ... }`. Every declared
        parameter is already destructured into scope as a local with the exact
        property name from the parameter schema. Return the body statements
        only — no function wrapper, no import lines.

        A state function must `return` a structured value. An action function
        returns nothing.

        These helpers are in scope with no import or declaration:
          • __mzClick(locator)                      — click that survives detached
                                                      nodes and intercept overlays
          • __mzSelectOption(page, trigger, text)   — framework-agnostic combobox
          • __mzPointerDrag(page, source, target)   — pointer-event drag
          • __mzPointerSwap(page, items, i, j)      — pointer-event swap

        ── DIAGNOSE BEFORE YOU EDIT ──
        Read the error, the runner log and the page URL together. The log is
        usually more informative than the exception: Playwright reports what it
        was waiting for and what the selector resolved to.

        Common causes, and the fix each one wants:

          • "strict mode violation … resolved to N elements" — the selector is
            ambiguous. Narrow it with .nth(i) / .first() / .last(). Do NOT add a
            parent container to re-scope it.
          • "waiting for locator … " then a timeout — the element never
            appeared. Either the selector is wrong (check it byte-for-byte
            against the `elements` list), or it is conditionally rendered and
            needs an explicit wait on the thing that reveals it.
          • "element is not visible" / "intercepts pointer events" — use
            __mzClick instead of .click().
          • A wrong or empty return from a state function — the read targeted
            the wrong node, or needed .allInnerTexts() / .textContent() rather
            than .innerText().
          • The page URL shows a different view than the function assumes — say
            so in `explanation`. Do not paper over it by adding navigation the
            function was never meant to perform.

        ── SELECTOR DISCIPLINE ──
        Selectors come from the signature's `elements` list, byte-for-byte.
        Never invent one. When no listed selector fits, prefer a Playwright
        accessibility locator (getByRole / getByLabel / getByPlaceholder /
        getByText / getByTitle) over a CSS guess.

        NEVER use positional CSS (:nth-of-type, :nth-child, :first-of-type,
        :last-child) to mean "the n-th of the matched set" — use .nth(i),
        .first(), .last(). This holds even when the signature's own selector
        contains such a pseudo-class: rewrite it.

        ── MINIMAL EDITS ──
        Change what is broken. A body that fails on one selector does not need
        its other five lines rewritten — a large diff is harder for the author
        to review and more likely to break something that worked.

        Do not add console.log, page.pause(), page.screenshot(), waits "just in
        case", or try/catch that swallows the failure. The harness owns
        observability, and a swallowed error turns a loud failure into a silent
        wrong result.

        If you genuinely cannot determine a fix from the evidence, return the
        body unchanged and use `explanation` to say what additional information
        would settle it.
        """;

    public async Task<FunctionFixResult> FixAsync(
        Signature signature,
        IReadOnlyList<Signature>? dependencies,
        SignatureFunction function,
        FunctionFailureContext failure,
        string? userHint,
        CancellationToken ct = default)
    {
        var opts = options.Value;
        var chat = ChatClientFactory.ForModel(opts, opts.GeneratorModel);

        var payload = JsonSerializer.Serialize(new
        {
            function = new
            {
                name = function.Name,
                kind = function.Kind.ToString().ToLowerInvariant(),
                description = function.Description,
                parametersJsonSchema = TryParse(function.ParametersJsonSchema),
                returnsJsonSchema = TryParse(function.ReturnsJsonSchema),
                preconditions = string.IsNullOrWhiteSpace(function.Preconditions)
                    ? null : function.Preconditions,
                body = function.Body,
            },
            failure = new
            {
                errorKind = failure.ErrorKind,
                errorMessage = failure.ErrorMessage,
                calledWithArgs = TryParse(failure.ArgsJson),
                pageUrl = failure.PageUrl,
                pageTitle = failure.PageTitle,
                runnerLog = Trim(failure.LogTail, 4000),
                stack = Trim(failure.ErrorStack, 1500),
            },
            signature = new
            {
                name = signature.Name,
                type = signature.Type,
                humanDescription = signature.HumanDescription,
                elements = signature.Elements
                    .OrderBy(e => e.Order)
                    .Select(e => new
                    {
                        selector = e.ElementSelector,
                        category = e.Category.ToString().ToLowerInvariant(),
                        elementType = e.ElementType,
                        description = e.ElementDescription,
                        logic = string.IsNullOrWhiteSpace(e.ElementLogic) ? null : e.ElementLogic,
                    }),
                relationships = TryParse(signature.RelationshipsJson),
            },
            // Sibling functions are context, not targets: seeing how a working
            // function selects the same rows is often the whole answer.
            siblingFunctions = signature.Functions
                .Where(f => f.Id != function.Id)
                .OrderBy(f => f.Order)
                .Select(f => new
                {
                    name = f.Name,
                    kind = f.Kind.ToString().ToLowerInvariant(),
                    description = f.Description,
                    body = f.Body,
                }),
            dependencies = dependencies is null || dependencies.Count == 0
                ? null
                : dependencies.Select(d => new
                {
                    name = d.Name,
                    elements = d.Elements.OrderBy(e => e.Order).Select(e => new
                    {
                        selector = e.ElementSelector,
                        elementType = e.ElementType,
                        description = e.ElementDescription,
                    }),
                }),
        }, new JsonSerializerOptions { WriteIndented = true });

        var hintBlock = string.IsNullOrWhiteSpace(userHint)
            ? string.Empty
            : $"\n\nThe author added this note. Treat it as authoritative about " +
              $"intent and about the app's behaviour — they have seen the page " +
              $"and you have not:\n\"\"\"\n{userHint.Trim()}\n\"\"\"";

        var sourceBlock = string.IsNullOrWhiteSpace(signature.SourceCode)
            ? string.Empty
            : $"\n\nComponent source (grounding — do NOT lift selectors from here; " +
              $"use it to understand intent and valid values):\n" +
              $"```tsx\n{signature.SourceCode}\n```";

        var userMsg =
            $"Repair the body of `{function.Name}`. Change only what the failure " +
            $"shows to be broken.\n\n{payload}{hintBlock}{sourceBlock}";

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

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(body))
            {
                throw new InvalidOperationException(
                    "The repair model returned no replacement body.");
            }

            // Models sometimes wrap the body back into the arrow function they
            // were told about. Unwrapping is safer than rejecting — the intent
            // is unambiguous and a retry costs the user another slow round-trip.
            body = StripFunctionWrapper(body!);

            var explanation = root.TryGetProperty("explanation", out var e)
                              && e.ValueKind == JsonValueKind.String
                ? e.GetString() ?? string.Empty
                : string.Empty;

            return new FunctionFixResult(body, explanation.Trim(), tokens);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "The repair model returned non-JSON content. First 200 chars: " +
                raw[..Math.Min(200, raw.Length)], ex);
        }
    }

    /// <summary>
    /// Unwrap <c>async (page, args) =&gt; { ... }</c> when the model re-emits the
    /// wrapper the harness already supplies. Leaves anything else untouched —
    /// a false positive here would silently corrupt a valid body.
    /// </summary>
    internal static string StripFunctionWrapper(string body)
    {
        var t = body.Trim();
        if (!t.StartsWith("async", StringComparison.Ordinal)
            && !t.StartsWith("(", StringComparison.Ordinal))
        {
            return body;
        }

        var arrow = t.IndexOf("=>", StringComparison.Ordinal);
        if (arrow < 0) return body;

        var open = t.IndexOf('{', arrow);
        if (open < 0) return body;

        var close = t.LastIndexOf('}');
        if (close <= open) return body;

        // Only unwrap when the arrow header really is a (page, args) signature;
        // otherwise this could be a body that legitimately opens with an
        // inline callback.
        var header = t[..arrow];
        if (!header.Contains("page", StringComparison.OrdinalIgnoreCase)) return body;

        return t[(open + 1)..close].Trim('\r', '\n').TrimEnd();
    }

    private static JsonElement? TryParse(string? json)
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

    private static string? Trim(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        // Keep the TAIL of a log — the failure is at the end.
        return s.Length <= max ? s : "…" + s[^max..];
    }
}
