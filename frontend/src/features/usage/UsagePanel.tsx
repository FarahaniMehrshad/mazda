import type { CSSProperties, ReactNode } from "react";
import { Link } from "react-router-dom";
import { Pill } from "@/shared/components/Primitives";
import { fmtMs } from "@/features/traces/api";
import {
  costNote,
  fmtInt,
  fmtUsd,
  fmtUsdExact,
  useSubjectUsage,
  type LlmCall,
  type UsageTotals,
} from "./api";

/**
 * Every LLM call made for one run or round trip: exact totals, then one row per
 * call. `live` keeps it refreshing while the work is still going.
 */
export function UsagePanel({ subject, refreshKey, live = false, title = "LLM usage" }: {
  subject: string | undefined;
  refreshKey?: unknown;
  live?: boolean;
  title?: string;
}) {
  const q = useSubjectUsage(subject, refreshKey, live);
  if (!subject) return null;
  return (
    <section className="mz-glass" aria-label={title} style={{ padding: 16, borderRadius: 14, display: "flex", flexDirection: "column", gap: 12 }}>
      <div style={{ display: "flex", alignItems: "baseline", gap: 10, flexWrap: "wrap" }}>
        <span style={{ fontSize: 13, fontWeight: 500 }}>{title}</span>
        {q.data && q.data.totals.calls > 0 && (
          <span style={{ fontSize: 12, color: "var(--text-3)" }}>cost {costNote(q.data.totals)}</span>
        )}
      </div>
      {q.isLoading && <span style={{ fontSize: 12, color: "var(--text-3)" }}>Loading…</span>}
      {q.error && <Pill tone="err">Could not load the usage.</Pill>}
      {q.data && q.data.totals.calls === 0 && (
        <span style={{ fontSize: 12, color: "var(--text-3)" }}>No LLM calls yet.</span>
      )}
      {q.data && q.data.totals.calls > 0 && (
        <>
          <UsageTiles t={q.data.totals} />
          <CallTable calls={q.data.calls} />
        </>
      )}
    </section>
  );
}

/** Totals as tiles: exact counts, never rounded. */
export function UsageTiles({ t }: { t: UsageTotals }) {
  const unknownCost = t.unknownCostCalls > 0;
  return (
    <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(112px, 1fr))", gap: 8 }}>
      <Tile label="Calls" value={fmtInt(t.calls)} sub={t.failedCalls ? `${t.failedCalls} failed` : fmtMs(t.durationMs) + " waiting"} />
      <Tile label="Input" value={fmtInt(t.input)} sub="uncached" />
      <Tile label="Cache read" value={fmtInt(t.cacheRead)} sub={pct(t.cacheRead, t.input + t.cacheRead + t.cacheWrite) + " of input"} />
      <Tile label="Cache write" value={fmtInt(t.cacheWrite)} sub="written to cache" />
      <Tile label="Output" value={fmtInt(t.output)} sub={t.reasoning ? `${fmtInt(t.reasoning)} reasoning` : "incl. reasoning"} />
      <Tile label="Total tokens" value={fmtInt(t.total)} sub="all of the above" />
      <Tile
        label={unknownCost ? "Cost (partial)" : t.estimatedCostCalls && !t.reportedCostCalls ? "Cost (estimated)" : "Cost"}
        value={fmtUsd(t.costUsd)}
        title={fmtUsdExact(t.costUsd)}
        sub={unknownCost ? `${t.unknownCostCalls} without a price`
          : t.estimatedCostCalls && t.reportedCostCalls ? "reported + estimated"
          : t.estimatedCostCalls ? "estimated from your prices" : "reported"}
        accent
      />
    </div>
  );
}

function Tile({ label, value, sub, title, accent }: { label: string; value: string; sub?: string; title?: string; accent?: boolean }) {
  return (
    <div title={title} style={{
      padding: "10px 12px", borderRadius: 10, background: "var(--glass-1)",
      border: `1px solid ${accent ? "rgba(106,209,154,0.35)" : "var(--glass-stroke)"}`, minWidth: 0,
    }}>
      <div style={{ fontFamily: "var(--f-mono)", fontSize: 10, letterSpacing: "0.06em", textTransform: "uppercase", color: "var(--text-3)" }}>{label}</div>
      <div style={{ fontFamily: "var(--f-mono)", fontSize: 16, marginTop: 4, color: accent ? "#8be0b4" : "var(--text-1)", overflowWrap: "anywhere" }}>{value}</div>
      {sub && <div style={{ fontSize: 11, color: "var(--text-4)", marginTop: 2 }}>{sub}</div>}
    </div>
  );
}

const pct = (part: number, whole: number) => (whole > 0 ? `${Math.round((part / whole) * 100)}%` : "0%");

/** One row per call, oldest first. `showSubject` adds what the call was for (the Usage page). */
export function CallTable({ calls, showSubject = false }: { calls: LlmCall[]; showSubject?: boolean }) {
  return (
    <div className="mz-scroll" style={{ overflowX: "auto" }}>
      <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 12 }}>
        <thead>
          <tr style={{ color: "var(--text-3)", textAlign: "left" }}>
            <Th>When</Th>
            {showSubject && <Th>For</Th>}
            <Th>Call</Th>
            <Th>Model</Th>
            <Th num>Input</Th>
            <Th num>Cache read</Th>
            <Th num>Cache write</Th>
            <Th num>Output</Th>
            <Th num>Cost</Th>
            <Th num>Took</Th>
            <Th>Result</Th>
          </tr>
        </thead>
        <tbody>
          {calls.map((c) => (
            <tr key={c.id} style={{ borderTop: "1px solid var(--glass-stroke)" }}>
              <Td mono>{new Date(c.createdAt).toLocaleTimeString()}</Td>
              {showSubject && <Td><SubjectCell c={c} /></Td>}
              <Td>
                {c.traceId ? <Link to={`/traces/${c.traceId}`} style={linkStyle}>{c.purpose}</Link> : c.purpose}
              </Td>
              <Td mono title={c.model !== c.resolvedModel ? `configured as ${c.model}` : undefined}>{c.resolvedModel || c.model}</Td>
              <Td num>{fmtInt(c.input)}</Td>
              <Td num>{fmtInt(c.cacheRead)}</Td>
              <Td num>{fmtInt(c.cacheWrite)}</Td>
              <Td num title={c.reasoning ? `${fmtInt(c.reasoning)} of it reasoning` : undefined}>
                {fmtInt(c.output)}{c.reasoning ? <span style={{ color: "var(--text-4)" }}> ({fmtInt(c.reasoning)} r)</span> : null}
              </Td>
              <Td num title={`${fmtUsdExact(c.costUsd)} · ${c.costSource}`}>
                {c.costUsd === null ? <span style={{ color: "var(--text-4)" }}>{c.total > 0 ? "no price" : "—"}</span> : fmtUsd(c.costUsd)}
                {c.costSource === "estimated" && <span style={{ color: "var(--text-4)" }}> est.</span>}
              </Td>
              <Td num>{fmtMs(c.durationMs)}</Td>
              <Td title={c.error || undefined}>
                {c.ok ? <span style={{ color: "#8be0b4" }}>ok</span> : <span style={{ color: "#f0a89e" }}>failed</span>}
              </Td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function SubjectCell({ c }: { c: LlmCall }) {
  const to = c.subjectId && (c.kind === "run" || c.kind === "mcp") ? `/runs/${c.subjectId}` : null;
  const text = c.subjectLabel || c.kind;
  return to ? <Link to={to} style={linkStyle}>{text}</Link> : <span>{text}</span>;
}

const linkStyle: CSSProperties = { color: "var(--text-1)", textDecoration: "underline", textDecorationColor: "var(--text-4)" };

function Th({ children, num }: { children: ReactNode; num?: boolean }) {
  return (
    <th style={{ padding: "6px 8px", fontWeight: 400, fontFamily: "var(--f-mono)", fontSize: 10.5, letterSpacing: "0.04em",
      textTransform: "uppercase", textAlign: num ? "right" : "left", whiteSpace: "nowrap" }}>
      {children}
    </th>
  );
}

function Td({ children, num, mono, title }: { children: ReactNode; num?: boolean; mono?: boolean; title?: string }) {
  return (
    <td title={title} style={{ padding: "6px 8px", textAlign: num ? "right" : "left", whiteSpace: "nowrap",
      fontFamily: num || mono ? "var(--f-mono)" : undefined, maxWidth: 320, overflow: "hidden", textOverflow: "ellipsis" }}>
      {children}
    </td>
  );
}
