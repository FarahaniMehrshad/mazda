import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@/shared/api/client";
import type { GenerationDto } from "@/shared/api/types";

export interface GenerateInput {
  signatureId: string;
  targetUrl: string;
  prompt: string;
}

export const generationApi = {
  recent: (take = 20) => api.get<GenerationDto[]>(`/api/generations/recent?take=${take}`),
  get: (id: string) => api.get<GenerationDto>(`/api/generations/${id}`),
  generate: (body: GenerateInput) => api.post<GenerationDto>("/api/generations/", body),
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

export function useGenerateScript() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: generationApi.generate,
    onSuccess: (data) => {
      qc.invalidateQueries({ queryKey: ["generations", "recent"] });
      qc.setQueryData(["generation", data.id], data);
    },
  });
}
