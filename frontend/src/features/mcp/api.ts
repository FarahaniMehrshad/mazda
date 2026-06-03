import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@/shared/api/client";
import type { McpToolDto } from "@/shared/api/types";

export interface McpToolInput {
  signatureId: string;
  name: string;
  displayName: string;
  description?: string;
  defaultSetupPrompt?: string;
  defaultTestPrompt?: string;
  enabled: boolean;
}

export const mcpApi = {
  list: () => api.get<McpToolDto[]>("/api/mcp-tools/"),
  get: (id: string) => api.get<McpToolDto>(`/api/mcp-tools/${id}`),
  create: (input: McpToolInput) => api.post<McpToolDto>("/api/mcp-tools/", input),
  update: (id: string, input: McpToolInput) =>
    api.put<McpToolDto>(`/api/mcp-tools/${id}`, { id, ...input }),
  remove: (id: string) => api.del<void>(`/api/mcp-tools/${id}`),
};

export function useMcpTools() {
  return useQuery({ queryKey: ["mcp-tools"], queryFn: mcpApi.list });
}

export function useCreateMcpTool() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: mcpApi.create,
    onSuccess: () => qc.invalidateQueries({ queryKey: ["mcp-tools"] }),
  });
}

export function useUpdateMcpTool() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, input }: { id: string; input: McpToolInput }) => mcpApi.update(id, input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["mcp-tools"] }),
  });
}

export function useDeleteMcpTool() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: mcpApi.remove,
    onSuccess: () => qc.invalidateQueries({ queryKey: ["mcp-tools"] }),
  });
}
