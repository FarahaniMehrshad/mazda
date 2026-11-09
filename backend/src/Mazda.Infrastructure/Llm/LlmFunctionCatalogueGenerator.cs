using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Infrastructure.Llm;

/// <summary>
/// Design-time generator for a signature's high-level function catalogue.
/// See <see cref="IFunctionCatalogueGenerator"/> for the state/action split
/// and how the runtime consumes what this emits.
///
/// One LLM round per signature: takes the full signature context (elements,
/// relationships, user flows, description, source) plus any transitive
/// dependencies, and returns a catalogue in a strict JSON shape that is
/// validated before it comes back. Uses the generator model (not the analyzer
/// model) — the body of every function is real Playwright TypeScript, and
/// weak models regress here badly.
/// </summary>
public class LlmFunctionCatalogueGenerator(IOptions<LlmOptions> options) : IFunctionCatalogueGenerator
{
    private static readonly string SystemPrompt = """
        You are Mazda's FUNCTION CATALOGUE author. You receive a fully-analyzed
        component Signature (interactive elements, informational elements,
        semantic relationships, user-flow graph, source) and produce the set of
        HIGH-LEVEL FUNCTIONS a runtime planner will later compose to fulfil
        user prompts.

        This is a DESIGN-TIME step, run once per signature. Everything you emit
        gets persisted and executed later, so precision matters more than
        breadth.

        ── THE TWO KINDS OF FUNCTION ──

        Every function belongs to exactly one kind:

          • "state"  — a read-only observer. Takes NO parameters. Returns a
                       structured JSON value (usually a list of records, or a
                       small key/value object). Runs automatically at request
                       time during gatherInformation to snapshot the UI. The
                       planner LLM SEES the returned data — it does not call
                       state functions itself. Name them as queries:
                       `GetTasks`, `GetActiveFilter`, `GetPagination`.

          • "action" — a side-effect-producing operation. Takes typed
                       parameters. Returns nothing meaningful. Compositional
                       building blocks the planner LLM sequences into a plan
                       (one function call per plan step). Names read as
                       commands: `AddTask`, `RemoveTask`, `DoneTask`,
                       `ChangePriority`.

        ── OUTPUT FORMAT ──
        Output JSON ONLY. No prose. No markdown. No code fences.

        Schema:
        {
          "functions": [
            {
              "name":          "PascalCase identifier, unique within this catalogue",
              "kind":          "state" | "action",
              "description":   "1-3 sentence English description of what the function does and when to use it",
              "parameters":    <full JSON Schema doc; ALWAYS a `type:"object"` schema, even when empty>,
              "returns":       <full JSON Schema doc for the return value>,
              "preconditions": "optional 1-sentence English precondition — empty string when none",
              "body":          "the TypeScript Playwright body — see BODY RULES below"
            }
          ]
        }

        ── NAMING RULES ──
          1. PascalCase, no spaces, no punctuation. Verb-noun for actions,
             query-noun for states.
          2. Unique WITHIN this catalogue. The runtime interpreter addresses
             functions by name — collisions are a bug.
          3. Names must convey USER INTENT, not UI mechanics.
             ✗ `ClickAddButton`, `TypeInTaskInput`, `SelectPriorityFromCombo`
             ✓ `AddTask(name, priority)`
             A single action function may fold 2–10 low-level DOM
             interactions. If your name reads like a click transcript, fold
             it up.
          4. Do NOT prefix `Do` / `Execute` / `Perform`. `AddTask`, not
             `DoAddTask`.

        ── PARAMETER RULES ──
          1. Every function has a `parameters` field. For state functions and
             for zero-arg actions use the empty-object schema:
                {"type":"object","properties":{},"required":[]}
          2. Give every parameter:
                - a JSON-schema `type` (`string`, `integer`, `number`,
                  `boolean`, `array`, `object`)
                - a `description` (1 sentence, what it means to a caller)
                - an `enum` when values are constrained (use the exact strings
                  the UI accepts) AND the full set of values is known — see
                  OPTION SETS: values merely observed in a recording are
                  examples, never an enum
                - correct membership in `required`
          3. Argument names are lowerCamelCase. They become variable names in
             the function body via destructuring, so match exactly.
          4. Prefer STRUCTURED args (a form section = one object arg) over
             long flat argument lists — but never fold a whole 40-field form
             into one giant `payload`. Split at semantic section boundaries.
          5. When an action targets a specific row / item, take a numeric
             `index` parameter (0-based). NEVER take a raw Playwright
             selector as a parameter — indices come from state functions,
             names come from the planner LLM reading state.

        ── RETURN RULES ──
          1. State functions MUST have a non-null return schema. Typically
             `type:"array"` of `type:"object"` records, or a small
             `type:"object"` for singletons like current filter / pagination.
          2. Action functions return null: `{"type":"null"}`.
          3. State return schemas must be self-describing enough that a
             planner LLM reading only the schema (not the data) knows what
             fields exist and what enums are valid. Repeat enum lists between
             params and returns when the same set applies both ways.

        ── BODY RULES ──
        The `body` is a TypeScript snippet executed inside an async function
        whose signature is:

            async (page: Page, args: <ParamShape>) => <Returns>

        You may rely on the following at runtime:

          1. `page` is a Playwright `Page` connected to the target app,
             positioned somewhere the component is visible.
          2. Every declared parameter is available as a local (destructured
             from `args`) with the exact name from your `parameters` schema.
          3. These helpers are ALREADY IN SCOPE. Prefer them over raw
             Playwright equivalents:

               • __mzClick(locator)
                   Robust click that retries past detached nodes / intercept
                   overlays. Use for every non-trivial click.

               • __mzSelectOption(page, triggerSelector, text)
                   Framework-agnostic combobox / tree-select / nz-tree-select
                   picker. Handles Persian/Arabic-Indic digit normalisation
                   and ZWNJ cleanup. Use for ANY custom-widget dropdown; fall
                   back to native `<select>` handling only for plain HTML
                   `<select>` elements.

               • __mzPointerDrag(page, sourceLocator, targetLocator)
                 __mzPointerSwap(page, aLocator, bLocator)
                   Pointer-event drag-and-drop that works with @dnd-kit,
                   react-beautiful-dnd, Angular CDK, etc.

          4. State functions RETURN their value with `return ...;`. Prefer a
             single `page.locator(...).evaluateAll(...)` call that runs in
             the browser to collect all records in one round-trip — it is
             faster and avoids races where the DOM shifts between reads.
          5. Action functions perform their effect and `return;` implicitly
             (return type is void).
          6. Await every Playwright call. Never `.click()` without `await`.
          7. Do NOT emit `import` / `require` — the harness wires everything
             up.
          8. Do NOT invent selectors. Reuse selector strings VERBATIM from
             the input signature. If a selector you need isn't in the
             signature, don't emit the function.
          9. Keep bodies focused. If a body grows past ~30 lines, you are
             probably folding two functions into one — split them.

        ── NO ASSERTIONS / NO POST-CONDITIONS — HARD RULE (action functions) ──
        An action PERFORMS interactions. It never checks what the UI shows
        afterwards — the planner reads state functions for that. A check that
        fails because the page renders slightly differently breaks an action
        that actually worked.
          ✗ expect(...) in any form, and every matcher (.toBe… / .toHave… /
            .toContainText …)
          ✗ page.waitForTimeout(...)
          ✗ page.waitForFunction(...)
          ✗ comparing state before / after (counts, texts) and throwing
          ✗ waiting for something to appear / disappear / become visible /
            hidden / detached AFTER the last interaction, e.g.
              await page.locator('div.chart-container').waitFor({ state: 'visible' });
              await rows.nth(i).waitFor({ state: 'detached' });
        After the LAST interaction the ONLY allowed waits are
        `page.waitForResponse(...)` for the requests that interaction fires —
        create the promise BEFORE the trigger (Promise.all, or `const done =
        page.waitForResponse(...)` first), await it after, then end.
        Allowed BEFORE a later interaction in the same function: waiting for
        what that next interaction needs (the menu / popup it clicks into,
        the tooltip it reads to resolve a `name`).
        Allowed throws: argument problems only (neither name nor index given,
        unknown name — list the available names).

        ── SELECTOR DISCIPLINE ──
        Every selector string in every body MUST be a byte-for-byte match of
        a value that appears as `elementSelector` in the input signature
        (or in one of its listed dependencies). Do not paraphrase. Do not
        substitute a `data-testid` for an `aria-label` because it looks
        cleaner. If a selector you need isn't in the signature, the signature
        is incomplete — omit the function rather than invent.

        ── CATALOGUE COVERAGE ──
        Aim for a catalogue that lets an average user request be fulfilled
        by 1–5 action calls. Concretely:

          1. Emit ONE state function per "obvious question the planner would
             ask about this component". For a to-do list: `GetTasks`, maybe
             `GetFilters`. For a form: `GetFormValues`. For a grid: `GetRows`,
             `GetSelectedRows`, `GetPagination`. Do NOT split state per-field;
             one function per coherent observation.
          2. Emit one action per DISTINCT user intent surfaced by the user
             flow. Every transition a user could describe in one sentence
             ("add a task", "remove the third row", "toggle filter to Done")
             is a candidate.
          3. When a compound flow needs a return value from a preceding
             action (`CreateProject → id`, then `AddTaskTo(projectId, ...)`)
             emit it as a SINGLE compound action (`CreateProjectAndAddTask`).
             Do not split — the planner has no way to pass return values
             between steps in v1.
          4. Do NOT emit trivial wrappers around single clicks
             (`ClickSaveButton`, `ClickCancelButton`). If the action is just
             one click, bake it into a larger meaningful action
             (`SubmitForm`, `CancelEdit`).
          5. Coverage target for a typical medium component:
                • 1–4 state functions
                • 3–15 action functions
             Substantially more usually means you're over-fragmenting;
             substantially fewer means you're under-covering the flows.

        ── ANTI-PATTERNS (do not emit) ──
          ✗ Functions whose body is a single `__mzClick`. Fold up.
          ✗ Functions that call `page.pause()` / `page.screenshot()` /
            `console.log` — the harness handles observability.
          ✗ Functions that hard-code test data. `AddTask` takes `name` as a
            parameter; it never hard-codes "buy milk".
          ✗ Functions that call other Mazda functions by name — the
            interpreter runs one function at a time. If you need composition,
            inline the underlying interactions.
          ✗ Functions with LLM / network / clock calls in the body — the
            whole point is deterministic runtime.
          ✗ Functions whose `body` is empty or a stub. Emit real code or
            drop the function.

        ── WORKED MINI-EXAMPLE (a to-do list component) ──
        Input signature has selectors like `[data-testid=task-row]`,
        `[data-testid=task-name]`, `[data-testid=task-checkbox]`,
        `[data-testid=priority-select]`, `[data-testid=add-task-input]`,
        `[data-testid=add-task-submit]`, `[data-testid^=remove-task-]`.

        A good catalogue:

        {
          "functions": [
            {
              "name": "GetTasks",
              "kind": "state",
              "description": "Snapshot of every task currently rendered, with 0-based index, name, priority, and done state.",
              "parameters": {"type":"object","properties":{},"required":[]},
              "returns": {
                "type": "array",
                "items": {
                  "type": "object",
                  "properties": {
                    "index":    {"type":"integer","description":"0-based row index"},
                    "name":     {"type":"string","description":"Task text as shown"},
                    "priority": {"type":"string","enum":["low","med","high"]},
                    "done":     {"type":"boolean"}
                  },
                  "required": ["index","name","priority","done"]
                }
              },
              "preconditions": "",
              "body": "return await page.locator('[data-testid=task-row]').evaluateAll(rows => rows.map((r, i) => ({\n  index: i,\n  name: (r.querySelector('[data-testid=task-name]')?.textContent ?? '').trim(),\n  priority: (r.getAttribute('data-priority') ?? 'low') as 'low'|'med'|'high',\n  done: r.querySelector('[data-testid=task-checkbox]')?.getAttribute('aria-checked') === 'true',\n})));"
            },
            {
              "name": "AddTask",
              "kind": "action",
              "description": "Creates a new task with the given text and priority. Appended to the end of the list.",
              "parameters": {
                "type": "object",
                "properties": {
                  "name":     {"type":"string","description":"Text of the new task"},
                  "priority": {"type":"string","enum":["low","med","high"],"description":"Priority level to assign"}
                },
                "required": ["name","priority"]
              },
              "returns": {"type":"null"},
              "preconditions": "The task list is the visible view.",
              "body": "await page.locator('[data-testid=add-task-input]').fill(name);\nawait __mzSelectOption(page, '[data-testid=priority-select]', priority);\nawait __mzClick(page.locator('[data-testid=add-task-submit]'));"
            },
            {
              "name": "RemoveTask",
              "kind": "action",
              "description": "Removes the task at the given 0-based index.",
              "parameters": {
                "type": "object",
                "properties": {"index":{"type":"integer","description":"0-based row index from GetTasks"}},
                "required": ["index"]
              },
              "returns": {"type":"null"},
              "preconditions": "",
              "body": "await __mzClick(page.locator('[data-testid=task-row]').nth(index).locator('[data-testid^=remove-task-]'));"
            },
            {
              "name": "DoneTask",
              "kind": "action",
              "description": "Marks the task at the given index as done. No-op if already done.",
              "parameters": {
                "type": "object",
                "properties": {"index":{"type":"integer","description":"0-based row index from GetTasks"}},
                "required": ["index"]
              },
              "returns": {"type":"null"},
              "preconditions": "",
              "body": "const cb = page.locator('[data-testid=task-row]').nth(index).locator('[data-testid=task-checkbox]');\nif ((await cb.getAttribute('aria-checked')) !== 'true') {\n  await __mzClick(cb);\n}"
            },
            {
              "name": "ChangePriority",
              "kind": "action",
              "description": "Sets the priority of the task at the given index to the given level.",
              "parameters": {
                "type": "object",
                "properties": {
                  "index":    {"type":"integer","description":"0-based row index from GetTasks"},
                  "priority": {"type":"string","enum":["low","med","high"]}
                },
                "required": ["index","priority"]
              },
              "returns": {"type":"null"},
              "preconditions": "",
              "body": "const row = page.locator('[data-testid=task-row]').nth(index);\nawait __mzSelectOption(page, await row.locator('[data-testid=priority-select]').evaluate(el => `#${el.id}`), priority);"
            }
          ]
        }

        With that catalogue, a planner LLM handles "mark buy-milk done" as a
        single ACTION plan `[DoneTask(index=<from GetTasks>)]` — no
        Playwright to write at runtime, no DOM to interpret.

        ── OPTION SETS (menus, listboxes, palettes, segmented choices) ──
        When the user picks one of N options (a floating menu, a listbox, an
        icon palette), the recording usually shows only the few options the
        user hovered or chose. The element logic / collection `count` tells you
        how many there really are.
          • Observed names are EXAMPLES, not the domain. Unless EVERY option's
            label is known (a native <select> whose options were captured, or
            source code that declares the list), NO `enum`: put the observed
            names in the parameter description as examples.
          • The action takes `name` (string) and/or `index` (integer, 0-based
            in the option set) — both optional, the body throws when neither is
            given. Match `name` trimmed and case / whitespace-insensitively:
            exact first, then contains. Read each option's label the SAME way
            it was observed: its visible text, an attribute (aria-label, title,
            attrs), or — when names only show as a tooltip — hover each option
            in turn and read the tooltip. When nothing matches, throw an Error
            listing the available names.
          • ALWAYS emit a state function `Get<Thing>Options` that opens the set
            if it is closed, returns EVERY option as `{ index, name, group }`
            (`group` = the section title the option sits under, READ FROM THE
            DOM per option — e.g. in `evaluate`, the closest preceding
            section-title element of the option's container; never a constant;
            '' if the set has no sections) and closes it
            again (Escape) if it opened it. The planner reads it to pick a
            name or an index.
          Example — an icon palette whose names only appear as tooltips:
            GetCardOptions:
              const opened = !(await menu.isVisible());
              if (opened) { await __mzClick(trigger); await menu.waitFor(); }
              const icons = menu.locator(<option selector>);
              const out = [];
              for (let i = 0; i < await icons.count(); i++) {
                await icons.nth(i).hover();
                const tip = page.locator(<tooltip selector>).last();
                await tip.waitFor({ state: 'visible', timeout: 1500 }).catch(() => {});
                const group = await icons.nth(i).evaluate(el => { /* walk up / back to the nearest section title */ … });
                out.push({ index: i, name: ((await tip.textContent().catch(() => '')) ?? '').trim(), group });
              }
              if (opened) await page.keyboard.press('Escape');
              return out;
            AddCard({ name?, index? }): same label reading to resolve `name` to
            an index, then click option `index` (plus waitForResponse for any
            request the click fires — nothing after that).

        ── PER-ITEM ELEMENTS (list / listitem and per-item controls) ──
        A signature describes a repeating container as a "list" element whose
        selector matches EVERY item (e.g. `#list > li`), plus per-item elements
        whose selector is that list selector followed by a path inside the item
        (e.g. `#list > li h3.task-title`). Address item i in ONE of two ways:
          • page-level, same index:  page.locator('#list > li h3.task-title').nth(i)
            (only when that element exists exactly once in EVERY item — not for
            elements that exist only in one mode, like an edit form), or
          • scoped to the item:      const row = page.locator('#list > li').nth(i);
                                     row.locator('h3.task-title')
            Inside a scoped locator use ONLY the part after the list selector.
            NEVER `row.locator('#list > li h3.task-title')`: a scoped locator
            cannot match through the item itself, so that never resolves.
        The same applies inside page.evaluate: `rowEl.querySelector('h3.task-title')`.
        Prefer the scoped form whenever the element is conditional (edit forms,
        per-mode buttons) or when reading several fields of one item.

        ── OBSERVED BEHAVIOUR (when the input has an "Observed behaviour" block) ──
        Recordings of a real user operating the live UI, condensed into
        episodes: each user action with what the page did in response
        (elements that appeared / disappeared / changed, requests fired,
        URL changes, settle time). Treat it as AUTHORITATIVE for runtime
        behaviour — it beats guesses from source code, and for third-party
        components it is often the only truth available.
          • Name actions after the TASKS seen in episode sequences (e.g. the
            user clicked a combobox, an option list appeared, they clicked
            an option → one action SelectEngine(engine)). Each recording
            carries a `title`, the analyzer's `understanding` and, when
            given, the user's `intent` — the user's own words for the task;
            follow their vocabulary in names and descriptions. The episodes
            remain the truth for HOW it works.
          • An action waits for the REQUESTS it was observed to fire, never for
            UI effects: `await page.waitForResponse(r => r.request().method()
            === 'POST' && new URL(r.url()).pathname.startsWith('/api/items'))`,
            created BEFORE the triggering click (Promise.all). Turn `{id}`
            path segments into a prefix / regex match. Observed UI effects
            (what appeared, toasts, counters) are for STATE functions and for
            knowing what to click next — never something an action waits on
            (see NO POST-CONDITIONS). Never `waitForTimeout`.
          • HOVER-REVEALED targets (`revealedBy.on = "hover"` in the episodes,
            or element logic "Only visible while hovering …"): hover the host
            first, then click through __mzClick —
                const row = page.locator(<item selector>).nth(i);
                await row.hover();
                await __mzClick(row.locator(<in-item path>));
            ALWAYS address the revealed element inside the hovered item
            (`row.locator(...)`), NEVER `page.locator(<list-prefixed
            selector>).nth(i)`: when it is rendered on hover (`via: "js"`,
            "appears on hover") only the hovered item has one, so a
            page-level `.nth(i)` counts revealed copies, not rows.
            For a hover that shows a tooltip / popover, hover the trigger and
            wait for the observed popup before reading it.
          • If a floating listbox / menu appeared after clicking a control,
            prefer __mzSelectOption on that control, or click the observed
            option selector with `.filter({ hasText })`.
          • Write STATE functions against elements that were observed to
            change or appear (rows added, counters, toasts).
          • Selectors still come verbatim from `elements` — the observed
            block explains behaviour; it is not a selector source. Templated
            request paths may come from it.

        ── FINAL CHECKS BEFORE YOU EMIT ──
          • Does any action end with a wait / check on the UI (a locator
            `.waitFor`, `waitForFunction`, a before/after comparison, expect)
            instead of only `waitForResponse`? Remove it.
          • Every option set has a Get…Options state function, and its action
            takes name and/or index — no enum of merely observed values?
          • Every hover-revealed target hovered first and addressed through
            the hovered item's locator (never page-level `.nth(i)`)?
          • Per-item elements used inside a row locator with only their
            in-item path (never the full list-prefixed selector)?
          • Every selector in every body appears verbatim in the input
            signature (for per-item elements, the in-item part after the
            list selector counts)? If not, drop that function.
          • Every action name reads as user-intent, not click-transcript?
            If not, rename or fold up.
          • Every state function returns something the planner could read
            names/indices out of? If not, add fields to the return.
          • Names unique? Kinds set? Bodies non-empty? Then emit.
        """;

    public async Task<FunctionCatalogueResult> GenerateAsync(
        Signature signature,
        IReadOnlyList<Signature>? dependencies = null,
        IReadOnlyList<string>? recordedEpisodes = null,
        CancellationToken ct = default)
    {
        var opts = options.Value;
        // Function bodies ARE Playwright TypeScript, not analysis JSON, so we
        // use the stronger GeneratorModel here — the AnalyzerModel is tuned
        // for JSON extraction and regresses hard on real code generation.
        var chat = ChatClientFactory.ForModel(opts, opts.GeneratorModel);

        var signatureView = new
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
            relationships = TryParseGraph(signature.RelationshipsJson),
            userFlows = TryParseGraph(signature.UserFlowsJson),
        };

        var depView = dependencies is null || dependencies.Count == 0
            ? null
            : dependencies.Select(d => new
            {
                name = d.Name,
                type = d.Type,
                humanDescription = d.HumanDescription,
                elements = d.Elements
                    .OrderBy(e => e.Order)
                    .Select(e => new
                    {
                        selector = e.ElementSelector,
                        category = e.Category.ToString().ToLowerInvariant(),
                        elementType = e.ElementType,
                        description = e.ElementDescription,
                    }),
            });

        var payload = JsonSerializer.Serialize(new
        {
            signature = signatureView,
            dependencies = depView,
        }, new JsonSerializerOptions { WriteIndented = true });

        // Source is included as trailing grounding so the LLM can distinguish
        // between "the signature says X" (authoritative for selectors) and
        // "the source implies Y" (useful for describing intent / uncovering
        // enum values / spotting subtle behaviour like optimistic updates).
        // If SourceCode is empty on an older signature we just skip that
        // block rather than shipping an empty fenced region.
        var sourceBlock = string.IsNullOrWhiteSpace(signature.SourceCode)
            ? string.Empty
            : $"\n\nComponent source (grounding — do NOT invent selectors from " +
              $"this; use it to understand intent and enum values):\n" +
              $"```tsx\n{signature.SourceCode}\n```";

        // Recorded sessions: what the live UI actually did after each action.
        // When there is no source code (recording-only signatures) this is
        // the only grounding beyond the element list itself.
        var observedBlock = recordedEpisodes is null || recordedEpisodes.Count == 0
            ? string.Empty
            : "\n\nObserved behaviour (recorded from real user sessions on the live UI — " +
              "AUTHORITATIVE for runtime behaviour, timing, and what appears after each action):\n" +
              string.Join("\n", recordedEpisodes.Select((e, i) => $"```json recording-{i + 1}\n{e}\n```"));

        var userMsg =
            $"Produce the function catalogue for this signature. Reuse every " +
            $"selector byte-for-byte from the `elements` list.\n\n" +
            $"{payload}{sourceBlock}{observedBlock}";

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

        List<GeneratedFunction> parsed;
        try
        {
            parsed = ParseAndValidate(raw);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Function catalogue generator returned non-JSON content. First 200 chars: " +
                $"{raw[..Math.Min(200, raw.Length)]}", ex);
        }

        // NO POST-CONDITIONS guard: one repair round for actions that assert
        // or wait on the UI, then strip what is still left at the tail.
        var flagged = parsed
            .Where(f => CatalogueBodyLint.IsAction(f.Kind) && CatalogueBodyLint.Issues(f.Body).Count > 0)
            .ToList();
        if (flagged.Count > 0)
        {
            try
            {
                var report = string.Join("\n\n", flagged.Select(f =>
                    $"- {f.Name}: " + string.Join("; ", CatalogueBodyLint.Issues(f.Body)) +
                    $"\n```ts\n{f.Body}\n```"));
                var repair = await chat.CompleteChatSafeAsync(
                    new List<ChatMessage>
                    {
                        new SystemChatMessage(SystemPrompt),
                        new UserChatMessage(userMsg),
                        new AssistantChatMessage(raw),
                        new UserChatMessage(
                            "These ACTION functions break the NO ASSERTIONS / NO POST-CONDITIONS hard rule:\n\n" +
                            report + "\n\n" +
                            "Rewrite ONLY these functions. Keep name, kind, description, parameters, returns and " +
                            "preconditions; fix the body so that after its last interaction it only awaits " +
                            "waitForResponse promises created before the trigger — no locator.waitFor, " +
                            "waitForFunction, waitForTimeout, expect, or before/after checks. " +
                            "Return {\"functions\": [...]} with just these functions."),
                    },
                    new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() },
                    ct);
                tokens += repair.Value.Usage?.TotalTokenCount ?? 0;
                var fixedFns = ParseAndValidate(LlmComponentAnalyzer.StripCodeFences(repair.Value.Content[0].Text ?? "{}"))
                    .ToDictionary(f => f.Name, StringComparer.Ordinal);
                parsed = parsed.Select(f => fixedFns.TryGetValue(f.Name, out var nf) && CatalogueBodyLint.IsAction(nf.Kind) ? nf : f).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Repair is best-effort; the strip below still applies.
            }
            parsed = parsed.Select(f => CatalogueBodyLint.IsAction(f.Kind) && CatalogueBodyLint.Issues(f.Body).Count > 0
                ? f with { Body = CatalogueBodyLint.StripTrailingUiWaits(f.Body) }
                : f).ToList();
        }

        return new FunctionCatalogueResult(parsed, tokens);
    }

    /// <summary>
    /// Parses a graph-shaped JSON string ({ nodes, edges } or the flow-graph
    /// shape) into a JsonElement so it can be re-serialised inline into the
    /// user prompt. Returns null on parse failure — the LLM tolerates a
    /// missing block far better than one containing a raw error string.
    /// </summary>
    private static JsonElement? TryParseGraph(string json)
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
    /// Validates the shape emitted by the LLM and returns the parsed list.
    /// The validation is intentionally strict: if the model returned garbage
    /// we want a clear write-time error, not a mysterious runtime failure
    /// inside the interpreter later.
    ///
    /// The invariants we enforce here mirror the ones documented in the
    /// system prompt so a violation on either side surfaces as one clear
    /// message rather than diverging silently.
    /// </summary>
    private static List<GeneratedFunction> ParseAndValidate(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("functions", out var fns)
            || fns.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Function catalogue generator returned JSON without the required " +
                "{ functions: [...] } shape.");
        }

        var result = new List<GeneratedFunction>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fn in fns.EnumerateArray())
        {
            var name = fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() : null;
            var kind = fn.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString() : null;
            var body = fn.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() : null;

            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("A function is missing `name`.");
            if (kind is not "state" and not "action")
                throw new InvalidOperationException(
                    $"Function '{name}' has invalid kind '{kind}' (expected 'state' or 'action').");
            if (string.IsNullOrWhiteSpace(body))
                throw new InvalidOperationException($"Function '{name}' is missing `body`.");
            if (!seenNames.Add(name!))
                throw new InvalidOperationException(
                    $"Duplicate function name '{name}'. Names must be unique within a catalogue.");

            var description = fn.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? "" : "";

            // Parameters is REQUIRED to be an object schema per the prompt.
            // If the model regressed and emitted null / non-object / missing,
            // fall back to the empty-object schema rather than blowing up —
            // this is a "salvage bad output" path, not a "silently accept
            // structural errors" one, so we still normalise both required
            // schema slots.
            var parameters =
                fn.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object
                    ? p.GetRawText()
                    : "{\"type\":\"object\",\"properties\":{},\"required\":[]}";

            // Returns may be either an object (typical) or the literal null
            // json type wrapped in an object like {"type":"null"} — accept the
            // object form only; if it's genuinely `null` in JSON, we still
            // fall back to the void schema so the interpreter has something
            // to validate against.
            var returns =
                fn.TryGetProperty("returns", out var r) && r.ValueKind == JsonValueKind.Object
                    ? r.GetRawText()
                    : "{\"type\":\"null\"}";

            var preconditions =
                fn.TryGetProperty("preconditions", out var pc) && pc.ValueKind == JsonValueKind.String
                    ? pc.GetString() ?? "" : "";

            result.Add(new GeneratedFunction(
                Name: name!,
                Kind: kind!,
                Description: description,
                ParametersJsonSchema: parameters,
                ReturnsJsonSchema: returns,
                Preconditions: preconditions,
                Body: body!));
        }

        // Empty catalogue is a signal, not an error — some signatures are
        // pure display and legitimately have no actions. We still emit a
        // warning-shaped exception because 99% of the time an empty return
        // means the LLM hit its context ceiling or misread the input.
        if (result.Count == 0)
            throw new InvalidOperationException(
                "Function catalogue generator returned zero functions. This " +
                "usually indicates a truncated response or a signature the " +
                "model couldn't interpret; try regenerating or trimming the " +
                "source code.");

        return result;
    }
}
