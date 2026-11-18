import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@/shared/api/client";

export type LlmProvider = "openai" | "claude-code";

/** US dollars per 1M tokens. Cached input and cache writes fall back to the input price when null. */
export interface LlmPrice {
  model: string;
  inputPerM: number;
  cachedInputPerM: number | null;
  cacheWritePerM: number | null;
  outputPerM: number;
}

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
  /** The price list in effect for estimating API-key costs. */
  prices: LlmPrice[];
  /** False while the built-in prices apply. */
  pricesCustomized: boolean;
  defaultPrices: LlmPrice[];
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
  /** The new price list; omit to keep the current one. */
  prices?: LlmPrice[];
  /** Go back to the built-in prices. */
  resetPrices?: boolean;
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
      qc.invalidateQueries({ queryKey: ["usage"] });
    },
  });
}

export function useTestLlm() {
  return useMutation({ mutationFn: () => api.post<LlmTestResult>("/api/settings/llm/test", {}) });
}
