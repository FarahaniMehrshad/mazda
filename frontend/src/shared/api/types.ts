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
  nodeCount: number;
  updatedAt: string;
  elements: SignatureElementDto[];
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

export type GenerationStatus = "Pending" | "Generating" | "Ready" | "Failed";
export type ExecutionStatus = "Idle" | "Running" | "Passed" | "FailedTest" | "Errored" | "Cancelled";

export interface GenerationDto {
  id: string;
  signatureId: string;
  setupPrompt: string;
  setupScript: string | null;
  analyzeStateScript: string | null;
  analyzeStateRawOutput: string | null;
  analyzeStateJson: string | null;
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
