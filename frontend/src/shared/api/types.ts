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
  setupPrompt: string;
  setupScript: string | null;
  analyzeStateScript: string | null;
  analyzeStateRawOutput: string | null;
  analyzeStateJson: string | null;
  // Optional CSS selector that scopes the analyze-state extraction to a
  // specific sub-tree of the page (modal, popover, panel, …). Null/empty
  // means whole-page extraction.
  analyzeStateScope: string | null;
  testPrompt: string;
  testScript: string | null;
  finalScript: string | null;
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
