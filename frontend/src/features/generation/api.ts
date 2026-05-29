import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@/shared/api/client";
import type { GenerationDto } from "@/shared/api/types";

export const generationApi = {
  recent: (take = 20) => api.get<GenerationDto[]>(`/api/generations/recent?take=${take}`),
  get: (id: string) => api.get<GenerationDto>(`/api/generations/${id}`),
  create: (signatureId: string) =>
    api.post<GenerationDto>("/api/generations/", { signatureId }),
  generateSetup: (id: string, setupPrompt: string) =>
    api.post<GenerationDto>(`/api/generations/${id}/setup`, { setupPrompt }),
  saveSetupScript: (id: string, script: string) =>
    api.put<GenerationDto>(`/api/generations/${id}/setup-script`, { script }),
  generateTest: (id: string, testPrompt: string) =>
    api.post<GenerationDto>(`/api/generations/${id}/test`, { testPrompt }),
  saveTestScript: (id: string, script: string) =>
    api.put<GenerationDto>(`/api/generations/${id}/test-script`, { script }),
  merge: (id: string) =>
    api.post<GenerationDto>(`/api/generations/${id}/merge`, {}),
  saveFinalScript: (id: string, script: string) =>
    api.put<GenerationDto>(`/api/generations/${id}/final-script`, { script }),
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

function invalidateOnSuccess(qc: ReturnType<typeof useQueryClient>) {
  return (data: GenerationDto) => {
    qc.invalidateQueries({ queryKey: ["generations", "recent"] });
    qc.setQueryData(["generation", data.id], data);
  };
}

export function useCreateGeneration() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: generationApi.create,
    onSuccess: invalidateOnSuccess(qc),
  });
}

export function useGenerateSetup() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, setupPrompt }: { id: string; setupPrompt: string }) =>
      generationApi.generateSetup(id, setupPrompt),
    onSuccess: invalidateOnSuccess(qc),
  });
}

export function useSaveSetupScript() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, script }: { id: string; script: string }) =>
      generationApi.saveSetupScript(id, script),
    onSuccess: invalidateOnSuccess(qc),
  });
}

export function useGenerateTest() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, testPrompt }: { id: string; testPrompt: string }) =>
      generationApi.generateTest(id, testPrompt),
    onSuccess: invalidateOnSuccess(qc),
  });
}

export function useSaveTestScript() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, script }: { id: string; script: string }) =>
      generationApi.saveTestScript(id, script),
    onSuccess: invalidateOnSuccess(qc),
  });
}

export function useMergeGeneration() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => generationApi.merge(id),
    onSuccess: invalidateOnSuccess(qc),
  });
}

export function useSaveFinalScript() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, script }: { id: string; script: string }) =>
      generationApi.saveFinalScript(id, script),
    onSuccess: invalidateOnSuccess(qc),
  });
}
