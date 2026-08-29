import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@/shared/api/client";
import type { GenerationDto } from "@/shared/api/types";

/**
 * Runtime API under the function-catalogue architecture. Two endpoints do
 * the real work:
 *   - plan: synchronous — gather state + LLM plan, returns Ready DTO with
 *           gatheredStateJson + planJson populated.
 *   - execute: fire-and-forget — kicks off the interpreter, returns
 *              immediately with executionStatus=Running.
 *
 * Live per-step events are pushed via SSE on
 * `/api/generations/{id}/execution/stream` — parse `__MZ_EVENT__ {...}`
 * lines.
 */
export const generationApi = {
  recent: (take = 20) => api.get<GenerationDto[]>(`/api/generations/recent?take=${take}`),
  get: (id: string) => api.get<GenerationDto>(`/api/generations/${id}`),
  plan: (signatureId: string, prompt: string) =>
    api.post<GenerationDto>("/api/generations/plan", { signatureId, prompt }),
  execute: (id: string) =>
    api.post<GenerationDto>(`/api/generations/${id}/execute`, {}),
};

export function useRecentGenerations(take = 20) {
  return useQuery({
    queryKey: ["generations", "recent", take],
    queryFn: () => generationApi.recent(take),
  });
}

export function useGeneration(id: string | undefined) {
  return useQuery({
    queryKey: ["generation", id],
    queryFn: () => generationApi.get(id!),
    enabled: !!id,
  });
}

function invalidate(qc: ReturnType<typeof useQueryClient>) {
  return (data: GenerationDto) => {
    qc.invalidateQueries({ queryKey: ["generations", "recent"] });
    qc.setQueryData(["generation", data.id], data);
  };
}

export function usePlanRun() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ signatureId, prompt }: { signatureId: string; prompt: string }) =>
      generationApi.plan(signatureId, prompt),
    onSuccess: invalidate(qc),
  });
}

export function useExecuteRun() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => generationApi.execute(id),
    onSuccess: invalidate(qc),
  });
}
