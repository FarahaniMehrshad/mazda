export interface AuthResponse {
  userId: string;
  email: string;
  displayName: string;
  token: string;
}

export interface SignatureElementDto {
  selector: string;
  elementType: string;
  description: string;
}

export interface SignatureDto {
  id: string;
  name: string;
  version: string;
  source: string;
  type: string;
  humanDescription: string;
  nodeCount: number;
  updatedAt: string;
  elements: SignatureElementDto[];
}

export type GenerationStatus = "Pending" | "Generating" | "Ready" | "Failed";

export interface GenerationDto {
  id: string;
  signatureId: string;
  targetUrl: string;
  prompt: string;
  status: GenerationStatus;
  script: string | null;
  failureReason: string | null;
  tokensUsed: number;
  createdAt: string;
}
