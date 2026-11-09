import { useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { ScreenShell, BtnPrimary, BtnGhost, Pill } from "@/shared/components/Primitives";
import { TopBar } from "@/shared/components/TopBar";
import { ApiError } from "@/shared/api/client";
import type { ProposedElementDto, RecordingDto, RecordingProposalDto } from "@/shared/api/types";
import { useSignature } from "@/features/signatures/api";
import { useCdpStatus } from "@/features/cdp/cdpApi";
import {
  useAnalyzeAllRecordings,
  useAnalyzeRecording,
  useApplyRecording,
  useDeleteRecording,
  useRecordingEpisodes,
  useRecordings,
  useStartRecording,
  useStopRecording,
  useUpdateRecording,
} from "./api";

/**
 * "Learn by watching".
 *
 * For components whose source MAZDA can't analyze (third-party libraries,
 * closed bundles), the user demonstrates the UI in their attached Chrome tab
 * while MAZDA records input, DOM mutations, network and navigation. A
 * completed recording is analyzed into proposed elements + flows, reviewed
 * here, and merged into the signature. Its episodes also ground the
 * function-catalogue generator.
 */
export function LearnPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const sig = useSignature(id);
  const cdp = useCdpStatus();
  const recordings = useRecordings(id);
  const start = useStartRecording(id);
  const stop = useStopRecording(id);

  const [intent, setIntent] = useState("");
  const [originsText, setOriginsText] = useState("");
  const [captureBodies, setCaptureBodies] = useState(true);
  const [maskAll, setMaskAll] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [reviewing, setReviewing] = useState<{ label: string; proposal: RecordingProposalDto; fresh: boolean } | null>(null);
  const [applied, setApplied] = useState<string | null>(null);

  const list = recordings.data ?? [];
  const active = list.find((r) => r.status === "Recording" || r.status === "Stopping") ?? null;

  // Pre-fill the origin allowlist from the attached tab.
  const attachedOrigin = useMemo(() => {
    try {
      return cdp.data?.attachedUrl ? new URL(cdp.data.attachedUrl).origin : "";
    } catch {
      return "";
    }
  }, [cdp.data?.attachedUrl]);
  useEffect(() => {
    if (!originsText && attachedOrigin.startsWith("http")) setOriginsText(attachedOrigin);
  }, [attachedOrigin]); // eslint-disable-line react-hooks/exhaustive-deps

  const attached = !!cdp.data?.attachedTargetId && !cdp.data?.attachedTabMissing;

  const onStart = async () => {
    setErr(null);
    setApplied(null);
    try {
      await start.mutateAsync({
        intent: intent.trim() || undefined,
        allowedOrigins: originsText
          .split(/[\s,]+/)
          .map((s) => s.trim())
          .filter(Boolean),
        captureBodies,
        maskAllInputs: maskAll,
      });
      setIntent("");
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : String(e));
    }
  };

  const onStop = async () => {
    if (!active) return;
    setErr(null);
    try {
      await stop.mutateAsync(active.id);
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : String(e));
    }
  };

  return (
    <ScreenShell>
      <TopBar crumb={`${sig.data?.name ?? "…"} · learn`} />
      <div className="mz-scroll" style={{ flex: 1, overflow: "auto" }}>
        <div style={{ padding: "32px 40px 80px", maxWidth: 1280, margin: "0 auto" }}>
          <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-end", gap: 24 }}>
            <div>
              <div className="mz-eyebrow">LEARN BY WATCHING</div>
              <div style={{ fontSize: 22, fontWeight: 500, marginTop: 6 }}>
                {sig.data?.name ?? "…"} — show MAZDA how it works
              </div>
              <div style={{ fontSize: 13, color: "var(--text-3)", marginTop: 6, maxWidth: 760, lineHeight: 1.5 }}>
                Start a recording, then use the component in your attached Chrome tab the way a user would: open
                dropdowns, fill fields, submit, drag. MAZDA records what you did and how the page reacted, then
                proposes elements and flows from what it saw. Use this for third-party components whose source
                MAZDA can't read.
              </div>
            </div>
            <div style={{ display: "flex", gap: 10 }}>
              <BtnGhost onClick={() => navigate(`/signatures/${id}`)}>Elements</BtnGhost>
              <BtnGhost onClick={() => navigate(`/signatures/${id}/functions`)}>Functions →</BtnGhost>
            </div>
          </div>

          {err && (
            <div style={{ marginTop: 16 }}>
              <Pill tone="err">{err}</Pill>
            </div>
          )}
          {applied && (
            <div className="mz-glass" style={{ marginTop: 16, padding: 14, fontSize: 13, display: "flex", gap: 12, alignItems: "center" }}>
              <Pill tone="ok">applied</Pill>
              <span style={{ flex: 1 }}>{applied}</span>
              <BtnPrimary onClick={() => navigate(`/signatures/${id}/functions`)}>Go to functions</BtnPrimary>
            </div>
          )}

          <div style={{ display: "grid", gridTemplateColumns: "340px 1fr", gap: 24, marginTop: 24, alignItems: "start" }}>
            {/* ── Control ── */}
            <div className="mz-glass" style={{ padding: 18, display: "flex", flexDirection: "column", gap: 14 }}>
              <div className="mz-eyebrow">BROWSER</div>
              {cdp.data?.available === false ? (
                <Pill tone="err">Chrome not reachable: {cdp.data.reason}</Pill>
              ) : attached ? (
                <div style={{ fontSize: 12, lineHeight: 1.5 }}>
                  <div style={{ color: "var(--text-2)" }}>{cdp.data?.attachedTitle || "Attached tab"}</div>
                  <div style={{ fontFamily: "var(--f-mono)", fontSize: 11, color: "var(--text-4)", wordBreak: "break-all" }}>
                    {cdp.data?.attachedUrl}
                  </div>
                </div>
              ) : (
                <Pill tone="warn">Attach a tab from the browser chip in the top bar first.</Pill>
              )}

              {!active ? (
                <>
                  <Field
                    label="What are you demonstrating? (optional)"
                    hint="Your goal in your own words. MAZDA names the recording itself after analyzing it and tells you what it understood."
                  >
                    <textarea
                      value={intent}
                      onChange={(e) => setIntent(e.target.value)}
                      placeholder={'e.g. "archive a task — not delete it"'}
                      rows={3}
                      style={{ ...inputStyle, fontFamily: "var(--f-sans)", resize: "vertical" }}
                    />
                  </Field>
                  <Field label="Allowed origins" hint="Only these origins are recorded (space or comma separated).">
                    <input value={originsText} onChange={(e) => setOriginsText(e.target.value)} style={inputStyle} />
                  </Field>
                  <Toggle checked={captureBodies} onChange={setCaptureBodies}>
                    Capture request/response bodies (truncated, secrets redacted)
                  </Toggle>
                  <Toggle checked={maskAll} onChange={setMaskAll}>
                    Mask every typed value, including rich-text editors and request bodies (passwords are always masked)
                  </Toggle>
                  <BtnPrimary onClick={onStart} disabled={!attached || start.isPending}>
                    {start.isPending ? "Attaching…" : "● Start recording"}
                  </BtnPrimary>
                  <div style={{ fontSize: 11, color: "var(--text-4)", lineHeight: 1.5 }}>
                    Don't run tests or functions against this tab while recording — their clicks would be recorded too.
                  </div>
                </>
              ) : (
                <ActiveControl rec={active} onStop={onStop} stopping={stop.isPending || active.status === "Stopping"} />
              )}
            </div>

            {/* ── Live feed / episodes ── */}
            <div style={{ display: "flex", flexDirection: "column", gap: 24, minWidth: 0 }}>
              {active && (
                <RecordingFeed
                  key={active.id}
                  recordingId={active.id}
                  onEnd={() => {
                    // Give the host a beat to persist the terminal state.
                    setTimeout(() => recordings.refetch(), 300);
                  }}
                />
              )}

              <RecordingList
                sigId={id!}
                list={list}
                loading={recordings.isLoading}
                onReview={(label, proposal, fresh) => setReviewing({ label, proposal, fresh })}
                onError={setErr}
              />
            </div>
          </div>
        </div>
      </div>

      {reviewing && (
        <ProposalReview
          sigId={id!}
          label={reviewing.label}
          fresh={reviewing.fresh}
          proposal={reviewing.proposal}
          onClose={() => setReviewing(null)}
          onApplied={(msg) => {
            setReviewing(null);
            setApplied(msg);
          }}
        />
      )}
    </ScreenShell>
  );
}

// ── Active session control ─────────────────────────────────────────

function ActiveControl({ rec, onStop, stopping }: { rec: RecordingDto; onStop: () => void; stopping: boolean }) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, []);
  const secs = Math.max(0, Math.floor((now - new Date(rec.startedAt).getTime()) / 1000));
  const mm = String(Math.floor(secs / 60)).padStart(2, "0");
  const ss = String(secs % 60).padStart(2, "0");
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
      <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
        <span
          style={{
            width: 10,
            height: 10,
            borderRadius: 99,
            background: stopping ? "var(--text-4)" : "#e85c4a",
            boxShadow: stopping ? "none" : "0 0 10px #e85c4a",
          }}
        />
        <span style={{ fontFamily: "var(--f-mono)", fontSize: 20 }}>
          {mm}:{ss}
        </span>
        <Pill tone={stopping ? "neutral" : "err"}>{stopping ? "stopping…" : "recording"}</Pill>
      </div>
      {rec.intent && <div style={{ fontSize: 12, color: "var(--text-3)", whiteSpace: "pre-wrap" }}>{rec.intent}</div>}
      <div style={{ fontSize: 11, color: "var(--text-4)", fontFamily: "var(--f-mono)" }}>
        {rec.allowedOrigins.join(", ")}
      </div>
      <BtnPrimary onClick={onStop} disabled={stopping}>
        {stopping ? "Finishing…" : "■ Stop recording"}
      </BtnPrimary>
    </div>
  );
}

// ── Live feed ──────────────────────────────────────────────────────

interface RecEvent {
  seq: number;
  t: number;
  kind: string;
  actionId: string | null;
  data: any; // eslint-disable-line @typescript-eslint/no-explicit-any
  url?: string;
}

interface FeedRow {
  key: string;
  t: number;
  kind: "action" | "nav" | "warn" | "info";
  title: string;
  value?: string;
  masked?: boolean;
  effects: string[];
  network: string[];
}

function parseRecEvent(line: string): RecEvent | null {
  const idx = line.indexOf("__MZ_REC__ ");
  if (idx < 0) return null;
  try {
    return JSON.parse(line.slice(idx + "__MZ_REC__ ".length)) as RecEvent;
  } catch {
    return null;
  }
}

function targetLabel(t: any): string { // eslint-disable-line @typescript-eslint/no-explicit-any
  if (!t) return "page";
  const who = t.role ?? t.tag ?? "element";
  if (t.name) return `${who} "${t.name}"`;
  if (t.text) return `${who} "${t.text}"`;
  const best = (t.candidates ?? []).find((c: any) => c.count === 1)?.selector ?? t.candidates?.[0]?.selector; // eslint-disable-line @typescript-eslint/no-explicit-any
  return best ? `${who} ${best}` : who;
}

function summarizeEffects(d: any): string[] { // eslint-disable-line @typescript-eslint/no-explicit-any
  const out: string[] = [];
  for (const a of d?.appeared ?? []) out.push(`+ ${targetLabel(a)}${a.floating ? " (floating)" : ""}`);
  if (d?.disappeared?.length) out.push(`− ${d.disappeared.length} removed`);
  for (const c of d?.changed ?? []) {
    if (c.attr === "class") {
      const parts = [...(c.added ?? []).map((x: string) => `+.${x}`), ...(c.removed ?? []).map((x: string) => `−.${x}`)];
      out.push(`~ class ${parts.join(" ")}`);
    } else out.push(`~ ${c.attr} ${c.from ?? "∅"} → ${c.to ?? "∅"}`);
  }
  for (const t of d?.text ?? []) out.push(`✎ "${t.from}" → "${t.to}"`);
  return out;
}

function pathOf(u: string): string {
  try {
    const x = new URL(u);
    return x.pathname + x.search;
  } catch {
    return u;
  }
}

function buildRows(events: RecEvent[]): FeedRow[] {
  const rows: FeedRow[] = [];
  const byAction = new Map<string, FeedRow>();
  const get = (id: string, t: number) => {
    let r = byAction.get(id);
    if (!r) {
      r = { key: id, t, kind: "action", title: "…", effects: [], network: [] };
      byAction.set(id, r);
      rows.push(r);
    }
    return r;
  };
  for (const e of events) {
    const d = e.data ?? {};
    switch (e.kind) {
      case "ready":
        rows.push({ key: `s${e.seq}`, t: e.t, kind: "info", title: `attached to ${pathOf(d.url ?? "")}`, effects: [], network: [] });
        break;
      case "action": {
        const r = get(e.actionId ?? `s${e.seq}`, e.t);
        const verb = d.type === "key" ? `press ${d.key}` : d.type === "hover" ? "hovered" : d.type === "unhover" ? "left" : d.type;
        r.title = d.type === "drag" ? `drag ${targetLabel(d.target)} → ${targetLabel(d.dest)}` : `${verb} ${targetLabel(d.target)}`;
        if (d.target?.revealedBy?.on === "hover") r.title += ` (needs hover on ${d.target.revealedBy.host})`;
        if (d.value !== undefined && d.value !== null) r.value = String(d.value);
        if (d.label) r.value = String(d.label);
        r.masked = !!d.masked;
        break;
      }
      case "mutations":
        if (e.actionId) get(e.actionId, e.t).effects.push(...summarizeEffects(d));
        break;
      case "network": {
        if (d.pending) {
          // Stuck request: always surface it, attributed to an action or not —
          // a page that "hangs" while recording is usually this.
          const secs = Math.round((d.ms ?? 0) / 1000);
          rows.push({
            key: `s${e.seq}`,
            t: e.t,
            kind: "warn",
            title: d.neverCompleted
              ? `⏳ ${d.method} ${pathOf(d.url ?? "")} never completed during the recording (${secs}s)`
              : `⏳ ${d.method} ${pathOf(d.url ?? "")} — no response after ${secs}s. The server or network may be down.`,
            effects: [],
            network: [],
          });
          break;
        }
        const line = `${d.method} ${pathOf(d.url ?? "")} ${d.status === 0 ? `✗ ${d.error ?? ""}` : d.status ?? ""} · ${d.ms}ms`;
        if (e.actionId) get(e.actionId, e.t).network.push(line);
        break;
      }
      case "nav":
        if (d.paused) rows.push({ key: `s${e.seq}`, t: e.t, kind: "warn", title: `left allowed origins (${d.origin}) — paused`, effects: [], network: [] });
        else if (e.actionId) get(e.actionId, e.t).effects.push(`→ ${pathOf(d.url)}`);
        else rows.push({ key: `s${e.seq}`, t: e.t, kind: "nav", title: `navigated to ${pathOf(d.url)}`, effects: [], network: [] });
        break;
      case "warn":
      case "limit":
        rows.push({ key: `s${e.seq}`, t: e.t, kind: "warn", title: d.msg ?? d.reason ?? e.kind, effects: [], network: [] });
        break;
      case "end":
        rows.push({ key: `s${e.seq}`, t: e.t, kind: "info", title: `ended (${d.reason})`, effects: [], network: [] });
        break;
    }
  }
  return rows;
}

function RecordingFeed({ recordingId, onEnd }: { recordingId: string; onEnd: () => void }) {
  const [events, setEvents] = useState<RecEvent[]>([]);
  const [errors, setErrors] = useState<string[]>([]);
  const bottomRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    // EventSource can't send Authorization; the stream is anonymous and keyed
    // by the unguessable recording id, same as run execution streams.
    const es = new EventSource(`/api/recordings/${recordingId}/stream`);
    // On a dropped connection EventSource reconnects and the server replays
    // the whole backlog; seq numbers are strictly increasing, so anything at
    // or below the last one seen is a replay.
    let lastSeq = -1;
    es.onmessage = (m) => {
      let line: string;
      try {
        line = JSON.parse(m.data);
      } catch {
        line = m.data;
      }
      const evt = parseRecEvent(line);
      if (evt) {
        if (evt.seq <= lastSeq) return;
        lastSeq = evt.seq;
        setEvents((xs) => [...xs, evt]);
      } else if (line.trim()) setErrors((xs) => (xs.includes(line) ? xs : [...xs.slice(-20), line]));
    };
    es.addEventListener("end", () => {
      es.close();
      onEnd();
    });
    es.onerror = () => {
      /* transient; the list refetch resyncs */
    };
    return () => es.close();
  }, [recordingId]); // eslint-disable-line react-hooks/exhaustive-deps

  const rows = useMemo(() => buildRows(events), [events]);
  const actions = events.filter((e) => e.kind === "action").length;
  const requests = events.filter((e) => e.kind === "network").length;

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ block: "nearest" });
  }, [rows.length]);

  return (
    <div className="mz-glass" style={{ padding: 0, overflow: "hidden" }}>
      <div
        style={{
          padding: "12px 16px",
          borderBottom: "1px solid var(--glass-stroke)",
          display: "flex",
          gap: 10,
          alignItems: "center",
        }}
      >
        <div className="mz-eyebrow" style={{ flex: 1 }}>LIVE</div>
        <Pill>{actions} actions</Pill>
        <Pill>{requests} requests</Pill>
        <Pill>{events.length} events</Pill>
      </div>
      <div className="mz-scroll" style={{ maxHeight: 460, overflow: "auto", padding: "6px 0" }}>
        {rows.length === 0 && (
          <div style={{ padding: 18, fontSize: 12, color: "var(--text-4)" }}>
            Attaching to the tab… once it says "attached", interact with the page.
          </div>
        )}
        {rows.map((r) => (
          <div key={r.key} style={{ padding: "8px 16px", borderBottom: "1px solid var(--glass-stroke)" }}>
            <div style={{ display: "flex", gap: 10, alignItems: "baseline" }}>
              <span style={{ fontFamily: "var(--f-mono)", fontSize: 10, color: "var(--text-4)", width: 48 }}>
                {(r.t / 1000).toFixed(1)}s
              </span>
              <span
                style={{
                  fontSize: 12.5,
                  color: r.kind === "warn" ? "#f1d18a" : r.kind === "action" ? "var(--text-1)" : "var(--text-3)",
                }}
              >
                {r.title}
              </span>
              {r.value !== undefined && (
                <span style={{ fontFamily: "var(--f-mono)", fontSize: 11.5, color: "var(--orange-100)" }}>
                  {r.masked ? "🔒 masked" : `"${r.value}"`}
                </span>
              )}
            </div>
            {(r.effects.length > 0 || r.network.length > 0) && (
              <div style={{ marginLeft: 58, marginTop: 4, display: "flex", flexDirection: "column", gap: 2 }}>
                {r.effects.slice(0, 6).map((x, i) => (
                  <span key={i} style={{ fontFamily: "var(--f-mono)", fontSize: 11, color: "var(--text-3)" }}>
                    {x}
                  </span>
                ))}
                {r.effects.length > 6 && (
                  <span style={{ fontSize: 11, color: "var(--text-4)" }}>+{r.effects.length - 6} more</span>
                )}
                {r.network.map((x, i) => (
                  <span key={`n${i}`} style={{ fontFamily: "var(--f-mono)", fontSize: 11, color: "#a8cce6" }}>
                    ⇄ {x}
                  </span>
                ))}
              </div>
            )}
          </div>
        ))}
        <div ref={bottomRef} />
      </div>
      {errors.length > 0 && (
        <details style={{ padding: "8px 16px", borderTop: "1px solid var(--glass-stroke)" }}>
          <summary style={{ cursor: "pointer", fontSize: 11, color: "var(--err)" }}>recorder log ({errors.length})</summary>
          <pre style={{ fontSize: 11, whiteSpace: "pre-wrap", color: "var(--text-3)" }}>{errors.join("\n")}</pre>
        </details>
      )}
    </div>
  );
}

// ── Recordings list ────────────────────────────────────────────────

function RecordingList({
  sigId,
  list,
  loading,
  onReview,
  onError,
}: {
  sigId: string;
  list: RecordingDto[];
  loading: boolean;
  onReview: (label: string, proposal: RecordingProposalDto, fresh: boolean) => void;
  onError: (msg: string) => void;
}) {
  const analyze = useAnalyzeRecording(sigId);
  const analyzeAll = useAnalyzeAllRecordings(sigId);
  const [fresh, setFresh] = useState(true);
  const completed = list.filter((r) => r.status === "Completed" && r.actionCount > 0);

  const onAnalyzeAll = async () => {
    setAnalyzingId("all");
    try {
      const proposal = await analyzeAll.mutateAsync({ fresh });
      onReview(`${proposal.sessionIds.length} recordings`, proposal, fresh);
    } catch (e) {
      onError(e instanceof ApiError ? e.message : String(e));
    } finally {
      setAnalyzingId(null);
    }
  };
  const update = useUpdateRecording(sigId);
  const remove = useDeleteRecording(sigId);
  const [analyzingId, setAnalyzingId] = useState<string | null>(null);
  const [viewing, setViewing] = useState<string | null>(null);

  const onAnalyze = async (rec: RecordingDto) => {
    setAnalyzingId(rec.id);
    try {
      const proposal = await analyze.mutateAsync(rec.id);
      onReview(rec.label, proposal, false);
    } catch (e) {
      onError(e instanceof ApiError ? e.message : String(e));
    } finally {
      setAnalyzingId(null);
    }
  };

  const onDelete = async (rec: RecordingDto) => {
    if (!confirm(`Delete recording "${rec.label}"? Its events are deleted too.`)) return;
    try {
      await remove.mutateAsync(rec.id);
    } catch (e) {
      onError(e instanceof ApiError ? e.message : String(e));
    }
  };

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
      <div style={{ display: "flex", alignItems: "center", gap: 12, flexWrap: "wrap" }}>
        <div className="mz-eyebrow" style={{ flex: 1 }}>RECORDINGS · {list.length}</div>
        {completed.length > 1 && (
          <>
            <Toggle checked={fresh} onChange={setFresh}>
              Rebuild learned elements from scratch
            </Toggle>
            <BtnPrimary onClick={onAnalyzeAll} disabled={analyzingId !== null}>
              {analyzingId === "all" ? "Analyzing all…" : `Analyze all ${completed.length} together`}
            </BtnPrimary>
          </>
        )}
      </div>
      {loading && <div style={{ color: "var(--text-3)", fontSize: 12 }}>Loading…</div>}
      {!loading && list.length === 0 && (
        <div
          style={{
            padding: 20,
            border: "1px dashed var(--glass-stroke-2)",
            borderRadius: 12,
            fontSize: 12.5,
            color: "var(--text-3)",
          }}
        >
          No recordings yet. Start one and demonstrate a task or two — a few minutes is plenty.
        </div>
      )}
      {list.map((r) => (
        <div key={r.id} className="mz-glass" style={{ padding: 14 }}>
          <div style={{ display: "flex", gap: 10, alignItems: "center", flexWrap: "wrap" }}>
            <span style={{ fontSize: 13.5, flex: 1, minWidth: 200 }} title={r.labelIsAuto ? "Named by MAZDA" : undefined}>
              {r.label}
            </span>
            <Pill tone={r.status === "Completed" ? "ok" : r.status === "Failed" ? "err" : "warn"}>{r.status}</Pill>
            <Pill>{r.actionCount} actions</Pill>
            <Pill>{duration(r)}</Pill>
            {r.appliedAt && <Pill tone="orange">applied</Pill>}
          </div>
          {r.error && (
            <div style={{ marginTop: 8, fontSize: 11.5, color: r.status === "Failed" ? "#f0a89e" : "var(--text-3)", whiteSpace: "pre-wrap" }}>
              {r.error}
            </div>
          )}
          {r.status === "Completed" && (
            <IntentAndUnderstanding
              rec={r}
              saving={update.isPending}
              onSave={(text) => update.mutateAsync({ id: r.id, body: { intent: text } })}
            />
          )}
          {r.status === "Completed" && (
            <div style={{ marginTop: 12, display: "flex", gap: 10, alignItems: "center", flexWrap: "wrap" }}>
              <BtnPrimary onClick={() => onAnalyze(r)} disabled={analyzingId !== null || r.actionCount === 0}>
                {analyzingId === r.id ? "Analyzing…" : r.hasProposal ? "Re-analyze" : "Analyze"}
              </BtnPrimary>
              <BtnGhost onClick={() => setViewing(viewing === r.id ? null : r.id)}>
                {viewing === r.id ? "Hide episodes" : "Episodes"}
              </BtnGhost>
              <Toggle
                checked={r.useForGrounding}
                onChange={(v) => update.mutate({ id: r.id, body: { useForGrounding: v } })}
              >
                Use when generating functions
              </Toggle>
              <span style={{ flex: 1 }} />
              <BtnGhost onClick={() => onDelete(r)}>Delete</BtnGhost>
            </div>
          )}
          {r.status === "Failed" && (
            <div style={{ marginTop: 10, display: "flex", justifyContent: "flex-end" }}>
              <BtnGhost onClick={() => onDelete(r)}>Delete</BtnGhost>
            </div>
          )}
          {viewing === r.id && <EpisodesView id={r.id} />}
        </div>
      ))}
    </div>
  );
}

/**
 * The tuning loop: what the user meant (intent, editable) next to what the
 * analyzer understood. If they don't match, the user rewords the intent and
 * re-analyzes.
 */
function IntentAndUnderstanding({
  rec,
  saving,
  onSave,
}: {
  rec: RecordingDto;
  saving: boolean;
  onSave: (intent: string) => Promise<unknown>;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(rec.intent);
  useEffect(() => {
    if (!editing) setDraft(rec.intent);
  }, [rec.intent, editing]);

  return (
    <div style={{ marginTop: 10, display: "grid", gridTemplateColumns: "1fr 1fr", gap: 10 }}>
      <div style={{ padding: 10, borderRadius: 8, background: "var(--glass-1)", border: "1px solid var(--glass-stroke)" }}>
        <div style={{ display: "flex", alignItems: "center", gap: 8, marginBottom: 6 }}>
          <span className="mz-eyebrow" style={{ flex: 1 }}>YOUR INTENT</span>
          {!editing && (
            <button type="button" onClick={() => setEditing(true)} style={linkBtn}>
              {rec.intent ? "Edit" : "Add"}
            </button>
          )}
        </div>
        {editing ? (
          <>
            <textarea
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              rows={3}
              autoFocus
              placeholder="What were you demonstrating?"
              style={{ ...inputStyle, fontFamily: "var(--f-sans)", resize: "vertical" }}
            />
            <div style={{ display: "flex", gap: 8, justifyContent: "flex-end", marginTop: 6 }}>
              <button type="button" onClick={() => setEditing(false)} style={linkBtn}>Cancel</button>
              <button
                type="button"
                disabled={saving}
                onClick={async () => {
                  await onSave(draft.trim());
                  setEditing(false);
                }}
                style={{ ...linkBtn, color: "var(--orange-100)" }}
              >
                {saving ? "Saving…" : "Save"}
              </button>
            </div>
          </>
        ) : (
          <div style={{ fontSize: 12.5, color: rec.intent ? "var(--text-2)" : "var(--text-4)", whiteSpace: "pre-wrap" }}>
            {rec.intent || "Not given — MAZDA works from the recording alone."}
          </div>
        )}
      </div>
      <div style={{ padding: 10, borderRadius: 8, background: "rgba(106,168,209,0.06)", border: "1px solid rgba(106,168,209,0.25)" }}>
        <div className="mz-eyebrow" style={{ marginBottom: 6 }}>MAZDA UNDERSTOOD</div>
        <div style={{ fontSize: 12.5, color: rec.understanding ? "var(--text-2)" : "var(--text-4)", whiteSpace: "pre-wrap" }}>
          {rec.understanding || "Analyze to see what MAZDA understood from this recording."}
        </div>
        {rec.understanding && editing === false && (
          <div style={{ fontSize: 11, color: "var(--text-4)", marginTop: 6 }}>
            Not what you meant? Refine your intent and re-analyze.
          </div>
        )}
      </div>
    </div>
  );
}

const linkBtn: CSSProperties = {
  background: "none",
  border: "none",
  padding: 0,
  fontSize: 11.5,
  color: "var(--text-3)",
  cursor: "pointer",
};

function duration(r: RecordingDto): string {
  const end = r.endedAt ? new Date(r.endedAt).getTime() : Date.now();
  const s = Math.max(0, Math.round((end - new Date(r.startedAt).getTime()) / 1000));
  return s >= 60 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${s}s`;
}

function EpisodesView({ id }: { id: string }) {
  const q = useRecordingEpisodes(id);
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const doc = useMemo<any>(() => {
    try {
      return q.data?.episodesJson ? JSON.parse(q.data.episodesJson) : null;
    } catch {
      return null;
    }
  }, [q.data]);
  if (q.isLoading) return <div style={{ marginTop: 10, fontSize: 12, color: "var(--text-3)" }}>Loading…</div>;
  if (!doc) return <div style={{ marginTop: 10, fontSize: 12, color: "var(--text-3)" }}>No episodes.</div>;
  return (
    <div style={{ marginTop: 12, display: "flex", flexDirection: "column", gap: 4 }}>
      {(doc.episodes ?? []).map((ep: any) => ( // eslint-disable-line @typescript-eslint/no-explicit-any
        <div key={ep.i} style={{ fontFamily: "var(--f-mono)", fontSize: 11.5, color: "var(--text-2)" }}>
          <span style={{ color: "var(--text-4)" }}>#{ep.i} </span>
          {ep.action} {targetLabel(ep.target)}
          {ep.value !== undefined && <span style={{ color: "var(--orange-100)" }}> "{ep.value}"</span>}
          {ep.count > 1 && <span style={{ color: "var(--text-4)" }}> ×{ep.count}</span>}
          {ep.revertsOnLeave && <span style={{ color: "var(--text-4)" }}> · while hovering</span>}
          {ep.target?.revealedBy?.on === "hover" && (
            <span style={{ color: "var(--orange-100)" }}> · needs hover on {ep.target.revealedBy.hostCollection ?? ep.target.revealedBy.host}</span>
          )}
          {ep.effects?.appeared?.length > 0 && <span style={{ color: "var(--text-3)" }}> · +{ep.effects.appeared.length} appeared</span>}
          {ep.network?.length > 0 && (
            <span style={{ color: "#a8cce6" }}> · {ep.network.map((n: any) => `${n.method} ${n.path}`).join(", ")}</span> // eslint-disable-line @typescript-eslint/no-explicit-any
          )}
          {ep.urlAfter && <span style={{ color: "var(--text-3)" }}> → {ep.urlAfter}</span>}
        </div>
      ))}
      <details style={{ marginTop: 8 }}>
        <summary style={{ cursor: "pointer", fontSize: 11, color: "var(--text-3)" }}>raw episodes JSON</summary>
        <pre
          className="mz-scroll"
          style={{ fontSize: 11, maxHeight: 320, overflow: "auto", whiteSpace: "pre-wrap", color: "var(--text-3)" }}
        >
          {JSON.stringify(doc, null, 2)}
        </pre>
      </details>
    </div>
  );
}

// ── Proposal review ────────────────────────────────────────────────

function ProposalReview({
  sigId,
  label,
  fresh,
  proposal,
  onClose,
  onApplied,
}: {
  sigId: string;
  label: string;
  fresh: boolean;
  proposal: RecordingProposalDto;
  onClose: () => void;
  onApplied: (msg: string) => void;
}) {
  const apply = useApplyRecording(sigId);
  const [accepted, setAccepted] = useState<Set<string>>(() => new Set(proposal.elements.map((e) => e.selector)));
  const [enrich, setEnrich] = useState(true);
  const [applyFlows, setApplyFlows] = useState(true);
  const [replaceLearned, setReplaceLearned] = useState(fresh);
  const [err, setErr] = useState<string | null>(null);

  const flowCounts = useMemo(() => {
    try {
      const g = JSON.parse(proposal.flowsJson || "{}");
      return { nodes: g.nodes?.length ?? 0, edges: g.edges?.length ?? 0 };
    } catch {
      return { nodes: 0, edges: 0 };
    }
  }, [proposal.flowsJson]);

  const newOnes = proposal.elements.filter((e) => e.isNew);
  const known = proposal.elements.filter((e) => !e.isNew);

  const toggle = (sel: string) =>
    setAccepted((s) => {
      const n = new Set(s);
      if (n.has(sel)) n.delete(sel);
      else n.add(sel);
      return n;
    });

  const onApply = async () => {
    setErr(null);
    try {
      const res = await apply.mutateAsync({
        id: proposal.recordingId,
        body: { acceptedSelectors: [...accepted], enrichExisting: enrich, applyFlows, replaceLearned: fresh && replaceLearned },
      });
      const parts = [
        res.removedElements ? `${res.removedElements} previously learned element(s) replaced` : null,
        `${res.addedElements} element(s) added`,
        res.enrichedElements ? `${res.enrichedElements} enriched` : null,
        applyFlows ? `${res.addedNodes} flow states / ${res.addedEdges} transitions added` : null,
      ].filter(Boolean);
      onApplied(
        parts.join(", ") +
          (res.functionsStale
            ? ". The function catalogue predates this — regenerate it so functions use what was learned."
            : ". Generate the function catalogue next — recordings are used as grounding."),
      );
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : String(e));
    }
  };

  return (
    <div
      style={{
        position: "fixed",
        inset: 0,
        background: "rgba(0,0,0,0.6)",
        display: "flex",
        justifyContent: "center",
        alignItems: "flex-start",
        padding: "48px 16px",
        zIndex: 50,
        overflow: "auto",
      }}
      onClick={onClose}
    >
      <div
        className="mz-glass-2"
        style={{ width: "min(980px, 100%)", padding: 24, display: "flex", flexDirection: "column", gap: 16 }}
        onClick={(e) => e.stopPropagation()}
      >
        <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
          <div style={{ flex: 1 }}>
            <div className="mz-eyebrow">PROPOSAL · {label}</div>
            <div style={{ fontSize: 18, marginTop: 4 }}>What MAZDA learned</div>
          </div>
          <Pill>{proposal.tokensUsed} tokens</Pill>
          <BtnGhost onClick={onClose}>Close</BtnGhost>
        </div>
        {proposal.notes && (
          <div style={{ fontSize: 12.5, color: "var(--text-3)", lineHeight: 1.5, whiteSpace: "pre-wrap" }}>{proposal.notes}</div>
        )}

        <ProposalSection title={`New elements · ${newOnes.length}`} items={newOnes} accepted={accepted} onToggle={toggle} />
        {known.length > 0 && (
          <>
            <ProposalSection
              title={`Already in the signature · ${known.length}`}
              subtitle="Applying appends what was observed to their logic notes."
              items={known}
              accepted={accepted}
              onToggle={toggle}
            />
            <Toggle checked={enrich} onChange={setEnrich}>
              Enrich existing elements with observed behaviour
            </Toggle>
          </>
        )}

        {fresh && (
          <Toggle checked={replaceLearned} onChange={setReplaceLearned}>
            Replace elements learned from earlier recordings with this set (source-derived and manual elements are kept)
          </Toggle>
        )}
        <Toggle checked={applyFlows} onChange={setApplyFlows}>
          Merge observed flows ({flowCounts.nodes} states, {flowCounts.edges} transitions) into the user-flow graph
        </Toggle>

        {err && <Pill tone="err">{err}</Pill>}
        <div style={{ display: "flex", justifyContent: "flex-end", gap: 10 }}>
          <BtnGhost onClick={() => setAccepted(new Set())}>Select none</BtnGhost>
          <BtnPrimary onClick={onApply} disabled={apply.isPending || (accepted.size === 0 && !applyFlows)}>
            {apply.isPending ? "Applying…" : `Apply to signature (${accepted.size})`}
          </BtnPrimary>
        </div>
      </div>
    </div>
  );
}

function ProposalSection({
  title,
  subtitle,
  items,
  accepted,
  onToggle,
}: {
  title: string;
  subtitle?: string;
  items: ProposedElementDto[];
  accepted: Set<string>;
  onToggle: (sel: string) => void;
}) {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
      <div className="mz-eyebrow">{title}</div>
      {subtitle && <div style={{ fontSize: 11.5, color: "var(--text-4)" }}>{subtitle}</div>}
      <div className="mz-glass" style={{ padding: 0, overflow: "hidden" }}>
        {items.length === 0 && <div style={{ padding: 14, fontSize: 12, color: "var(--text-4)" }}>None.</div>}
        {items.map((e) => (
          <label
            key={e.selector}
            style={{
              display: "grid",
              gridTemplateColumns: "20px 1fr auto",
              gap: 12,
              padding: "12px 16px",
              borderBottom: "1px solid var(--glass-stroke)",
              cursor: "pointer",
            }}
          >
            <input type="checkbox" checked={accepted.has(e.selector)} onChange={() => onToggle(e.selector)} />
            <div style={{ minWidth: 0 }}>
              <div style={{ fontFamily: "var(--f-mono)", fontSize: 12.5, color: "var(--orange-100)", wordBreak: "break-all" }}>
                {e.selector}
              </div>
              {e.matchesExisting && e.matchesExisting !== e.selector && (
                <div style={{ fontFamily: "var(--f-mono)", fontSize: 10.5, color: "var(--text-4)" }}>
                  same as {e.matchesExisting}
                </div>
              )}
              <div style={{ fontSize: 12, color: "var(--text-2)", marginTop: 2 }}>{e.description}</div>
              {e.logic && (
                <div style={{ fontSize: 11, color: "var(--text-4)", marginTop: 4, whiteSpace: "pre-wrap" }}>{e.logic}</div>
              )}
              {e.evidence.length > 0 && (
                <div style={{ fontSize: 10.5, color: "var(--text-4)", marginTop: 4, fontFamily: "var(--f-mono)" }}>
                  seen in episodes {e.evidence.map((i) => `#${i}`).join(" ")}
                </div>
              )}
            </div>
            <div style={{ display: "flex", gap: 6, alignItems: "flex-start" }}>
              <Pill tone={e.category === "informational" ? "info" : "orange"}>{e.category}</Pill>
              <Pill>{e.elementType}</Pill>
            </div>
          </label>
        ))}
      </div>
    </div>
  );
}

// ── Small form bits ────────────────────────────────────────────────

const inputStyle: CSSProperties = {
  width: "100%",
  boxSizing: "border-box",
  padding: "8px 10px",
  borderRadius: 8,
  border: "1px solid var(--glass-stroke-2)",
  background: "var(--glass-1)",
  color: "var(--text-1)",
  fontSize: 12.5,
  fontFamily: "var(--f-mono)",
};

function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <label style={{ display: "flex", flexDirection: "column", gap: 6 }}>
      <span style={{ fontSize: 11, color: "var(--text-3)" }}>{label}</span>
      {children}
      {hint && <span style={{ fontSize: 10.5, color: "var(--text-4)" }}>{hint}</span>}
    </label>
  );
}

function Toggle({
  checked,
  onChange,
  children,
}: {
  checked: boolean;
  onChange: (v: boolean) => void;
  children: ReactNode;
}) {
  return (
    <label style={{ display: "flex", gap: 8, alignItems: "flex-start", fontSize: 12, color: "var(--text-2)", cursor: "pointer" }}>
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} style={{ marginTop: 2 }} />
      <span>{children}</span>
    </label>
  );
}
