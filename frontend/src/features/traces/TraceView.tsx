import { useMemo, useState } from "react";
import { Pill } from "@/shared/components/Primitives";
import { Select } from "@/shared/components/Select";
import { catOf, fmtMs, useTrace, type TraceNode, type TraceView as TraceData } from "./api";
import { fmtInt, fmtTok, fmtUsd, fmtUsdExact } from "@/features/usage/api";

/**
 * Where the time went in one operation:
 *   1. time by category (each node counted once, by its own time); network is expected
 *   2. the slowest lines of the function bodies
 *   3. functions: calls, total, time between Playwright calls
 *   4. a body with the time each line took
 *   5. the timeline: operation → replay → harness phase / function → Playwright call
 */
export function TraceViewById({ id, live = false }: { id: string; live?: boolean }) {
  const q = useTrace(id, live);
  if (q.isLoading) return <div style={{ fontSize: 13, color: "var(--text-3)" }}>Loading the trace…</div>;
  if (q.error || !q.data) return <Pill tone="err">Could not load the trace.</Pill>;
  return <TraceView data={q.data} />;
}

export function TraceView({ data }: { data: TraceData }) {
  const [sel, setSel] = useState<{ body: string; line: number | null } | null>(null);
  const firstBody = data.summary.lines[0]?.body ?? Object.keys(data.trace.bodies)[0] ?? null;
  const body = sel?.body ?? firstBody;

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      <Breakdown data={data} />
      <div style={{ display: "grid", gridTemplateColumns: "minmax(0, 3fr) minmax(0, 2fr)", gap: 16, alignItems: "start" }}>
        <SlowLines data={data} onPick={(b, l) => setSel({ body: b, line: l })} selected={sel} />
        <Functions data={data} onPick={(b) => setSel({ body: b, line: null })} />
      </div>
      {body && (
        <Source data={data} body={body} line={sel?.body === body ? sel.line : null}
          onLine={(l) => setSel({ body, line: l })} onBody={(b) => setSel({ body: b, line: null })} />
      )}
      <Timeline data={data} />
    </div>
  );
}

// ── 1. time by category ──────────────────────────────────────────────

function Breakdown({ data }: { data: TraceData }) {
  const total = Math.max(1, data.summary.totalMs);
  const buckets = data.summary.buckets.filter((b) => b.ms > 0);
  const avoidable = Math.max(0, data.summary.totalMs - buckets.filter((b) => b.normal).reduce((s, b) => s + b.ms, 0));
  return (
    <div className="mz-glass" style={{ padding: 16 }}>
      <div style={{ display: "flex", alignItems: "baseline", gap: 12, flexWrap: "wrap" }}>
        <div className="mz-eyebrow">WHERE THE TIME WENT</div>
        <span style={{ fontSize: 13 }}>{fmtMs(data.summary.totalMs)} in total</span>
        <span style={{ fontSize: 12, color: "var(--text-3)" }}>
          {fmtMs(avoidable)} outside network waits ({Math.round((avoidable / total) * 100)}%)
        </span>
      </div>
      {data.summary.llm && (
        <div style={{ fontSize: 12, color: "var(--text-2)", marginTop: 8, fontFamily: "var(--f-mono)" }}
          title={`${fmtInt(data.summary.llm.input)} input + ${fmtInt(data.summary.llm.cacheRead)} cache read + ${fmtInt(data.summary.llm.cacheWrite)} cache write + ${fmtInt(data.summary.llm.output)} output (${fmtInt(data.summary.llm.reasoning)} reasoning) · ${fmtUsdExact(data.summary.llm.costUsd)}`}>
          LLM: {data.summary.llm.calls} call{data.summary.llm.calls === 1 ? "" : "s"}
          {" · "}{fmtTok(data.summary.llm.input + data.summary.llm.cacheRead + data.summary.llm.cacheWrite)} in
          {data.summary.llm.cacheRead > 0 && <> ({fmtTok(data.summary.llm.cacheRead)} cached)</>}
          {" · "}{fmtTok(data.summary.llm.output)} out
          {" · "}{fmtUsd(data.summary.llm.costUsd)}{data.summary.llm.unknownCostCalls ? " + unpriced calls" : ""}
        </div>
      )}
      <div style={{ display: "flex", height: 14, borderRadius: 7, overflow: "hidden", marginTop: 12, background: "var(--glass-1)" }}>
        {buckets.map((b) => (
          <div key={b.category} title={`${catOf(b.category).label}: ${fmtMs(b.ms)}`}
            style={{ width: `${(b.ms / total) * 100}%`, background: catOf(b.category).color, opacity: b.normal ? 0.45 : 0.9, minWidth: b.ms > 0 ? 2 : 0 }} />
        ))}
      </div>
      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(250px, 1fr))", gap: "8px 20px", marginTop: 14 }}>
        {buckets.map((b) => (
          <div key={b.category} title={catOf(b.category).hint} style={{ display: "flex", alignItems: "center", gap: 8, fontSize: 12 }}>
            <span style={{ width: 10, height: 10, borderRadius: 3, background: catOf(b.category).color, opacity: b.normal ? 0.45 : 1, flex: "none" }} />
            <span style={{ color: b.normal ? "var(--text-3)" : "var(--text-1)" }}>{catOf(b.category).label}</span>
            <span style={{ flex: 1 }} />
            <span style={{ fontFamily: "var(--f-mono)" }}>{fmtMs(b.ms)}</span>
            <span style={{ fontFamily: "var(--f-mono)", color: "var(--text-3)", width: 38, textAlign: "right" }}>{Math.round((b.ms / total) * 100)}%</span>
          </div>
        ))}
      </div>
    </div>
  );
}

// ── 2. slowest lines ─────────────────────────────────────────────────

function SlowLines({ data, onPick, selected }: {
  data: TraceData; onPick: (body: string, line: number) => void; selected: { body: string; line: number | null } | null;
}) {
  const lines = data.summary.lines.slice(0, 15);
  return (
    <div className="mz-glass" style={{ padding: 16 }}>
      <div className="mz-eyebrow">SLOWEST LINES</div>
      {lines.length === 0 ? (
        <div style={{ fontSize: 12, color: "var(--text-3)", marginTop: 8 }}>No function body ran in this trace.</div>
      ) : (
        <table style={{ width: "100%", borderCollapse: "collapse", marginTop: 8, fontSize: 12, tableLayout: "fixed" }}>
          <colgroup>
            <col style={{ width: 110 }} />
            <col />
            <col style={{ width: 120 }} />
            <col style={{ width: 70 }} />
            <col style={{ width: 92 }} />
          </colgroup>
          <tbody>
            {lines.map((l) => {
              const active = selected?.body === l.body && selected.line === l.line;
              return (
                <tr key={`${l.body}:${l.line}`} onClick={() => onPick(l.body, l.line)}
                  style={{ cursor: "pointer", background: active ? "var(--glass-3)" : undefined, borderTop: "1px solid var(--glass-stroke)" }}>
                  <td style={{ padding: "6px 6px", whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis", color: "var(--text-2)" }} title={`${l.fn}:${l.line}`}>{l.fn}:{l.line}</td>
                  <td style={{ padding: "6px 6px", fontFamily: "var(--f-mono)", fontSize: 11, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }} title={l.source}>{l.source}</td>
                  <td style={{ padding: "6px 6px", whiteSpace: "nowrap", overflow: "hidden" }}><CatDot c={l.category} /></td>
                  <td style={{ padding: "6px 6px", fontFamily: "var(--f-mono)", textAlign: "right", whiteSpace: "nowrap" }}>{fmtMs(l.ms)}</td>
                  <td style={{ padding: "6px 6px", color: "var(--text-3)", textAlign: "right", lineHeight: 1.3 }}>
                    ×{l.calls}{l.timedOut > 0 && <div style={{ color: "var(--err)" }}>{l.timedOut} timed out</div>}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </div>
  );
}

function CatDot({ c }: { c: string }) {
  return (
    <span style={{ display: "inline-flex", alignItems: "center", gap: 6, color: "var(--text-2)" }} title={catOf(c).hint}>
      <span style={{ width: 8, height: 8, borderRadius: 2, background: catOf(c).color }} />
      {catOf(c).label}
    </span>
  );
}

// ── 3. functions ─────────────────────────────────────────────────────

function Functions({ data, onPick }: { data: TraceData; onPick: (body: string) => void }) {
  const bodyOf = (fn: string) => Object.entries(data.trace.bodies).find(([, b]) => b.fn === fn)?.[0];
  return (
    <div className="mz-glass" style={{ padding: 16 }}>
      <div className="mz-eyebrow">FUNCTIONS</div>
      {data.summary.functions.length === 0 ? (
        <div style={{ fontSize: 12, color: "var(--text-3)", marginTop: 8 }}>No function was called.</div>
      ) : (
        <table style={{ width: "100%", borderCollapse: "collapse", marginTop: 8, fontSize: 12 }}>
          <thead>
            <tr style={{ color: "var(--text-3)", textAlign: "left" }}>
              <th style={{ fontWeight: 400, padding: "4px 6px" }}>function</th>
              <th style={{ fontWeight: 400, padding: "4px 6px", textAlign: "right" }}>calls</th>
              <th style={{ fontWeight: 400, padding: "4px 6px", textAlign: "right" }}>total</th>
              <th style={{ fontWeight: 400, padding: "4px 6px", textAlign: "right" }} title="The function's own JavaScript between Playwright calls">own code</th>
            </tr>
          </thead>
          <tbody>
            {data.summary.functions.map((f) => {
              const b = bodyOf(f.fn);
              return (
                <tr key={f.fn} onClick={() => b && onPick(b)} style={{ cursor: b ? "pointer" : undefined, borderTop: "1px solid var(--glass-stroke)" }}>
                  <td style={{ padding: "6px 6px" }}>{f.fn}</td>
                  <td style={{ padding: "6px 6px", textAlign: "right", fontFamily: "var(--f-mono)" }}>{f.calls}</td>
                  <td style={{ padding: "6px 6px", textAlign: "right", fontFamily: "var(--f-mono)" }}>{fmtMs(f.ms)}</td>
                  <td style={{ padding: "6px 6px", textAlign: "right", fontFamily: "var(--f-mono)", color: "var(--text-3)" }}>{fmtMs(f.betweenCallsMs)}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </div>
  );
}

// ── 4. a body, line by line ──────────────────────────────────────────

function Source({ data, body, line, onLine, onBody }: {
  data: TraceData; body: string; line: number | null; onLine: (l: number) => void; onBody: (b: string) => void;
}) {
  const b = data.trace.bodies[body];
  const stats = useMemo(() => {
    const m = new Map<number, { ms: number; calls: number; timedOut: number; category: string }>();
    for (const l of data.summary.lines) if (l.body === body) m.set(l.line, { ms: l.ms, calls: l.calls, timedOut: l.timedOut, category: l.category });
    return m;
  }, [data, body]);
  const calls = useMemo(() => {
    if (line == null) return [];
    const byId = new Map(data.trace.nodes.map((n) => [n.id, n]));
    return data.trace.nodes.filter((n) => {
      if (n.body !== body || n.line !== line || n.category === "function") return false;
      const p = n.parent != null ? byId.get(n.parent) : undefined;
      return !p || p.body !== n.body || p.line !== n.line;
    });
  }, [data, body, line]);
  if (!b) return null;
  const max = Math.max(1, ...Array.from(stats.values()).map((s) => s.ms));
  const versions = Object.entries(data.trace.bodies);

  return (
    <div className="mz-glass" style={{ padding: 16 }}>
      <div style={{ display: "flex", alignItems: "center", gap: 12, flexWrap: "wrap" }}>
        <div className="mz-eyebrow">SOURCE</div>
        <Select value={body} onChange={onBody} ariaLabel="Function" style={{ minWidth: 220 }}
          options={versions.map(([k, v]) => {
            // The same function can appear in several versions (a round trip fixes bodies as it goes).
            const label = v.fn + (versions.filter(([, x]) => x.fn === v.fn).length > 1 ? ` (version ${k.split("@")[1]})` : "");
            return { value: k, label, text: label };
          })} />
        <span style={{ fontSize: 12, color: "var(--text-3)" }}>Time per line, for the calls each line made. Click a line for its calls.</span>
      </div>
      <div style={{ marginTop: 10, fontFamily: "var(--f-mono)", fontSize: 12, overflowX: "auto" }}>
        {b.lines.map((text, i) => {
          const n = i + 1;
          const s = stats.get(n);
          const heat = s ? s.ms / max : 0;
          return (
            <div key={n} onClick={() => s && onLine(n)}
              style={{ display: "grid", gridTemplateColumns: "34px 86px 54px 1fr", alignItems: "center", cursor: s ? "pointer" : "default",
                background: line === n ? "var(--glass-3)" : s ? `rgba(234, 112, 40, ${0.06 + heat * 0.3})` : undefined, borderRadius: 3 }}>
              <span style={{ color: "var(--text-4)", textAlign: "right", paddingRight: 8 }}>{n}</span>
              <span style={{ textAlign: "right", paddingRight: 8, color: s?.timedOut ? "var(--err)" : "var(--text-1)" }}>{s ? fmtMs(s.ms) : ""}</span>
              <span style={{ color: "var(--text-3)", paddingRight: 8 }}>{s ? `×${s.calls}` : ""}</span>
              <span style={{ whiteSpace: "pre", color: s ? "var(--text-1)" : "var(--text-2)" }}>{text || " "}</span>
            </div>
          );
        })}
      </div>
      {line != null && calls.length > 0 && (
        <div style={{ marginTop: 12, borderTop: "1px solid var(--glass-stroke)", paddingTop: 10 }}>
          <div style={{ fontSize: 12, color: "var(--text-3)", marginBottom: 6 }}>Line {line}: {calls.length} call(s)</div>
          {calls.slice(0, 50).map((c) => (
            <div key={c.id} style={{ display: "grid", gridTemplateColumns: "90px 140px 1fr", gap: 8, fontSize: 12, padding: "3px 0" }}>
              <span style={{ fontFamily: "var(--f-mono)", textAlign: "right", color: c.timedOut ? "var(--err)" : undefined }}>{fmtMs(c.ms)}</span>
              <CatDot c={c.timedOut ? "timeout" : c.category} />
              <span style={{ fontFamily: "var(--f-mono)", fontSize: 11, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }} title={c.error ?? c.name}>
                {c.name}{c.error ? <span style={{ color: "var(--err)" }}> — {c.error}</span> : null}
              </span>
            </div>
          ))}
          {calls.length > 50 && <div style={{ fontSize: 12, color: "var(--text-3)" }}>… {calls.length - 50} more</div>}
        </div>
      )}
    </div>
  );
}

// ── 5. timeline ──────────────────────────────────────────────────────

function Timeline({ data }: { data: TraceData }) {
  const { roots, kids } = useMemo(() => {
    const ids = new Set(data.trace.nodes.map((n) => n.id));
    const kids = new Map<number, TraceNode[]>();
    const roots: TraceNode[] = [];
    for (const n of data.trace.nodes) {
      if (n.parent != null && ids.has(n.parent)) {
        const list = kids.get(n.parent) ?? [];
        list.push(n);
        kids.set(n.parent, list);
      } else roots.push(n);
    }
    for (const l of kids.values()) l.sort((a, b) => a.start - b.start || a.id - b.id);
    roots.sort((a, b) => a.start - b.start);
    return { roots, kids };
  }, [data]);

  // Open what matters: big nodes, a few levels down.
  const [open, setOpen] = useState<Set<number>>(() => {
    const s = new Set<number>();
    const limit = data.trace.ms * 0.08;
    const walk = (n: TraceNode, depth: number) => {
      if (depth > 3 || n.ms < limit) return;
      s.add(n.id);
      for (const k of kids.get(n.id) ?? []) walk(k, depth + 1);
    };
    roots.forEach((r) => walk(r, 0));
    return s;
  });
  const toggle = (id: number) => setOpen((s) => { const x = new Set(s); if (x.has(id)) x.delete(id); else x.add(id); return x; });

  const rows: { n: TraceNode; depth: number }[] = [];
  const push = (n: TraceNode, depth: number) => {
    rows.push({ n, depth });
    if (open.has(n.id)) for (const k of (kids.get(n.id) ?? []).slice(0, 300)) push(k, depth + 1);
  };
  roots.forEach((r) => push(r, 0));
  const t0 = data.trace.start;
  const span = Math.max(1, data.trace.ms);

  return (
    <div className="mz-glass" style={{ padding: 16 }}>
      <div style={{ display: "flex", alignItems: "baseline", gap: 12 }}>
        <div className="mz-eyebrow">TIMELINE</div>
        <span style={{ fontSize: 12, color: "var(--text-3)" }}>Click a row to open it. Bars share one time axis.</span>
      </div>
      <div style={{ marginTop: 10, fontSize: 12 }}>
        {rows.map(({ n, depth }) => {
          const has = (kids.get(n.id)?.length ?? 0) > 0;
          const c = n.timedOut ? "timeout" : n.category;
          return (
            <div key={n.id} onClick={() => has && toggle(n.id)}
              style={{ display: "grid", gridTemplateColumns: "minmax(0, 46%) 80px minmax(0, 1fr)", alignItems: "center", gap: 8, padding: "2px 0",
                cursor: has ? "pointer" : "default", borderTop: depth === 0 ? "1px solid var(--glass-stroke)" : undefined }}>
              <div style={{ paddingLeft: depth * 14, display: "flex", alignItems: "center", gap: 6, minWidth: 0 }}>
                <span style={{ width: 10, color: "var(--text-3)", flex: "none" }}>{has ? (open.has(n.id) ? "▾" : "▸") : ""}</span>
                <span style={{ width: 8, height: 8, borderRadius: 2, background: catOf(c).color, flex: "none" }} title={catOf(c).label} />
                <span style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap", color: n.timedOut ? "var(--err)" : undefined }}
                  title={(n.fn && n.line ? `${n.fn}:${n.line} · ` : "") + n.name + (n.error ? ` — ${n.error}` : "")}>
                  {n.name}{n.fn && n.line ? <span style={{ color: "var(--text-3)" }}> · {n.fn}:{n.line}</span> : null}
                  {n.llm ? (
                    <span style={{ color: "var(--text-3)", fontFamily: "var(--f-mono)" }}
                      title={`${fmtInt(n.llm.input)} input + ${fmtInt(n.llm.cacheRead)} cache read + ${fmtInt(n.llm.cacheWrite)} cache write + ${fmtInt(n.llm.output)} output · ${fmtUsdExact(n.llm.costUsd)} (${n.llm.costSource})`}>
                      {" · "}{fmtTok(n.llm.input + n.llm.cacheRead + n.llm.cacheWrite)} in / {fmtTok(n.llm.output)} out · {fmtUsd(n.llm.costUsd)}
                    </span>
                  ) : null}
                </span>
              </div>
              <span style={{ fontFamily: "var(--f-mono)", textAlign: "right" }}>{fmtMs(n.ms)}</span>
              <div style={{ position: "relative", height: 10, background: "var(--glass-1)", borderRadius: 2 }}>
                <div style={{ position: "absolute", left: `${((n.start - t0) / span) * 100}%`, width: `max(2px, ${(n.ms / span) * 100}%)`,
                  top: 0, bottom: 0, background: catOf(c).color, opacity: c === "network" ? 0.5 : 0.85, borderRadius: 2 }} />
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
