import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@/shared/api/client";

/**
 * Round trip: Mazda rebuilds from a recording the function calls that
 * reproduce it, replays them, compares every step with what the user did,
 * and fixes what differs. The author confirms the plan before anything runs.
 */

export interface PlanStep {
  call: string;
  argsJson: string;
  episodes: number[];
  contrastArgsJson: string | null;
  why: string;
}

export interface RoundTripStep extends PlanStep {
  index: number;
  /** One plain line per demonstrated interaction the step reproduces. */
  demoSteps: string[];
}

export interface Finding {
  severity: "blocking" | "unexplained" | "note";
  kind: string;
  key: string;
  summary: string;
  demo?: string | null;
  replay?: string | null;
}

export interface StepResult {
  index: number;
  call: string;
  verdict: "identical" | "equivalent" | "different" | "question" | "not-run";
  findings: Finding[];
  judge: { verdict: string; reason: string } | null;
  question: string | null;
  error: string | null;
  durationMs: number;
  run: number;
  demoRequests: string[];
  replayRequests: string[];
  console: string[];
}

export interface LogEntry {
  at: string;
  kind: "info" | "replay" | "verdict" | "fix" | "revert" | "plan" | "question" | "error";
  message: string;
  function?: string | null;
}

export type RoundTripStatus = "Draft" | "Running" | "NeedsPlan" | "NeedsAnswers" | "Done" | "Failed" | "Stopped";

export interface RoundTrip {
  id: string;
  signatureId: string;
  recordingId: string;
  recordingLabel: string;
  status: RoundTripStatus;
  plan: RoundTripStep[];
  notes: string;
  planProblem: string | null;
  pendingPlanChange: { step: number; call: string; argsJson: string; explanation: string } | null;
  result: { steps: StepResult[]; replays: number; summary: string } | null;
  log: LogEntry[];
  error: string;
  createdAt: string;
  updatedAt: string;
  /** The page every replay opens first: the author's choice, or where the recording started. */
  startUrl: string;
  /** Where the recording started (query values hidden). */
  recordedStartUrl: string;
  startUrlIsCustom: boolean;
  /** A model judges the pictures of steps the comparison cannot decide; off (the default) = you are asked. */
  pictureJudge: boolean;
}

export interface RoundTripSummary {
  id: string;
  recordingId: string;
  recordingLabel: string;
  status: RoundTripStatus;
  summary: string;
  createdAt: string;
}

export const roundTripApi = {
  list: (sigId: string) => api.get<RoundTripSummary[]>(`/api/signatures/${sigId}/roundtrips`),
  start: (sigId: string, recordingId: string) => api.post<RoundTrip>(`/api/signatures/${sigId}/roundtrips`, { recordingId }),
  get: (id: string) => api.get<RoundTrip>(`/api/roundtrips/${id}`),
  savePlan: (id: string, steps: PlanStep[]) => api.put<RoundTrip>(`/api/roundtrips/${id}/plan`, { steps }),
  /** An empty url goes back to the page the recording started on. */
  setStartUrl: (id: string, url: string) => api.put<RoundTrip>(`/api/roundtrips/${id}/start-url`, { url }),
  setPictureJudge: (id: string, enabled: boolean) => api.put<RoundTrip>(`/api/roundtrips/${id}/picture-judge`, { enabled }),
  confirm: (id: string) => api.post<RoundTrip>(`/api/roundtrips/${id}/confirm`, { confirmed: true }),
  /** Ends a running round trip; a fix still being checked is undone. */
  stop: (id: string) => api.post<RoundTrip>(`/api/roundtrips/${id}/stop`, {}),
  planChange: (id: string, accept: boolean) => api.post<RoundTrip>(`/api/roundtrips/${id}/plan-change`, { accept }),
  answer: (id: string, step: number, same: boolean) => api.post<RoundTrip>(`/api/roundtrips/${id}/answers`, { step, same }),
  revert: (id: string, fn: string) => api.post<RoundTrip>(`/api/roundtrips/${id}/revert`, { function: fn }),
  shot: (id: string, side: "demo" | "replay", step: number, phase: "before" | "after", run?: number) =>
    api.get<string>(`/api/roundtrips/${id}/shots/${side}/${step}/${phase}${run ? `?run=${run}` : ""}`),
};

export function useRoundTrips(sigId: string | undefined) {
  return useQuery({ queryKey: ["roundtrips", sigId], queryFn: () => roundTripApi.list(sigId!), enabled: !!sigId });
}

export function useRoundTrip(id: string | null) {
  return useQuery({
    queryKey: ["roundtrip", id],
    queryFn: () => roundTripApi.get(id!),
    enabled: !!id,
    // While it runs, the stored results and log move; the live feed covers the seconds in between.
    refetchInterval: (q) => (q.state.data?.status === "Running" ? 4000 : false),
  });
}

/** A picture, or null when that side has none. Keyed by run so a new replay refetches. */
export function useShot(id: string, side: "demo" | "replay", step: number, phase: "before" | "after", run?: number, enabled = true) {
  return useQuery({
    queryKey: ["roundtrip-shot", id, side, step, phase, run ?? 0],
    queryFn: () => roundTripApi.shot(id, side, step, phase, run).catch(() => null),
    staleTime: Infinity,
    enabled,
  });
}

/** Every mutation returns the round trip; keep the cache and the list in step. */
export function useRoundTripMutation<A>(fn: (a: A) => Promise<RoundTrip>, sigId: string | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: (rt) => {
      qc.setQueryData(["roundtrip", rt.id], rt);
      qc.invalidateQueries({ queryKey: ["roundtrips", sigId] });
      qc.invalidateQueries({ queryKey: ["signature-functions", sigId] });
    },
  });
}
