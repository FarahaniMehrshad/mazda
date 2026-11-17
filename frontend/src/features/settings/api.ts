import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@/shared/api/client";

export type LlmProvider = "openai" | "claude-code";

/**
 * The LLM connection. Fields hold what is saved ("" = not saved, .env applies);
 * `defaults` are the .env values (shown as placeholders). The API key and the
 * bridge token never reach the browser: only whether one is set.
 */
export interface LlmSettingsDto {
  provider: "" | LlmProvider;
  openAiBaseUrl: string;
  openAiApiKeySaved: boolean;
  openAiAnalyzerModel: string;
  openAiGeneratorModel: string;
  claudeBridgeUrl: string;
  claudeAnalyzerModel: string;
  claudeGeneratorModel: string;
  timeoutSeconds: number;
  defaults: {
    provider: LlmProvider;
    openAiBaseUrl: string;
    openAiApiKeySet: boolean;
    openAiAnalyzerModel: string;
    openAiGeneratorModel: string;
    claudeBridgeUrl: string;
    claudeBridgeTokenSet: boolean;
    claudeAnalyzerModel: string;
    claudeGeneratorModel: string;
    timeoutSeconds: number;
  };
  effective: {
    provider: LlmProvider;
    analyzerModel: string;
    generatorModel: string;
    ready: boolean;
    notReadyReason: string | null;
  };
}

export interface UpdateLlmSettings {
  provider: LlmProvider;
  openAiBaseUrl: string;
  /** Empty keeps the saved key. */
  openAiApiKey: string;
  clearOpenAiApiKey: boolean;
  openAiAnalyzerModel: string;
  openAiGeneratorModel: string;
  claudeBridgeUrl: string;
  claudeAnalyzerModel: string;
  claudeGeneratorModel: string;
  timeoutSeconds: number;
}

export interface LlmTestResult {
  ok: boolean;
  provider: string;
  model: string;
  reply: string | null;
  ms: number;
  tokens: number;
  error: string | null;
}

const KEY = ["settings", "llm"];

export function useLlmSettings() {
  return useQuery({ queryKey: KEY, queryFn: () => api.get<LlmSettingsDto>("/api/settings/llm") });
}

export function useSaveLlmSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: UpdateLlmSettings) => api.put<LlmSettingsDto>("/api/settings/llm", body),
    onSuccess: (data) => {
      qc.setQueryData(KEY, data);
      qc.invalidateQueries({ queryKey: ["meta"] });
    },
  });
}

export function useTestLlm() {
  return useMutation({ mutationFn: () => api.post<LlmTestResult>("/api/settings/llm/test", {}) });
}
