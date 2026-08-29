import { useEffect, useMemo, useRef, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { ScreenShell, StepIndicator, BtnPrimary, Pill } from "@/shared/components/Primitives";
import { TopBar } from "@/shared/components/TopBar";
import { useStepNavigator } from "@/shared/components/useStepNavigator";
import { useSignature } from "@/features/signatures/api";
import { useGeneration, usePlanRun, useExecuteRun } from "./api";
import type {
  GenerationDto,
  GatheredStateBlob,
  PlanBlob,
  ExecutionEnvelopeBlob,
  StepResultBlob,
  StepErrorKind,
} from "@/shared/api/types";
import { ApiError } from "@/shared/api/client";

/**
 * Step 7 of the wizard — Run.
 *
 * Two URL shapes:
 *   /signatures/:id/run          — fresh run against a signature
 *   /runs/:id                    — resume an existing run
 *
 * Flow:
 *   1. Enter prompt → Plan
 *      Server runs gather (Playwright) + plan (LLM), returns a Ready
 *      generation whose gatheredStateJson + planJson are populated.
 *   2. Review the state snapshot + plan.
 *   3. Execute — fire-and-forget, subscribe to SSE for per-step events.
 *      The interpreter emits `__MZ_EVENT__ {...}` lines the frontend
 *      parses into step cards.
 *   4. When SSE closes, refetch the generation for the terminal envelope.
 */
export function RunPage() {
  const params = useParams<{ id: string; sigId: string }>();
  const runId = params.id;
  const routeSigId = params.sigId;
  const navigate = useNavigate();

  // Two entry paths. From /runs/:id we have the run but not the signature;
  // load the run first, use its signatureId to fetch the signature. From
  // /signatures/:id/run we have the sig id up front and no run yet.
  const gen = useGeneration(runId);
  const sigId = routeSigId ?? gen.data?.signatureId;
  const sig = useSignature(sigId);

  const navigator = useStepNavigator(
    sigId
      ? runId
        ? { mode: "run", runId, signatureId: sigId }
        : { mode: "signature", signatureId: sigId }
      : null,
  );

  const [prompt, setPrompt] = useState("");
  const [err, setErr] = useState<string | null>(null);
  const plan = usePlanRun();
  const execute = useExecuteRun();

  // Pre-fill prompt from an existing run (resume case).
  useEffect(() => {
    if (gen.data?.prompt && !prompt) setPrompt(gen.data.prompt);
  }, [gen.data?.prompt]); // eslint-disable-line react-hooks/exhaustive-deps

  const onPlan = async () => {
    if (!sigId) return;
    setErr(null);
    try {
      const created = await plan.mutateAsync({ signatureId: sigId, prompt });
      navigate(`/runs/${created.id}`, { replace: true });
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : String(e));
    }
  };

  const onExecute = async () => {
    if (!runId) return;
    setErr(null);
    try {
      await execute.mutateAsync(runId);
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : String(e));
    }
  };

  const gathered = useMemo(() => parseBlob<GatheredStateBlob>(gen.data?.gatheredStateJson), [gen.data]);
  const planBlob = useMemo(() => parseBlob<PlanBlob>(gen.data?.planJson), [gen.data]);
  const envelope = useMemo(() => parseBlob<ExecutionEnvelopeBlob>(gen.data?.executionEnvelopeJson), [gen.data]);

  return (
    <ScreenShell>
      <TopBar />
      {/*
        Same scroll pattern as FunctionsPage — `mz-stage` is a flex column,
        so this child must claim flex:1 + overflow:auto or the RunPage
        (which grows tall once state / plan / execution panes render) will
        clip at the viewport bottom.
      */}
      <div className="mz-scroll" style={{ flex: 1, overflow: "auto" }}>
        <div style={{ padding: "40px 48px 80px", maxWidth: 1200, margin: "0 auto" }}>
        <StepIndicator step={7} onStepClick={navigator} />

        <div style={{ marginTop: 32 }}>
          <div style={{ fontSize: 11, fontFamily: "var(--f-mono)", color: "var(--text-3)", letterSpacing: "0.05em" }}>
            STEP 07 · RUN
          </div>
          <div style={{ fontSize: 22, fontWeight: 500, marginTop: 6 }}>
            {sig.data?.name ?? "…"}
          </div>
        </div>

        <PromptPane
          prompt={prompt}
          onPromptChange={setPrompt}
          onPlan={onPlan}
          onExecute={onExecute}
          planning={plan.isPending}
          executing={execute.isPending}
          gen={gen.data ?? null}
          canExecute={!!runId && gen.data?.status === "Ready" && gen.data?.executionStatus !== "Running"}
        />

        {err && (
          <div style={{ marginTop: 20 }}>
            <Pill tone="err">{err}</Pill>
          </div>
        )}
        {gen.data?.failureReason && !err && (
          <div style={{ marginTop: 20 }}>
            <Pill tone="err">{gen.data.failureReason}</Pill>
          </div>
        )}

        {gathered && <StatePane state={gathered} />}
        {planBlob && <PlanPane plan={planBlob} />}
        {runId && gen.data?.executionStatus && gen.data.executionStatus !== "Idle" && (
          <ExecutionPane runId={runId} status={gen.data.executionStatus} envelope={envelope} onDone={() => gen.refetch()} />
        )}
        </div>
      </div>
    </ScreenShell>
  );
}

// ── Prompt pane ──────────────────────────────────────────────────────

function PromptPane({
  prompt,
  onPromptChange,
  onPlan,
  onExecute,
  planning,
  executing,
  gen,
  canExecute,
}: {
  prompt: string;
  onPromptChange: (v: string) => void;
  onPlan: () => void;
  onExecute: () => void;
  planning: boolean;
  executing: boolean;
  gen: GenerationDto | null;
  canExecute: boolean;
}) {
  const readOnly = !!gen;
  return (
    <div style={{ marginTop: 24 }}>
      <div style={{ fontSize: 11, color: "var(--text-3)", marginBottom: 6, fontFamily: "var(--f-mono)" }}>
        prompt
      </div>
      <textarea
        value={prompt}
        onChange={(e) => onPromptChange(e.target.value)}
        placeholder="What do you want done? e.g. 'mark buy-milk as done, then add buy-bread as high priority'"
        rows={4}
        readOnly={readOnly}
        style={{
          width: "100%",
          padding: "14px 16px",
          background: "var(--glass-1)",
          border: "1px solid var(--glass-stroke-2)",
          borderRadius: 10,
          color: "var(--text-1)",
          fontSize: 14,
          resize: "vertical",
          opacity: readOnly ? 0.75 : 1,
        }}
      />
      <div style={{ marginTop: 12, display: "flex", gap: 10, alignItems: "center" }}>
        {!gen && (
          <BtnPrimary onClick={onPlan} disabled={planning || !prompt.trim()}>
            {planning ? "Planning…" : "Plan"}
          </BtnPrimary>
        )}
        {gen && canExecute && (
          <BtnPrimary onClick={onExecute} disabled={executing}>
            {executing ? "Starting…" : "Execute plan"}
          </BtnPrimary>
        )}
        {gen && (
          <Pill tone={gen.status === "Ready" ? "ok" : gen.status === "Failed" ? "err" : "info"}>
            {gen.status}
          </Pill>
        )}
        {gen?.executionStatus && gen.executionStatus !== "Idle" && (
          <Pill
            tone={
              gen.executionStatus === "Passed"
                ? "ok"
                : gen.executionStatus === "Running"
                ? "info"
                : "err"
            }
          >
            exec: {gen.executionStatus}
          </Pill>
        )}
        {gen && gen.tokensUsed > 0 && (
          <span style={{ fontSize: 11, color: "var(--text-3)", fontFamily: "var(--f-mono)" }}>
            {gen.tokensUsed} tokens
          </span>
        )}
      </div>
    </div>
  );
}

// ── State snapshot pane ──────────────────────────────────────────────

function StatePane({ state }: { state: GatheredStateBlob }) {
  return (
    <Section title="Gathered state" badge={state.manifest} subtitle={`captured ${new Date(state.capturedAt).toLocaleString()}`}>
      <pre
        style={{
          margin: 0,
          padding: 14,
          background: "var(--glass-1)",
          border: "1px solid var(--glass-stroke-2)",
          borderRadius: 10,
          fontFamily: "var(--f-mono)",
          fontSize: 12,
          maxHeight: 260,
          overflow: "auto",
        }}
      >
        {JSON.stringify(state.state, null, 2)}
      </pre>
    </Section>
  );
}

// ── Plan pane ────────────────────────────────────────────────────────

function PlanPane({ plan }: { plan: PlanBlob }) {
  return (
    <Section title="Plan" subtitle={`${plan.steps.length} step${plan.steps.length === 1 ? "" : "s"}`}>
      {plan.reasoning && (
        <div style={{ fontSize: 13, color: "var(--text-2)", marginBottom: 12, fontStyle: "italic" }}>
          {plan.reasoning}
        </div>
      )}
      {plan.steps.length === 0 ? (
        <div style={{ fontSize: 13, color: "var(--text-3)" }}>
          The planner emitted an empty plan — usually means the prompt didn't ground into the state.
        </div>
      ) : (
        <ol
          style={{
            margin: 0,
            padding: 0,
            listStyle: "none",
            display: "flex",
            flexDirection: "column",
            gap: 8,
          }}
        >
          {plan.steps.map((s, i) => (
            <li
              key={s.id}
              style={{
                display: "grid",
                gridTemplateColumns: "40px 1fr",
                gap: 12,
                padding: "10px 14px",
                background: "var(--glass-1)",
                border: "1px solid var(--glass-stroke-2)",
                borderRadius: 8,
              }}
            >
              <span style={{ fontFamily: "var(--f-mono)", fontSize: 11, color: "var(--text-3)" }}>
                {String(i + 1).padStart(2, "0")}
              </span>
              <div>
                <div style={{ fontFamily: "var(--f-mono)", fontSize: 13 }}>
                  {s.call}
                  <span style={{ color: "var(--text-3)" }}>({formatArgs(s.args)})</span>
                </div>
              </div>
            </li>
          ))}
        </ol>
      )}
    </Section>
  );
}

function formatArgs(args: Record<string, unknown>): string {
  const entries = Object.entries(args);
  if (entries.length === 0) return "";
  return entries.map(([k, v]) => `${k}=${JSON.stringify(v)}`).join(", ");
}

// ── Execution pane (SSE-driven live steps) ───────────────────────────

interface LiveEvent {
  type: string;
  id?: string;
  call?: string;
  args?: Record<string, unknown>;
  durationMs?: number;
  error?: { kind: string; message: string };
}

function ExecutionPane({
  runId,
  status,
  envelope,
  onDone,
}: {
  runId: string;
  status: string;
  envelope: ExecutionEnvelopeBlob | null;
  onDone: () => void;
}) {
  const [events, setEvents] = useState<LiveEvent[]>([]);
  const [rawLog, setRawLog] = useState<string[]>([]);
  const esRef = useRef<EventSource | null>(null);

  // Subscribe to the SSE stream while executing. Close on unmount or when
  // the server sends the terminal 'end' event.
  useEffect(() => {
    if (status !== "Running") return;
    // EventSource can't set Authorization headers; the backend accepts the
    // stream unauthenticated (the id is unguessable GUID) so it's fine.
    const es = new EventSource(`/api/generations/${runId}/execution/stream`);
    esRef.current = es;

    es.onmessage = (e) => {
      let line: string;
      try {
        line = JSON.parse(e.data);
      } catch {
        line = e.data;
      }
      const evt = parseMzEvent(line);
      if (evt) setEvents((es) => [...es, evt]);
      // Everything (event lines included) goes to the raw log for debugging.
      setRawLog((log) => [...log, line]);
    };
    es.addEventListener("end", () => {
      es.close();
      esRef.current = null;
      // Give the server a beat to persist the terminal state, then refetch
      // so the envelope shows up.
      setTimeout(onDone, 250);
    });
    es.onerror = () => {
      // Silent — SSE errors on connection loss are common; the onDone
      // refetch will resync when the stream ends normally.
    };

    return () => {
      es.close();
      esRef.current = null;
    };
  }, [runId, status]); // eslint-disable-line react-hooks/exhaustive-deps

  // Once the envelope arrives, the SSE list is redundant — the envelope
  // has authoritative step data. Prefer it when present.
  const steps: StepResultBlob[] = envelope?.steps ?? deriveStepsFromEvents(events);

  return (
    <Section title="Execution" subtitle={status === "Running" ? "live" : status}>
      <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
        {steps.map((s) => (
          <StepRow key={s.id} step={s} />
        ))}
        {steps.length === 0 && (
          <div style={{ fontSize: 12, color: "var(--text-3)" }}>
            {status === "Running" ? "Waiting for first step…" : "No steps."}
          </div>
        )}
      </div>

      {envelope && (
        <div style={{ marginTop: 16, display: "flex", gap: 8, alignItems: "center" }}>
          <Pill tone={envelope.outcome === "ok" ? "ok" : "err"}>
            {envelope.outcome === "ok" ? "Passed" : "Failed"}
          </Pill>
          <span style={{ fontSize: 11, color: "var(--text-3)", fontFamily: "var(--f-mono)" }}>
            {envelope.stepsExecuted} / {envelope.stepsTotal} executed
          </span>
        </div>
      )}

      {rawLog.length > 0 && (
        <details style={{ marginTop: 16 }}>
          <summary style={{ cursor: "pointer", fontSize: 11, color: "var(--text-3)", fontFamily: "var(--f-mono)" }}>
            raw log ({rawLog.length} lines)
          </summary>
          <pre
            style={{
              margin: "8px 0 0",
              padding: 12,
              background: "var(--glass-1)",
              border: "1px solid var(--glass-stroke-2)",
              borderRadius: 8,
              fontFamily: "var(--f-mono)",
              fontSize: 11,
              maxHeight: 240,
              overflow: "auto",
              whiteSpace: "pre-wrap",
            }}
          >
            {rawLog.join("\n")}
          </pre>
        </details>
      )}
    </Section>
  );
}

function StepRow({ step }: { step: StepResultBlob }) {
  const tone = step.status === "ok" ? "ok" : step.status === "failed" ? "err" : "neutral";
  return (
    <div
      style={{
        display: "grid",
        gridTemplateColumns: "auto 1fr auto",
        gap: 12,
        alignItems: "center",
        padding: "10px 14px",
        background: "var(--glass-1)",
        border: "1px solid var(--glass-stroke-2)",
        borderRadius: 8,
      }}
    >
      <Pill tone={tone}>{step.status}</Pill>
      <div>
        <div style={{ fontFamily: "var(--f-mono)", fontSize: 13 }}>
          {step.call} <span style={{ color: "var(--text-3)" }}>[{step.id}]</span>
        </div>
        {step.error && (
          <div style={{ fontSize: 12, color: "#f0a89e", marginTop: 4 }}>
            <b>{step.error.kind}:</b> {step.error.message}
          </div>
        )}
      </div>
      {step.durationMs > 0 && (
        <span style={{ fontSize: 11, color: "var(--text-3)", fontFamily: "var(--f-mono)" }}>
          {step.durationMs}ms
        </span>
      )}
    </div>
  );
}

/**
 * The harness prints `__MZ_EVENT__ {...}` lines through Playwright's
 * reporter, which may prefix them with icons / paths / whitespace. Grep
 * for the sentinel anywhere on the line and parse the JSON that follows.
 * Non-matching lines return null and get shown only in the raw log.
 */
function parseMzEvent(line: string): LiveEvent | null {
  const idx = line.indexOf("__MZ_EVENT__ ");
  if (idx < 0) return null;
  const payload = line.slice(idx + "__MZ_EVENT__ ".length).trim();
  try {
    return JSON.parse(payload) as LiveEvent;
  } catch {
    return null;
  }
}

/**
 * Reconstruct a step list from the live event stream while the envelope
 * hasn't arrived yet. Once step-ok / step-failed appears, that's the final
 * per-step verdict; step-started alone becomes an "in progress" placeholder.
 */
function deriveStepsFromEvents(events: LiveEvent[]): StepResultBlob[] {
  const map = new Map<string, StepResultBlob>();
  for (const e of events) {
    if (!e.id) continue;
    if (e.type === "step-started") {
      if (!map.has(e.id)) {
        map.set(e.id, {
          id: e.id,
          call: e.call ?? "",
          status: "skipped",
          durationMs: 0,
          error: null,
        });
      }
    } else if (e.type === "step-ok" || e.type === "step-failed") {
      // e.error.kind is `string` on the wire (LiveEvent), but StepErrorKind
      // is the closed union documented in types.ts. Anything the harness
      // emits is already in that set — cast at this narrow boundary rather
      // than widening the whole StepResultBlob.
      const err: StepResultBlob["error"] = e.error
        ? { kind: e.error.kind as StepErrorKind, message: e.error.message }
        : null;
      map.set(e.id, {
        id: e.id,
        call: e.call ?? map.get(e.id)?.call ?? "",
        status: e.type === "step-ok" ? "ok" : "failed",
        durationMs: e.durationMs ?? 0,
        error: err,
      });
    }
  }
  return Array.from(map.values());
}

// ── Section helper (used by every pane below the prompt) ─────────────

function Section({
  title,
  badge,
  subtitle,
  children,
}: {
  title: string;
  badge?: string;
  subtitle?: string;
  children: React.ReactNode;
}) {
  return (
    <div style={{ marginTop: 32 }}>
      <div style={{ display: "flex", alignItems: "baseline", gap: 12, marginBottom: 12 }}>
        <div style={{ fontSize: 13, fontWeight: 500 }}>{title}</div>
        {badge && <Pill tone="neutral">{badge}</Pill>}
        {subtitle && <span style={{ fontSize: 11, color: "var(--text-3)", fontFamily: "var(--f-mono)" }}>{subtitle}</span>}
      </div>
      {children}
    </div>
  );
}

function parseBlob<T>(json: string | null | undefined): T | null {
  if (!json) return null;
  try {
    return JSON.parse(json) as T;
  } catch {
    return null;
  }
}
