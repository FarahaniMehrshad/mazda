import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { api } from "@/shared/api/client";
import type { SignatureDto, SignatureElementDto, SignatureDependencyDto } from "@/shared/api/types";

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
  analyze: (fileName: string, source: string, description?: string, name?: string) =>
    api.post<SignatureDto>("/api/signatures/analyze", { fileName, source, description, name }),
  generateFlows: (id: string) =>
    api.post<SignatureDto>(`/api/signatures/${id}/flows/generate`, {}),
  saveFlows: (id: string, flowsJson: string) =>
    api.put<SignatureDto>(`/api/signatures/${id}/flows`, { flowsJson }),
  analyzeInformational: (id: string) =>
    api.post<SignatureDto>(`/api/signatures/${id}/analyze-informational`, {}),
  reanalyzeInteractive: (id: string) =>
    api.post<SignatureDto>(`/api/signatures/${id}/reanalyze-interactive`, {}),
  analyzeRelationships: (id: string) =>
    api.post<SignatureDto>(`/api/signatures/${id}/analyze-relationships`, {}),
  saveRelationships: (id: string, graphJson: string) =>
    api.put<SignatureDto>(`/api/signatures/${id}/relationships`, { graphJson }),
  // ── Dependencies ──
  listDependencies: (id: string) =>
    api.get<SignatureDependencyDto[]>(`/api/signatures/${id}/dependencies`),
  addDependency: (id: string, dependsOnId: string) =>
    api.post<SignatureDependencyDto[]>(`/api/signatures/${id}/dependencies`, { dependsOnId }),
  removeDependency: (id: string, depId: string) =>
    api.del<SignatureDependencyDto[]>(`/api/signatures/${id}/dependencies/${depId}`),
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
    mutationFn: ({ fileName, source, description, name }: { fileName: string; source: string; description?: string; name?: string }) =>
      signatureApi.analyze(fileName, source, description, name),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["signatures"] }),
  });
}

export function useGenerateFlows() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id }: { id: string }) => signatureApi.generateFlows(id),
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

export function useAnalyzeInformational() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => signatureApi.analyzeInformational(id),
    onSuccess: (data) => {
      qc.invalidateQueries({ queryKey: ["signatures"] });
      qc.setQueryData(["signature", data.id], data);
    },
  });
}

export function useReanalyzeInteractive() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => signatureApi.reanalyzeInteractive(id),
    onSuccess: (data) => {
      qc.invalidateQueries({ queryKey: ["signatures"] });
      qc.setQueryData(["signature", data.id], data);
    },
  });
}

export function useAnalyzeRelationships() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => signatureApi.analyzeRelationships(id),
    onSuccess: (data) => {
      qc.invalidateQueries({ queryKey: ["signatures"] });
      qc.setQueryData(["signature", data.id], data);
    },
  });
}

export function useSaveRelationships() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, graphJson }: { id: string; graphJson: string }) =>
      signatureApi.saveRelationships(id, graphJson),
    onSuccess: (data) => {
      qc.invalidateQueries({ queryKey: ["signatures"] });
      qc.setQueryData(["signature", data.id], data);
    },
  });
}

// ── Dependencies ──────────────────────────────────────────────────

export function useSignatureDependencies(id: string | undefined) {
  return useQuery({
    queryKey: ["signature-dependencies", id],
    queryFn: () => signatureApi.listDependencies(id!),
    enabled: !!id,
  });
}

export function useAddSignatureDependency(signatureId: string | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (dependsOnId: string) => signatureApi.addDependency(signatureId!, dependsOnId),
    onSuccess: (data) => {
      qc.setQueryData(["signature-dependencies", signatureId], data);
      // Adding a dep invalidates derived caches on the signature — refetch so
      // the Last-* indicators (which the editor surfaces indirectly via the
      // run pre-fills) come back fresh.
      qc.invalidateQueries({ queryKey: ["signature", signatureId] });
      qc.invalidateQueries({ queryKey: ["signatures"] });
    },
  });
}

export function useRemoveSignatureDependency(signatureId: string | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (depId: string) => signatureApi.removeDependency(signatureId!, depId),
    onSuccess: (data) => {
      qc.setQueryData(["signature-dependencies", signatureId], data);
      qc.invalidateQueries({ queryKey: ["signature", signatureId] });
      qc.invalidateQueries({ queryKey: ["signatures"] });
    },
  });
}
