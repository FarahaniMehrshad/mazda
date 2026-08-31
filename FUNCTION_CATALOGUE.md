# Function Catalogue — design & implementation plan

> Status: **backend + frontend complete, running.** Live function testing
> ([§11](#11-live-function-testing)) landed 2026-08-30. This document is both the
> design record and the hand-off doc for the feature that replaced Mazda's
> runtime script-generation with a pre-built catalogue of high-level functions.

---

## Table of contents

1. [The problem](#1-the-problem)
2. [The core idea](#2-the-core-idea)
3. [Contracts](#3-contracts)
4. [Design decisions (the six questions)](#4-design-decisions-the-six-questions)
5. [Deliberate v1 limits](#5-deliberate-v1-limits)
6. [The wizard, before and after](#6-the-wizard-before-and-after)
7. [Implementation slices](#7-implementation-slices)
8. [File inventory](#8-file-inventory)
9. [Build/runtime issues hit along the way](#9-buildruntime-issues-hit-along-the-way)
10. [Remaining cleanup](#10-remaining-cleanup)
11. [Live function testing](#11-live-function-testing)
12. [Browser attachment (which tab)](#12-browser-attachment-which-tab)

---

## 1. The problem

In the original architecture, every agent request made the LLM do heavy work at
runtime:

```
Signature + user prompt + captured state  →  LLM  →  raw Playwright .spec.ts  →  execute
```

The model had to hold the whole element inventory in context, invent selectors,
reason about DOM structure, AND write correct Playwright — all on the hot path,
per request. Three costs followed:

- **Tokens.** Every request re-sent the full signature and paid for full script
  generation.
- **Latency.** Script generation is the slowest LLM call in the pipeline.
- **Reliability.** Generated Playwright is the single largest source of run
  failures. The model can be right about intent and still write a click that
  misses.

## 2. The core idea

Shift the Playwright authoring to **design time**, once per component. At
runtime the LLM only picks functions and fills arguments.

```
DESIGN TIME (once per signature)
  Signature (elements + relationships + flows)  →  LLM  →  Function catalogue
                                                            (name, schemas, body)

RUNTIME (per request)
  state functions  →  gather()  →  state snapshot ─┐
                                                   ├→ LLM → Plan → interpret → execute
  user prompt ─────────────────────────────────────┘
```

Two LLM calls become one heavy (amortised, offline) and one light (on the hot
path). The runtime call cannot produce broken Playwright, because it does not
produce Playwright at all — it produces a list of `(functionName, args)` pairs
validated against JSON Schema before anything touches a browser.

### Worked example — a to-do list

Catalogue generated once:

```
state:   GetTasks()  → Task[]
         GetFilters() → { view, sortBy }
action:  AddTask(name, priority)
         RemoveTask(index)
         DoneTask(index)
         ChangePriority(index, priority)
         SetFilter(view)
```

Request: *"mark buy-milk done and add buy-bread as high priority"*

1. `gather()` runs `GetTasks` + `GetFilters`, returns the live snapshot.
2. Planner LLM sees the catalogue + snapshot + prompt, emits two steps.
3. Interpreter runs `DoneTask(index=0)` then `AddTask(name="buy bread", priority="high")`.

No Playwright written at runtime. No DOM reasoning at runtime.

## 3. Contracts

### 3.1 Function

Persisted as `signature_functions` rows, one per function, child of a Signature.

| Field | Purpose |
|---|---|
| `Name` | PascalCase, unique per signature. The planner addresses functions by this string. |
| `Kind` | `State` or `Action` (see below). |
| `Description` | 1–3 sentences. What the planner reads to decide relevance. About **user goals**, not DOM mechanics. |
| `ParametersJsonSchema` | Full JSON Schema, always `type:"object"` even when empty. Enums for constrained values. |
| `ReturnsJsonSchema` | Full JSON Schema. `{"type":"null"}` for actions; structured for state. |
| `Preconditions` | Optional English note. Context for the planner; **not** enforced in v1. |
| `Body` | TypeScript executed as `async (page, args) => { ... }`. Params destructured from `args`. |
| `Order` | Display order + deterministic gather sequence. |

**The two kinds:**

- **`state`** — read-only observer, no parameters, returns structured JSON. Runs
  automatically during gather. The planner **never calls these** — it consumes
  their aggregated output.
- **`action`** — parameterised, side-effect producing, returns nothing. These are
  what the planner composes into plans.

The state/action split is what makes the runtime unambiguous. Without it, the
planner is tempted to interleave reads mid-plan, which v1 does not support.

**Available in every body** (injected by `LocalProcessScriptRunner`'s existing
regex-gated helper injection — bodies use them freely with no declaration):

- `__mzClick(locator)` — retries past detached nodes and intercept overlays
- `__mzSelectOption(page, trigger, text)` — framework-agnostic combobox picker
- `__mzPointerDrag` / `__mzPointerSwap` — pointer-event DnD
- `__mzConnectViaHost` — CDP host rewriting (applied to the harness preamble)

### 3.2 Gathered state

```json
{
  "signature": "todo-list@1.2.0",
  "capturedAt": "2026-08-09T13:22:04Z",
  "state": {
    "GetTasks": [
      { "index": 0, "name": "buy milk", "priority": "med", "done": false }
    ],
    "GetFilters": { "view": "all", "sortBy": "created" }
  }
}
```

Keys are state-function names. Dependency-owned functions are namespaced
(`Cart.GetItems`). A function that throws during gather lands as
`{"__mz_error": "..."}` rather than aborting the whole snapshot — the planner
can see what was and wasn't observed.

### 3.3 Plan

Flat sequence. Literal args only. No variables, no refs, no control flow.

```json
{
  "reasoning": "buy milk is index 0 (not done). Mark it, then append the new task.",
  "steps": [
    { "id": "s1", "call": "DoneTask", "args": { "index": 0 } },
    { "id": "s2", "call": "AddTask",  "args": { "name": "buy bread", "priority": "high" } }
  ]
}
```

Validation at parse time: unknown function name → reject; args failing the
function's schema → reject. `reasoning` is free text, never validated, useful
for audit.

### 3.4 Execution envelope

```json
{
  "outcome": "failed",
  "stepsTotal": 2,
  "stepsExecuted": 1,
  "steps": [
    { "id": "s1", "call": "DoneTask", "status": "ok",     "durationMs": 340, "error": null },
    { "id": "s2", "call": "AddTask",  "status": "failed", "durationMs": 120,
      "error": { "kind": "RuntimeError", "message": "..." } }
  ],
  "capturedState": { ... },
  "plan": { ... }
}
```

Envelopes are self-contained — state and plan are embedded so the Runs page can
render a historical run without a second fetch.

Error kinds: `SchemaMismatch`, `FunctionNotFound`, `PreconditionFailed`,
`RuntimeError`, `HarnessError`.

Step statuses: `ok`, `failed`, `skipped`. Fail-fast means everything after the
first failure is `skipped`.

## 4. Design decisions (the six questions)

These were settled explicitly before any code was written.

| # | Question | Decision |
|---|---|---|
| 1 | Plan expressiveness — allow loops/branches? | **No control flow.** Sequence only. If N items need acting on, the planner emits N explicit steps. |
| 2 | Failure policy | **Fail fast.** Stop at the first failed step. No retry, no LLM re-planning on the hot path. |
| 3 | Keep the old wizard steps alongside? | **No.** Rip out Setup / Analyze-state / Test / Merge entirely. The Functions step replaces them. |
| 4 | May function bodies call the `__mz*` helpers internally? | **Yes.** Bodies use them freely; the runner injects on demand. |
| 5 | Function granularity | **Avoid giant functions.** No `SubmitForm(payload)` swallowing 40 fields. Split at semantic boundaries; also avoid trivial single-click wrappers. |
| 6 | Resolving fuzzy user references ("the groceries task") | **Via gather, not mid-plan.** State functions run first and return concrete data; the planner reads names/indices out of the snapshot and writes literal args. |

Answer 6 is the one that simplified everything downstream: because the planner
already holds concrete state when it plans, the plan needs no variables, no
`$refs`, and no step-to-step data flow. That is what made 1 viable.

## 5. Deliberate v1 limits

Known, accepted, documented — not oversights.

- **All state functions run on every request.** No lazy/on-demand state. If a
  signature grows expensive observers, add a `runInGather: false` flag later.
- **The plan cannot observe between action steps.** If the world shifts mid-plan
  (a modal opens, indices shift), later steps operate on stale assumptions and
  fail. Fail-fast surfaces this cleanly; the user re-runs.
- **Return values do not flow between steps.** A workflow needing
  `CreateProject() → id` then `AddTaskTo(id, ...)` must be modelled as one
  compound action (`CreateProjectAndAddTask`). The catalogue generator's prompt
  says so explicitly.
- **Preconditions are advisory.** Stored and shown to the planner, never
  enforced by the interpreter.
- **Bodies are stored as strings**, not on-disk TypeScript modules. No tsserver
  checking while authoring. Revisit only if authoring feels flaky.

## 6. The wizard, before and after

**Before — 10 steps**

```
01 Upload  02 Interactable  03 Informational  04 Relationships  05 User flows
06 Setup   07 Analyze state 08 Test prompt    09 Merge          10 Execution
```

**After — 7 steps**

```
01 Upload  02 Interactable  03 Informational  04 Relationships  05 User flows
06 Functions   07 Run
```

Steps 1–5 are unchanged and remain signature-scoped. Steps 6–10 collapse into
two: authoring the catalogue, then running prompts against it.

Routes:

```
/signatures/new                 Upload
/signatures/:id                 Interactable
/signatures/:id/informational   Informational
/signatures/:id/relationships   Relationships
/signatures/:id/flows           User flows
/signatures/:id/functions       Functions        (new)
/signatures/:id/run             Run — fresh      (new)
/runs/:id                       Run — resume     (new)
```

## 7. Implementation slices

Delivered in six slices, each independently reviewable.

### Slice 1 — Domain, persistence, catalogue generator

- `SignatureFunction` entity + `FunctionKind` enum.
- `Signature` gains `Functions`; `InvalidateDerivedCaches()` clears them (bodies
  hard-reference selectors, so an element edit invalidates them).
- EF config for `signature_functions`; unique index on `(signature_id, name)`.
- DDL block in `Program.cs`.
- `IFunctionCatalogueGenerator` port + `LlmFunctionCatalogueGenerator`.

The generator's system prompt is the heart of this slice: naming rules
(user-intent not click-transcript), parameter rules (JSON Schema with enums and
descriptions), body rules (the `__mz*` contract, selector discipline), coverage
targets (1–4 state, 3–15 action for a typical component), explicit
anti-patterns, and a full worked to-do catalogue as an in-prompt exemplar. It
uses the **generator** model, not the analyzer model — bodies are real code and
weak models regress badly.

### Slice 2 — Generate + list use cases

- `GenerateFunctionCatalogueCommand` — ownership check, early reject when the
  signature has no elements, dependency resolution via
  `IEffectiveSignatureResolver`, wholesale replace.
- `ListSignatureFunctionsQuery` — dedicated read so function bodies do not
  hydrate into every `SignatureDto`.
- `ReplaceFunctionsAsync` on the repository, using the same tracker-clear +
  `ExecuteDelete` + re-attach pattern as `ReplaceElementsAsync` (EF
  orphan-tracking on field-backed `WithOne` collections is unreliable).
- Endpoints: `GET` and `POST .../functions/generate`.

### Slice 3 — Per-function CRUD

- Create / Update / Delete commands with validators.
- Name-collision pre-checks returning a clean `409` rather than leaking a
  Postgres unique-violation as a `500`.
- JSON well-formedness validation on both schema fields (not JSON-Schema
  semantics — that is the interpreter's job).
- Delete pre-checks existence so an already-gone function `404`s instead of
  silently `204`-ing.
- Endpoints: `POST`, `PUT .../{fid}`, `DELETE .../{fid}`.

### Slice 4 — Runtime primitives

- Ports: `IStateGatherer`, `IPlanGenerator`, `IPlanInterpreter`; records for
  `GatheredState`, `Plan`, `PlanStep`, `StepResult`, `ExecutionEnvelope`;
  `HarnessException` for runner-level failures.
- `FunctionHarnessBuilder` — stitches catalogue bodies into an ephemeral
  `.spec.ts`. Two shapes: gather (runs state functions, prints JSON between
  `__MZ_STATE_BEGIN__` / `__MZ_STATE_END__` sentinels) and execute (dispatch
  table + plan literal + per-step `__MZ_EVENT__` JSON lines). Parses each
  function's parameter schema to emit the destructuring statement bodies expect.
  Namespaces dependency functions.
- `StateGatherer` — `RunOnceAsync`, sentinel extraction, fast path for
  catalogues with no state functions.
- `LlmPlanGenerator` — the runtime LLM. Sees action functions only. Nine worked
  examples in the prompt including "no such item → empty plan". Cross-checks
  every emitted `call` against the catalogue.
- `PlanInterpreter` — `RunAsync` so events stream through the existing SSE
  registry; reconstructs the envelope from the event log, marking
  never-started steps `skipped`.

Every harness starts with `chromium.connectOverCDP('http://localhost:9222')`,
which the existing runner rewrites to `host.docker.internal` and pairs with the
injected `__mzConnectViaHost` helper — no new runner plumbing was needed.

### Slice 5 — Generation rewire

- `Generation` slimmed from 15 mutable fields to four data slots that fill in
  order: `Prompt` → `GatheredStateJson` → `PlanJson` → `ExecutionEnvelopeJson`.
- `PlanRunCommand` (synchronous: gather + plan) and `ExecuteRunCommand`
  (fire-and-forget: interpret, persist terminal state in a fresh DI scope).
- MCP invoke handler moved onto the same pipeline, run synchronously because MCP
  is request/response. Wire compatibility kept: `setup_prompt` + `test_prompt`
  are joined into one planner prompt.
- EF mapping and additive DDL. Legacy columns kept but unmapped; `NOT NULL`
  dropped on the two that were required.
- Endpoints trimmed from twelve to four (+ the unchanged SSE stream).

### Slice 6 — Frontend

- `types.ts` — new `GenerationDto` shape, `SignatureFunctionDto`, typed views of
  each JSON blob.
- `functionsApi.ts` — React Query hooks over the five function endpoints.
- `FunctionsPage.tsx` — two panes (state above, action below), per-row
  parameter preview parsed from the schema, Regenerate-all, and an editor modal
  with Monaco for both schemas and the body.
- `RunPage.tsx` — prompt → Plan → state snapshot + plan review → Execute → live
  SSE step cards → terminal envelope. Handles both the fresh-run and
  resume-run URL shapes.
- Step indicator 10 → 7; `useStepNavigator` rewritten.
- Router: seven legacy routes removed, three added.
- Seven legacy page files stubbed to `export {};` for a clean `git rm`.

## 8. File inventory

**New**

```
backend/src/Mazda.Domain/Modules/Signatures/SignatureFunction.cs
backend/src/Mazda.Application/Modules/Signatures/SignatureFunctionUseCases.cs
backend/src/Mazda.Infrastructure/Llm/LlmFunctionCatalogueGenerator.cs
backend/src/Mazda.Infrastructure/Llm/LlmPlanGenerator.cs
backend/src/Mazda.Infrastructure/Runner/FunctionHarnessBuilder.cs
backend/src/Mazda.Infrastructure/Runner/StateGatherer.cs
backend/src/Mazda.Infrastructure/Runner/PlanInterpreter.cs
frontend/src/features/signatures/functionsApi.ts
frontend/src/features/signatures/FunctionsPage.tsx
frontend/src/features/generation/RunPage.tsx
```

**Rewritten**

```
backend/src/Mazda.Domain/Modules/Generations/Generation.cs
backend/src/Mazda.Application/Modules/Generations/GenerationUseCases.cs
frontend/src/features/generation/api.ts
frontend/src/shared/components/useStepNavigator.ts
frontend/src/app/App.tsx
```

**Edited**

```
backend/src/Mazda.Domain/Modules/Signatures/Signature.cs
backend/src/Mazda.Application/Common/Abstractions/Services.cs
backend/src/Mazda.Application/Common/Abstractions/Repositories.cs
backend/src/Mazda.Application/Modules/Signatures/SignatureDependencyUseCases.cs
backend/src/Mazda.Application/Modules/Mcp/McpToolUseCases.cs
backend/src/Mazda.Infrastructure/Persistence/MazdaDbContext.cs
backend/src/Mazda.Infrastructure/Persistence/Repositories.cs
backend/src/Mazda.Infrastructure/DependencyInjection.cs
backend/src/Mazda.Api/Program.cs
backend/src/Mazda.Api/Endpoints/Endpoints.cs
frontend/src/shared/api/types.ts
frontend/src/shared/components/Primitives.tsx
frontend/src/features/signatures/UserFlowsPage.tsx
frontend/src/features/generation/RunsPage.tsx
frontend/src/features/studio/StudioPage.tsx
```

**Stubbed, pending `git rm`**

```
backend/src/Mazda.Application/Modules/Generations/AnalyzeScriptBuilder.cs
frontend/src/features/generation/SetupPage.tsx
frontend/src/features/generation/AnalyzeStatePage.tsx
frontend/src/features/generation/TestPromptPage.tsx
frontend/src/features/generation/MergePage.tsx
frontend/src/features/generation/ExecutionPage.tsx
frontend/src/features/generation/NewRunPage.tsx
frontend/src/features/generation/ScriptEditor.tsx
```

## 9. Build/runtime issues hit along the way

Recorded because several are traps this codebase will hit again.

| Symptom | Cause | Fix |
|---|---|---|
| `CS1061: Generation does not contain InvalidateAnalyzeAndDownstreamCaches` | Dependency use cases called a method removed in the aggregate rewrite. | Dropped the calls — Generations are now single-shot snapshots, so invalidating history would be wrong. Also removed the newly-unused repo dependency. |
| `CS0029: Cannot convert Task to CancellationToken` | Named the `CancellationToken` parameter `_`, which then shadowed the discard in `_ = Task.Run(...)`. | Renamed to `_ct`. |
| `TS2322` on step error kind | Wire `LiveEvent.error.kind` is `string`; `StepResultBlob` wants the closed `StepErrorKind` union. | Cast at the narrow boundary rather than widening the blob type. |
| `TS2339: testPrompt does not exist` in `StudioPage` | Missed this file when updating consumers of the old DTO. | Updated to `prompt` + a stage badge derived from which blob is populated. |
| `42703: column "setup_prompt" does not exist` on boot | A legacy `ALTER COLUMN ... TYPE text` block ran unconditionally, but on a **fresh** DB those columns are never created (they were dropped from the EF mapping). | Wrapped the widening block in `information_schema` existence guards. |
| `FormatException: Expected an ASCII digit` on boot | `ExecuteSqlRaw` runs SQL through `string.Format`; the JSON `DEFAULT` literals on `signature_functions` contain `{` and `}`. | Doubled every brace in those literals. |
| "Next" from User flows jumped to Run | `UserFlowsPage` hard-coded `navigate('/runs/new')`, a route removed in slice 6, which fell through to the catch-all. | Routes to `/signatures/:id/functions`. |
| Functions page content clipped at viewport bottom | `.mz-stage` is a flex column; children must claim `flex:1; overflow:auto` to become the scroll region. | Wrapped page content in a `mz-scroll` region on both new pages. |

**Two recurring traps worth internalising:**

1. **`ExecuteSqlRaw` + literal braces.** Any `{` or `}` in raw SQL must be
   doubled. Applies to JSON defaults, `DO $$` bodies containing braces, anything.
2. **`EnsureCreated` + hand-written migrations.** Removing a column from the EF
   mapping means every unrelated migration line touching that column must be
   guarded, because fresh databases will not have it. This class of bug
   disappears entirely under real EF migrations, which `README.md` already
   documents but the codebase does not use.

## 10. Remaining cleanup

Not blockers; none of these affect a working build.

- [x] `git rm` the eight stub files listed above.
- [x] Remove the now-dead `Signature.LastSetupPrompt`, `LastTestPrompt`,
      `LastSetupScript`, `LastTestScript`, `LastFinalScript`,
      `LastAnalyzeState*` fields and their EF mappings — nothing reads them.
- [x] Migration to `DROP COLUMN` the abandoned `generations` columns
      (`setup_prompt`, `test_prompt`, `setup_script`, `analyze_state_*`,
      `test_script`, `final_script`). Done as one block at the END of the
      `Program.cs` startup sequence, with the `ADD COLUMN` / widening blocks
      that used to create those same columns deleted — otherwise every boot
      would add them back and drop them again. The nine dead `signatures`
      `last_*` columns go in the same block.
- [ ] Design polish pass on `FunctionsPage` / `RunPage` — they are functional
      and use the existing tokens, but have not had the visual review the
      older pages got.
- [x] **"Test this function"** affordance on each catalogue row — see
      [§11](#11-live-function-testing).
- [ ] Consider migrating from `EnsureCreated` + ad-hoc `ALTER` blocks to real
      EF migrations (see the trap note above).

### Found while doing the above

- [ ] **`POST .../functions` (hand-add a function) fails with
      `DbUpdateConcurrencyException`** — *"expected 1 row(s), but actually
      affected 0"* from `CreateSignatureFunctionHandler`'s `SaveChangesAsync`.
      `sig.AddFunction(fn)` mutates the field-backed `_functions` collection on
      a tracked aggregate and relies on EF to infer the INSERT, which is the
      same orphan/attach unreliability that
      [§7 slice 2](#slice-2--generate--list-use-cases) worked around for
      `ReplaceFunctionsAsync`. The generate and delete paths bypass the tracker
      and are unaffected; only hand-adding is broken. Likely fix: an explicit
      `AddFunctionAsync` on the repository that does
      `db.SignatureFunctions.Add(fn)` directly rather than going through the
      navigation.

## 11. Live function testing

Run one catalogue function against the CDP-attached browser from the Functions
page, with caller-supplied arguments — a design-time debugging affordance, not
a runtime path.

**The contract that shapes everything else:** a body that throws is a
*successful* request whose result carries `ok: false` and the error message.
Seeing that message is the entire point. Only an unreachable browser or a spec
that will not compile produces a failed `Result` (502).

**Backend**

- `FunctionHarnessBuilder.BuildTestHarness` — a third harness shape beside
  gather and execute, fenced with `__MZ_FNTEST_BEGIN__` / `__MZ_FNTEST_END__`.
  It declares the **whole** catalogue (both kinds, root and dependencies) and
  invokes only the target, so a body leaning on a sibling helper still runs.
  The `try/catch` lives inside the emitted spec. It also captures `page.url()`
  and `page.title()` alongside the result — a wrong-page failure then explains
  itself.
- `HarnessOutput` — the sentinel-fence parsing and log-tail helpers, lifted out
  of `StateGatherer` so both readers share one implementation.
- `IFunctionTester` / `FunctionTester` — same shape as `StateGatherer`:
  build harness → `RunOnceAsync` → parse the fence. A missing fence means the
  harness died before reporting, which is the only `HarnessException` case.
- `FunctionArgs.Validate` — settles the open question of how to check arguments
  without a JSON Schema library. Checks the four things the catalogue generator
  actually emits: unknown property names (reported *before* missing-required, so
  a typo'd `labell` doesn't get reported as "missing `label`"), required
  properties, primitive types, and enums. Nested schemas, `oneOf`/`anyOf`,
  formats and bounds are ignored by design. If `PlanInterpreter`'s unimplemented
  `SchemaMismatch` path ever needs real validation, that is the moment to pull in
  a library and retire this.
- `TestSignatureFunctionCommand` + `POST /api/signatures/{id}/functions/{fid}/test`.
  Slow by nature; covered by the 30m `proxy_read_timeout` on `/api/`.

**Frontend**

- `functionForm.tsx` — `parseParams` / `isSimpleParamSet` / `extractParamPreview`
  / `prettifyJson` / `LabeledInput` / `LabeledSelect`, extracted from
  `FunctionsPage` so the page and the panel share them without importing each
  other.
- `FunctionTestPanel.tsx` — a modal off the catalogue row. Arguments come from a
  form generated from the parameter schema (text / number / checkbox / enum
  select), with a raw-JSON Monaco fallback for anything nested and an
  "Edit as JSON" escape hatch. Results show a pass/fail pill, duration, the
  return value, the error with a collapsible stack, and the runner log tail.
  Action functions carry an explicit "changes the live page, cannot be undone"
  warning.

**Why the panel is not embedded in `FunctionEditor`:** it runs the *saved* body.
Putting it inside the editor would let you tweak a body and "test" it while
actually exercising the previous version — a footgun worth more than the
convenience it buys.

---

*Written from the working session that designed and built this feature. The
implemented system is the source of truth for behaviour; this document explains
why it is shaped the way it is.*

## 12. Browser attachment (which tab)

Every harness used to open with:

```js
const ctx  = browser.contexts()[0] ?? await browser.newContext();
const page = ctx.pages()[0]        ?? await ctx.newPage();
```

`pages()[0]` is whichever target Chrome reports first — **not** the focused tab,
and not in tab order. With one tab open it is right by luck; with several it is
a coin toss, and gather / execute / function-test all silently agreed on the
wrong answer together. Observed while testing: two tabs opened as
`example.com` then `wikipedia.org` produced Wikipedia as `pages()[0]`.

**The model:** a per-user attachment, like DevTools attaching to a target. One
"currently attached tab" applies everywhere, changed in one place — the chip in
the TopBar.

### How a tab is identified

CDP `targetId`, never URL. Two tabs on the same app, or an SPA whose routes
share an origin, make URL ambiguous. A targetId survives reloads and
navigations and dies only with the tab. The harness resolves it by scanning
every context (normal tabs share one default context, but incognito windows and
other profiles get their own) and calling `Target.getTargetInfo` on each page.

When the attached tab is gone the harness **throws rather than falling back** to
the first page. Silently driving a different tab than the one you attached is
worse than a clear failure.

### Two constraints worth knowing

**Listing tabs is not a plain HTTP GET.** Chrome 111+ rejects any `Host:` header
other than localhost on `/json/*`, and the API container necessarily reaches
Chrome as `host.docker.internal`. `CdpClient` spoofs `Host: localhost:<port>` on
every request — the same workaround `__mzConnectViaHost` performs for the
WebSocket side (problem #2 of the three-problem stack in MAZDA.md). Without it
every probe 403s and is indistinguishable from "no browser running".

**Stale attachments are caught twice.** `CdpTargetResolver.ResolveValidatedAsync`
checks the tab still exists before spawning Playwright, so the common case is a
~0.7s `400` with one sentence instead of a ~20s Playwright spin whose reason is
buried in a log tail. The harness throw stays as the last line of defence for
the race where the tab closes in between.

### Pieces

| Piece | Where |
|---|---|
| `UserSettings` (port + attached targetId + display title/url) | `Mazda.Domain/Modules/Identity/UserSettings.cs`, table `user_settings` |
| `ICdpClient` / `CdpClient` — probe + list, with the Host spoof | `Mazda.Infrastructure/Cdp/CdpClient.cs` |
| `CdpTarget` threaded into all three harness shapes | `IStateGatherer`, `IPlanInterpreter`, `IFunctionTester` |
| `__mzPickPage` targetId resolution | `FunctionHarnessBuilder.AppendPreamble` |
| Status / attach / detach / port endpoints | `GET|POST /api/cdp/*` |
| TopBar chip + panel | `frontend/src/features/cdp/CdpPanel.tsx` |

An unreachable browser is reported **in the payload** (`available: false` plus a
`reason` naming the flags to start Chrome with), not as a failed request —
"Chrome isn't running" is a normal state the panel has to render.

### A CSS trap this hit

The panel is portalled to `document.body`. It lives inside `TopBar`, which sets
`backdrop-filter: blur(20px)`, and **a filtered ancestor becomes the containing
block for its `position: fixed` descendants** — so the overlay's `inset: 0`
resolved to the top bar's own box (measured: 1100x62) and the dialog rendered as
a clipped strip. Any future fixed-position UI mounted from inside `TopBar` needs
the same portal.
