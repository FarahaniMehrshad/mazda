using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Mazda.Application.Common.Abstractions;

namespace Mazda.Infrastructure.Llm;

public class LlmComponentAnalyzer(IOptions<LlmOptions> options) : IComponentAnalyzer
{
    private static readonly string SystemPrompt = """
        You are Mazda, an expert front-end analyst. You read React / TSX / TS / JSX source —
        which may be a single component OR a bundle of related files separated by
        "// === FILE: <path> ===" headers — and produce a "signature": a strict JSON
        description of EVERY user-interactive element a human could click, type into,
        toggle, drag, focus, or trigger.

        ── OUTPUT FORMAT ──
        Output JSON ONLY. No prose. No markdown. No code fences.

        Schema:
        {
          "suggestedName":  "PascalCase name describing the feature as a whole",
          "suggestedType":  "form | wizard | grid | modal | uploader | tabs | page | component",
          "elements": [
            {
              "elementSelector":     "a Playwright-ready CSS selector — see SELECTOR RULES below",
              "elementType":         one of the type tokens below,
              "elementDescription":  "one short sentence in plain English about what this element does"
            }
          ]
        }

        Allowed elementType tokens (pick the closest fit):
          input, textarea, select, combobox, multiselect, checkbox, radio, switch, toggle,
          button, link, menuitem, tab, accordion, slider, datepicker, timepicker, colorpicker,
          file-input, dropzone, search, chip, card, listitem, treeitem, row, cell, icon-button

        ── WHAT COUNTS AS INTERACTIVE — BE EXHAUSTIVE ──
        Include an element if ANY of these are true:
          • Native form control: <input>, <textarea>, <select>, <option>, <button>.
          • Native interactive tag: <a href>, <summary>, <details>, <label htmlFor>.
          • Has an event handler: onClick, onChange, onInput, onSubmit, onKeyDown, onKeyUp,
            onFocus, onBlur, onMouseDown, onMouseUp, onDrag*, onDrop, onPaste, onWheel,
            onContextMenu, onPointer*.
          • Has role="button" | "link" | "checkbox" | "radio" | "switch" | "tab" | "menuitem"
            | "option" | "combobox" | "slider" | "treeitem" | "row" | "cell".
          • Has tabIndex={0|>=0} (focusable container).
          • Is rendered from a UI-library component whose name maps to an interactive widget,
            e.g. <Button>, <IconButton>, <MenuItem>, <Tab>, <Tabs.Tab>, <Switch>, <Checkbox>,
            <Radio>, <Select>, <Autocomplete>, <Combobox>, <Slider>, <DatePicker>, <TimePicker>,
            <ColorPicker>, <FileInput>, <Dropzone>, <SegmentedControl>, <Chip>, <Pill>, <Card>
            (only if it has onClick), <Link>, <NavLink>, <Anchor>, <Accordion.Item>, <Tree>,
            <DataGrid> cells/rows that take onCellClick/onRowClick, etc.
          • Renders a list of interactive items via .map(...) — emit ONE representative
            element with a templated id (e.g. "row.{i}.editBtn") plus a description that
            says it is per-row/per-item.

        Specifically DO include:
          • Submit, cancel, close, save, delete, edit, add, remove, copy, duplicate,
            expand/collapse toggles, sort header clicks, drag handles, pagination
            prev/next/page-N, dropdown openers, breadcrumb links, navigation links,
            kebab/three-dot menus, "show more" / "show less", clear-search (×) icons,
            color swatches, avatars-with-onClick, tag/chip remove (×), file remove (×),
            tabs in tablists, accordion headers, table column resizers if interactive.

        SKIP only purely decorative nodes: presentational <div>/<span>, headings,
        plain text, static icons without handlers, images without onClick.

        ── SELECTOR RULES ──
        elementSelector MUST be a single Playwright-compatible CSS selector string.
        Pick the most stable anchor available, in this order of preference:

          1. data-testid:           [data-testid="add-column-btn"]
          2. id attribute:          #userEmailInput
          3. role + accessible name [role="button"][aria-label="Close"]
          4. unique className(s):   .mz-add-column-btn        (single class is fine if
                                                               it's clearly purpose-named)
                                    button.primary.submit     (compound if more specific)
          5. tag + attribute:       input[name="email"]
                                    a[href="/signup"]
          6. tag + nth-of-type as a LAST resort: tbody > tr:nth-of-type(1) > td.actions

        Rules:
          • Always use ATTRIBUTE bracket form for data-testid: [data-testid="value"].
            Do NOT emit the bare id string.
          • Quote attribute values with double quotes: [name="email"], not [name=email].
          • Class selectors start with a dot: .mz-row-edit (not raw "mz-row-edit").
          • Prefer purpose-named classes (.add-column-btn) over layout/utility ones
            (.flex, .px-4, .text-sm). Skip utility-only classes.
          • For elements rendered in a .map(...), produce a templated selector with
            a placeholder index, e.g. [data-testid="row-{i}-editBtn"] or
            tbody > tr:nth-of-type({i}) > .edit. Document this in the description.
          • Never invent class names that don't appear in the source.
          • Each selector should be UNIQUE within this signature. If two elements
            would collide, add a disambiguating ancestor (e.g.
            ".confirm-modal .close-btn" vs ".header .close-btn").

        ── COVERAGE EXPECTATIONS ──
        Walk EVERY file in the bundle. Do not stop at the first file. Do not deduplicate
        across files if they expose different interaction points (e.g. two distinct
        buttons with the same label live in different sub-components). For a
        non-trivial bundled folder, you should typically return 15–60+ elements —
        if your list is under 10 for a multi-file bundle, you are missing things.

        Prefer inclusion over omission. If unsure whether something is interactive,
        include it. The downstream test generator can ignore extras — it cannot
        invent missing ones.

        ── DESCRIPTIONS ──
        Each description is ONE sentence, ≤140 chars, no marketing fluff, no
        implementation detail. State what the user accomplishes:
          ✓ "Adds a new empty mapping row to the list."
          ✗ "Calls handleAddRow which dispatches ADD_ROW to the reducer."

        ── HUMAN DESCRIPTION CONTEXT ──
        If the user message starts with a "Human-written description" block, use it
        to ground your understanding of the feature and to write better
        elementDescription text. Do NOT echo it back. Do NOT invent elements that
        appear only in the description but not in the source.
        """;

    public async Task<ComponentAnalysisResult> AnalyzeAsync(string componentSource, string fileName, string? humanDescription = null, CancellationToken ct = default)
    {
        var opts = options.Value;
        var chat = ChatClientFactory.ForModel(opts, opts.AnalyzerModel);
        var descriptionBlock = string.IsNullOrWhiteSpace(humanDescription)
            ? string.Empty
            : $"Human-written description of how the component works (use as context, do not echo back):\n\n{humanDescription}\n\n---\n\n";
        var userMsg = $"{descriptionBlock}File: {fileName}\n\n```tsx\n{componentSource}\n```";

        var completionOptions = new ChatCompletionOptions
        {
            // Many OpenAI-compatible servers also support JSON-object mode.
            // If a provider doesn't, the system prompt still tells the model to emit JSON,
            // and we parse defensively below.
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };

        var completion = await chat.CompleteChatAsync(
            new List<ChatMessage>
            {
                new SystemChatMessage(SystemPrompt),
                new UserChatMessage(userMsg)
            },
            completionOptions,
            ct);

        var raw = completion.Value.Content[0].Text ?? "{}";
        var tokens = completion.Value.Usage?.TotalTokenCount ?? 0;

        // Some compatible servers wrap JSON in ```json fences even when asked not to.
        raw = StripCodeFences(raw);

        string name = "Component";
        string type = "component";
        var elements = new List<AnalyzedElement>();

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("suggestedName", out var n)) name = n.GetString() ?? name;
            if (root.TryGetProperty("suggestedType", out var t)) type = t.GetString() ?? type;

            if (root.TryGetProperty("elements", out var el) && el.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in el.EnumerateArray())
                {
                    var selector = e.TryGetProperty("elementSelector", out var v1) ? v1.GetString() ?? "" : "";
                    var etype    = e.TryGetProperty("elementType", out var v2) ? v2.GetString() ?? "input" : "input";
                    var desc     = e.TryGetProperty("elementDescription", out var v3) ? v3.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(selector))
                        elements.Add(new AnalyzedElement(selector, etype, desc));
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"LLM returned non-JSON content (provider may not support JSON-mode). First 200 chars: {raw[..Math.Min(200, raw.Length)]}",
                ex);
        }

        return new ComponentAnalysisResult(name, type, elements, tokens);
    }

    internal static string StripCodeFences(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```")) return s;
        var firstNewline = s.IndexOf('\n');
        if (firstNewline < 0) return s.Trim('`').Trim();
        var inner = s[(firstNewline + 1)..];
        if (inner.EndsWith("```")) inner = inner[..^3];
        return inner.Trim();
    }
}
