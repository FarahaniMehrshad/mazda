using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Infrastructure.Llm;

public class LlmScriptGenerator(IOptions<LlmOptions> options) : IScriptGenerator
{
    private static readonly string SystemPrompt = """
        You are Mazda, an assistant that writes Playwright Test scripts (TypeScript, .spec.ts).

        You will receive:
        - (Optional) A "Human-written description" block: prose explaining how the component
          works. Use it as grounding context for choosing the right actions and assertions.
          Do not echo it back in the output.
        - A Signature: a structured list of CSS-selector-anchored elements for one UI component.
        - A target URL.
        - A user prompt in plain English describing what should happen on that URL.

        Output a SINGLE Playwright .spec.ts file as plain text — no markdown fences, no commentary.
        Requirements:
        - `import { test, expect } from '@playwright/test';`
        - One top-level `test(...)` block named after the prompt's intent.
        - Locate elements via `page.locator('<elementSelector>')`. The selector value from the
          Signature is already a valid CSS selector — pass it through verbatim. Examples:
            page.locator('[data-testid="add-column-btn"]')
            page.locator('.mz-add-column-btn')
            page.locator('button.primary[type="submit"]')
        - When a Signature selector contains a "{i}" placeholder it is a template for a
          repeated row/item — substitute a concrete index (or .nth(i)) and add a comment
          explaining the row chosen.
        - Translate the user's English instruction into the right sequence of actions
          (fill, click, check, selectOption, waitFor, expect).
        - Add a short header comment with: target URL, signature name, and the prompt verbatim.
        - End with at least one `expect(...)` assertion if the prompt implies a verifiable outcome.
        - Do not invent selectors that aren't in the Signature. If something the prompt asks for
          isn't covered by the Signature, add a `// TODO:` comment instead of guessing a selector.
        """;

    public async Task<ScriptGenerationResult> GenerateAsync(Signature signature, string targetUrl, string prompt, CancellationToken ct = default)
    {
        var opts = options.Value;
        var chat = ChatClientFactory.ForModel(opts, opts.GeneratorModel);

        var sigJson = JsonSerializer.Serialize(new
        {
            name = signature.Name,
            version = signature.Version,
            type = signature.Type,
            elements = signature.Elements.OrderBy(e => e.Order).Select(e => new
            {
                elementSelector = e.ElementSelector,
                elementType = e.ElementType,
                elementDescription = e.ElementDescription
            })
        }, new JsonSerializerOptions { WriteIndented = true });

        var descriptionBlock = string.IsNullOrWhiteSpace(signature.HumanDescription)
            ? string.Empty
            : $"""
            Human-written description of how the component works (use as context, do not echo back):

            {signature.HumanDescription}

            ---

            """;

        var userMsg = $"""
            {descriptionBlock}Target URL: {targetUrl}

            User prompt:
            {prompt}

            Signature:
            {sigJson}
            """;

        var completion = await chat.CompleteChatAsync(
            new List<ChatMessage>
            {
                new SystemChatMessage(SystemPrompt),
                new UserChatMessage(userMsg)
            },
            new ChatCompletionOptions(),
            ct);

        var script = LlmComponentAnalyzer.StripCodeFences(completion.Value.Content[0].Text ?? string.Empty);
        var tokens = completion.Value.Usage?.TotalTokenCount ?? 0;

        return new ScriptGenerationResult(script, tokens);
    }
}
