import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@/shared/api/client";
import type { SignatureFunctionDto } from "@/shared/api/types";

/**
 * Payload shape for POST / PUT on a single function. Server rebinds
 * SignatureId / FunctionId from the route; passing dummies here is fine.
 */
export interface FunctionInput {
  name: string;
  kind: "state" | "action";
  description: string;
  parametersJsonSchema: string;
  returnsJsonSchema: string;
  preconditions: string;
  body: string;
  order?: number;
}

export const functionsApi = {
  list: (sigId: string) =>
    api.get<SignatureFunctionDto[]>(`/api/signatures/${sigId}/functions`),
  generate: (sigId: string) =>
    api.post<SignatureFunctionDto[]>(`/api/signatures/${sigId}/functions/generate`, {}),
  create: (sigId: string, input: FunctionInput) =>
    api.post<SignatureFunctionDto>(`/api/signatures/${sigId}/functions`, input),
  update: (sigId: string, fnId: string, input: FunctionInput) =>
    api.put<SignatureFunctionDto>(`/api/signatures/${sigId}/functions/${fnId}`, {
      // Order is required on the wire; default to 0 if the caller didn't set it.
      order: 0,
      ...input,
    }),
  remove: (sigId: string, fnId: string) =>
    api.del<void>(`/api/signatures/${sigId}/functions/${fnId}`),
};

export function useSignatureFunctions(sigId: string | undefined) {
  return useQuery({
    queryKey: ["signature-functions", sigId],
    queryFn: () => functionsApi.list(sigId!),
    enabled: !!sigId,
  });
}

export function useGenerateFunctions(sigId: string | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => functionsApi.generate(sigId!),
    onSuccess: (data) => {
      qc.setQueryData(["signature-functions", sigId], data);
    },
  });
}

export function useCreateFunction(sigId: string | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: FunctionInput) => functionsApi.create(sigId!, input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["signature-functions", sigId] }),
  });
}

export function useUpdateFunction(sigId: string | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ fnId, input }: { fnId: string; input: FunctionInput }) =>
      functionsApi.update(sigId!, fnId, input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["signature-functions", sigId] }),
  });
}

export function useDeleteFunction(sigId: string | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (fnId: string) => functionsApi.remove(sigId!, fnId),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["signature-functions", sigId] }),
  });
}
