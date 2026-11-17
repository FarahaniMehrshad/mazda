import { useEffect, useMemo, useRef, useState } from "react";
import { useNavigate, useParams, useSearchParams } from "react-router-dom";
import { ScreenShell, BtnPrimary, BtnGhost, Pill } from "@/shared/components/Primitives";
import { TopBar } from "@/shared/components/TopBar";
import { ApiError } from "@/shared/api/client";
import { useSignature } from "@/features/signatures/api";
import { useRecordings } from "@/features/recordings/api";
import { TraceLinks } from "@/features/traces/TracePage";
import { ZoomableImage, type ViewerImage } from "@/shared/components/ImageViewer";
import {
  roundTripApi,
  useRoundTrip,
  useRoundTripMutation,
  useRoundTrips,
  useShot,
  type PlanStep,
  type RoundTrip,
  type RoundTripStep,
  type StepResult,
} from "./api";

/**
 * Round trip: check the functions against a recording and fix them.
 *
 *   1. pick a recording       -> Mazda rebuilds the function calls that reproduce it
 *   2. review the plan        -> nothing runs until the author confirms
 *   3. replay, compare, fix   -> live progress
 *   4. results                -> per step: verdict, differences, demo vs replay pictures,
 *                                questions ("same result?"), plan changes, undo
 */
export function RoundTripPage() {
  const { id } = useParams<{ id: string }>();
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const sig = useSignature(id);
  const recordings = useRecordings(id);
  const list = useRoundTrips(id);
  const selected = params.get("rt");
  const rt = useRoundTrip(selected);
  const [recordingId, setRecordingId] = useState<string>("");
  const [err, setErr] = useState<string | null>(null);

  const start = useRoundTripMutation((rid: string) => roundTripApi.start(id!, rid), id);
  const completed = (recordings.data ?? []).filter((r) => r.status === "Completed" && r.hasEpisodes);

  const onStart = async () => {
    setErr(null);
    try {
      const created = await start.mutateAsync(recordingId || completed[0]?.id);
      setParams({ rt: created.id });
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : String(e));
    }
  };

  return (
    <ScreenShell>
      <TopBar crumb={`${sig.data?.name ?? "…"} · check against a recording`} />
      <div className="mz-scroll" style={{ flex: 1, overflow: "auto" }}>
        <div style={{ padding: "32px 40px 80px", maxWidth: 1280, margin: "0 auto" }}>
          <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-end", gap: 24 }}>
            <div>
              <div className="mz-eyebrow">ROUND TRIP</div>
              <div style={{ fontSize: 22, fontWeight: 500, marginTop: 6 }}>{sig.data?.name ?? "…"} — check the functions against what you did</div>
              <div style={{ fontSize: 13, color: "var(--text-3)", marginTop: 6, maxWidth: 780, lineHeight: 1.5 }}>
                Mazda turns one of your recordings into the function calls that should repeat it, replays them on the attached tab,
                compares every step with what happened while you recorded, and fixes the functions that do something else.
              </div>
            </div>
            <div style={{ display: "flex", gap: 10 }}>
              <BtnGhost onClick={() => navigate(`/signatures/${id}/learn`)}>Learn by watching</BtnGhost>
              <BtnGhost onClick={() => navigate(`/signatures/${id}/functions`)}>Functions →</BtnGhost>
            </div>
          </div>

          <div style={{ display: "grid", gridTemplateColumns: "300px 1fr", gap: 24, marginTop: 24, alignItems: "start" }}>
            <div className="mz-glass" style={{ padding: 16, display: "flex", flexDirection: "column", gap: 12 }}>
              <div className="mz-eyebrow">RECORDING</div>
              {completed.length === 0 ? (
                <div style={{ fontSize: 12, color: "var(--text-3)", lineHeight: 1.5 }}>
                  No finished recording yet. Record yourself doing the task on the Learn by watching page first.
                </div>
              ) : (
                <>
                  <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
                    {completed.map((r) => (
                      <label key={r.id} style={{ display: "flex", gap: 8, fontSize: 13, cursor: "pointer", alignItems: "baseline" }}>
                        <input
                          type="radio"
                          name="recording"
                          checked={(recordingId || completed[0]?.id) === r.id}
                          onChange={() => setRecordingId(r.id)}
                        />
                        <span>{r.label}</span>
                      </label>
                    ))}
                  </div>
                  <BtnPrimary onClick={onStart} disabled={start.isPending}>
                    {start.isPending ? "Rebuilding the plan…" : "Rebuild the plan from it"}
                  </BtnPrimary>
                </>
              )}
              {err && <Pill tone="err">{err}</Pill>}

              {(list.data?.length ?? 0) > 0 && (
                <>
                  <div className="mz-eyebrow" style={{ marginTop: 8 }}>EARLIER ROUND TRIPS</div>
                  {list.data!.map((x) => (
                    <button
                      key={x.id}
                      type="button"
                      onClick={() => setParams({ rt: x.id })}
                      style={{
                        textAlign: "left", background: x.id === selected ? "var(--glass-2, var(--glass-1))" : "none",
                        border: "1px solid var(--glass-stroke-2)", borderRadius: 8, padding: "8px 10px", color: "var(--text-2)", cursor: "pointer", fontSize: 12,
                      }}
                    >
                      <div style={{ display: "flex", gap: 6, alignItems: "center" }}>
                        <StatusPill status={x.status} />
                        <span>{x.recordingLabel}</span>
                      </div>
                      {x.summary && <div style={{ color: "var(--text-3)", marginTop: 4 }}>{x.summary}</div>}
                    </button>
                  ))}
                </>
              )}
            </div>

            <div>
              {!selected && (
                <div className="mz-glass" style={{ padding: 18, fontSize: 13, color: "var(--text-3)", lineHeight: 1.6 }}>
                  Pick a recording and rebuild the plan. You will see the calls Mazda wants to replay before anything runs.
                </div>
              )}
              {selected && rt.isLoading && <div style={{ color: "var(--text-3)" }}>Loading…</div>}
              {rt.data && <RoundTripView rt={rt.data} sigId={id!} />}
            </div>
          </div>
        </div>
      </div>
    </ScreenShell>
  );
}

function StatusPill({ status }: { status: RoundTrip["status"] }) {
  const map: Record<RoundTrip["status"], { tone: "ok" | "err" | "warn" | "info" | "neutral"; label: string }> = {
    Draft: { tone: "info", label: "plan to review" },
    Running: { tone: "warn", label: "running" },
    NeedsPlan: { tone: "warn", label: "plan change to decide" },
    NeedsAnswers: { tone: "warn", label: "questions for you" },
    Done: { tone: "ok", label: "done" },
    Failed: { tone: "err", label: "failed" },
    Stopped: { tone: "neutral", label: "stopped by you" },
  };
  return <Pill tone={map[status].tone}>{map[status].label}</Pill>;
}

// ── One round trip ───────────────────────────────────────────────────

function RoundTripView({ rt, sigId }: { rt: RoundTrip; sigId: string }) {
  const [err, setErr] = useState<string | null>(null);
  const confirm = useRoundTripMutation(() => roundTripApi.confirm(rt.id), sigId);
  const decide = useRoundTripMutation((accept: boolean) => roundTripApi.planChange(rt.id, accept), sigId);
  const stop = useRoundTripMutation(() => roundTripApi.stop(rt.id), sigId);
  const [confirming, setConfirming] = useState(false);
  const running = rt.status === "Running";
  // Stopping takes a moment (the browser run is ended and an untested fix undone); keep saying so until it has.
  const [stopping, setStopping] = useState(false);
  useEffect(() => {
    if (!running) setStopping(false);
  }, [running]);

  const guard = async (fn: () => Promise<unknown>) => {
    setErr(null);
    try {
      await fn();
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : String(e));
    }
  };

  // Functions this round trip changed and kept: each can be undone.
  const fixed = useMemo(() => {
    const kept = new Set<string>();
    for (const e of rt.log) {
      if (!e.function) continue;
      if (e.kind === "fix" && e.message.startsWith("Kept the fix")) kept.add(e.function);
      if (e.kind === "revert") kept.delete(e.function);
    }
    return [...kept];
  }, [rt.log]);

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      <div className="mz-glass" style={{ padding: 16, display: "flex", gap: 12, alignItems: "center", flexWrap: "wrap" }}>
        <StatusPill status={rt.status} />
        <span style={{ fontSize: 13 }}>Recording: {rt.recordingLabel}</span>
        {rt.result?.summary && <span style={{ fontSize: 13, color: "var(--text-3)" }}>{rt.result.summary}</span>}
        <span style={{ flex: 1 }} />
        {running && (
          <BtnGhost
            onClick={() => guard(async () => { setStopping(true); try { await stop.mutateAsync(undefined); } catch (e) { setStopping(false); throw e; } })}
            disabled={stopping || stop.isPending}
          >
            {stopping ? "Stopping…" : "Stop"}
          </BtnGhost>
        )}
        {!running && rt.status !== "NeedsPlan" && (
          <BtnPrimary onClick={() => setConfirming(true)} disabled={!!rt.planProblem || confirm.isPending}>
            {rt.status === "Draft" ? "Confirm plan and run" : "Run again"}
          </BtnPrimary>
        )}
      </div>
      <TraceLinks subject={rt.id} refreshKey={`${rt.status}-${rt.log.length}`} />
      {rt.error && <Pill tone="err">{rt.error}</Pill>}
      {err && <Pill tone="err">{err}</Pill>}

      {rt.status === "NeedsPlan" && rt.pendingPlanChange && (
        <div className="mz-glass" style={{ padding: 16, border: "1px solid rgba(232,176,74,0.35)" }}>
          <div style={{ fontSize: 13, fontWeight: 500 }}>Mazda thinks step {rt.pendingPlanChange.step + 1} of the plan is wrong</div>
          <div style={{ fontSize: 13, color: "var(--text-2)", marginTop: 6, lineHeight: 1.5 }}>{rt.pendingPlanChange.explanation}</div>
          <div style={{ fontFamily: "var(--f-mono)", fontSize: 12, marginTop: 8 }}>
            {rt.plan[rt.pendingPlanChange.step]?.call}({rt.plan[rt.pendingPlanChange.step]?.argsJson}) → {rt.pendingPlanChange.call}({rt.pendingPlanChange.argsJson})
          </div>
          <div style={{ display: "flex", gap: 8, marginTop: 12 }}>
            <BtnPrimary onClick={() => guard(() => decide.mutateAsync(true))} disabled={decide.isPending}>Change the step and replay</BtnPrimary>
            <BtnGhost onClick={() => guard(() => decide.mutateAsync(false))} disabled={decide.isPending}>Keep the step as it is</BtnGhost>
          </div>
        </div>
      )}

      {running && <LiveFeed id={rt.id} />}

      <StartPage rt={rt} sigId={sigId} editable={!running} onError={setErr} />
      <PictureJudgeSwitch rt={rt} sigId={sigId} editable={!running} onError={setErr} />

      <PlanEditor rt={rt} sigId={sigId} editable={!running && rt.status !== "NeedsPlan"} onError={setErr} />

      {rt.result && rt.result.steps.length > 0 && (
        <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
          <div className="mz-eyebrow">RESULTS</div>
          {rt.result.steps.map((s) => (
            <StepCard key={s.index} rt={rt} sigId={sigId} step={rt.plan[s.index]} result={s} />
          ))}
        </div>
      )}

      {fixed.length > 0 && !running && (
        <div className="mz-glass" style={{ padding: 14, display: "flex", gap: 10, alignItems: "center", flexWrap: "wrap" }}>
          <span style={{ fontSize: 13 }}>Mazda changed these functions:</span>
          {fixed.map((f) => (
            <RevertButton key={f} rt={rt} sigId={sigId} fn={f} onError={setErr} />
          ))}
        </div>
      )}

      <details className="mz-glass" style={{ padding: 14 }} open={running}>
        <summary style={{ cursor: "pointer", fontSize: 13 }}>What happened ({rt.log.length})</summary>
        <div style={{ marginTop: 8, display: "flex", flexDirection: "column", gap: 4 }}>
          {rt.log.map((e, i) => (
            <div key={i} style={{ fontSize: 12, color: e.kind === "error" ? "#f0a89e" : e.kind === "fix" ? "#a8e6c4" : "var(--text-2)", lineHeight: 1.5 }}>
              <span style={{ color: "var(--text-4)", fontFamily: "var(--f-mono)" }}>{new Date(e.at).toLocaleTimeString()} </span>
              {e.message}
            </div>
          ))}
        </div>
      </details>

      {confirming && (
        <ConfirmDialog
          rt={rt}
          onCancel={() => setConfirming(false)}
          onRun={() => {
            setConfirming(false);
            void guard(() => confirm.mutateAsync(undefined));
          }}
        />
      )}
    </div>
  );
}

function RevertButton({ rt, sigId, fn, onError }: { rt: RoundTrip; sigId: string; fn: string; onError: (m: string | null) => void }) {
  const revert = useRoundTripMutation(() => roundTripApi.revert(rt.id, fn), sigId);
  return (
    <BtnGhost
      onClick={async () => {
        onError(null);
        try {
          await revert.mutateAsync(undefined);
        } catch (e) {
          onError(e instanceof ApiError ? e.message : String(e));
        }
      }}
      disabled={revert.isPending}
    >
      Undo the fix to {fn}
    </BtnGhost>
  );
}

function LiveFeed({ id }: { id: string }) {
  const [lines, setLines] = useState<string[]>([]);
  const bottom = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    // EventSource can't send Authorization; the stream is anonymous and keyed by the unguessable id.
    const es = new EventSource(`/api/roundtrips/${id}/stream`);
    setLines([]);
    es.onmessage = (m) => {
      let line: string;
      try {
        line = JSON.parse(m.data);
      } catch {
        line = m.data;
      }
      setLines((l) => [...l, line].slice(-400));
    };
    es.addEventListener("end", () => es.close());
    es.onerror = () => es.close();
    return () => es.close();
  }, [id]);
  // Braces matter: newer browsers return a Promise from scrollIntoView, and an effect
  // that returns a non-function makes React crash when it calls it as the cleanup.
  useEffect(() => {
    bottom.current?.scrollIntoView({ block: "nearest" });
  }, [lines]);
  return (
    <div className="mz-glass" style={{ padding: 14 }}>
      <div className="mz-eyebrow">LIVE</div>
      <div style={{ marginTop: 8, maxHeight: 220, overflow: "auto", fontFamily: "var(--f-mono)", fontSize: 12, color: "var(--text-2)", lineHeight: 1.6 }}>
        {lines.map((l, i) => (
          <div key={i}>{l}</div>
        ))}
        <div ref={bottom} />
      </div>
    </div>
  );
}

function ConfirmDialog({ rt, onCancel, onRun }: { rt: RoundTrip; onCancel: () => void; onRun: () => void }) {
  const [ack, setAck] = useState(false);
  const contrasts = rt.plan.filter((s) => s.contrastArgsJson).length;
  return (
    <div
      role="dialog"
      aria-modal="true"
      style={{ position: "fixed", inset: 0, background: "rgba(0,0,0,0.6)", backdropFilter: "blur(4px)", display: "grid", placeItems: "center", zIndex: 100, padding: 24 }}
      onClick={(e) => {
        if (e.target === e.currentTarget) onCancel();
      }}
    >
      <div style={{ background: "var(--surface-1, #14100c)", border: "1px solid var(--glass-stroke-2)", borderRadius: 14, padding: 24, width: "min(640px, 100%)" }}>
        <div style={{ fontSize: 16, fontWeight: 500 }}>Replay this plan for real</div>
        <div style={{ fontSize: 13, color: "#f1d18a", marginTop: 10, lineHeight: 1.6 }}>
          Mazda opens{" "}
          {rt.startUrlIsCustom ? (
            <span style={{ fontFamily: "var(--f-mono)", wordBreak: "break-all" }}>{rt.startUrl}</span>
          ) : (
            "the page your recording started on"
          )}{" "}
          and performs these {rt.plan.length} step(s) on the attached tab — at
          least {contrasts > 0 ? "three" : "twice"} times ({contrasts > 0 ? "twice as recorded, once with other choices" : "twice as recorded"}), and again after
          every fix it tries. Whatever they create or change in the app is really created or changed each time.
        </div>
        <label style={{ display: "flex", gap: 8, marginTop: 16, fontSize: 13, cursor: "pointer" }}>
          <input type="checkbox" checked={ack} onChange={(e) => setAck(e.target.checked)} />
          <span>I reviewed the plan, and the app can take these actions repeatedly.</span>
        </label>
        <div style={{ display: "flex", justifyContent: "flex-end", gap: 10, marginTop: 20 }}>
          <BtnGhost onClick={onCancel}>Cancel</BtnGhost>
          <BtnPrimary onClick={onRun} disabled={!ack}>Run</BtnPrimary>
        </div>
      </div>
    </div>
  );
}

// ── Start page ───────────────────────────────────────────────────────

/** The page every replay opens before the first step; the recording's by default. */
function StartPage({ rt, sigId, editable, onError }: { rt: RoundTrip; sigId: string; editable: boolean; onError: (m: string | null) => void }) {
  const save = useRoundTripMutation((url: string) => roundTripApi.setStartUrl(rt.id, url), sigId);
  const [draft, setDraft] = useState<string | null>(null);

  const submit = async (url: string) => {
    onError(null);
    try {
      await save.mutateAsync(url.trim());
      setDraft(null);
    } catch (e) {
      onError(e instanceof ApiError ? e.message : String(e));
    }
  };

  const field = { flex: 1, minWidth: 0, padding: "6px 8px", background: "var(--glass-1)", border: "1px solid var(--glass-stroke-2)", borderRadius: 6, color: "var(--text-1)", fontFamily: "var(--f-mono)", fontSize: 12 } as const;

  return (
    <div className="mz-glass" style={{ padding: 16 }}>
      <div style={{ display: "flex", alignItems: "center", gap: 10, flexWrap: "wrap" }}>
        <div className="mz-eyebrow">START PAGE</div>
        <span style={{ fontSize: 12, color: "var(--text-3)" }}>
          {rt.startUrlIsCustom ? "Chosen for this round trip; every replay opens it first." : "Where the recording started; every replay opens it first."}
        </span>
      </div>
      {draft === null ? (
        <div style={{ display: "flex", alignItems: "center", gap: 10, marginTop: 10 }}>
          <a
            href={rt.startUrl || undefined}
            target="_blank"
            rel="noreferrer"
            style={{ flex: 1, minWidth: 0, fontFamily: "var(--f-mono)", fontSize: 12, wordBreak: "break-all", color: "var(--text-1)" }}
          >
            {rt.startUrl || "(none: the replays start on whatever page the tab shows)"}
          </a>
          {editable && <BtnGhost onClick={() => setDraft(rt.startUrl)}>Change</BtnGhost>}
          {editable && rt.startUrlIsCustom && (
            <BtnGhost onClick={() => void submit("")} disabled={save.isPending}>Use the recording's</BtnGhost>
          )}
        </div>
      ) : (
        <form
          style={{ display: "flex", alignItems: "center", gap: 10, marginTop: 10 }}
          onSubmit={(e) => {
            e.preventDefault();
            void submit(draft);
          }}
        >
          <input
            autoFocus
            type="url"
            value={draft}
            placeholder="https://…"
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Escape") setDraft(null);
            }}
            style={field}
          />
          <BtnGhost type="button" onClick={() => setDraft(null)}>Cancel</BtnGhost>
          <BtnPrimary type="submit" disabled={save.isPending || draft.trim() === rt.startUrl}>Save</BtnPrimary>
        </form>
      )}
      {rt.startUrlIsCustom && rt.recordedStartUrl && (
        <div style={{ fontSize: 11, color: "var(--text-3)", marginTop: 8, wordBreak: "break-all" }}>
          The recording started on <span style={{ fontFamily: "var(--f-mono)" }}>{rt.recordedStartUrl}</span>
        </div>
      )}
    </div>
  );
}

/** Whether a model looks at the pictures of steps the comparison cannot decide. Off by default. */
function PictureJudgeSwitch({ rt, sigId, editable, onError }: { rt: RoundTrip; sigId: string; editable: boolean; onError: (m: string | null) => void }) {
  const save = useRoundTripMutation((on: boolean) => roundTripApi.setPictureJudge(rt.id, on), sigId);
  const toggle = async (on: boolean) => {
    onError(null);
    try {
      await save.mutateAsync(on);
    } catch (e) {
      onError(e instanceof ApiError ? e.message : String(e));
    }
  };
  return (
    <div className="mz-glass" style={{ padding: 16 }}>
      <label style={{ display: "flex", gap: 10, alignItems: "flex-start", cursor: editable ? "pointer" : "default" }}>
        <input
          type="checkbox"
          aria-label="Picture judge"
          checked={rt.pictureJudge}
          disabled={!editable || save.isPending}
          onChange={(e) => void toggle(e.target.checked)}
          style={{ marginTop: 3 }}
        />
        <span>
          <span className="mz-eyebrow">PICTURE JUDGE</span>{" "}
          <span style={{ fontSize: 12, color: "var(--text-3)" }}>{rt.pictureJudge ? "on" : "off"}</span>
          <span style={{ display: "block", fontSize: 12, color: "var(--text-3)", marginTop: 4, lineHeight: 1.5 }}>
            {rt.pictureJudge
              ? "When the comparison cannot decide whether a step did what you did, a model compares the before/after pictures first; you are asked only when it is unsure."
              : "When the comparison cannot decide whether a step did what you did, you are asked to compare the pictures. Turn on to let a model judge them first (one model call per such step)."}
          </span>
        </span>
      </label>
    </div>
  );
}

// ── Plan ─────────────────────────────────────────────────────────────

function PlanEditor({ rt, sigId, editable, onError }: { rt: RoundTrip; sigId: string; editable: boolean; onError: (m: string | null) => void }) {
  const save = useRoundTripMutation((steps: PlanStep[]) => roundTripApi.savePlan(rt.id, steps), sigId);
  const [draft, setDraft] = useState<PlanStep[] | null>(null);
  const steps: (PlanStep & Partial<RoundTripStep>)[] = draft ?? rt.plan;

  const set = (i: number, patch: Partial<PlanStep>) =>
    setDraft((steps as PlanStep[]).map((s, k) => (k === i ? { ...s, ...patch } : s)));

  const onSave = async () => {
    onError(null);
    try {
      await save.mutateAsync((draft ?? rt.plan).map(({ call, argsJson, episodes, contrastArgsJson, why }) => ({ call, argsJson, episodes, contrastArgsJson, why })));
      setDraft(null);
    } catch (e) {
      onError(e instanceof ApiError ? e.message : String(e));
    }
  };

  const field = { width: "100%", padding: "6px 8px", background: "var(--glass-1)", border: "1px solid var(--glass-stroke-2)", borderRadius: 6, color: "var(--text-1)", fontFamily: "var(--f-mono)", fontSize: 12 } as const;

  return (
    <div className="mz-glass" style={{ padding: 16 }}>
      <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
        <div className="mz-eyebrow">PLAN</div>
        <span style={{ fontSize: 12, color: "var(--text-3)" }}>The calls that should repeat what you did, in order.</span>
        <span style={{ flex: 1 }} />
        {editable && draft && (
          <>
            <BtnGhost onClick={() => setDraft(null)}>Discard changes</BtnGhost>
            <BtnPrimary onClick={onSave} disabled={save.isPending}>Save plan</BtnPrimary>
          </>
        )}
      </div>
      {rt.planProblem && <div style={{ marginTop: 10 }}><Pill tone="err">{rt.planProblem}</Pill></div>}
      {rt.notes && <div style={{ fontSize: 12, color: "var(--text-3)", marginTop: 8, lineHeight: 1.5 }}>{rt.notes}</div>}
      <div style={{ marginTop: 12, display: "flex", flexDirection: "column", gap: 10 }}>
        {steps.map((s, i) => (
          <div key={i} style={{ border: "1px solid var(--glass-stroke-2)", borderRadius: 8, padding: 12 }}>
            <div style={{ display: "flex", gap: 10, alignItems: "baseline" }}>
              <span style={{ fontFamily: "var(--f-mono)", color: "var(--text-3)" }}>{i + 1}</span>
              {editable ? (
                <input value={s.call} onChange={(e) => set(i, { call: e.target.value })} style={{ ...field, width: 220 }} />
              ) : (
                <span style={{ fontFamily: "var(--f-mono)", fontSize: 13 }}>{s.call}</span>
              )}
              <span style={{ fontSize: 12, color: "var(--text-3)", flex: 1 }}>{s.why}</span>
              {editable && (
                <BtnGhost onClick={() => setDraft((steps as PlanStep[]).filter((_, k) => k !== i))}>Remove</BtnGhost>
              )}
            </div>
            {s.demoSteps && s.demoSteps.length > 0 && !draft && (
              <div style={{ fontSize: 12, color: "var(--text-2)", marginTop: 6 }}>Repeats: {s.demoSteps.join(" → ")}</div>
            )}
            <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr 160px", gap: 10, marginTop: 8 }}>
              <label style={{ fontSize: 11, color: "var(--text-3)" }}>
                Inputs (your choice)
                <input value={s.argsJson} onChange={(e) => set(i, { argsJson: e.target.value })} readOnly={!editable} style={field} />
              </label>
              <label style={{ fontSize: 11, color: "var(--text-3)" }}>
                Another choice (to learn what carries it)
                <input
                  value={s.contrastArgsJson ?? ""}
                  placeholder="none"
                  onChange={(e) => set(i, { contrastArgsJson: e.target.value || null })}
                  readOnly={!editable}
                  style={field}
                />
              </label>
              <label style={{ fontSize: 11, color: "var(--text-3)" }}>
                Recording actions
                <input
                  value={s.episodes.join(", ")}
                  onChange={(e) =>
                    set(i, { episodes: e.target.value.split(/[\s,]+/).filter(Boolean).map(Number).filter((n) => Number.isInteger(n)) })
                  }
                  readOnly={!editable}
                  style={field}
                />
              </label>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

// ── Results ──────────────────────────────────────────────────────────

const VERDICT: Record<StepResult["verdict"], { tone: "ok" | "err" | "warn" | "neutral"; label: string }> = {
  identical: { tone: "ok", label: "same as your demonstration" },
  equivalent: { tone: "ok", label: "same result" },
  different: { tone: "err", label: "does something else" },
  question: { tone: "warn", label: "needs your answer" },
  "not-run": { tone: "neutral", label: "not run" },
};

function StepCard({ rt, sigId, step, result }: { rt: RoundTrip; sigId: string; step: RoundTripStep | undefined; result: StepResult }) {
  const answer = useRoundTripMutation((same: boolean) => roundTripApi.answer(rt.id, result.index, same), sigId);
  const [open, setOpen] = useState(result.verdict === "different" || result.verdict === "question");
  const shots = useStepShots(rt.id, result.index, result.run, result.call, open);
  const v = VERDICT[result.verdict];
  const blocking = result.findings.filter((f) => f.severity !== "note");
  const notes = result.findings.filter((f) => f.severity === "note");

  return (
    <div className="mz-glass" style={{ padding: 14 }}>
      <div style={{ display: "flex", gap: 10, alignItems: "center", cursor: "pointer" }} onClick={() => setOpen((o) => !o)}>
        <span style={{ fontFamily: "var(--f-mono)", color: "var(--text-3)" }}>{result.index + 1}</span>
        <span style={{ fontFamily: "var(--f-mono)", fontSize: 13 }}>
          {result.call}
          <span style={{ color: "var(--text-3)" }}>({step?.argsJson === "{}" ? "" : step?.argsJson})</span>
        </span>
        <Pill tone={v.tone}>{v.label}</Pill>
        <span style={{ flex: 1 }} />
        <span style={{ fontSize: 11, color: "var(--text-4)" }}>{open ? "hide" : "show"}</span>
      </div>

      {open && (
        <div style={{ marginTop: 10 }}>
          {result.error && <div style={{ fontSize: 12, color: "#f0a89e", whiteSpace: "pre-wrap" }}><b>It failed with:</b> {result.error}</div>}
          {blocking.map((f, i) => (
            <div key={i} style={{ fontSize: 13, color: f.severity === "blocking" ? "#f0a89e" : "#f1d18a", marginTop: 4, lineHeight: 1.5 }}>
              {f.summary}
            </div>
          ))}
          {result.judge && (
            <div style={{ fontSize: 12, color: "var(--text-2)", marginTop: 6 }}>
              Looking at the pictures, Mazda says <b>{result.judge.verdict}</b>: {result.judge.reason}
            </div>
          )}
          {result.verdict === "question" && (
            <div style={{ marginTop: 10, display: "flex", gap: 10, alignItems: "center", flexWrap: "wrap" }}>
              <span style={{ fontSize: 13 }}>{result.question ?? "Did the function do what you did?"}</span>
              <BtnPrimary onClick={() => answer.mutate(true)} disabled={answer.isPending}>Same result</BtnPrimary>
              <BtnGhost onClick={() => answer.mutate(false)} disabled={answer.isPending}>Not the same</BtnGhost>
            </div>
          )}

          <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 14, marginTop: 12 }}>
            <Side title="What you did">
              {step?.demoSteps && <div style={{ fontSize: 12, color: "var(--text-2)", lineHeight: 1.6 }}>{step.demoSteps.join(" → ")}</div>}
              <Requests lines={result.demoRequests} />
              <Pictures shots={shots.slice(0, 2)} gallery={shots} offset={0} />
            </Side>
            <Side title={`What ${result.call} did (replay ${result.run})`}>
              <Requests lines={result.replayRequests} />
              <Pictures shots={shots.slice(2, 4)} gallery={shots} offset={2} />
            </Side>
          </div>

          {(notes.length > 0 || result.console.length > 0) && (
            <details style={{ marginTop: 10 }}>
              <summary style={{ fontSize: 12, color: "var(--text-3)", cursor: "pointer" }}>Notes and console ({notes.length + result.console.length})</summary>
              <ul style={{ margin: "6px 0 0", paddingLeft: 18, fontSize: 12, color: "var(--text-3)", lineHeight: 1.6 }}>
                {notes.map((n, i) => <li key={`n${i}`}>{n.summary}</li>)}
                {result.console.map((c, i) => <li key={`c${i}`} style={{ fontFamily: "var(--f-mono)" }}>{c}</li>)}
              </ul>
            </details>
          )}
        </div>
      )}
    </div>
  );
}

interface Shot {
  caption: string;
  label: string;
  src: string | null;
  loading: boolean;
}

/** The step's four pictures, in viewing order: what you did before / after, what the function did before / after. */
function useStepShots(id: string, step: number, run: number, call: string, enabled: boolean): Shot[] {
  const qs = [
    { q: useShot(id, "demo", step, "before", undefined, enabled), caption: "What you did: before", label: "before" },
    { q: useShot(id, "demo", step, "after", undefined, enabled), caption: "What you did: after", label: "after" },
    { q: useShot(id, "replay", step, "before", run, enabled), caption: `What ${call} did (replay ${run}): before`, label: "before" },
    { q: useShot(id, "replay", step, "after", run, enabled), caption: `What ${call} did (replay ${run}): after`, label: "after" },
  ];
  return qs.map(({ q, caption, label }) => ({ caption: `Step ${step + 1}. ${caption}`, label, src: q.data ? `data:image/jpeg;base64,${q.data}` : null, loading: q.isLoading }));
}

/** Before / after side by side; a click opens the picture bigger, ← / → go through all four of the step. */
function Pictures({ shots, gallery, offset }: { shots: Shot[]; gallery: Shot[]; offset: number }) {
  const viewable: ViewerImage[] = gallery.filter((g) => g.src).map((g) => ({ src: g.src!, caption: g.caption }));
  return (
    <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 8, marginTop: 8 }}>
      {shots.map((s, k) => (
        <div key={s.label}>
          <div style={{ fontSize: 11, color: "var(--text-4)" }}>{s.label}</div>
          {s.src ? (
            <ZoomableImage
              src={s.src}
              alt={s.caption}
              gallery={viewable}
              index={viewable.findIndex((v) => v.caption === gallery[offset + k].caption)}
              style={{ border: "1px solid var(--glass-stroke-2)", borderRadius: 6 }}
            />
          ) : (
            <div style={{ fontSize: 11, color: "var(--text-4)" }}>{s.loading ? "…" : "no picture"}</div>
          )}
        </div>
      ))}
    </div>
  );
}

function Side({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div style={{ padding: 10, border: "1px solid var(--glass-stroke-2)", borderRadius: 8, minWidth: 0 }}>
      <div style={{ fontSize: 12, fontWeight: 500, color: "var(--text-2)", marginBottom: 6 }}>{title}</div>
      {children}
    </div>
  );
}

function Requests({ lines }: { lines: string[] }) {
  return (
    <div style={{ marginTop: 6 }}>
      <div style={{ fontSize: 11, color: "var(--text-4)" }}>The app was sent</div>
      {lines.length === 0 ? (
        <div style={{ fontSize: 11, color: "var(--text-4)" }}>nothing</div>
      ) : (
        <pre style={{ margin: "4px 0 0", padding: 8, fontSize: 11, fontFamily: "var(--f-mono)", color: "var(--text-2)", background: "var(--glass-1)", border: "1px solid var(--glass-stroke-2)", borderRadius: 6, maxHeight: 140, overflow: "auto", whiteSpace: "pre-wrap", wordBreak: "break-word" }}>
          {lines.join("\n")}
        </pre>
      )}
    </div>
  );
}
