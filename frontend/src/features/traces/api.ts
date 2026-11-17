import { useQuery } from "@tanstack/react-query";
import { api } from "@/shared/api/client";

/**
 * Traces: where the time went in a function check, a gather + plan, a run or a
 * round trip. Every node has an absolute start (Unix ms) and a duration; a
 * Playwright call also carries the function and body line that made it.
 */

export interface TraceNode {
  id: number;
  parent: number | null;
  name: string;
  category: string;
  start: number;
  ms: number;
  fn?: string | null;
  body?: string | null;
  line?: number | null;
  error?: string | null;
  timedOut?: boolean;
}

export interface TraceBody {
  fn: string;
  lines: string[];
}

export interface TraceDoc {
  kind: string;
  label: string;
  start: number;
  ms: number;
  nodes: TraceNode[];
  bodies: Record<string, TraceBody>;
}

export interface TraceBucket {
  category: string;
  ms: number;
  count: number;
  normal: boolean;
}

export interface TraceLine {
  fn: string;
  body: string;
  line: number;
  source: string;
  ms: number;
  calls: number;
  timedOut: number;
  category: string;
}

export interface TraceFunction {
  fn: string;
  calls: number;
  ms: number;
  betweenCallsMs: number;
}

export interface TraceView {
  id: string;
  kind: string;
  label: string;
  subjectId: string;
  createdAt: string;
  updatedAt: string;
  trace: TraceDoc;
  summary: { totalMs: number; buckets: TraceBucket[]; lines: TraceLine[]; functions: TraceFunction[] };
}

export interface TraceListItem {
  id: string;
  kind: string;
  label: string;
  totalMs: number;
  createdAt: string;
}

export const traceApi = {
  get: (id: string) => api.get<TraceView>(`/api/traces/${id}`),
  list: (subject: string) => api.get<TraceListItem[]>(`/api/traces?subject=${subject}`),
};

/** One trace. `live` refetches every few seconds (a round trip saves its trace as it goes). */
export function useTrace(id: string | undefined, live = false) {
  return useQuery({
    queryKey: ["trace", id],
    queryFn: () => traceApi.get(id!),
    enabled: !!id,
    refetchInterval: live ? 5000 : false,
  });
}

export function useTraces(subject: string | undefined, refreshKey?: unknown) {
  return useQuery({
    queryKey: ["traces", subject, refreshKey],
    queryFn: () => traceApi.list(subject!),
    enabled: !!subject,
  });
}

/** What each category means, how it is drawn, and whether its time is expected. */
export const CATEGORY: Record<string, { label: string; color: string; hint: string }> = {
  timeout: { label: "timed out", color: "#e85c4a", hint: "Playwright calls that waited their full timeout (also when the body swallowed the error with .catch)" },
  sleep: { label: "fixed pauses", color: "#e8b04a", hint: "waitForTimeout and harness pauses: time spent waiting for nothing" },
  action: { label: "page actions", color: "#ea7028", hint: "click, fill, hover… includes Playwright waiting for the element to be ready" },
  wait: { label: "element waits", color: "#c79bf2", hint: "waitFor, waitForSelector, expect" },
  read: { label: "page reads", color: "#7cc4bd", hint: "textContent, count, evaluate…" },
  capture: { label: "screenshots", color: "#d1a36a", hint: "screenshots and accessibility snapshots taken by the harness" },
  harness: { label: "harness", color: "#b5866a", hint: "settling, recorder, forensics, harness code between steps" },
  startup: { label: "start-up", color: "#9a8f86", hint: "run directory, starting Playwright and TypeScript, connecting to the browser, shutting down" },
  llm: { label: "LLM", color: "#6ad19a", hint: "model calls (planning, judging, diagnosing)" },
  db: { label: "database", color: "#7d8fb3", hint: "saving and loading" },
  js: { label: "function code", color: "#e0d070", hint: "a function's own JavaScript between Playwright calls" },
  backend: { label: "backend", color: "#806a5a", hint: "server-side work outside the spans above" },
  network: { label: "network (normal)", color: "#6aa8d1", hint: "waiting for responses and navigation: expected" },
  function: { label: "function", color: "#e0d070", hint: "one call of a catalogue function" },
  operation: { label: "operation", color: "#806a5a", hint: "a phase of the operation" },
  other: { label: "other", color: "#666", hint: "" },
};

export const catOf = (c: string) => CATEGORY[c] ?? CATEGORY.other;

export function fmtMs(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(ms < 10_000 ? 2 : 1)} s`;
  const m = Math.floor(ms / 60_000);
  return `${m} min ${Math.round((ms % 60_000) / 1000)} s`;
}
