# Mazda — session context

A snapshot of everything Claude has learned about this project across the session,
intended as a hand-off doc for the next session or for human onboarding.

---

## 1. What Mazda is

A wizard-driven app that turns a user-supplied React/Angular/Vue component
(folder) + a plain-English test description into a runnable Playwright
`.spec.ts`, executes it against a Chrome instance the user is running locally,
and streams the result back to the browser.

It is built as a three-service Docker Compose stack:

- `db` — Postgres 16
- `api` — .NET 8 minimal-API (C#), plus Node 20 + Playwright installed inside
  the image so it can spawn the generated tests
- `web` — Vite + React + TypeScript, served by nginx

LLM calls go through an OpenAI-compatible gateway configured via `.env`
(`LLM_BASE_URL`, `LLM_API_KEY`, `LLM_ANALYZER_MODEL`, `LLM_GENERATOR_MODEL`).
At time of writing both are `openai/gpt-5.2-chat`.

---

## 2. The 9-step wizard (current)

```
01 Upload              UploadPage              upload component folder + optional MD
02 Interactable        EditorPage              review/edit interactive signature elements
03 Informational       InformationalReviewPage review/edit informational elements,
                                               deep-analyze pass available
04 User flows          UserFlowsPage           ReactFlow graph of UI states + transitions
05 Setup               SetupPage               English-prompt -> Playwright fixtures
06 Analyze state       AnalyzeStatePage        extract live UI text -> semantic state JSON
07 Test prompt         TestPromptPage          English-prompt -> Playwright test body
08 Merge & finalize    MergePage               LLM merge of setup + test snippets
09 Execution           ExecutionPage           run final spec, SSE log stream
```

Signature steps (1–4) operate on a `Signature` aggregate; generation steps
(5–9) operate on a `Generation` aggregate that references a Signature.

---

## 3. Domain model

### Signature (= a component's element inventory + flow graph)
- `Id`, `OwnerId`, `Name`, `Version`, `Source` (the bundled source text),
  `Type` (form/wizard/grid/…), `HumanDescription`, `UserFlowsJson`
- Elements (one-to-many):
  - `ElementSelector` — Playwright-ready CSS selector
  - `Category` — `Interactive | Informational`
  - `ElementType` — vocabulary differs per category
    - Interactive: `button | input | select | combobox | … | dropzone | …`
    - Informational: `text | label | value | status | count | heading | badge |
      image-alt | timestamp | error-text | tooltip | list | listitem`
  - `ElementDescription` — one-sentence "what is this"
  - `ElementLogic` — optional structural / lifecycle / relational notes
    (cardinality, index semantics, conditional existence, sibling
    relationships, sort order). Required for repeating containers and
    conditionally-rendered elements; empty for self-explanatory ones.
  - `Order`
- "Last used" memoised fields used to pre-fill new Generations:
  `LastSetupPrompt`, `LastTestPrompt`, `LastSetupScript`, `LastTestScript`,
  `LastFinalScript`, `LastAnalyzeStateScript`.

### Generation (= one wizard run against a Signature)
- `SignatureId`
- Setup phase: `SetupPrompt`, `SetupScript`
- Analyze-state phase: `AnalyzeStateScript`, `AnalyzeStateRawOutput`,
  `AnalyzeStateJson`
- Test phase: `TestPrompt`, `TestScript`
- `FinalScript` (merged)
- Status: `Status` (Pending/Generating/Ready/Failed), `FailureReason`,
  `TokensUsed`
- Execution: `ExecutionStatus` (Idle/Running/Passed/FailedTest/Errored/
  Cancelled), `ExitCode`, `ExecutionLog`, `ExecutionStartedAt`,
  `ExecutionEndedAt`

---

## 4. Backend layout

```
backend/src/
  Mazda.Domain/           — entities, value objects, enums. No deps.
  Mazda.Application/      — MediatR commands + handlers, use cases, the
                            deterministic AnalyzeScriptBuilder, abstractions
                            (I*Analyzer, IScriptRunner, IStateBuilder, etc.).
                            Depends only on Domain.
  Mazda.Infrastructure/   — EF Core (Npgsql), LLM clients (OpenAI SDK), runner
                            implementation, repositories. Depends on
                            Application + Domain. NEVER reference Infrastructure
                            from Application — it broke the build once.
  Mazda.Api/              — minimal-API endpoints, JWT auth, Program.cs (EF
                            EnsureCreated + idempotent ALTER TABLEs).
```

### LLM classes (in `Mazda.Infrastructure/Llm/`)
- `LlmComponentAnalyzer` — first-pass analyzer, emits BOTH categories.
- `LlmInformationalAnalyzer` — dedicated deep pass for informational elements
  only. Knows the existing signature so it doesn't re-emit interactive entries.
- `LlmFlowAnalyzer` — emits the user-flow graph JSON.
- `LlmScriptGenerator` — three methods: `GenerateSetupAsync`,
  `GenerateTestAsync`, `MergeAsync`. The test prompt is the largest and
  contains many hard rules (see §7).
- `LlmStateBuilder` — converts raw analyze-state JSON into semantic component
  state JSON.

### Runner (`Mazda.Infrastructure/Runner/`)
- `LocalProcessScriptRunner` — spawns `npx playwright test` in a per-run temp
  dir. Two entry points:
  - `RunAsync(genId, script)` — streams output line-by-line into the
    `IExecutionRegistry` so the SSE endpoint can tail it. Used by step 9.
  - `RunOnceAsync(script)` — synchronous, no registry interaction, returns
    full captured output. Used by step 6 (analyze-state).
- `InMemoryExecutionRegistry` — per-generation circular buffer + subscriber
  channels. `Reset(genId)` wipes a previous run before a new one starts.
- Auto-rewrites loopback hosts in `connectOverCDP` calls:
  - `localhost:PORT` / `127.0.0.1:PORT` → `host.docker.internal:PORT`
  - The bare-string form is upgraded to the helper-call form because Chrome's
    DevTools port verifies the `Host:` header. The injected helper
    `__mzConnectViaHost` does an HTTP probe with `Host: localhost:PORT`,
    rewrites the returned `webSocketDebuggerUrl` host back to
    `host.docker.internal`, then connects WS with the same spoofed Host.
- Injects two other helpers when the test references them:
  - `__mzPointerDrag(page, source, target)` — pointer-event drag (works on
    Angular CDK / @dnd-kit / react-dnd / react-beautiful-dnd / custom).
    `__mzResolveDragSource` auto-finds an inner drag handle if one of the
    common markers is the only match inside the passed source.
  - `__mzPointerSwap(page, items, i, j)` — true positional swap. For
    non-adjacent indices it does the two-step protocol; for adjacent it
    short-circuits to one drag.
- `StripAnsi` regex strips CSI/OSC/2-byte ANSI sequences (and bare CSI bodies
  that escape via tools eating the ESC) from every captured line before it
  enters the registry / persisted log. Lifted `FORCE_COLOR` override; only
  `NO_COLOR=1` remains in the spawn env.
- After a failed run, the runner emits the path of `test-results/.../trace.zip`
  so the user can `npx playwright show-trace` on the host.

### DB / migrations
- EF `EnsureCreated()` runs on startup (no migrations directory exists).
- Schema changes since the original `EnsureCreated()` snapshot are applied via
  idempotent `ALTER TABLE … ADD COLUMN IF NOT EXISTS …` blocks in
  `Mazda.Api/Program.cs`. **Never** modify the existing EF model + drop the
  volume; always add a corresponding `ADD COLUMN IF NOT EXISTS` so existing
  DBs upgrade cleanly.
- Columns added this way so far:
  - `signatures.human_description`
  - `signatures.user_flows`
  - `signatures.last_setup_prompt`, `last_test_prompt`
  - `signatures.last_setup_script`, `last_test_script`, `last_final_script`
  - `signatures.last_analyze_state_script`
  - `signature_elements.category` (default `'Interactive'`)
  - `signature_elements.element_logic`
  - `generations.execution_*` columns
  - `generations.setup_script`, `test_script`, `final_script` widened from
    `varchar(4000)` to `text`
  - `generations.analyze_state_script`, `analyze_state_raw_output`,
    `analyze_state_json`
  - `generations.setup_prompt`, `test_prompt` widened to `text`

### Save behaviour for signature elements — IMPORTANT
- The naive `sig.ReplaceElements(newList)` approach (Clear + AddRange on the
  backing field) does NOT reliably trigger EF Core's orphan-cascade DELETEs
  because of the `HasMany().WithOne()` + `PropertyAccessMode.Field` combo.
- The current correct approach lives in `SignatureRepository.ReplaceElementsAsync`:
  1. `db.ChangeTracker.Clear()` — wipe all snapshots.
  2. `db.SignatureElements.Where(SignatureId == sigId).ExecuteDeleteAsync()` —
     bulk DELETE old rows in SQL.
  3. `signature.ReplaceElements(newElements)` — rebuild the in-memory nav.
  4. `db.Signatures.Attach(signature)` + set `State = Modified`.
  5. `foreach child in signature.Elements: db.Entry(child).State = Added` —
     force-add (Attach would otherwise mark them `Unchanged` because their
     Guid PKs are non-default).
- Anything else has produced `DbUpdateConcurrencyException` ("expected 1
  row, affected 0") and is a known dead end.

---

## 5. Frontend layout

```
frontend/src/
  app/                    — top-level App + RequireAuth
  features/
    auth/                 — login/register, Zustand auth store
    signatures/           — Upload, Editor (interactable), Informational,
                            UserFlows
    generation/           — NewRun, Setup, AnalyzeState, TestPrompt, Merge,
                            Execution, Runs
    studio/               — dashboard
  shared/
    api/                  — typed DTOs + fetch client
    components/           — TopBar, Brand, Primitives (BtnPrimary/Ghost/Pill,
                            ScreenShell, StepIndicator), Icons
    tokens/               — globals.css + tokens.css (CSS variables, "mz-*"
                            classes)
    monaco-setup.ts       — Monaco editor loader (locally bundled, not CDN)
```

### Notable design choices
- Monaco is bundled locally (not the CDN loader) because the user's network
  was slow; `monaco-editor` is an explicit npm dep and Vite's `?worker`
  imports wire the workers.
- Google Fonts were dropped from `index.html` (same reason) — falls back to
  system fonts via the CSS variable chain in `tokens.css`.
- ReactFlow + dagre power the user-flows graph viz.
- TanStack Query handles all server state. Mutations call
  `qc.setQueryData(["signature"|"generation", id], data)` after success so
  the UI updates instantly without a refetch.
- Zustand v5 + `useShallow` from `zustand/react/shallow` for the auth store.
  Zustand v5 with the naive selector pattern (returning a new object every
  render) causes an infinite re-render → React error #185; `useShallow`
  is the fix.
- The frontend SSE consumer is in `ExecutionPage`. It opens an `EventSource`
  to `/api/generations/:id/execution/stream` only when `executionStatus`
  equals `"Running"`, closes on the `end` event, and refetches the
  generation to pull the terminal state.
- The Studio Hero hero is responsive: at <720px the glyph hides and the
  layout collapses to a single column. `flex-shrink: 0` on `.mz-hero` is
  required because the parent is a flex column with `overflow: auto`.

### Routes (all under `RequireAuth` except `/login`)
```
/                              StudioPage
/login                         AuthPage
/signatures                    LibraryPage
/signatures/new                UploadPage
/signatures/:id                EditorPage (interactable review)
/signatures/:id/informational  InformationalReviewPage
/signatures/:id/flows          UserFlowsPage
/runs                          RunsPage
/runs/new                      NewRunPage (auto-creates a Generation)
/runs/:id/setup                SetupPage
/runs/:id/analyze-state        AnalyzeStatePage
/runs/:id/test                 TestPromptPage
/runs/:id/merge                MergePage
/runs/:id/execute              ExecutionPage
/runs/:id                      ExecutionPage (canonical view URL)
```

---

## 6. CDP-attach plumbing (Chrome on the host)

The user's Chrome runs on their Windows host with `--remote-debugging-port=9222`
(and `--remote-debugging-address=0.0.0.0` so the container can reach it). The
runner is in a Linux container. Three issues stack up; the runner's helper
injection + auto-rewrite solves all of them:

1. **TCP reach** — container can't hit `127.0.0.1:9222`. Rewriter swaps
   loopback hosts in `connectOverCDP` URLs to `host.docker.internal`.
2. **HTTP `Host:` check** — Chrome v111+ rejects any `Host:` other than
   `localhost` on `/json/version`. The `__mzConnectViaHost` helper fetches
   the version probe via raw `node:http` with `Host: localhost:<port>`.
3. **WS `Host:` check + bad echoed URL** — Chrome's `webSocketDebuggerUrl`
   reflects the `Host:` we sent (so it advertises `ws://localhost:9222/...`,
   which from inside the container is the container's own loopback). The
   helper rewrites the host back to `host.docker.internal`, then calls
   `chromium.connectOverCDP({ endpointURL: rewrittenUrl, headers: { Host:
   'localhost:9222' } })` so the WS upgrade also spoofs Host.

The rewriter only modifies `connectOverCDP` calls. Other references in the
script (target-page URL prefixes, `page.goto` arguments, comments) are left
verbatim — a previous bug that mangled a `TARGET_PREFIX` string was caused
by a blanket rewrite and is no longer the behaviour.

---

## 7. LLM prompts — the rules they enforce

The test-generation system prompt
(`LlmScriptGenerator.TestSystemPrompt`) is the single biggest accumulation
of learnings. Selected hard rules currently in place:

- **No TODO / FIXME / placeholder comments** anywhere in generated tests.
- **Selector fallback ladder**: Signature selector → Playwright accessibility
  locators (`getByRole/getByLabel/getByPlaceholder/getByText/getByTitle`) →
  attribute selector. Use whichever the prompt-text fits best, no comment
  about the fallback.
- **Locator scoping**: pass-Signature-selectors-to-`page.locator` directly,
  never chain them onto unrelated containers. To disambiguate use `.nth(i)`,
  not parent scoping.
- **Indexing**: never `:nth-of-type(n)` / `:nth-child(n)` / `:first-of-type`
  / `:last-child` etc. for "n-th of the matched set"; always `.nth(i)` /
  `.first()` / `.last()`. Rewrite obligation applies even when the Signature
  itself emits the pseudo.
- **Element category awareness**: `interactive` → action target; `informational`
  → read & assert / sort / filter to identify the right interactive target.
  Do not click an informational element; do not read a button label as data.
- **`elementLogic` is required reading** before picking an index or assuming
  an element will exist (lifecycle, conditional rendering, cardinality
  relationships).
- **Drag**: default to `__mzPointerDrag(page, source, target)`. `dragTo` only
  when source is HTML5-native (`draggable="true"` or natively draggable tag).
- **Drag handle**: when a Signature element of category `interactive` and
  type `draggable` exists with a description referencing reordering the same
  entity that another element describes, that drag-handle element is the
  source — not the parent card.
- **Swap**: for positional swaps in a sortable list, `__mzPointerSwap(page,
  items, i, j)`. Banned to write the two-drag sequence manually or use a
  single drag (which is a reorder, not a swap, in most sort libraries).
- **`select` element type**: never `locator.selectOption(...)`. Open the
  dropdown with a click, then click the option by visible text (`getByText`
  by default, `getByRole('option', …)` only when the dropdown is known to be
  ARIA-compliant — many custom dropdowns are not).
- **Swap assertions**: identity, not position. `boundingBox()` `y` doesn't
  change when items swap because slot positions are fixed. Compare
  `innerText` / `data-testid` / `data-id` / a stable child instead.
- **Text-API consistency**: don't mix `.innerText()` capture with `toHaveText`
  assertion (the former adds `\n` between block children; `toHaveText`
  reads `textContent` which doesn't). Use plain `expect(x).toBe(y)` when both
  sides were captured with the same method, or capture with `.textContent()`
  to match `toHaveText`.
- **Attribute reads**: don't pass `null` from `getByAttribute(...)` into
  `toHaveAttribute(...)` — Playwright crashes with a TypeError. The
  TypeScript `!` is compile-time only. Guard with an explicit null check or
  fall back to text-based identity.
- The test generator now also sees the analyze-state JSON as ground truth
  for "the existing X" / "the current Y" references.

The analyzer system prompt (`LlmComponentAnalyzer.SystemPrompt`) has its
own set of rules — combinator semantics, drag-handle detection across CDK /
@dnd-kit / react-dnd / react-beautiful-dnd / generic markers, conditional
existence requiring explicit logic notes, etc.

---

## 8. How to run the stack

```
docker compose -f C:\codes\mazda-app\docker-compose.yml up -d --build
```

Frontend at <http://localhost:5173>, API at <http://localhost:5080>, DB on
host port 5434. The api container builds the frontend so a `--force-recreate
web` is required after frontend changes.

Common follow-ups:

- Inspect a run's persisted log:
  ```sql
  SELECT execution_log FROM generations WHERE "Id" = '<uuid>';
  ```
- Inspect signature elements:
  ```sql
  SELECT element_selector, category, "ElementType", "ElementDescription",
         element_logic
  FROM signature_elements
  WHERE "SignatureId" = '<uuid>'
  ORDER BY "Order";
  ```
- Tail api logs: `docker compose logs api --tail 80`

---

## 9. Quirks / things that have bitten us

- `gh` CLI is not installed; PRs must be opened via a compare URL.
- Token in `.env`: any rotation needs an api container recreate
  (`docker compose up -d --build --force-recreate api`).
- Frontend bundle is ~3.8 MB unminified, ~1 MB gzipped — Monaco + ReactFlow
  + xyflow are the heavy hitters. Vite warns about this on every build; it
  is functional and we've not split the bundle.
- The `node_modules` directory of the runner's temp run dirs is a symlink to
  `/usr/lib/node_modules` (global Playwright install) — created on first
  run. Required because Node's ESM resolution doesn't look at the global
  store.
- Local pgAdmin / external tools should connect on `localhost:5434` (the
  exposed port), not 5432.

---

## 10. Open / known issues

- None pending at the moment, beyond the general "frontend bundle is large"
  warning and the LLM occasionally still tripping on a prompt edge case.
  Most rule additions to the prompts in §7 are the product of a single
  observed failure being escalated to a hard rule.
