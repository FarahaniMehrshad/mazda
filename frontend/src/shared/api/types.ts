export interface AuthResponse {
  userId: string;
  email: string;
  displayName: string;
  token: string;
}

export interface SignatureElementDto {
  selector: string;
  category: "Interactive" | "Informational" | string;
  elementType: string;
  description: string;
  /** Optional structural / lifecycle / relational notes. May be empty. */
  logic?: string;
}

export interface SignatureDto {
  id: string;
  name: string;
  version: string;
  source: string;
  type: string;
  humanDescription: string;
  userFlowsJson: string;
  /**
   * Element-relationship graph JSON produced by the relationship-analysis
   * step. Captures semantic edges between selectors that the DOM tree itself
   * doesn't reveal (a parent container's heading labels its children, a
   * button controls a field, a row's name identifies its delete action,
   * etc.). Empty string until the step runs.
   */
  relationshipsJson: string;
  nodeCount: number;
  updatedAt: string;
  elements: SignatureElementDto[];
  /**
   * IDs of OTHER signatures this one depends on. Generation pipelines union
   * every transitive dep's elements into the LLM context, so a test author
   * can reference behaviour of an embedded component without re-declaring
   * its selectors. Empty when the signature stands alone.
   */
  dependencyIds: string[];
}

export interface SignatureDependencyDto {
  id: string;
  name: string;
  type: string;
  elementCount: number;
}

export interface UserFlowsGraph {
  nodes: Array<{ id: string; label: string; description?: string }>;
  edges: Array<{
    id: string;
    from: string;
    to: string;
    action: string;
    elementSelector: string;
    label: string;
    description?: string;
    value?: string;
  }>;
}

/**
 * Element-relationship graph produced by signature step 4.
 *
 * - `nodes` reuse the selectors from the signature where possible. `role`
 *   distinguishes element nodes (interactive / informational) from purely
 *   structural pivot nodes (container / section) introduced by the
 *   analyzer when a relation pivots on a group / panel that isn't itself
 *   a signature element.
 *
 * - `edges` carry a `relation` discriminator (the structural relationship
 *   type — "labels", "controls", "grouped-under", "refers-to", …) plus
 *   a short human-readable `label` for graph display.
 *
 * The viewer renders this graph via @xyflow/react + dagre, mirroring the
 * UserFlowsPage layout.
 */
export type RelationshipRelation =
  | "labels"
  | "grouped-under"
  | "controls"
  | "submits"
  | "opens"
  | "closes"
  | "mutually-exclusive"
  | "depends-on"
  | "refers-to"
  | "sibling-of"
  | "list-item-of"
  | "value-of"
  | "related"
  | string;

export interface RelationshipGraph {
  nodes: Array<{
    id: string;
    selector: string;
    label: string;
    role: "interactive" | "informational" | "container" | "section" | string;
    description?: string;
  }>;
  edges: Array<{
    id: string;
    from: string;
    to: string;
    relation: RelationshipRelation;
    label: string;
    description?: string;
  }>;
}

export type GenerationStatus = "Pending" | "Generating" | "Ready" | "Failed";
export type ExecutionStatus = "Idle" | "Running" | "Passed" | "FailedTest" | "Errored" | "Cancelled";

export interface McpToolDto {
  id: string;
  signatureId: string;
  signatureName: string;
  name: string;
  displayName: string;
  description: string;
  defaultSetupPrompt: string;
  defaultTestPrompt: string;
  enabled: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface GenerationDto {
  id: string;
  signatureId: string;
  /** The natural-language prompt the planner LLM saw. */
  prompt: string;
  /**
   * JSON blob produced by the state-gather phase — shape
   * `{ signature, capturedAt, state: { <fnName>: <return> } }`. Null until
   * the gather step completes; set once, never mutated.
   */
  gatheredStateJson: string | null;
  /**
   * JSON blob produced by the planner LLM — shape
   * `{ reasoning, steps: [{ id, call, args }, ...] }`. Null until the plan
   * step completes.
   */
  planJson: string | null;
  /**
   * JSON blob produced by the interpreter — shape documented in
   * `ExecutionEnvelope`. Null while executing / never run.
   */
  executionEnvelopeJson: string | null;
  status: GenerationStatus;
  failureReason: string | null;
  tokensUsed: number;
  executionStatus: ExecutionStatus;
  exitCode: number | null;
  executionLog: string | null;
  executionStartedAt: string | null;
  executionEndedAt: string | null;
  createdAt: string;
  updatedAt: string;
}

// ── Signature function catalogue ─────────────────────────────────────

/**
 * One high-level function on a signature. The runtime pipeline splits into
 * two kinds:
 *   - "state"   — read-only observer, no args, returns structured JSON.
 *                 Runs automatically during gatherInformation.
 *   - "action"  — parameterised side-effect. Planner LLM composes plans
 *                 out of these.
 */
export interface SignatureFunctionDto {
  id: string;
  name: string;
  kind: "state" | "action";
  description: string;
  /** Full JSON Schema doc for the function's parameter object. */
  parametersJsonSchema: string;
  /** Full JSON Schema doc for the return value. */
  returnsJsonSchema: string;
  /** Optional 1-sentence English precondition. Empty when unconditional. */
  preconditions: string;
  /**
   * TypeScript Playwright snippet — the body of the function, executed at
   * runtime inside a per-run harness that also exposes the `__mz*` helpers.
   */
  body: string;
  order: number;
  updatedAt: string;
}

/**
 * Result of running one catalogue function against the live CDP browser from
 * the Functions page.
 *
 * `ok: false` is a normal, successful response — a body that threw is exactly
 * what the author is testing for. The request itself only fails when the
 * browser is unreachable or the stitched spec won't run.
 */
export interface FunctionTestResultDto {
  /** Qualified name actually invoked — namespaced when it came from a dependency. */
  call: string;
  ok: boolean;
  /** JSON text of the return value. Null for action functions, which return nothing. */
  returnedJson: string | null;
  errorKind: string | null;
  errorMessage: string | null;
  errorStack: string | null;
  durationMs: number;
  /** Where the browser actually was — a wrong-page failure explains itself. */
  pageUrl: string | null;
  pageTitle: string | null;
  logTail: string;
}

/** One driveable Chrome tab, as reported by CDP's /json/list. */
export interface CdpTabDto {
  /** Stable for the life of the tab; survives reloads, dies when it closes. */
  targetId: string;
  title: string;
  url: string;
  attached: boolean;
}

export interface CdpStatusDto {
  available: boolean;
  browserVersion: string | null;
  /** Why the browser could not be reached. Null when available. */
  reason: string | null;
  port: number;
  /** Empty when nothing is attached — harnesses then take the first page found. */
  attachedTargetId: string;
  /** Captured at attach time, so the attachment stays nameable while Chrome is closed. */
  attachedTitle: string;
  attachedUrl: string;
  /** Attached to a tab that is no longer open — runs would fail until re-picked. */
  attachedTabMissing: boolean;
  tabs: CdpTabDto[];
}

// ── Runtime pipeline blob shapes ────────────────────────────────────
// These are the typed views of the JSON blobs on GenerationDto. The wire
// still carries opaque strings; consumers parse into these when rendering.

export interface GatheredStateBlob {
  signature: string;        // e.g. "todo-list@1.2.0"
  capturedAt: string;
  state: Record<string, unknown>;
}

export interface PlanStepBlob {
  id: string;
  call: string;
  args: Record<string, unknown>;
}

export interface PlanBlob {
  reasoning: string | null;
  steps: PlanStepBlob[];
}

export type StepStatus = "ok" | "failed" | "skipped";
export type RunOutcome = "ok" | "failed";
export type StepErrorKind =
  | "SchemaMismatch"
  | "FunctionNotFound"
  | "PreconditionFailed"
  | "RuntimeError"
  | "HarnessError";

export interface StepResultBlob {
  id: string;
  call: string;
  status: StepStatus;
  durationMs: number;
  error: { kind: StepErrorKind; message: string } | null;
}

export interface ExecutionEnvelopeBlob {
  outcome: RunOutcome;
  stepsTotal: number;
  stepsExecuted: number;
  steps: StepResultBlob[];
  capturedState: GatheredStateBlob;
  plan: PlanBlob;
}
