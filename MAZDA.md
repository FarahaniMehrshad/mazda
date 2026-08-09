# MAZDA

> A UI-driven assistant pipeline. Given a frontend component and a plain-English instruction, MAZDA synthesizes and executes a Playwright script that drives the real UI on a real browser — so an LLM agent can "use" an app the same way a human does, without going through an API.

---

## Table of contents

1. [What MAZDA is, and why it exists](#what-mazda-is-and-why-it-exists)
2. [Mental model of the system](#mental-model-of-the-system)
3. [Current implementation (today's codebase)](#current-implementation-todays-codebase)
   - [Backend layout](#backend-layout)
   - [The Signature aggregate](#the-signature-aggregate)
   - [The wizard pipeline (10 steps)](#the-wizard-pipeline-10-steps)
   - [LLM ports and analyzers](#llm-ports-and-analyzers)
   - [Runtime helpers injected into generated tests](#runtime-helpers-injected-into-generated-tests)
   - [MCP server](#mcp-server)
   - [Frontend](#frontend)
   - [Persistence](#persistence)
   - [Deployment](#deployment)
4. [Proposed new architecture](#proposed-new-architecture)
   - [Why a redesign](#why-a-redesign)
   - [Front-driven vs API-driven assistants](#front-driven-vs-api-driven-assistants)
   - [Two environments: Design and Operational](#two-environments-design-and-operational)
   - [The Page Manifest DSL](#the-page-manifest-dsl)
   - [Services in the new architecture](#services-in-the-new-architecture)
   - [Lifecycle and versioning](#lifecycle-and-versioning)
5. [Mapping today's code → the proposed services](#mapping-todays-code--the-proposed-services)
6. [Glossary](#glossary)

---

## What MAZDA is, and why it exists

There are two ways an LLM agent can drive an application:

| Approach | How it works | Where it hurts |
|---|---|---|
| **API-driven** | Agent calls the backend API directly. | APIs are often undocumented, scattered across many endpoints, require client-side logic to produce valid input/output shapes, and don't compose well across services. |
| **Front-driven (UI-driven)** | Agent drives the frontend just like a human user. | Some UI elements (canvases, custom widgets) aren't selectable from the DOM; interactions can require many clicks; selectors are unstable. |

MAZDA is a **front-driven** assistant pipeline. Its bet is that for many products — especially complex ones with weak API docs and rich client-side logic — driving the UI is the more tractable problem. To make UI-driving robust, MAZDA shifts the cost: it does heavy LLM work **once per component** (at design time) to produce a structured description of every UI surface, then uses that description at **runtime** to generate and execute Playwright scripts cheaply and reliably.

The core thesis: **a stable, semantic description of the UI is a better contract for an LLM than the raw DOM**.

---

## Mental model of the system

```
DESIGN-TIME (per component, per change)
    Component source code  ─┐
    DOM snapshots          ─┤
    Documentation          ─┼─►  LLM analysis  ─►  Signature / Manifest  ─►  Storage
    Manual hints           ─┤                      (stable JSON DSL)
    Live browser session   ─┘

RUNTIME (per agent request)
    User prompt           ─┐
    Current page state    ─┼─►  LLM script-gen  ─►  Playwright script  ─►  Browser execution
    Signature / Manifest  ─┘
```

Two cost centers, deliberately separated:
- **Design-time** is LLM-heavy and slow, but happens once.
- **Runtime** is fast and predictable because the LLM has a rich, stable context (the signature/manifest) instead of raw DOM.

---

## Current implementation (today's codebase)

Today's MAZDA is a Mazda.Api backend + React frontend living in `C:\codes\mazda-app`. It implements the design + runtime split as a **wizard** rather than as two separate environments — both phases run side-by-side in the same app.

### Backend layout

Clean-architecture .NET 8 solution:

```
backend/
  Mazda.sln
  src/
    Mazda.Domain/         ─ Pure domain entities + value objects
    Mazda.Application/    ─ CQRS use cases (MediatR), validators, ports
    Mazda.Infrastructure/ ─ EF Core, LLM clients, script runner, MCP host
    Mazda.Api/            ─ Minimal-API endpoints, DI, auth, Program.cs
```

Key cross-cutting tech:
- **MediatR** for CQRS dispatch (every use case is `IRequest<Result<TDto>>`).
- **FluentValidation** for command validation.
- **EF Core 8 + Npgsql** for persistence; schema is provisioned by `EnsureCreated` plus a sequence of idempotent `ADD COLUMN IF NOT EXISTS` blocks in `Program.cs` — no migrations folder.
- **OpenAI SDK** with an `OpenAI-compatible` endpoint pattern (works with OpenAI, OpenRouter, vLLM, Ollama, etc.).
- **JWT bearer auth** for the REST API; MCP traffic also uses Mazda bearer tokens.

### The Signature aggregate

A **Signature** is MAZDA's structured description of one component. It lives in `Mazda.Domain.Modules.Signatures`:

```csharp
class Signature
{
    Guid    OwnerId, Id;
    string  Name, Version, Source, SourceCode;   // SourceCode = the actual bundled component text
    string  Type;                                // form | wizard | grid | modal | …
    string  HumanDescription;
    string  UserFlowsJson;                       // step 5 output
    string  RelationshipsJson;                   // step 4 output (NEW)
    string  LastSetupPrompt, LastTestPrompt;
    string  LastSetupScript, LastTestScript, LastFinalScript;
    string  LastAnalyzeStateScript, LastAnalyzeStateRawOutput, LastAnalyzeStateJson;
    string  LastAnalyzeStateScope;
    List<SignatureElement>     Elements;
    List<SignatureDependency>  Dependencies;
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

`InvalidateDerivedCaches()` clears `UserFlowsJson`, `RelationshipsJson`, analyze-state caches, and last-test/final-script buffers whenever the element set meaningfully changes — so a downstream wizard step never operates on selectors that no longer exist.

### The wizard pipeline (10 steps)

Each step is addressable by URL via `useStepNavigator`:

| # | Step | URL | What runs | Outputs |
|---|---|---|---|---|
| 1 | Upload | `/signatures/new` | First-pass analyzer over uploaded source | Signature created with `elements[]` |
| 2 | Interactable | `/signatures/:id` | Hand-review + optional re-analyze (LLM) | Edited interactive subset |
| 3 | Informational | `/signatures/:id/informational` | Deep informational pass (LLM) | Read-target selectors |
| 4 | **Relationships** | `/signatures/:id/relationships` | Relationship graph analyzer (LLM) | `{nodes, edges}` semantic graph |
| 5 | User flows | `/signatures/:id/flows` | Flow analyzer (LLM) | DFA-shaped flow graph JSON |
| 6 | Setup | `/runs/:id/setup` | LLM generates Playwright fixtures | Setup script |
| 7 | Analyze state | `/runs/:id/analyze-state` | Generates + runs an extraction script | Raw extract + semantic state JSON |
| 8 | Test prompt | `/runs/:id/test` | LLM generates the test body | Test script |
| 9 | Merge & finalize | `/runs/:id/merge` | LLM reconciles setup + test | Final `.spec.ts` |
| 10 | Execution | `/runs/:id/execute` | Spawns Playwright, streams logs via SSE | Run report |

Steps 1–5 are **signature-scoped** (per component). Steps 6–10 are **run-scoped** (per agent request) and pre-fill from the signature's `Last*` buffers.

### LLM ports and analyzers

The Application layer defines the LLM contract; Infrastructure implements it.

**Ports** (in `Mazda.Application.Common.Abstractions.Services`):

| Port | Used by step | Output |
|---|---|---|
| `IComponentAnalyzer` | 1, 2 | First-pass element extraction |
| `IInformationalAnalyzer` | 3 | Deep read-target extraction |
| `IRelationshipAnalyzer` | 4 | Semantic graph `{nodes, edges}` |
| `IFlowAnalyzer` | 5 | User-flow DFA JSON |
| `IScriptGenerator` | 6, 8, 9 | Playwright code |
| `IStateBuilder` | 7 | Semantic state JSON from raw extract |

**Implementations** (`Mazda.Infrastructure.Llm`):
`LlmComponentAnalyzer`, `LlmInformationalAnalyzer`, `LlmRelationshipAnalyzer`, `LlmFlowAnalyzer`, `LlmScriptGenerator`, `LlmStateBuilder`. Each is a thin class around the OpenAI Chat client with a carefully-engineered system prompt and strict JSON output validation.

**Relationship vocabulary** (step 4, the most recent addition):
`labels`, `grouped-under`, `controls`, `submits`, `opens`, `closes`, `mutually-exclusive`, `depends-on`, `refers-to`, `sibling-of`, `list-item-of`, `value-of`, `related`. Direction matters — labelers/controllers/members are always the `from` side.

**Effective-signature resolver** (`IEffectiveSignatureResolver`): when a signature has dependencies, the resolver returns the transitive closure in topological order (deepest dep first, root last). The generator unions every component's elements + flows + relationships into one labeled context block so test prompts can reference embedded components transparently.

### Runtime helpers injected into generated tests

Generated tests are not pure raw Playwright. `LocalProcessScriptRunner` injects a set of TypeScript helpers into every `.spec.ts` before execution, but only those the script actually uses:

- `__mzClick(locator)` — robust click that retries past detached nodes and intercept-overlay issues.
- `__mzSelectOption(page, trigger, text)` — framework-agnostic combobox / tree-select / nz-tree-select picker. Pre-baselines visible popups, observes which one opened, types the filter text, finds the option scoped to the popup container, clicks it, then clicks the trigger's parent to dismiss any lingering popover. Includes Persian/Arabic-Indic digit normalization, ZWNJ stripping, bidi-mark cleanup.
- `__mzPointerDrag` / `__mzPointerSwap` — pointer-event-based DnD that works with @dnd-kit, react-beautiful-dnd, Angular CDK, etc.
- `__mzConnectViaHost` — rewrites `chromium.connectOverCDP('http://localhost:9222')` to `host.docker.internal` automatically.

This injection is gated by regex detection — the runner only adds what the script references.

### MCP server

A first-class Streamable-HTTP MCP endpoint at `/api/mcp`, authenticated with the user's Mazda JWT. Each `McpTool` row in the DB:
- targets a specific Signature,
- carries a default setup-prompt and default test-prompt,
- when called by an external agent, kicks off a generation (same use-cases as the wizard) with the agent's prompt grafted on top.

So an external LLM can use MAZDA as a tool: "drive this UI to accomplish X."

### Frontend

React + TypeScript + Vite, served by nginx in production.

Stack:
- React Router, Zustand for auth state, React Query for fetching.
- `@monaco-editor/react` for JSON / TS editors throughout the wizard.
- `@xyflow/react` + `@dagrejs/dagre` for the User-flow page and the Relationships page — same graph-rendering kit, different node/edge styling.

Pages:
- `LibraryPage`, `UploadPage`, `EditorPage` (step 2), `InformationalReviewPage` (step 3), `RelationshipsPage` (step 4, **NEW**), `UserFlowsPage` (step 5).
- `NewRunPage`, `SetupPage`, `AnalyzeStatePage`, `TestPromptPage`, `MergePage`, `ExecutionPage`, `RunsPage`.
- `McpServerPage` for managing tool exposures.
- `StudioPage`, `AuthPage`, `LibraryPage`.

### Persistence

PostgreSQL via Npgsql. Tables:

| Table | Purpose |
|---|---|
| `users` | accounts |
| `signatures` | one row per component analysis |
| `signature_elements` | child rows (interactive + informational) |
| `signature_dependencies` | composite PK `(signature_id, depends_on_signature_id)` |
| `generations` | one row per run (setup + analyze + test + final + execution) |
| `mcp_tools` | external-agent tool exposures |

No EF migrations folder; schema is created by `EnsureCreated` and incrementally patched by `ALTER TABLE … ADD COLUMN IF NOT EXISTS` blocks in `Program.cs`. New columns this session: `relationships_json` on `signatures`.

### Deployment

Docker Compose with three services:
- `api` (.NET 8 SDK build → ASP.NET runtime, with Node + Playwright installed for the runner).
- `web` (Node 20 build → nginx serving the SPA bundle).
- `db` (PostgreSQL).

Build tricks:
- BuildKit `--mount=type=cache,id=mazda-nuget,target=/root/.nuget/packages` so package downloads survive across builds.
- `.dockerignore` excludes host-side `bin/` / `obj/` to avoid `NETSDK1064` (host's `project.assets.json` referencing Windows paths overlaying the container's restored one).
- `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1`: the API doesn't bundle Chromium; it connects to the user's existing browser via CDP using `__mzConnectViaHost`.

---

## Proposed new architecture

The document the team circulated proposes a deeper restructuring. The wizard becomes **two distinct environments** with **five distinct services**, instead of one monolithic app.

### Why a redesign

The wizard works well for development but mixes concerns that should be separable:
- Design-time LLM work and runtime execution share the same database and the same backend process.
- There's no first-class "ship a manifest to the customer" boundary — customers get the same image developers run.
- The browser pool is implicit, scoped to the runner; it can't be reused for design-time observation or for parallel agent traffic.
- The MCP server is bolted onto the API rather than a peer service.

The new design treats each concern as a deployable unit with its own contract.

### Front-driven vs API-driven assistants

The proposal calls out an important up-front decision: when building an assistant for a product, pick the entry point based on which surface has lower complexity for THAT product:

- If APIs are well-documented, single-service, and don't hide logic in the client → **API-driven** wins.
- If APIs are tangled, undocumented, or the client carries meaningful logic → **front-driven** wins.

MAZDA's contribution is making the front-driven path tractable, so teams that would otherwise be forced into API-driven work despite the pain have a real alternative.

### Two environments: Design and Operational

| Environment | Purpose | LLM usage | Where it runs |
|---|---|---|---|
| **Design Environment** (محیط طراحی) | Author and refine manifests | Heavy — many tokens per manifest, but once per component | Internal (dev team) |
| **Operational Environment** (محیط عملیاتی) | Serve agent traffic at the customer site | Moderate — per agent request | Deployed at customer |

The manifests produced by Design are **shipped** to Operational as versioned artifacts, piggy-backing on the regular customer release cycle. In dev installations, both environments live side-by-side so authors can debug a freshly-built manifest immediately.

### The Page Manifest DSL

Every manifest is a JSON document with these sections:

1. **Informational elements** — selectors the test reads (status, counts, dynamic values, list cells). The selectors a test would use to ASSERT, SORT BY, FILTER BY, or PICK a row.
2. **Interactional elements** — selectors the agent acts on (buttons, inputs, drag handles, dropzones). Templated for repeated rows (`row-{i}-edit`).
3. **Dependencies on other manifests** — a manifest can declare it embeds another. Runner's `gatherInformation` recurses; the LLM generator unions everyone's elements into one labeled context.
4. **User Flow (DFA / state graph)** — nodes = canonical UI states, edges = element-anchored transitions. JSON-serializable. Used as a reachability oracle for the script generator.
5. **High-level functions** — pre-baked Playwright abstractions like `moveItemFromList1To2(itemName)`, `selectOption(trigger, label)`. The LLM composes these instead of writing low-level DOM code, which dramatically improves reliability for complex compound actions.
6. **`gatherInformation()`** — a per-manifest function that captures the current state of the component. Knows to open modals/popovers first if needed, then reads every informational selector. Output is JSON consumed by the runner-LLM before script generation.

This DSL is the proposal's "stable LLM contract" — the thing every service operates on.

### Services in the new architecture

```
DESIGN ENVIRONMENT                          OPERATIONAL ENVIRONMENT
  Inputs → Manifest Studio → Storage(D)       External Agent
              ↓                                   ↓
        Browser Manager(D)                    MCP Server → Runner ← Storage(O)
                                                                ↓
                                                          Browser Manager(O)
                ↓ ship version
              [vertical sync between the two storages]
```

#### Manifest Studio
- **Shape:** Backend service + Frontend client + LLM client.
- **Auth:** None (internal authoring surface, not in the customer's AM/SSO).
- **Inputs:** zip / folder / loose component files / DOM snapshots / Markdown / product specs / manual hints / live browser observation (via Browser Manager).
- **Responsibilities:**
  - Filter inputs by type and size before sending to LLM.
  - Call LLM with a tuned system prompt to produce a draft manifest.
  - Round-trip with the author: add files, re-prompt, edit fields, re-validate.
  - Validate shape (no extra keys, key ordering, dependency-edge integrity).
  - Publish the validated manifest to Storage with a version bump.
  - Read existing manifests from Storage for iterative refinement.
- **In dev:** can drive the Runner directly to preview/debug a freshly-built manifest (the "preview / debug" bridge in the diagram).

#### Manifest Storage
- **Shape:** Backend service + DB. **No LLM.**
- **Responsibilities:**
  - Persist manifests + versions + dependency edges.
  - Serve fast reads (it's on the hot path of every agent request).
  - Identical software on both sides of the design / operational boundary — only the contents differ (Operational is a subset shipped at release time).

#### Browser Manager
- **Shape:** Backend service + Frontend client. **No LLM.**
- **Responsibilities:**
  - Spin up headless browser instances on demand.
  - Open tabs per caller request.
  - Inject per-tab cookies / session state.
  - Return stable `browserId` handles other services use to attach.
- **Exists in both environments** (design copy used by Studio for live observation; ops copy driven by Runner during execution).

#### Manifest Runner
- **Shape:** Backend service + Frontend client + LLM client.
- **Inputs:** `(manifestId, prompt, browserId)`.
- **Recipe:**
  1. Connect to the browser via `browserId`.
  2. Call the manifest's `gatherInformation`. If the manifest declares dependencies, recurse into theirs and merge the captured state.
  3. Send the merged state + the manifest + the user prompt to the LLM. Get back a Playwright script (possibly using the manifest's high-level functions).
  4. Execute the script in the connected browser.
  5. Persist a run report — screenshots + logs + scripts — to a run-reports DB.
- This is the runtime hot path. Every agent request ultimately flows through here.

#### MCP Server
- **Shape:** Backend service only.
- **Responsibilities:**
  - Expose every manifest as an MCP tool.
  - Each tool's input schema is `(browserId, prompt)`.
  - On invocation, delegate to Runner.
  - Capture screenshots + logs for audit.
- **Auth:** the agent-facing surface; this is what external LLM clients connect to.

### Lifecycle and versioning

Manifests evolve as the client UI evolves. The proposal acknowledges this and explicitly accepts a **human-in-the-loop** model:

- Client changes happen at a manageable rate; manual review of the resulting manifest changes is feasible.
- Each manifest carries a version. Storage tracks versions per manifest.
- Shipping an updated manifest to a customer is part of the customer's regular update cycle — not a separate "manifest deploy" pipeline.
- The goal is to keep this lag short, ideally with concurrent updates so the manifest never lags the UI in practice.

---

## Mapping today's code → the proposed services

For anyone navigating from the implemented wizard to the proposed services, here's the rough correspondence:

| Proposed service | Today's code closest to it |
|---|---|
| **Manifest Studio** | The wizard steps 1–5: `UploadPage`, `EditorPage`, `InformationalReviewPage`, `RelationshipsPage`, `UserFlowsPage` + the analyzers (`LlmComponentAnalyzer`, `LlmInformationalAnalyzer`, `LlmRelationshipAnalyzer`, `LlmFlowAnalyzer`). |
| **Manifest Storage** | `Mazda.Infrastructure.Persistence` — `signatures`, `signature_elements`, `signature_dependencies`, and the new `relationships_json` column. |
| **Browser Manager** | Not yet a first-class service. Today the runner spawns Playwright directly; CDP connection is via `__mzConnectViaHost`. Would need to be extracted. |
| **Manifest Runner** | `Mazda.Infrastructure.Runner.LocalProcessScriptRunner` + `LlmScriptGenerator` + `LlmStateBuilder` + the wizard steps 6–10. |
| **MCP Server** | `/api/mcp` Streamable-HTTP endpoint and the `mcp_tools` table. Already a peer in spirit; would become its own deployable. |

The **manifest concept** in the proposal is a rename + enrichment of today's **Signature**:

| Today's Signature has | Proposal's Manifest adds |
|---|---|
| `elements[]` (interactive + informational) | unchanged |
| `Dependencies` | unchanged |
| `UserFlowsJson` | unchanged |
| `RelationshipsJson` (NEW) | unchanged |
| — | first-class `gatherInformation` function |
| — | first-class catalogue of **high-level Playwright functions** the LLM composes |

Today the runtime helpers (`__mzClick`, `__mzSelectOption`, `__mzPointerDrag`, …) are a **shared** library injected into every script. The proposal generalizes this idea: each manifest carries its OWN catalogue of high-level functions tuned to the component, so the LLM has even less invention to do at runtime.

---

## Glossary

| Term | Meaning |
|---|---|
| **Signature** | (today) MAZDA's structured description of one component. Becomes the Manifest in the proposal. |
| **Manifest** | (proposed) The DSL document that describes one page/component for the assistant. |
| **Element** | One selector in a signature/manifest, classified as Interactive or Informational, with a description and optional structural logic. |
| **Selector** | A Playwright-ready CSS selector. Templated with `{i}` for repeated rows. |
| **Dependency** | When manifest A embeds manifest B's component; the runner unions B's elements/state into A's context. |
| **User Flow** | DFA-shaped graph of UI states + element-anchored transitions. |
| **Relationship graph** | Semantic edges between selectors (labels, grouped-under, controls, refers-to, …) that the DOM tree doesn't reveal. |
| **gatherInformation** | A per-manifest function that captures the live state of the component — opens modals first if needed, reads every informational selector, returns JSON. |
| **High-level function** | A pre-built Playwright abstraction the LLM can call by name instead of synthesizing low-level DOM steps. |
| **Setup script / Test script / Final script** | The three stages of a run: fixtures, the test body, and the merged single `.spec.ts`. |
| **Analyze state** | The step that runs an extraction script in the browser and turns its raw output into a semantic JSON via `IStateBuilder`. |
| **`browserId`** | (proposed) Opaque handle to a Browser-Manager-owned headless browser instance. |
| **CDP** | Chrome DevTools Protocol. MAZDA connects to externally-running browsers via CDP instead of bundling Chromium in its image. |
| **MCP** | Model Context Protocol. The server-side tool exposure that lets external agents call MAZDA as a set of tools. |

---

*This document covers both the implemented system (as of this session — including the new Relationships step) and the proposed redesign. The implemented system is the source of truth for what's running; the redesign is the target architecture for the next major refactor.*
