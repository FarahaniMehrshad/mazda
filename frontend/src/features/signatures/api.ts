import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { api } from "@/shared/api/client";
import type { SignatureDto, SignatureElementDto } from "@/shared/api/types";

export interface SaveSignatureInput {
  id?: string;
  name: string;
  source?: string;
  type?: string;
  elements: SignatureElementDto[];
}

export const signatureApi = {
  list: () => api.get<SignatureDto[]>("/api/signatures/"),
  get: (id: string) => api.get<SignatureDto>(`/api/signatures/${id}`),
  create: (body: SaveSignatureInput) => api.post<SignatureDto>("/api/signatures/", body),
  update: (id: string, body: SaveSignatureInput) => api.put<SignatureDto>(`/api/signatures/${id}`, body),
  remove: (id: string) => api.del<void>(`/api/signatures/${id}`),
  analyze: (fileName: string, source: string, description?: string) =>
    api.post<SignatureDto>("/api/signatures/analyze", { fileName, source, description }),
  generateFlows: (id: string, cypressSource?: string) =>
    api.post<SignatureDto>(`/api/signatures/${id}/flows/generate`, { cypressSource }),
  saveFlows: (id: string, flowsJson: string) =>
    api.put<SignatureDto>(`/api/signatures/${id}/flows`, { flowsJson }),
};

export function useSignatures() {
  return useQuery({
    queryKey: ["signatures"],
    queryFn: signatureApi.list,
  });
}

export function useSignature(id: string | undefined) {
  return useQuery({
    queryKey: ["signature", id],
    queryFn: () => signatureApi.get(id!),
    enabled: !!id,
  });
}

export function useSaveSignature() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: SaveSignatureInput) =>
      input.id ? signatureApi.update(input.id, input) : signatureApi.create(input),
    onSuccess: (data) => {
      qc.invalidateQueries({ queryKey: ["signatures"] });
      qc.setQueryData(["signature", data.id], data);
    },
  });
}

export function useDeleteSignature() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: signatureApi.remove,
    onSuccess: () => qc.invalidateQueries({ queryKey: ["signatures"] }),
  });
}

export function useAnalyzeComponent() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ fileName, source, description }: { fileName: string; source: string; description?: string }) =>
      signatureApi.analyze(fileName, source, description),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["signatures"] }),
  });
}

export function useGenerateFlows() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, cypressSource }: { id: string; cypressSource?: string }) =>
      signatureApi.generateFlows(id, cypressSource),
    onSuccess: (data) => {
      qc.invalidateQueries({ queryKey: ["signatures"] });
      qc.setQueryData(["signature", data.id], data);
    },
  });
}

export function useSaveFlows() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, flowsJson }: { id: string; flowsJson: string }) =>
      signatureApi.saveFlows(id, flowsJson),
    onSuccess: (data) => {
      qc.invalidateQueries({ queryKey: ["signatures"] });
      qc.setQueryData(["signature", data.id], data);
    },
  });
}
