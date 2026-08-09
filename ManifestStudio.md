# Manifest Studio

> Enterprise-grade UI-driving assistant platform.
> Author once, ship to every customer, let AI agents drive your product like a human user.

This document is a **complete project specification** for the Manifest Studio platform. It is intended as the starting point for a fresh codebase, separate from the Mazda prototype that informed it.

---

## Table of contents

1. [Vision & non-goals](#1-vision--non-goals)
2. [The Manifest — the central abstraction](#2-the-manifest--the-central-abstraction)
3. [System architecture](#3-system-architecture)
4. [The Manifest DSL — formal specification](#4-the-manifest-dsl--formal-specification)
5. [Services in detail](#5-services-in-detail)
6. [Cross-cutting concerns](#6-cross-cutting-concerns)
7. [Technology choices](#7-technology-choices)
8. [Project & repository structure](#8-project--repository-structure)
9. [Data model](#9-data-model)
10. [Public API surface](#10-public-api-surface)
11. [Deployment topologies](#11-deployment-topologies)
12. [Local development](#12-local-development)
13. [Roadmap — phased rollout](#13-roadmap--phased-rollout)
14. [Migration story from Mazda](#14-migration-story-from-mazda)
15. [Open questions to resolve before kick-off](#15-open-questions-to-resolve-before-kick-off)
16. [Glossary](#16-glossary)

---

## 1. Vision & non-goals

### Elevator pitch

**Manifest Studio is the design surface and runtime that lets LLM agents drive any web application without an API contract.** A team authors a *Manifest* — a stable, semantic description of a screen — once per component. At runtime, agents combine that Manifest with a natural-language instruction; the platform synthesizes and executes a Playwright script against a real browser, then returns a structured result with screenshots and logs.

### Why this exists

In most real-world enterprise products:

- APIs are partially documented, scattered across many services, and require client-side logic to compose correctly.
- The UI carries domain knowledge the API doesn't expose (validations, conditional flows, multi-step wizards).
- Customers want AI assistants on top of their existing product *now*, without an 18-month API redesign.

Manifest Studio lets you ship a usable agent surface in the **time it takes to describe the UI**, not the time it takes to redesign the back-end.

### Success criteria

| Dimension | Target |
|---|---|
| Time to first working agent on a new screen | < 2 hours, measured from "developer has source code" to "agent can drive screen end-to-end" |
| Manifest authoring cost per screen | ≤ 1 LLM-assisted session + ≤ 30 minutes of human review |
| Agent-request p95 latency | < 8 seconds end-to-end (excluding browser navigation) |
| Customer-side install footprint | A single Helm chart + database; no internet egress required for runtime |
| Manifest update lag (after a UI change) | < 1 release cycle; ideally co-shipped with the UI change |

### Non-goals

- **Not** a Playwright IDE. The script is an intermediate artifact, not a user-facing product.
- **Not** a no-code workflow builder. Authors are engineers / SDETs / domain experts, not end users.
- **Not** an API gateway / RPA replacement for backend integration. When an API exists and is good, use the API.
- **Not** a screen-scraping / RPA-style record-and-replay. Manifests are deliberate, structured, and semantic — not captured.

---

## 2. The Manifest — the central abstraction

A **Manifest** is the platform's contract between the UI and the LLM. It is:

- **Stable** — selectors and descriptions don't depend on transient DOM state.
- **Semantic** — describes *what* an element is for, not just where it is.
- **Composable** — a Manifest can depend on other Manifests (embedded components).
- **Versioned** — every Manifest carries a version; clients pin or float.
- **Executable** — every Manifest declares how to capture the current page state (`gatherInformation`) and a catalogue of high-level Playwright functions ready for the LLM to call.

### What goes in a Manifest

1. **Interactional elements** — selectors the agent acts on.
2. **Informational elements** — selectors the agent / test reads.
3. **Relationship graph** — semantic edges between selectors (labels, controls, grouped-under, refers-to, …).
4. **User flow** — DFA of canonical UI states + element-anchored transitions.
5. **Dependencies** — other Manifests this one embeds.
6. **High-level functions** — pre-built composable Playwright snippets.
7. **`gatherInformation`** — live-state capture function.
8. **Metadata** — name, version, owner, tags, source-code hash, runtime requirements.

Each section has formal types — see §4.

### Why Manifest, not Signature?

Mazda used the name *Signature* because it described the *shape* of a component. Manifest is a better name for an enterprise product because:

- **It implies declared intent.** A manifest is a shipping document — it tells the consumer exactly what's inside.
- **It implies versioning and identity.** Manifests are first-class, releasable artifacts.
- **It composes with the platform name.** *Manifest Studio* / *Manifest Runner* / *Manifest Registry* form a coherent vocabulary.

---

## 3. System architecture

### 3.1 Two environments

| Environment | Audience | LLM cost profile | Where it runs |
|---|---|---|---|
| **Design** | Internal authors (devs, SDETs, domain experts) | Heavy at authoring time; idle otherwise | Centralized cloud or vendor's own infra |
| **Operational** | Customer's deployed install + external agents | Steady per-request | Customer's data center, cloud account, or air-gapped enclave |

A Manifest authored in Design is **released** to one or more Operational environments through a versioned ship-and-pin pipeline.

### 3.2 Five services

| Service | Purpose | LLM? | DB? | Public-facing? |
|---|---|---|---|---|
| **Manifest Studio** | Authoring tool (LLM-assisted draft + human review) | yes | reads | no (internal) |
| **Manifest Registry** | Versioned storage and read-API for manifests | no | yes | no (cluster-internal) |
| **Browser Manager** | Headless browser pool with stable handles | no | small | no |
| **Manifest Runner** | Executes (manifestId, prompt) against a browser | yes | logs/reports | no (cluster-internal) |
| **MCP Gateway** | Customer/agent-facing tool surface | no (delegates) | small | **yes** |

> *(Renaming the old "Manifest Storage" to* **Manifest Registry** *— better captures the role: catalogued, versioned, queryable artifacts. Same data, clearer mental model.)*

### 3.3 High-level flow

```
   Design Environment                                Operational Environment
   ──────────────────                                ────────────────────────

   Inputs ──► Studio ──► Registry                    External agent
              │              │                              │
       Browser Mgr           │ versioned                    ▼
              ▲              │ ship                    MCP Gateway
              └─ debug/preview                              │ delegate
                       (Studio → Runner)                    ▼
                                                         Runner ◄── Registry
                                                            │
                                                            ▼
                                                        Browser Mgr ──► customer browser
                                                            │
                                                            ▼
                                                        Run Reports
```

See `mazda-architecture.svg` for the polished version; it's a visual companion to this document and applies as-is when you mentally substitute *Signature → Manifest*.

### 3.4 Service-to-service contracts

| From | To | Protocol | Why |
|---|---|---|---|
| Studio | Registry | gRPC | Lots of small writes during authoring; bi-streaming for iterative diffs |
| Studio | Browser Mgr | gRPC | Long-lived session for live-observation |
| Studio | Runner | gRPC | Dev-only preview / debug runs |
| Runner | Registry | gRPC | Cold read of manifest + transitive deps |
| Runner | Browser Mgr | gRPC | Attach by `browserId`, execute scripts |
| Runner | LLM | HTTP/JSON | OpenAI-compatible chat completion |
| Runner | Run Reports DB | direct | Append-only logs + screenshot blob storage |
| MCP Gateway | Runner | gRPC | Tool invocation fan-out |
| Customer agent | MCP Gateway | MCP Streamable-HTTP | Standard MCP surface |

All gRPC, except agent-facing MCP. The single public ingress is the MCP Gateway; everything else is cluster-internal.

---

## 4. The Manifest DSL — formal specification

### 4.1 Document shape (TypeScript-style schema)

```typescript
interface Manifest {
  // ── Identity ─────────────────────────────────────────────────
  apiVersion: "manifest.studio/v1";
  kind: "Manifest";
  metadata: {
    name: string;              // PascalCase
    version: string;           // SemVer
    component: {
      type: ComponentType;     // "form" | "wizard" | "grid" | "modal" | ...
      sourceHash?: string;     // hash of the bundled source the manifest was built from
    };
    owner: { team: string; contacts: string[] };
    tags: string[];
    annotations?: Record<string, string>;
    humanDescription: string;  // markdown, free-form
  };

  // ── Element inventory ────────────────────────────────────────
  spec: {
    elements: Element[];
    relationships: RelationshipGraph;
    userFlow: UserFlowGraph;
    dependencies: DependencyRef[];
    functions: HighLevelFunction[];
    gatherInformation: GatherFnSpec;
  };
}

interface Element {
  id: string;                                          // unique within manifest
  selector: string;                                    // Playwright-ready CSS, may contain {i}
  category: "interactive" | "informational";
  type: ElementType;                                   // "button" | "input" | "status" | ...
  description: string;                                 // ≤ 140 chars
  logic?: string;                                      // structural / lifecycle notes
  templated?: boolean;                                 // true if selector has placeholders
}

interface RelationshipGraph {
  nodes: Array<{ id: string; selector: string; label: string; role: NodeRole; description?: string }>;
  edges: Array<{ id: string; from: string; to: string; relation: RelationType; label: string; description?: string }>;
}

type RelationType =
  | "labels" | "grouped-under" | "controls" | "submits" | "opens" | "closes"
  | "mutually-exclusive" | "depends-on" | "refers-to" | "sibling-of"
  | "list-item-of" | "value-of" | "related";

interface UserFlowGraph {
  nodes: Array<{ id: string; label: string; description?: string; isInitial?: boolean; isTerminal?: boolean }>;
  edges: Array<{ id: string; from: string; to: string; action: string; elementSelector: string; label: string; description?: string; value?: string }>;
}

interface DependencyRef {
  manifestName: string;        // referenced by name + version range
  versionRange: string;        // SemVer range
}

interface HighLevelFunction {
  name: string;                // e.g. "moveItemBetweenLists"
  description: string;         // what the function accomplishes
  parameters: ParameterSpec[];
  // The Playwright TypeScript body. Template strings can reference parameters.
  // Body MUST be deterministic and side-effect-bounded.
  body: string;
  returns?: TypeRef;
}

interface GatherFnSpec {
  body: string;                // TypeScript function body returning a JSON-serializable object
  schema: JsonSchema;          // schema of the returned object
  recurseDependencies: boolean; // whether the runner should recurse into deps' gather fns
}
```

### 4.2 Validation rules (enforced by Registry on write)

- **Schema check** — every required field present, types correct.
- **Selector uniqueness** — within a single Manifest, every `Element.selector` value must be unique (or templated with distinct templates).
- **Relationship node references** — every edge's `from` / `to` must reference an `Element.id` or a synthetic node defined in `nodes`.
- **Dependency resolvability** — referenced Manifests must exist in the Registry; version ranges must resolve.
- **No circular dependencies** — the Registry refuses writes that would create a cycle.
- **Function safety** — `body` strings are parsed (TypeScript AST), forbidden tokens rejected (`require`, `process`, `eval`, `fs.*`, `import('child_process')`, etc.). Allow-listed Playwright globals only.
- **Determinism budget** — `gatherInformation` body's static complexity (AST node count) capped to prevent abuse.

### 4.3 Versioning semantics

Manifests use **SemVer**:

| Change | Bump |
|---|---|
| Adding a new optional element / relation | minor |
| Adding a high-level function | minor |
| Changing a selector | **major** (breaks consumers' pinned scripts) |
| Removing or renaming an element / function | **major** |
| Changing description, tags, owner | patch |
| Adding a dependency | minor |
| Tightening a dependency version range | **major** |

Registry refuses publishes that violate these rules — automatic SemVer linter.

### 4.4 Storage format

YAML is the canonical on-disk format (humans review diffs in PRs). Wire format is protobuf for transport between services, with a deterministic YAML ↔ protobuf converter in the shared SDK.

---

## 5. Services in detail

### 5.1 Manifest Studio

**Purpose:** the design-time tool for authoring and reviewing Manifests.

**Responsibilities:**

- Ingest authoring inputs (zip, folder, loose files, DOM snapshots, Markdown, product specs, manual hints).
- Filter inputs by type and size; redact secrets before they reach the LLM.
- Call the LLM to produce a draft Manifest with five sub-passes:
  1. First-pass element extraction (interactive + informational).
  2. Deep informational pass.
  3. Relationship graph extraction.
  4. User-flow inference.
  5. High-level-function suggestion (from observed user actions in browser sessions, if connected).
- Round-trip with the author: chip-level edits, re-prompt with feedback, re-validate.
- Drive a Browser Manager browser to observe live state and refine selectors.
- Drive a Runner instance to preview a manifest end-to-end (dev-only "debug bridge").
- Publish a validated Manifest to the Registry with a SemVer bump.
- Pull existing Manifests from the Registry for iterative refinement.

**Tech stack:**

- **Backend:** .NET 9 (preferred) or Node + Fastify.
- **Frontend:** React 18 + TypeScript + Vite + TanStack Query + Monaco editor + `@xyflow/react` for graph viewers.
- **LLM client:** abstracted via `ILlmProvider` — OpenAI-compatible by default; pluggable for Azure OpenAI, Anthropic, vLLM, Ollama.
- **Auth:** none in the customer's installation (Studio is not deployed there). In the vendor / partner cloud installation: OIDC (Auth0, Okta, EntraID).

**Scaling characteristics:**

- Bursty: heavy load during authoring sessions, idle otherwise.
- Stateless backend; state lives in Registry and per-session ephemeral browser instances.
- Horizontal scaling: replicas behind a sticky-session load balancer (sticky because of the Monaco-editor / draft state).

**Public-facing?** No — internal authoring surface only.

### 5.2 Manifest Registry

**Purpose:** the source of truth for Manifests. Versioned, queryable, replicable.

**Responsibilities:**

- CRUD on Manifests with strict SemVer validation.
- Resolve dependency closures: given a Manifest, return it + every transitive dependency in topological order.
- Diff: compute and serve diffs between two versions of the same Manifest.
- Audit log: who published what, when, with what change-summary.
- Publication channels: `dev`, `staging`, `stable`, `<customer>` tags so a Manifest version can be released to specific environments.
- Read-only mirroring: customer-side installs receive a one-way read-replica of their published channel.

**Tech stack:**

- **Backend:** .NET 9 with EF Core + PostgreSQL 16.
- **Object store** for any large blobs (gather schemas, function bodies): S3-compatible (AWS S3, MinIO, Azure Blob).
- **Cache:** Redis for hot reads of resolved closures (TTL ~5 minutes, invalidated on publish).
- **No LLM, no browser** — pure software.

**Scaling characteristics:**

- Read-heavy at runtime (every agent request triggers a closure read).
- Write-light (a few publishes per day per team).
- Horizontal: stateless API replicas + read replicas of Postgres.
- Cross-region: Postgres logical replication for the customer-side mirror.

### 5.3 Browser Manager

**Purpose:** pool of headless browsers with stable handles, used by both Studio and Runner.

**Responsibilities:**

- Spin up / tear down headless browser instances.
- Maintain a pool with configurable warm-instance count.
- Allocate tabs on demand; return a stable `browserId` and `tabId`.
- Per-tab cookie / auth / session injection.
- Per-tab navigation primitives (goto, reload, close).
- CDP relay: Runner / Studio interact with the tab through the manager's CDP proxy, so they never need direct browser access.
- Health monitoring: kill instances that exceed memory / CPU thresholds, recycle every N hours.

**Tech stack:**

- **Backend:** Node + Playwright server-side, exposed via gRPC.
- **Browser:** Chromium bundled via Playwright. (Customer-side installs that already have Chrome may use CDP to attach instead of bundling — configurable per deployment.)
- **No DB.** Pool state is in-process; small persistent KV (Redis) for session metadata.
- **No LLM.**

**Scaling characteristics:**

- Memory-bound (each browser ≈ 200–400 MB).
- Horizontal scaling by adding replicas; each manages its own pool.
- A small directory service routes `browserId` → owning replica (Redis-backed).

### 5.4 Manifest Runner

**Purpose:** the runtime hot path. Executes one `(manifestId, prompt, browserId)` request end-to-end.

**Responsibilities — execution recipe:**

1. Attach to the browser by `browserId`.
2. Resolve the Manifest + transitive deps from the Registry.
3. Run each manifest's `gatherInformation` against the live tab. Merge into a single state object.
4. Compose the LLM prompt:
   - manifest catalogue (selectors, relationships, flows, high-level fn list),
   - current state (from step 3),
   - the user's instruction,
   - any optional setup / dependency context.
5. Call the script-generation LLM; receive a Playwright TypeScript snippet that calls the manifest's high-level functions and selectors.
6. Validate the generated script (AST allow-list, no forbidden globals, must compile).
7. Execute against the browser, streaming stdout/stderr to a live channel.
8. On completion: persist run report (script, stdout, stderr, screenshots, return value) to the run-reports store.
9. Return the structured result to the caller.

**Tech stack:**

- **Backend:** .NET 9 for orchestration; Node sidecar for actual Playwright execution (to keep Chromium / Playwright in its native environment).
- **Run reports DB:** PostgreSQL (small index) + S3 (screenshots, full logs).
- **Streaming:** SSE for human consumers (Studio debug bridge); gRPC server-streaming for service consumers (MCP Gateway).
- **LLM client:** shared `ILlmProvider` SDK.

**Scaling characteristics:**

- CPU + LLM-bound.
- Horizontal: stateless workers, work distributed via a job queue (NATS JetStream or Redis Streams).
- Backpressure: queue length surfaced as a metric; auto-scale on it.

### 5.5 MCP Gateway

**Purpose:** the agent-facing tool surface. The only public ingress.

**Responsibilities:**

- Expose every published Manifest as one MCP tool with schema:
  ```json
  { "browserId": "string", "prompt": "string", "options?": { ... } }
  ```
- Authenticate callers via OIDC bearer tokens (customer's IdP).
- Authorize: per-token, which Manifests are exposed.
- Translate MCP tool calls → Runner invocations.
- Capture per-call audit records.
- Provide an MCP `resources/list` endpoint that surfaces the manifest catalogue for discovery.

**Tech stack:**

- **Backend:** .NET 9 implementing the MCP Streamable-HTTP transport.
- **Auth:** OIDC bearer + per-customer mTLS for B2B clients.
- **Rate limiting:** per-token quota via Redis.

**Scaling characteristics:**

- Stateless. Pure proxy + auth.
- Horizontal: replicas behind an L7 load balancer with health checks.

---

## 6. Cross-cutting concerns

### 6.1 Multi-tenancy

- **Tenant** = customer organization.
- Every Registry / Runner / Gateway resource carries `tenantId`. RLS-style filtering at every query layer.
- Cross-tenant data access is forbidden by default; one explicit "platform admin" role can audit across tenants.
- Studio is single-tenant per install. In a SaaS topology, each tenant gets its own Studio + Registry namespace; in customer-owned topology, the entire stack is single-tenant by definition.

### 6.2 Authentication & Authorization

- **Inter-service:** mTLS (cert-manager + SPIRE/SPIFFE recommended).
- **End user:** OIDC against the customer's IdP. RBAC roles:
  - `manifest.author` — create/edit Manifests
  - `manifest.publisher` — release versions to channels
  - `runner.invoke` — call tools
  - `runner.viewReports` — read run reports
  - `platform.admin` — everything + cross-tenant
- **Agents:** OAuth2 client-credentials grant against the customer's IdP, scoped to specific Manifests.

### 6.3 Observability

- **Logs:** structured JSON, shipped to Loki / Elasticsearch.
- **Metrics:** OpenTelemetry → Prometheus. Standard SLOs published per service:
  - Runner request success rate ≥ 99.5% (excluding 4xx).
  - Runner p95 latency < 8 s.
  - Registry read p99 < 50 ms.
- **Traces:** OpenTelemetry distributed traces from MCP Gateway → Runner → Registry / Browser Manager / LLM.
- **Run reports:** every Runner execution archives the full script + stdout + screenshots, with a 90-day retention default (configurable per tenant).

### 6.4 Reliability & idempotency

- **Job IDs:** every Runner invocation gets a UUIDv7. Re-submitting the same job ID returns the original result rather than re-executing.
- **Retries:** Runner retries LLM and Browser Manager calls with exponential backoff + jitter.
- **Circuit breakers:** if LLM provider errors > threshold, Runner serves a structured "service degraded" error rather than queueing forever.
- **Graceful degradation:** if the Relationship analyzer is unavailable, Studio publishes a Manifest with an empty relationship graph and a warning — the Runner still works, just with weaker grounding.

### 6.5 Audit & compliance

- Every state-mutating action is logged: `who, what, when, before, after, request-id`.
- Audit log is **append-only**, shipped to an immutable WORM bucket.
- PII handling: by default, screenshots stored in run reports are **redacted** by a small CV model that masks form-input contents. Per-tenant opt-out.
- **Air-gap mode:** every component supports a "no internet egress" deployment, including the LLM (point at on-prem vLLM / Ollama).
- **Data residency:** Registry + run reports per-tenant DBs can be pinned to specific regions.

### 6.6 Versioning & release pipeline

```
  Author publishes      dev channel ──► staging channel ──► stable channel
  Manifest v1.2.0   ──►   (auto)         (gated by smoke)    (gated by approval)
                            │                  │                    │
                            ▼                  ▼                    ▼
                         dev cluster    staging cluster      customer clusters
                         (auto-pulls)   (auto-pulls)         (pulls on next bump)
```

Customers can pin to a specific version or float on a channel. A "stable" release to a customer is a Helm-values bump in their cluster.

---

## 7. Technology choices

### 7.1 Backend

**Primary:** .NET 9 with C# 13.
- Clean-architecture layering (Domain → Application → Infrastructure → Api), MediatR for CQRS, FluentValidation for commands.
- Carries forward the discipline that worked in Mazda.
- Excellent gRPC story (`Grpc.AspNetCore`), first-class OpenTelemetry, mature EF Core, fast iteration.

**Sidecar:** Node 20 for the Playwright execution sandbox inside Runner and the Browser Manager. The official Playwright is a Node library; running it in a Node process avoids the maintenance burden of any third-party .NET binding.

**Why not pure Node everywhere?** Type safety, refactorability, and DI ergonomics for a service mesh of this size are stronger in .NET. The Node sidecar pattern keeps Playwright where it belongs.

**Why not Go / Rust?** Both are credible. .NET wins for this project because of:
- the team's existing investment in C# (carries the Mazda learning),
- excellent LLM SDK ecosystem (`Microsoft.Extensions.AI`),
- enterprise CI/CD tooling parity.

### 7.2 Frontend

- React 18 + TypeScript 5 + Vite.
- TanStack Query for data, Zustand for cross-page state.
- Monaco editor (`@monaco-editor/react`) for YAML/JSON/TS editing.
- `@xyflow/react` + `@dagrejs/dagre` for graph viewers.
- Tailwind CSS + Radix primitives for UI; keep the design vocabulary disciplined.
- Storybook for component dev and visual regression.

### 7.3 Databases

| Use | Choice | Why |
|---|---|---|
| Manifest catalogue | PostgreSQL 16 | ACID, JSONB for the manifest body, mature replication |
| Run reports (metadata) | PostgreSQL 16 | join-friendly with manifest catalogue |
| Run reports (logs + screenshots) | S3-compatible | cheap, retention policies, easy to keep on-prem with MinIO |
| Cache | Redis 7 | resolved-closure cache, browser-id routing, rate limits |
| Job queue | NATS JetStream **or** Redis Streams | NATS for high-throughput multi-tenant; Redis Streams for single-tenant simplicity |

### 7.4 LLM provider abstraction

Define an `ILlmProvider` SDK with:
- `ChatComplete(messages, options, ct) → CompletionResult`
- `Embed(text, options, ct) → EmbeddingResult`
- supports OpenAI, Azure OpenAI, Anthropic, vLLM, Ollama via configuration.

Per-tenant configuration: which provider, which model, which endpoint, which credentials. Cost reports per tenant.

### 7.5 Browser automation

Playwright 1.48+. Bundled Chromium by default; CDP attach mode for customers who provide their own browser image.

### 7.6 Containerization & orchestration

- Multi-stage Docker images with BuildKit cache mounts (Mazda's pattern).
- Distroless runtime images where feasible.
- Kubernetes manifests + Helm chart per service.
- Optional service mesh (Linkerd or Istio) for mTLS automation; cert-manager + SPIRE alternative for mesh-less installs.

---

## 8. Project & repository structure

A **monorepo** with clear service boundaries. Single source of truth, atomic cross-service refactors, easier shared SDKs.

```
manifest-studio/
├── README.md
├── docs/
│   ├── architecture.md           # high-level + diagrams
│   ├── manifest-dsl.md           # the canonical DSL spec
│   ├── llm-prompts/              # all system prompts under version control
│   └── runbooks/                 # ops runbooks per service
├── proto/                        # Protobuf definitions (the cross-service ABI)
│   ├── registry/v1/
│   ├── runner/v1/
│   ├── browser/v1/
│   ├── studio/v1/
│   └── common/v1/
├── services/
│   ├── studio/                   # .NET + React app
│   │   ├── backend/              # ASP.NET Core minimal API + gRPC client
│   │   ├── frontend/             # Vite + React
│   │   └── Dockerfile
│   ├── registry/
│   │   ├── src/
│   │   ├── migrations/
│   │   └── Dockerfile
│   ├── browser-manager/
│   │   ├── src/                  # Node + Playwright + gRPC server
│   │   └── Dockerfile
│   ├── runner/
│   │   ├── src/                  # .NET orchestrator
│   │   ├── sandbox/              # Node sidecar that runs Playwright
│   │   └── Dockerfile
│   └── mcp-gateway/
│       ├── src/
│       └── Dockerfile
├── packages/                     # cross-service shared SDKs
│   ├── manifest-sdk-dotnet/      # parse/validate/produce manifests in .NET
│   ├── manifest-sdk-typescript/  # ditto in TS — used by Studio frontend
│   ├── llm-provider-dotnet/      # ILlmProvider implementations
│   └── otel-defaults/            # shared OpenTelemetry config
├── tools/
│   ├── manifest-cli/             # CLI: lint, diff, publish, install
│   └── codegen/                  # protobuf → .NET / TS / Node generators
├── infra/
│   ├── helm/                     # one chart per service + an umbrella chart
│   ├── terraform/                # vendor-cloud infra
│   └── compose/                  # docker-compose for local dev
├── examples/
│   ├── sample-manifests/         # canonical example components
│   └── customer-recipes/         # how to wire into common products
├── tests/
│   ├── e2e/
│   ├── load/
│   └── contracts/                # protobuf compatibility checks
└── .github/
    └── workflows/                # CI: build, test, lint, publish, sign
```

### Tooling

- **Nx** or **Bazel** for the monorepo build graph (Nx if you want a lower learning curve, Bazel for strict reproducibility).
- **buf** for protobuf linting + breaking-change detection.
- **EditorConfig + Prettier + dotnet-format** enforced in CI.
- **Renovate** for dep PRs.
- **Sigstore / cosign** for image signing.

---

## 9. Data model

### 9.1 Registry tables

```sql
-- tenants ------------------------------------------------------------
CREATE TABLE tenants (
  id              uuid PRIMARY KEY,
  name            text NOT NULL,
  created_at      timestamptz NOT NULL DEFAULT now()
);

-- manifests: one row per manifest + version --------------------------
CREATE TABLE manifests (
  id              uuid PRIMARY KEY,
  tenant_id       uuid NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  name            text NOT NULL,                       -- PascalCase
  version         text NOT NULL,                       -- SemVer
  channel         text NOT NULL DEFAULT 'dev',         -- dev|staging|stable|<custom>
  body            jsonb NOT NULL,                      -- the canonical manifest JSON
  source_hash     text,                                -- hash of the component source
  human_description text NOT NULL DEFAULT '',
  owner_team      text NOT NULL,
  published_by    uuid NOT NULL,                       -- user id
  published_at    timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, name, version)
);
CREATE INDEX ON manifests(tenant_id, name, channel);
CREATE INDEX ON manifests(tenant_id, name, version);

-- manifest dependencies (dep graph, version ranges) ------------------
CREATE TABLE manifest_dependencies (
  manifest_id     uuid NOT NULL REFERENCES manifests(id) ON DELETE CASCADE,
  depends_on_name text NOT NULL,
  version_range   text NOT NULL,                       -- SemVer range
  PRIMARY KEY (manifest_id, depends_on_name)
);

-- audit log ---------------------------------------------------------
CREATE TABLE audit_log (
  id              bigserial PRIMARY KEY,
  tenant_id       uuid NOT NULL,
  actor_id        uuid,
  action          text NOT NULL,                       -- "manifest.publish", ...
  target_kind     text NOT NULL,
  target_id       text NOT NULL,
  before          jsonb,
  after           jsonb,
  at              timestamptz NOT NULL DEFAULT now()
);
```

### 9.2 Run-reports tables

```sql
-- run_invocations: one per agent request ------------------------------
CREATE TABLE run_invocations (
  id              uuid PRIMARY KEY,
  tenant_id       uuid NOT NULL,
  manifest_id     uuid NOT NULL,
  manifest_version text NOT NULL,
  browser_id      text NOT NULL,
  prompt          text NOT NULL,
  status          text NOT NULL,                       -- queued|running|succeeded|failed
  started_at      timestamptz,
  finished_at     timestamptz,
  exit_code       int,
  llm_tokens      int,
  script_blob_url text,                                -- S3 reference
  log_blob_url    text,                                -- S3 reference
  screenshots_prefix text                              -- S3 prefix
);
CREATE INDEX ON run_invocations(tenant_id, started_at DESC);
CREATE INDEX ON run_invocations(manifest_id);
```

### 9.3 MCP-Gateway tables

```sql
-- mcp_tool_exposures: which manifests are exposed as tools ------------
CREATE TABLE mcp_tool_exposures (
  id              uuid PRIMARY KEY,
  tenant_id       uuid NOT NULL,
  manifest_name   text NOT NULL,
  version_pin     text,                                -- specific version OR null for channel-floating
  channel_pin     text,                                -- "stable" / etc; null if version_pin is set
  tool_name       text NOT NULL,                       -- the MCP tool name
  display_name    text NOT NULL,
  description     text NOT NULL,
  enabled         boolean NOT NULL DEFAULT true,
  UNIQUE (tenant_id, tool_name)
);
```

---

## 10. Public API surface

The only thing customers interact with directly is the **MCP Gateway**. Internal APIs are gRPC; here's the high-level surface.

### 10.1 MCP Gateway (Streamable-HTTP)

Standard MCP. Tools dynamically generated from the catalogue. Each tool's input schema:

```json
{
  "type": "object",
  "required": ["browserId", "prompt"],
  "properties": {
    "browserId":  { "type": "string", "description": "Stable handle from Browser Manager" },
    "prompt":     { "type": "string", "description": "What the agent wants accomplished" },
    "options":    { "type": "object", "additionalProperties": false, "properties": {
      "manifestVersion": { "type": "string", "description": "Override the pinned version" },
      "dryRun":          { "type": "boolean" }
    }}
  }
}
```

Output:

```json
{
  "ok": true,
  "runId": "uuid",
  "manifestVersion": "1.4.2",
  "state": { "...": "JSON from gatherInformation, post-execution" },
  "screenshots": [{ "label": "after-submit", "url": "https://..." }],
  "logs": "..."
}
```

### 10.2 Browser Manager (gRPC)

```protobuf
service BrowserManager {
  rpc Reserve(ReserveRequest) returns (ReserveResponse);
  rpc Release(ReleaseRequest) returns (google.protobuf.Empty);
  rpc Navigate(NavigateRequest) returns (NavigateResponse);
  rpc InjectCookies(InjectCookiesRequest) returns (google.protobuf.Empty);
  rpc Status(StatusRequest) returns (StatusResponse);
}
```

### 10.3 Runner (gRPC)

```protobuf
service Runner {
  rpc Invoke(InvokeRequest) returns (stream InvokeEvent);
  rpc GetRun(GetRunRequest) returns (RunReport);
  rpc ListRuns(ListRunsRequest) returns (ListRunsResponse);
}
```

### 10.4 Registry (gRPC)

```protobuf
service Registry {
  rpc Publish(PublishRequest) returns (PublishResponse);
  rpc Get(GetRequest) returns (Manifest);
  rpc Resolve(ResolveRequest) returns (ResolveResponse);   // returns transitive closure
  rpc List(ListRequest) returns (ListResponse);
  rpc Diff(DiffRequest) returns (DiffResponse);
  rpc Promote(PromoteRequest) returns (google.protobuf.Empty); // dev→staging→stable
}
```

---

## 11. Deployment topologies

### 11.1 Local dev (`docker-compose`)

Single machine. All services on one network. SQLite or local Postgres. Minio for S3. Local Ollama or remote OpenAI for LLM.

### 11.2 Vendor staging cluster

Single Kubernetes cluster. Each service has its own deployment + service. Postgres on RDS/CloudSQL. S3 on the cloud provider. LLM via configured provider.

### 11.3 Customer production install

The **customer-side stack** is a subset:

- Registry (read-replica of vendor's stable channel)
- Browser Manager
- Runner
- MCP Gateway

Studio is **not** deployed customer-side — Manifests are authored centrally and shipped out via the Registry mirror.

Bundled as an umbrella Helm chart with sensible defaults. Single command install:

```bash
helm install manifest-studio ms/manifest-studio \
  --set tenantId=acme \
  --set llm.provider=azureOpenAI \
  --set llm.endpoint=... \
  --set registry.mirrorFrom=https://registry.vendor.com
```

### 11.4 Air-gapped install

Same as 11.3 but:
- All container images pre-loaded from a vendor-supplied tarball.
- LLM is on-prem (vLLM with a locally-loaded model).
- Manifest updates arrive via signed tarball, not over the network. A `manifest-cli import` command unloads the tarball into the local Registry.

---

## 12. Local development

### 12.1 Prerequisites

- .NET 9 SDK
- Node 20 LTS
- Docker Desktop or Podman
- `buf`, `nx`, `pnpm`
- `manifest-cli` (built from the monorepo)

### 12.2 Bootstrap

```bash
git clone git@github.com:<org>/manifest-studio.git
cd manifest-studio
make bootstrap        # installs deps, generates protobuf, primes DBs
docker compose -f infra/compose/dev.yml up -d
make dev              # nx run-many parallel watch
open http://localhost:5173  # Studio frontend
```

### 12.3 Mocked LLM

A `MockLlmProvider` lives in `packages/llm-provider-dotnet`. Activated by `MS_LLM_PROVIDER=mock`. Returns canned, deterministic responses based on input hash — for fast tests and offline development.

---

## 13. Roadmap — phased rollout

A 6-month plan, broken into vertical slices. Each phase delivers a usable system.

### Phase 0 — Foundations (weeks 1–3)

- Monorepo skeleton, CI, build graph.
- Protobuf contracts for all five services.
- Manifest DSL spec finalized; YAML schema + JSON schema published.
- `manifest-sdk-dotnet` + `manifest-sdk-typescript` packages with parse / validate / produce.
- Local dev environment runs.

**Deliverable:** can write a Manifest YAML file by hand and validate it via the CLI.

### Phase 1 — Studio MVP (weeks 4–9)

- Studio backend + frontend.
- Registry minimal: write + read + list + audit log.
- First-pass element extraction LLM (port the Mazda prompt).
- Browser Manager: reserve / navigate / release.
- Live observation: Studio can drive Browser Manager to inspect a target page.

**Deliverable:** an author can upload component source, get a draft Manifest, refine it, and publish v1.0.0 to dev channel.

### Phase 2 — Runner MVP (weeks 10–14)

- Runner orchestrator + Node sandbox.
- Generation of Playwright scripts that call manifest-declared functions.
- `gatherInformation` execution + recursion into dependencies.
- Run-reports persistence: stdout + screenshots in S3-compatible store.
- Studio's "preview / debug" bridge: invoke Runner from inside Studio.

**Deliverable:** an authored Manifest can be invoked end-to-end with a prompt; the result is observable in Studio.

### Phase 3 — MCP Gateway + multi-tenant (weeks 15–18)

- MCP Gateway exposing manifests as tools.
- OIDC integration; per-tenant RBAC.
- mTLS between services.
- Per-tenant LLM-provider config.
- Tool-exposure management UI in Studio.

**Deliverable:** an external LLM agent can authenticate and invoke a tool that drives a real browser via the platform.

### Phase 4 — Enterprise hardening (weeks 19–24)

- Observability: OTel everywhere, runbooks, on-call dashboards.
- Helm umbrella chart + customer-install docs.
- Air-gapped install path (signed-tarball updates).
- Channel-based release pipeline (dev → staging → stable).
- Audit-log export + WORM bucket integration.
- Load tests + SLO definitions.

**Deliverable:** a customer can install the operational stack in their own cluster, point their IdP at it, and receive signed Manifest updates from the vendor.

### Phase 5+ — Optional expansions

- Public Manifest marketplace.
- Visual flow editor (currently the User-flow page is read-only).
- Authoring agents that propose Manifest edits autonomously from telemetry.
- Multi-modal capture (incorporate UI screenshots into LLM context for better grounding).
- Selector self-healing (when a Manifest selector breaks, suggest a fix from runtime telemetry).

---

## 14. Migration story from Mazda

Mazda is the prototype. Manifest Studio is the productized successor. Migration plan:

| Mazda artifact | Manifest Studio destination | Notes |
|---|---|---|
| `Signature` aggregate | `Manifest` (rename + schema enrichment) | Add `metadata.version`, `metadata.owner`, `spec.functions` |
| `SignatureElement` | `Element` | One-to-one; field renames only |
| `SignatureDependency` | `DependencyRef` | Add `versionRange` instead of pointing to a specific ID |
| `UserFlowsJson` | `spec.userFlow` | Same shape |
| `RelationshipsJson` | `spec.relationships` | Same shape |
| LLM analyzers (Component, Informational, Relationship, Flow) | Studio's authoring pipeline | Direct port, behind a unified `IManifestAuthoringPipeline` |
| `LlmScriptGenerator` | Runner's script-gen step | Direct port with manifest-driven function calls |
| Runtime helpers (`__mzClick`, `__mzSelectOption`, etc.) | Become **per-manifest high-level functions** | Move from auto-injection to declared function bodies |
| `LocalProcessScriptRunner` | Runner's Node sandbox | Reuse the Playwright execution discipline |
| MCP endpoint at `/api/mcp` | MCP Gateway service | Extract from monolith |
| `mazda-app` `signatures` table | Registry `manifests` table | One-shot migration script |

A `mazda-import` CLI command takes a Mazda Postgres dump and emits a tree of YAML Manifests ready to publish to the Registry.

**Backward compat:** during the migration, MCP tools generated from Mazda Signatures and Manifest Studio Manifests should be addressable from the same agent. The MCP Gateway treats both as first-class catalogue entries during the cutover window.

---

## 15. Open questions to resolve before kick-off

These are real architectural decisions, not implementation details. Answer each before Phase 0.

1. **Topology model:** SaaS (vendor hosts everything) vs customer-hosted (only the Operational stack runs at the customer) vs both? Pricing model follows.
2. **LLM cost model:** does the customer bring their own LLM key, or does the vendor proxy and bill?
3. **Manifest authorship boundary:** can customers author their own Manifests (Studio is shipped to them) or only the vendor authors (Studio stays internal)?
4. **Browser provisioning:** does Browser Manager bundle Chromium, or does the customer supply browser images (e.g., to match their fleet)?
5. **Data residency:** which customers require their run-reports to stay in-country / in their own S3 bucket?
6. **Compliance certifications:** SOC 2, ISO 27001, HIPAA — which are on the 12-month roadmap?
7. **Manifest signing:** should published Manifests be cryptographically signed by the publishing team so customers can verify origin? (Strongly recommended — design it in from day one.)
8. **CDP attach vs bundled browser:** if customers want to drive their own already-running Chrome (e.g., to inherit auth), the CDP attach path needs first-class support.
9. **Recovery story:** when a Manifest selector breaks after a UI change, how fast can the platform self-heal? (Could be a paid feature — "Selector Insurance".)
10. **Open core vs proprietary:** is the Manifest DSL open and the rest proprietary? Or is the whole platform OSS with paid hosting?

---

## 16. Glossary

| Term | Meaning |
|---|---|
| **Manifest** | The platform's central artifact — a structured, semantic description of one UI component, plus its functions, flows, and relationships. |
| **Manifest Studio** | The product. Also the name of one service (the authoring tool). |
| **Manifest Registry** | The versioned catalogue service. |
| **Manifest Runner** | The runtime execution service. |
| **Browser Manager** | Headless browser pool service. |
| **MCP Gateway** | Agent-facing tool surface. The platform's only public ingress. |
| **Element** | One selector + classification + description in a Manifest. |
| **Relationship graph** | Semantic edges between selectors (labels, controls, grouped-under, …). |
| **User flow** | DFA of canonical UI states + element-anchored transitions. |
| **High-level function** | A pre-built Playwright snippet declared on a Manifest, callable by the LLM. |
| **`gatherInformation`** | A per-Manifest function that captures live state of the component. |
| **`browserId`** | Opaque handle returned by Browser Manager, used to attach to a specific tab. |
| **Channel** | A release lane in the Registry (`dev`, `staging`, `stable`, `<custom>`). |
| **Tenant** | A customer organization. The root entity for multi-tenancy. |
| **Tool exposure** | A Manifest published as an MCP tool, with optional version / channel pin. |
| **CDP** | Chrome DevTools Protocol. |
| **MCP** | Model Context Protocol — the standard tool-call protocol agents use. |
| **Run report** | Persisted record of one Runner invocation: script, logs, screenshots, return value. |

---

*This document is the authoritative specification for a brand-new Manifest Studio codebase. It draws on lessons learned from the Mazda prototype but is not bound by Mazda's choices. Where the document says "preferred" or "recommended", that's a strong default — change only with rationale recorded in `docs/decisions/`.*
