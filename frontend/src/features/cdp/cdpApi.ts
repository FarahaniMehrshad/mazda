import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@/shared/api/client";
import type { CdpStatusDto } from "@/shared/api/types";

/**
 * Which Chrome, and which tab, MAZDA drives.
 *
 * `status` probes the browser and lists its tabs in one round-trip. An
 * unreachable browser comes back as `available: false` with a `reason` — a
 * successful response describing a normal state, not an error — so the panel
 * can render the explanation and a retry.
 */
export const cdpApi = {
  status: () => api.get<CdpStatusDto>("/api/cdp/status"),
  attach: (targetId: string) => api.post<CdpStatusDto>("/api/cdp/attach", { targetId }),
  detach: () => api.post<CdpStatusDto>("/api/cdp/detach", {}),
  setPort: (port: number) => api.put<CdpStatusDto>("/api/cdp/port", { port }),
};

const KEY = ["cdp", "status"];

export function useCdpStatus(enabled = true) {
  return useQuery({
    queryKey: KEY,
    queryFn: cdpApi.status,
    enabled,
    // Tabs open, close and navigate while the panel is on screen, and the
    // titles go stale fast. Cheap call, so keep it fresh rather than making
    // the user hit refresh to see reality.
    refetchInterval: enabled ? 10_000 : false,
    staleTime: 0,
  });
}

/** All three mutations return the fresh status, so they seed the cache directly. */
function useCdpMutation<TArgs>(fn: (args: TArgs) => Promise<CdpStatusDto>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: (data) => qc.setQueryData(KEY, data),
  });
}

export const useAttachTab = () => useCdpMutation((targetId: string) => cdpApi.attach(targetId));
export const useDetachTab = () => useCdpMutation((_: void) => cdpApi.detach());
export const useSetCdpPort = () => useCdpMutation((port: number) => cdpApi.setPort(port));
