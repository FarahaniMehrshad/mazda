import { useQuery } from "@tanstack/react-query";
import { api } from "@/shared/api/client";

/**
 * LLM usage: every call Mazda made, exactly as the provider reported it.
 *
 * Token kinds follow one convention for every provider:
 *   input       uncached input
 *   cacheRead   input served from the prompt cache (cheaper)
 *   cacheWrite  input written to the prompt cache (Claude: dearer than input)
 *   output      everything the model wrote, reasoning included
 *   reasoning   the part of output spent thinking
 */

export type CostSource = "reported" | "estimated" | "unknown";

export interface UsageTotals {
  calls: number;
  failedCalls: number;
  input: number;
  cacheRead: number;
  cacheWrite: number;
  output: number;
  reasoning: number;
  total: number;
  costUsd: number;
  reportedCostCalls: number;
  estimatedCostCalls: number;
  unknownCostCalls: number;
  durationMs: number;
}

export interface LlmCall {
  id: string;
  createdAt: string;
  kind: string;
  subjectId: string | null;
  subjectLabel: string;
  purpose: string;
  traceId: string | null;
  provider: string;
  model: string;
  resolvedModel: string;
  input: number;
  cacheRead: number;
  cacheWrite: number;
  output: number;
  reasoning: number;
  total: number;
  costUsd: number | null;
  costSource: CostSource;
  durationMs: number;
  ok: boolean;
  error: string;
}

export interface SubjectUsage {
  totals: UsageTotals;
  calls: LlmCall[];
}

export interface UsageGroup {
  key: string;
  totals: UsageTotals;
}

export interface UsageSummary {
  totals: UsageTotals;
  byDay: UsageGroup[];
  byModel: UsageGroup[];
  byKind: UsageGroup[];
  recent: LlmCall[];
}

/** Every call for a run or round trip. `live` refetches while it is still running. */
export function useSubjectUsage(subject: string | undefined, refreshKey?: unknown, live = false) {
  return useQuery({
    queryKey: ["usage", "subject", subject, refreshKey],
    queryFn: () => api.get<SubjectUsage>(`/api/usage/calls?subject=${subject}`),
    enabled: !!subject,
    refetchInterval: live ? 5000 : false,
  });
}

/** Totals per subject (for lists). */
export function useSubjectsUsage(ids: string[]) {
  const key = ids.join(",");
  return useQuery({
    queryKey: ["usage", "subjects", key],
    queryFn: () => api.get<Record<string, UsageTotals>>(`/api/usage/subjects?ids=${key}`),
    enabled: ids.length > 0,
  });
}

/** Usage since `from` (null: all time). Days are the viewer's days. */
export function useUsageSummary(from: Date | null) {
  const tz = -new Date().getTimezoneOffset();
  const qs = new URLSearchParams({ tz: String(tz) });
  if (from) qs.set("from", from.toISOString());
  return useQuery({
    queryKey: ["usage", "summary", from?.toISOString() ?? "all"],
    queryFn: () => api.get<UsageSummary>(`/api/usage/summary?${qs.toString()}`),
  });
}

// ── Formatting ───────────────────────────────────────────────────────

const intFmt = new Intl.NumberFormat();

/** Exact count with separators: 182,345. */
export const fmtInt = (n: number) => intFmt.format(n);

/** Short count: 182k, 1.2M (exact value in a title). */
export function fmtTok(n: number): string {
  if (n < 1000) return String(n);
  if (n < 10_000) return `${(n / 1000).toFixed(1)}k`;
  if (n < 1_000_000) return `${Math.round(n / 1000)}k`;
  return `${(n / 1_000_000).toFixed(n < 10_000_000 ? 2 : 1)}M`;
}

/** Dollars with enough digits to stay honest for small calls: $0.0042, $1.27. */
export function fmtUsd(n: number | null | undefined): string {
  if (n === null || n === undefined) return "—";
  if (n === 0) return "$0";
  if (n < 0.01) return `$${n.toFixed(4)}`;
  if (n < 1) return `$${n.toFixed(3)}`;
  return `$${n.toFixed(2)}`;
}

/** Exact dollars, for titles. */
export const fmtUsdExact = (n: number | null | undefined) => (n === null || n === undefined ? "unknown" : `$${n.toFixed(8).replace(/0+$/, "").replace(/\.$/, "")}`);

export const KIND_LABEL: Record<string, string> = {
  run: "runs",
  roundtrip: "round trips",
  catalogue: "catalogue generation",
  "function-fix": "function fixes",
  "recording-analysis": "recording analysis",
  "signature-analysis": "component analysis",
  mcp: "MCP tool runs",
  "connection-test": "connection tests",
  unattributed: "unattributed",
};

export const kindLabel = (k: string) => KIND_LABEL[k] ?? k.replace(/-/g, " ");

/** Where a call or a total's cost comes from, in words. */
export function costNote(t: Pick<UsageTotals, "reportedCostCalls" | "estimatedCostCalls" | "unknownCostCalls">): string {
  const parts: string[] = [];
  if (t.reportedCostCalls) parts.push("reported by the provider");
  if (t.estimatedCostCalls) parts.push("estimated from your prices");
  let s = parts.join(" + ");
  if (t.unknownCostCalls) s += `${s ? "; " : ""}${t.unknownCostCalls} call${t.unknownCostCalls === 1 ? "" : "s"} without a price`;
  return s || "no cost";
}

/** Link to what a call was for: its run's page, else its trace (round trips, function checks). */
export function subjectLink(c: Pick<LlmCall, "kind" | "subjectId" | "traceId">): string | null {
  if (c.subjectId && (c.kind === "run" || c.kind === "mcp")) return `/runs/${c.subjectId}`;
  if (c.traceId) return `/traces/${c.traceId}`;
  return null;
}
