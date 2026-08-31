# MAZDA

> A UI-driven assistant pipeline. Given a frontend component and a plain-English
> instruction, MAZDA drives the real UI in a real browser — so an LLM agent can
> "use" an app the same way a human does, without going through an API.

---

## Table of contents

1. [What MAZDA is, and why it exists](#what-mazda-is-and-why-it-exists)
2. [Mental model of the system](#mental-model-of-the-system)
3. [The Signature aggregate](#the-signature-aggregate)
4. [The function catalogue](#the-function-catalogue)
5. [The wizard pipeline (7 steps)](#the-wizard-pipeline-7-steps)
6. [Runtime: gather → plan → execute](#runtime-gather--plan--execute)
7. [Backend layout](#backend-layout)
8. [LLM ports and implementations](#llm-ports-and-implementations)
9. [Runtime helpers injected into generated code](#runtime-helpers-injected-into-generated-code)
10. [Playwright authoring rules](#playwright-authoring-rules) — *hard-won, read before writing a body*
11. [CDP attach: the three-problem stack](#cdp-attach-the-three-problem-stack)
12. [MCP server](#mcp-server)
13. [Frontend](#frontend)
14. [Persistence](#persistence)
15. [Deployment](#deployment)
16. [Running and operating the stack](#running-and-operating-the-stack)
17. [Quirks and gotchas](#quirks-and-gotchas)
18. [Glossary](#glossary)

---

## What MAZDA is, and why it exists

There are two ways an LLM agent can drive an application:

| Approach | How it works | Where it hurts |
|---|---|---|
| **API-driven** | Agent calls the backend API directly. | APIs are often undocumented, scattered across many endpoints, require client-side logic to produce valid input/output shapes, and don't compose well across services. |
| **Front-driven (UI-driven)** | Agent drives the frontend just like a human user. | Some UI elements (canvases, custom widgets) aren't selectable from the DOM; interactions can require many clicks; selectors are unstable. |

MAZDA is a **front-driven** assistant pipeline. Its bet is that for many
products — especially complex ones with weak API docs and rich client-side
logic — driving the UI is the more tractable problem.

When you build an assistant for a product, pick the entry point based on which
surface has lower complexity for *that* product: well-documented,
single-service APIs that don't hide logic in the client favour API-driven;
tangled or undocumented APIs, or a client that carries meaningful logic,
favour front-driven. MAZDA's contribution is making the front-driven path
tractable, so teams that would otherwise be forced into API-driven work despite
the pain have a real alternative.

To make UI-driving robust, MAZDA shifts the cost: it does heavy LLM work
**once per component** (at design time) to produce a structured description of
every UI surface *and a catalogue of executable operations over it*, then uses
that catalogue at **runtime** to fulfil requests cheaply and reliably.

The core thesis: **a stable, semantic description of the UI — plus a
pre-built set of operations over it — is a far better contract for an LLM than
the raw DOM.**

---

## Mental model of the system

```
DESIGN-TIME (per component, per change)
    Component source code  ─┐
    DOM snapshots          ─┤
    Documentation          ─┼─►  LLM analysis  ─►  Signature  ─►  Storage
    Manual hints           ─┘                      + function catalogue

RUNTIME (per agent request)
    state functions  ─►  gather()  ─►  state snapshot ─┐
                                                       ├─►  LLM  ─►  Plan
    user prompt  ──────────────────────────────────────┘             │
                                                                     ▼
                                                          interpreter executes
                                                          action functions in
                                                          the real browser
```

Two cost centres, deliberately separated:

- **Design-time** is LLM-heavy and slow, but happens once per component.
- **Runtime** is fast and predictable because the LLM never writes browser code.
  It picks function names from a catalogue and fills in arguments grounded in a
  freshly-captured state snapshot. Everything it emits is schema-validated
  before a browser is touched.

---

## The Signature aggregate

A **Signature** is MAZDA's structured description of one component. It lives in
`Mazda.Domain.Modules.Signatures`:

```csharp
class Signature
{
    Guid    OwnerId, Id;
    string  Name, Version, Source, SourceCode;   // SourceCode = the bundled component text
    string  Type;                                // form | wizard | grid | modal | …
    string  HumanDescription;
    string  UserFlowsJson;                       // step 5 output
    string  RelationshipsJson;                   // step 4 output
    List<SignatureElement>     Elements;
    List<SignatureDependency>  Dependencies;
    List<SignatureFunction>    Functions;        // step 6 output — the catalogue
}

class SignatureElement
{
    string           ElementSelector;     // Playwright-ready CSS
    ElementCategory  Category;            // Interactive | Informational
    string           ElementType;         // button | input | text | status | …
    string           ElementDescription;  // 1 sentence, plain English
    string           ElementLogic;        // optional structural / lifecycle notes
    int              Order;
}

class SignatureDependency
{
    Guid SignatureId, DependsOnSignatureId;
}
```

`InvalidateDerivedCaches()` clears `UserFlowsJson`, `RelationshipsJson`, **and
the entire function catalogue** whenever the element set meaningfully changes —
function bodies hard-reference selectors, so an element rename or removal makes
them stale in exactly the way it makes the relationship graph stale.

**Effective-signature resolver** (`IEffectiveSignatureResolver`): when a
signature has dependencies, the resolver returns the transitive closure in
topological order (deepest dep first, root last). Every design-time and runtime
step unions the dependency graph's elements, relationships and functions into
one context so a prompt can reference embedded components transparently.

---

## The function catalogue

Each signature carries a catalogue of **high-level functions** — the operations
an agent can perform on the component. This is what makes runtime cheap: the
planner LLM composes these by name instead of writing Playwright.

```csharp
class SignatureFunction
{
    string        Name;                  // PascalCase, unique per signature
    FunctionKind  Kind;                  // State | Action
    string        Description;           // what the planner reads
    string        ParametersJsonSchema;  // always a type:"object" schema
    string        ReturnsJsonSchema;     // {"type":"null"} for actions
    string        Preconditions;         // advisory English, not enforced
    string        Body;                  // TypeScript Playwright snippet
    int           Order;
}
```

**Two kinds, and the split matters:**

- **`State`** — read-only observer. No parameters. Returns structured JSON.
  Runs automatically during gather. The planner **never calls these** — it
  consumes their aggregated output. Named as queries: `GetTasks`,
  `GetActiveFilter`, `GetPagination`.
- **`Action`** — parameterised, side-effect producing, returns nothing. These
  are what the planner sequences into a plan. Named as commands: `AddTask`,
  `RemoveTask`, `ChangePriority`.

A body executes as `async (page, args) => { ... }` with every declared
parameter destructured into scope and the `__mz*` helpers available.

**Worked example — a to-do list component:**

```
state:   GetTasks()  → Task[]
         GetFilters() → { view, sortBy }
action:  AddTask(name, priority)
         RemoveTask(index)
         DoneTask(index)
         ChangePriority(index, priority)
```

Given that catalogue, *"mark buy-milk done and add buy-bread as high priority"*
becomes a two-step plan with literal arguments — no DOM reasoning, no
Playwright authored at request time.

**Granularity rules** the generator's prompt enforces: names convey user
intent, not click transcripts (`AddTask(name, priority)`, never
`ClickAddButton()`); no giant catch-alls that swallow a 40-field form; no
trivial single-click wrappers; row-targeting actions take a numeric `index`,
never a raw selector.

---

## The wizard pipeline (7 steps)

Each step is addressable by URL via `useStepNavigator`.

| # | Step | URL | What runs | Outputs |
|---|---|---|---|---|
| 1 | Upload | `/signatures/new` | First-pass analyzer over uploaded source | Signature created with `elements[]` |
| 2 | Interactable | `/signatures/:id` | Hand-review + optional re-analyze (LLM) | Edited interactive subset |
| 3 | Informational | `/signatures/:id/informational` | Deep informational pass (LLM) | Read-target selectors |
| 4 | Relationships | `/signatures/:id/relationships` | Relationship graph analyzer (LLM) | `{nodes, edges}` semantic graph |
| 5 | User flows | `/signatures/:id/flows` | Flow analyzer (LLM) | DFA-shaped flow graph JSON |
| 6 | **Functions** | `/signatures/:id/functions` | Catalogue generator (LLM) + hand-editing | State + action functions |
| 7 | **Run** | `/signatures/:id/run` or `/runs/:id` | gather → plan → execute | State snapshot, plan, execution envelope |

Steps 1–6 are **signature-scoped** (per component, done once). Step 7 is
**run-scoped** — one pass per user request.

---

## Runtime: gather → plan → execute

Three phases per request. Only the middle one involves an LLM.

### 1. Gather (deterministic)

`IStateGatherer` stitches every state function into an ephemeral spec, runs it
once against the connected browser, and collects the returns:

```json
{
  "signature": "todo-list@1.2.0",
  "capturedAt": "2026-08-09T13:22:04Z",
  "state": {
    "GetTasks": [{ "index": 0, "name": "buy milk", "priority": "med", "done": false }],
    "GetFilters": { "view": "all", "sortBy": "created" }
  }
}
```

Keys are function names; dependency-owned functions are namespaced
(`Cart.GetItems`). A function that throws lands as `{"__mz_error": "..."}`
rather than aborting the snapshot — the planner sees what was and wasn't
observed. Signatures with no state functions skip the browser round-trip
entirely.

### 2. Plan (the only runtime LLM call)

`IPlanGenerator` sees the **action** functions, the state snapshot, and the
user's prompt. State functions are deliberately hidden from it — showing them
would tempt it into reading state mid-plan, which the contract doesn't support.

```json
{
  "reasoning": "buy milk is index 0 (not done). Mark it, then append the new task.",
  "steps": [
    { "id": "s1", "call": "DoneTask", "args": { "index": 0 } },
    { "id": "s2", "call": "AddTask",  "args": { "name": "buy bread", "priority": "high" } }
  ]
}
```

Flat sequence. **Literal arguments only** — no variables, no `$refs`, no
interpolation. **No control flow** — acting on N items means N explicit steps.
Every `call` is cross-checked against the catalogue at parse time; an unknown
name rejects the whole plan before anything runs.

Because gather already produced concrete state, the planner can resolve fuzzy
references itself ("the groceries task" → read the names, pick the index) and
write literals. That single design choice is what lets the plan format stay
this small.

### 3. Execute (deterministic)

`IPlanInterpreter` builds a second ephemeral spec — a dispatch table over the
action functions plus the plan as a JSON literal — and runs it. Each step emits
a structured `__MZ_EVENT__` line that streams through the existing
`IExecutionRegistry` to the SSE endpoint, so the UI renders per-step progress
live.

**Fail-fast:** the first failed step stops the run. Everything after it is
reported as `skipped`.

The terminal result is an **execution envelope**:

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

Envelopes embed the state and plan, so a historical run renders without a
second fetch. Error kinds: `SchemaMismatch`, `FunctionNotFound`,
`PreconditionFailed`, `RuntimeError`, `HarnessError`.

### Accepted limits of this design

- All state functions run on every request — no lazy/on-demand state.
- A plan can't observe between action steps; if the world shifts mid-plan
  (a modal opens, indices move), later steps hold stale assumptions and fail.
- Return values don't flow between steps. A workflow needing
  `CreateProject() → id` then `AddTaskTo(id, …)` is modelled as one compound
  action.

These are deliberate. Each keeps the contract small enough that the runtime
stays predictable.

---

## Backend layout

Clean-architecture .NET 8 solution:

```
backend/
  Mazda.sln
  src/
    Mazda.Domain/         ─ Pure domain entities + value objects
    Mazda.Application/    ─ CQRS use cases (MediatR), validators, ports
    Mazda.Infrastructure/ ─ EF Core, LLM clients, runner, harness builder
    Mazda.Api/            ─ Minimal-API endpoints, DI, auth, Program.cs
```

Key cross-cutting tech:

- **MediatR** for CQRS dispatch (every use case is `IRequest<Result<TDto>>`).
- **FluentValidation** for command validation.
- **EF Core 8 + Npgsql** for persistence; schema is provisioned by
  `EnsureCreated` plus a sequence of idempotent `ADD COLUMN IF NOT EXISTS` /
  guarded `ALTER` blocks in `Program.cs` — no migrations folder.
- **OpenAI SDK** against any OpenAI-compatible endpoint (OpenAI, OpenRouter,
  vLLM, Ollama, …).
- **JWT bearer auth** for the REST API; MCP traffic uses the same tokens.

> ⚠️ Two traps this layout keeps setting. **(1)** `ExecuteSqlRaw` passes SQL
> through `string.Format`, so every literal `{` or `}` in raw SQL must be
> doubled. **(2)** Under `EnsureCreated`, removing a column from an EF mapping
> means fresh databases never create it — so every unrelated `ALTER` touching
> that column must be guarded on `information_schema`. Real EF migrations would
> retire both traps.

---

## LLM ports and implementations

The Application layer defines the contracts; Infrastructure implements them.

**Design-time** (`Mazda.Application.Common.Abstractions`):

| Port | Used by step | Output |
|---|---|---|
| `IComponentAnalyzer` | 1, 2 | First-pass element extraction |
| `IInformationalAnalyzer` | 3 | Deep read-target extraction |
| `IRelationshipAnalyzer` | 4 | Semantic graph `{nodes, edges}` |
| `IFlowAnalyzer` | 5 | User-flow DFA JSON |
| `IFunctionCatalogueGenerator` | 6 | State + action functions |

**Runtime:**

| Port | Phase | LLM? |
|---|---|---|
| `IStateGatherer` | gather | No |
| `IPlanGenerator` | plan | **Yes** — the only one |
| `IPlanInterpreter` | execute | No |

**Implementations** live in `Mazda.Infrastructure.Llm` (`LlmComponentAnalyzer`,
`LlmInformationalAnalyzer`, `LlmRelationshipAnalyzer`, `LlmFlowAnalyzer`,
`LlmFunctionCatalogueGenerator`, `LlmPlanGenerator`) and
`Mazda.Infrastructure.Runner` (`FunctionHarnessBuilder`, `StateGatherer`,
`PlanInterpreter`, `LocalProcessScriptRunner`).

Each LLM class is a thin wrapper around the OpenAI chat client with a
carefully-engineered system prompt and strict JSON output validation. The
catalogue generator and the plan generator both use the **generator** model
rather than the analyzer model — one writes real Playwright, the other needs
solid instruction-following to ground arguments in observed state.

**Relationship vocabulary** (step 4): `labels`, `grouped-under`, `controls`,
`submits`, `opens`, `closes`, `mutually-exclusive`, `depends-on`, `refers-to`,
`sibling-of`, `list-item-of`, `value-of`, `related`. Direction matters —
labellers, controllers and members are always the `from` side.

---

## Runtime helpers injected into generated code

Function bodies are not pure raw Playwright. `LocalProcessScriptRunner` injects
a set of TypeScript helpers into every spec before execution, but only those the
code actually references (regex-gated detection):

- `__mzClick(locator)` — robust click that retries past detached nodes and
  intercept-overlay issues, falling back to `dispatchEvent`.
- `__mzSelectOption(page, trigger, text)` — framework-agnostic combobox /
  tree-select picker. Pre-baselines visible popups, observes which one opened,
  types the filter text, finds the option scoped to that popup, clicks it, then
  dismisses any lingering popover. Includes Persian/Arabic-Indic digit
  normalisation, ZWNJ stripping, and bidi-mark cleanup.
- `__mzPointerDrag` / `__mzPointerSwap` — pointer-event DnD that works with
  @dnd-kit, react-beautiful-dnd, Angular CDK, etc.
- `__mzConnectViaHost` — rewrites `chromium.connectOverCDP('http://localhost:9222')`
  to `host.docker.internal` automatically.

Function bodies use these freely with no declaration — `FunctionHarnessBuilder`
emits `connectOverCDP('http://localhost:9222')` in every harness preamble and
the runner's existing rewrite + injection path handles the rest.

Two further runner behaviours worth knowing:

- **ANSI stripping.** `StripAnsi` removes CSI/OSC/two-byte escape sequences —
  and bare CSI bodies whose `ESC` was eaten upstream — from every captured line
  before it reaches the registry or the persisted log. Without it the browser
  renders `[33m` / `[7m` garbage inside `<pre>` blocks.
- **No colour env vars are set on the child.** Playwright hard-codes
  `FORCE_COLOR=1` on the workers it spawns (`runner/workerHost.js`). A
  `NO_COLOR` set on the parent is inherited into that worker, Node sees the
  conflict and emits a warning through `process.emitWarning` — which is
  *deferred*, so it can land between the two `console.log` calls a harness uses
  to fence its JSON payload and corrupt it. We cannot stop Playwright setting
  `FORCE_COLOR` on its own children, so the fix is not to create the conflict.
  `StripAnsi` was already doing the real work.
- **Fenced payloads are parsed per line, not concatenated.** For the same
  reason: anything can race into the fence. `HarnessOutput.ExtractFenced` picks
  the interior line that parses as JSON rather than gluing the interior
  together.
- **Trace hand-off.** After a failed run the runner prints the path of
  `test-results/.../trace.zip` plus the `npx playwright show-trace` command to
  run on the host.

---

## Playwright authoring rules

These are the accumulated lessons from watching generated browser code fail.
They were originally hard rules in the old test-generator prompt; they now
govern **function bodies** — whether written by `LlmFunctionCatalogueGenerator`
or edited by hand on the Functions page. Every one exists because a specific
run broke.

**Selectors and indexing**

- **Selector ladder.** Signature selector → Playwright accessibility locator
  (`getByRole` / `getByLabel` / `getByPlaceholder` / `getByText` / `getByTitle`)
  → attribute selector. Never invent a selector that appears in neither the
  signature nor the source.
- **Don't re-scope.** Pass signature selectors to `page.locator` directly; never
  chain them onto unrelated containers. Disambiguate with `.nth(i)`, not by
  adding a parent.
- **Never positional CSS.** No `:nth-of-type(n)`, `:nth-child(n)`,
  `:first-of-type`, `:last-child` for "the n-th of the matched set" — always
  `.nth(i)` / `.first()` / `.last()`. This rewrite obligation holds even when
  the signature itself emitted the pseudo-selector.
- **Read `elementLogic` first.** Before picking an index or assuming an element
  exists, check its logic note — it carries lifecycle, conditional rendering,
  and cardinality relationships that the selector alone doesn't express.

**Element categories**

- `interactive` → an action target. `informational` → read, assert, sort or
  filter by, to identify *which* interactive target to hit.
- Don't click an informational element; don't read a button's label as data.

**Interactions**

- **Clicks** go through `__mzClick`. A raw `.click()` hard-fails on the things
  real UIs are full of: hover-revealed icons, `pointer-events: none` ancestors,
  absolutely-positioned overlays, the screen-reader-only file input behind a
  styled uploader.
- **Dropdowns.** Never `locator.selectOption(...)` for a custom widget — use
  `__mzSelectOption`, which scopes the option search to a popup that became
  visible *after* the trigger click. Plain HTML `<select>` is the only exception.
- **Drag** defaults to `__mzPointerDrag(page, source, target)`. `dragTo` only
  when the source is HTML5-native (`draggable="true"` or a natively draggable
  tag). When a `draggable`-typed interactive element exists whose description
  references reordering the same entity as another element, *that handle* is the
  drag source — not the parent card.
- **Swaps** use `__mzPointerSwap(page, items, i, j)`. Writing the two-drag
  sequence by hand is banned, and a single drag is a *reorder*, not a swap, in
  most sortable libraries.

**Reading and asserting**

- **Swap assertions test identity, not position.** `boundingBox().y` does not
  change when two items swap — slot positions are fixed. Compare `innerText`,
  `data-testid`, `data-id`, or a stable child instead.
- **Don't mix text APIs.** `.innerText()` inserts `\n` between block children;
  `toHaveText` reads `textContent`, which doesn't. Either capture both sides
  the same way and use `expect(x).toBe(y)`, or capture with `.textContent()` to
  match `toHaveText`.
- **Guard attribute reads.** Never pass a possibly-`null` `getAttribute(...)`
  result into `toHaveAttribute(...)` — Playwright throws a TypeError, and
  TypeScript's `!` is compile-time only. Null-check explicitly or fall back to
  text-based identity.

**Hygiene**

- No `TODO` / `FIXME` / placeholder comments in a body.
- No `page.pause()`, `page.screenshot()`, or `console.log` — the harness owns
  observability.
- No hard-coded test data: `AddTask` takes `name` as a parameter; it never
  bakes in `"buy milk"`.
- No network, clock, or LLM calls — runtime determinism is the whole point.

> The catalogue generator's system prompt currently enforces the naming,
> parameter, return, selector-discipline, coverage and anti-pattern rules, plus
> the `__mz*` helper contract. The finer interaction and assertion rules above
> are **not all encoded in it yet** — they are the natural next additions when a
> generated body gets one of them wrong.

`LlmComponentAnalyzer`'s prompt carries its own accumulated rules: CSS
combinator semantics, drag-handle detection across Angular CDK / @dnd-kit /
react-dnd / react-beautiful-dnd / generic markers, and the requirement that
conditionally-rendered elements get an explicit logic note.

---

## CDP attach: the three-problem stack

The user's Chrome runs on their Windows host with
`--remote-debugging-port=9222 --remote-debugging-address=0.0.0.0`. The runner
lives in a Linux container. Three problems stack up, and the rewrite +
`__mzConnectViaHost` injection solves all three:

1. **TCP reach.** The container can't hit `127.0.0.1:9222`. The rewriter swaps
   loopback hosts in `connectOverCDP` URLs to `host.docker.internal`.
2. **HTTP `Host:` check.** Chrome v111+ rejects any `Host:` other than
   `localhost` on `/json/version`. The helper fetches the version probe via raw
   `node:http` with `Host: localhost:<port>`.
3. **WS `Host:` check and a wrong echoed URL.** Chrome's `webSocketDebuggerUrl`
   reflects the `Host:` we sent — so it advertises `ws://localhost:9222/…`,
   which from inside the container resolves to the container's own loopback
   where nothing listens. The helper rewrites that host back to
   `host.docker.internal`, then connects with
   `connectOverCDP({ endpointURL, headers: { Host: 'localhost:9222' } })` so the
   WS upgrade spoofs `Host` too.

### Which tab it drives

Connecting is only half the problem: a Chrome with five tabs open has five
candidate pages. Harnesses used to take `contexts()[0].pages()[0]` — the first
target Chrome reports, which is neither the focused tab nor tab order.

MAZDA now carries a **per-user browser attachment**: a CDP `targetId` stored on
`user_settings`, picked from the panel behind the TopBar chip, and applied
identically by gather, execute and function tests. The harness resolves it by
scanning every context and matching `Target.getTargetInfo`, and throws — rather
than falling back to the first page — when the attached tab is gone.

Listing tabs from the API needs the same `Host: localhost:<port>` spoof as
problem 2 above; `CdpClient` applies it. `FUNCTION_CATALOGUE.md` §12 has the
full design.

**The rewriter only touches `connectOverCDP` calls.** Everything else in a spec
— `page.goto` arguments, URL-prefix matchers, comments — is left verbatim. An
earlier blanket rewrite mangled a `TARGET_PREFIX` string the script compared
against the live page URL; that is no longer the behaviour and must not be
reintroduced.

---

## MCP server

A first-class Streamable-HTTP MCP endpoint at `/api/mcp`, authenticated with the
user's MAZDA JWT. Each `McpTool` row targets a specific Signature and carries
default prompts.

When an external agent invokes a tool, the handler runs the same
gather → plan → execute pipeline the wizard uses, **synchronously** (MCP is
request/response, so the caller blocks until the plan finishes). The result
carries the execution envelope so the agent gets structured per-step data, and
the run appears on the Runs page attributed to the tool.

So an external LLM can use MAZDA as a tool: *"drive this UI to accomplish X."*

---

## Frontend

React + TypeScript + Vite, served by nginx in production.

Stack:

- React Router, Zustand for auth state, React Query for fetching.
- `@monaco-editor/react` for JSON / TypeScript editing.
- `@xyflow/react` + `@dagrejs/dagre` for the User-flow and Relationships pages.

Pages:

- **Signature-scoped:** `LibraryPage`, `UploadPage`, `EditorPage` (step 2),
  `InformationalReviewPage` (step 3), `RelationshipsPage` (step 4),
  `UserFlowsPage` (step 5), `FunctionsPage` (step 6).
- **Run-scoped:** `RunPage` (step 7), `RunsPage` (history).
- `McpServerPage`, `StudioPage`, `AuthPage`.

`FunctionsPage` shows two panes — state functions above, action functions
below — with a per-row parameter preview parsed from the schema, a
regenerate-all action, and an editor modal carrying Monaco for both JSON
schemas and the body.

`RunPage` walks the whole runtime in one screen: prompt → Plan → review the
state snapshot and the plan → Execute → live step cards driven by the SSE
`__MZ_EVENT__` stream → terminal envelope. It handles both the fresh-run
(`/signatures/:id/run`) and resume (`/runs/:id`) URL shapes.

### Notable design choices

- **Monaco is bundled locally**, not loaded from the CDN — `monaco-editor` is an
  explicit npm dependency and Vite's `?worker` imports wire the workers. Google
  Fonts were likewise dropped from `index.html`; typography falls back through
  the CSS variable chain in `tokens.css`. Both because the network was slow.
- **Zustand v5 needs `useShallow`.** The naive selector pattern (returning a
  fresh object every render) causes an infinite re-render loop → React error
  \#185. `useShallow` from `zustand/react/shallow` is the fix, and the auth
  store uses it.
- **TanStack Query owns all server state.** Mutations call
  `qc.setQueryData(["signature"|"generation", id], data)` on success so the UI
  updates without a refetch round-trip.
- **`.mz-stage` is a flex column.** Any page whose content can grow must claim
  `flex: 1; overflow: auto` on its scroll wrapper, or it silently clips at the
  viewport bottom with no scrollbar. Same reason `.mz-hero` needs
  `flex-shrink: 0` — default shrinking squashes it and clips its own content
  under `overflow: hidden`.
- **The SSE consumer lives in `RunPage`.** It opens an `EventSource` only while
  `executionStatus === "Running"`, parses `__MZ_EVENT__` lines into step cards,
  closes on the `end` event, and refetches the generation to pull the terminal
  envelope.

---

## Persistence

PostgreSQL via Npgsql. Tables:

| Table | Purpose |
|---|---|
| `users` | accounts |
| `signatures` | one row per component analysis |
| `signature_elements` | child rows (interactive + informational) |
| `signature_dependencies` | composite PK `(signature_id, depends_on_signature_id)` |
| `signature_functions` | the catalogue — unique on `(signature_id, name)` |
| `user_settings` | per-user CDP attachment (port + tab `targetId`) |
| `generations` | one row per run |
| `mcp_tools` | external-agent tool exposures |

A `generations` row is a single-shot record of one request, filled in order:
`prompt` → `gathered_state_json` → `plan_json` → `execution_envelope_json`.
Nothing re-executes an old generation, so historical rows stay accurate to what
happened at the time.

### Schema evolution rule

No EF migrations folder; schema is created by `EnsureCreated` and incrementally
patched by idempotent blocks in `Program.cs`. The rule that keeps this working:

> **Never change the EF model and expect a volume drop to fix it.** Always add a
> matching `ADD COLUMN IF NOT EXISTS` (or guarded `ALTER`) so existing databases
> upgrade cleanly *and* fresh ones land in the same shape.

Several `generations` columns from earlier architectures (`setup_prompt`,
`test_prompt`, `setup_script`, `analyze_state_*`, `test_script`, `final_script`)
still exist in the database but are no longer mapped by EF; a follow-up
migration can drop them.

### Replacing a child collection — the only approach that works

`Elements` and `Functions` are field-backed collections mapped with
`HasMany().WithOne()` + `PropertyAccessMode.Field`. With that combination, the
naive `sig.ReplaceElements(newList)` (Clear + AddRange on the backing field)
does **not** reliably trigger EF Core's orphan-cascade DELETEs. It produces
`DbUpdateConcurrencyException` — *"expected 1 row, affected 0"* — because the
parent's navigation snapshot still points at rows that were deleted or are
about to be.

`SignatureRepository.ReplaceElementsAsync` / `ReplaceFunctionsAsync` do this
instead, and any future child collection must follow the same five steps:

1. `db.ChangeTracker.Clear()` — wipe every snapshot so `SaveChanges` can't diff
   against phantom rows.
2. `db.Set<TChild>().Where(x => x.SignatureId == sigId).ExecuteDeleteAsync()` —
   bulk-delete in one SQL statement, bypassing the tracker entirely.
3. `signature.ReplaceX(newChildren)` — rebuild the in-memory navigation.
4. `db.Signatures.Attach(signature)` and set `State = Modified`.
5. `foreach (var child in signature.X) db.Entry(child).State = Added` — force
   `Added`; `Attach` alone would mark them `Unchanged` because their Guid PKs
   are already non-default, and EF would silently skip the inserts.

Anything else is a known dead end.

`ReplaceElementsAsync` additionally content-hashes the collection before and
after, and only calls `InvalidateDerivedCaches()` when something actually
changed — so a rename-and-resave doesn't wipe the relationship graph, flows and
function catalogue for no reason.

---

## Deployment

Docker Compose with three services:

- `api` (.NET 8 SDK build → ASP.NET runtime, with Node + Playwright installed
  for the runner).
- `web` (Node 20 build → nginx serving the SPA bundle).
- `db` (PostgreSQL).

Build notes:

- BuildKit `--mount=type=cache,id=mazda-nuget,target=/root/.nuget/packages` so
  package downloads survive across builds.
- `.dockerignore` excludes host-side `bin/` / `obj/` to avoid `NETSDK1064`.
- `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1`: the API doesn't bundle Chromium; it
  connects to the user's existing browser over CDP via `__mzConnectViaHost`.
- `docker compose down -v` removes the `mazda-pgdata` volume for a clean
  schema rebuild.

---

## Running and operating the stack

```bash
docker compose up -d --build
```

Frontend at <http://localhost:5173>, API at <http://localhost:5080/swagger>,
Postgres on host port **5434** (not 5432 — external tools like pgAdmin must use
the exposed port).

Rebuild only what changed — the two images are independent:

```bash
docker compose up -d --build web    # frontend changes
docker compose up -d --build api    # backend changes
```

Rotating a value in `.env` (an LLM key, the JWT secret) needs the api container
recreated, not just restarted:

```bash
docker compose up -d --build --force-recreate api
```

For fast frontend iteration, skip the container entirely — Vite watches the
filesystem and hot-reloads, while the backend keeps running in Docker:

```bash
cd frontend && npm install && npm run dev     # http://localhost:5173
```

**Introspection recipes**

```bash
docker compose logs api --tail 80
```

```sql
-- What a run captured, planned, and did
SELECT prompt, gathered_state_json, plan_json, execution_envelope_json
FROM generations WHERE "Id" = '<uuid>';

-- A signature's element inventory
SELECT element_selector, category, "ElementType", "ElementDescription",
       element_logic
FROM signature_elements
WHERE "SignatureId" = '<uuid>'
ORDER BY "Order";

-- A signature's function catalogue
SELECT name, kind, description, "order"
FROM signature_functions
WHERE signature_id = '<uuid>'
ORDER BY "order";
```

> Column casing is mixed by design: properties inherited from `Entity`
> (`Id`, `CreatedAt`, `UpdatedAt`) are never explicitly mapped, so EF emits them
> quoted in PascalCase, while everything mapped via `HasColumnName` is
> snake_case. Hand-written SQL must match — and `"order"` needs quoting anyway,
> being a reserved word.

---

## Quirks and gotchas

- **`ExecuteSqlRaw` runs SQL through `string.Format`.** Every literal `{` or `}`
  must be doubled. This bit the JSON `DEFAULT` values on `signature_functions`
  and surfaces as `FormatException: Expected an ASCII digit`.
- **`EnsureCreated` + removed columns.** Dropping a column from an EF mapping
  means fresh databases never create it, so any unrelated `ALTER` still touching
  it fails with `42703`. Guard on `information_schema` — or move to real EF
  migrations, which retires this whole class of bug.
- **The runner's `node_modules` is a symlink** to `/usr/lib/node_modules` (the
  global Playwright install), created on first run in each temp run dir.
  Required because Node's ESM resolution doesn't consult the global store.
- **The frontend bundle is large** — roughly 3.8 MB unminified, ~1 MB gzipped.
  Monaco, ReactFlow and xyflow are the heavy hitters. Vite warns on every build;
  it is functional and the bundle has not been split.
- **Don't name a `CancellationToken` parameter `_`** in a handler that also does
  `_ = Task.Run(...)` — the parameter shadows the discard and the compiler tries
  to assign a `Task` to a `CancellationToken`.
- **`gh` CLI is not installed** on the dev machine; PRs go through a compare URL.
- Most rules in [Playwright authoring rules](#playwright-authoring-rules) exist
  because a single observed failure got escalated to a hard rule. When a
  generated body fails in a new way, the fix is usually a new rule there rather
  than a patch to one function.

---

## Glossary

| Term | Meaning |
|---|---|
| **Signature** | MAZDA's structured description of one component — elements, relationships, flows, and a function catalogue. |
| **Element** | One selector in a signature, classified Interactive or Informational, with a description and optional structural logic. |
| **Selector** | A Playwright-ready CSS selector. Templated with `{i}` for repeated rows. |
| **Dependency** | When signature A embeds signature B's component; every step unions B's elements, relationships and functions into A's context. |
| **User flow** | DFA-shaped graph of UI states + element-anchored transitions. |
| **Relationship graph** | Semantic edges between selectors (labels, grouped-under, controls, refers-to, …) that the DOM tree doesn't reveal. |
| **Function catalogue** | The set of high-level operations on a signature — what the planner composes at runtime. |
| **State function** | A no-argument reader returning structured JSON. Runs during gather; never called by the planner directly. |
| **Action function** | A parameterised, side-effect-producing operation. The planner's building block. |
| **Gather** | The deterministic phase that runs every state function and merges their returns into a state snapshot. |
| **Plan** | A flat, ordered list of `(function, literal args)` steps produced by the runtime LLM. |
| **Interpreter** | The deterministic phase that executes a plan step by step, fail-fast, streaming events. |
| **Execution envelope** | The terminal record of a run: outcome, per-step results, plus the state and plan that produced it. |
| **Harness** | The ephemeral `.spec.ts` that `FunctionHarnessBuilder` stitches from function bodies for one gather or execute pass. |
| **CDP** | Chrome DevTools Protocol. MAZDA connects to an externally-running browser instead of bundling Chromium. |
| **MCP** | Model Context Protocol. The server-side tool exposure that lets external agents call MAZDA. |

---

*This document describes the implemented system and is the single source of
truth for how MAZDA works — architecture, accumulated lessons, and operational
recipes. `FUNCTION_CATALOGUE.md` is its companion: the design reasoning behind
the catalogue architecture and the record of how it was built.*
