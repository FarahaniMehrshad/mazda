import { Link, useParams } from "react-router-dom";
import { ScreenShell, Pill } from "@/shared/components/Primitives";
import { TopBar } from "@/shared/components/TopBar";
import { fmtMs, useTrace, useTraces } from "./api";
import { TraceView } from "./TraceView";

const KIND: Record<string, string> = {
  "function-check": "function check",
  plan: "gather state + plan",
  execute: "run",
  roundtrip: "round trip",
  "roundtrip-plan": "round trip: rebuild the plan",
};

/** One trace, full page. A round trip still running saves its trace as it goes, so this refreshes. */
export function TracePage() {
  const { id } = useParams<{ id: string }>();
  const first = useTrace(id);
  // Open spans are saved as "… (running)": keep refreshing until the operation is done.
  const live = !!first.data?.trace.nodes.some((n) => n.name.endsWith("(running)"));
  const q = useTrace(id, live);
  const siblings = useTraces(q.data?.subjectId);

  return (
    <ScreenShell>
      <TopBar crumb="trace" />
      <div className="mz-scroll" style={{ flex: 1, overflow: "auto" }}>
        <div style={{ padding: "32px 40px 80px", maxWidth: 1280, margin: "0 auto" }}>
          <div className="mz-eyebrow">TRACE · {KIND[q.data?.kind ?? ""] ?? q.data?.kind ?? ""}</div>
          <div style={{ fontSize: 22, fontWeight: 500, marginTop: 6 }}>{q.data?.label ?? "…"}</div>
          {q.data && (
            <div style={{ fontSize: 13, color: "var(--text-3)", marginTop: 6 }}>
              {new Date(q.data.createdAt).toLocaleString()} · {fmtMs(q.data.summary.totalMs)}
              {live && <> · <Pill tone="warn">still running</Pill></>}
            </div>
          )}
          {(siblings.data?.length ?? 0) > 1 && (
            <div style={{ display: "flex", gap: 8, flexWrap: "wrap", marginTop: 12 }}>
              {siblings.data!.slice(0, 12).map((t) => (
                <Link key={t.id} to={`/traces/${t.id}`}
                  style={{ fontSize: 12, padding: "3px 10px", borderRadius: 999, textDecoration: "none",
                    border: "1px solid var(--glass-stroke-2)", color: t.id === id ? "var(--text-1)" : "var(--text-3)",
                    background: t.id === id ? "var(--glass-3)" : undefined }}>
                  {KIND[t.kind] ?? t.kind} · {fmtMs(t.totalMs)} · {new Date(t.createdAt).toLocaleTimeString()}
                </Link>
              ))}
            </div>
          )}
          <div style={{ marginTop: 20 }}>
            {q.isLoading && <div style={{ fontSize: 13, color: "var(--text-3)" }}>Loading…</div>}
            {q.error && <Pill tone="err">Could not load the trace.</Pill>}
            {q.data && <TraceView data={q.data} />}
          </div>
        </div>
      </div>
    </ScreenShell>
  );
}

/** Links to the traces about a subject (a function, a generation, a round trip), newest first. */
export function TraceLinks({ subject, refreshKey, max = 4 }: { subject: string | undefined; refreshKey?: unknown; max?: number }) {
  const list = useTraces(subject, refreshKey);
  if (!list.data || list.data.length === 0) return null;
  return (
    <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
      <span style={{ fontSize: 12, color: "var(--text-3)" }}>Where the time went:</span>
      {list.data.slice(0, max).map((t) => (
        <Link key={t.id} to={`/traces/${t.id}`}
          style={{ fontSize: 12, padding: "3px 10px", borderRadius: 999, textDecoration: "none", border: "1px solid var(--glass-stroke-2)", color: "var(--text-2)" }}>
          {KIND[t.kind] ?? t.kind} · {fmtMs(t.totalMs)}
        </Link>
      ))}
    </div>
  );
}
