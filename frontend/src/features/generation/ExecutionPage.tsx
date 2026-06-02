import { useEffect, useMemo, useRef, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { TopBar } from "@/shared/components/TopBar";
import {
  ScreenShell,
  BtnPrimary,
  BtnGhost,
  Pill,
  StepIndicator,
} from "@/shared/components/Primitives";
import { Icon } from "@/shared/icons/Icon";
import { useGeneration, useExecuteGeneration } from "./api";
import type { ExecutionStatus } from "@/shared/api/types";
import { ApiError } from "@/shared/api/client";

function statusTone(s: ExecutionStatus): "ok" | "err" | "warn" | "neutral" {
  switch (s) {
    case "Passed": return "ok";
    case "FailedTest":
    case "Errored": return "err";
    case "Running": return "warn";
    case "Cancelled": return "warn";
    default: return "neutral";
  }
}

export function ExecutionPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { data: gen, isLoading, refetch } = useGeneration(id);
  const execute = useExecuteGeneration();

  const [lines, setLines] = useState<string[]>([]);
  const [streaming, setStreaming] = useState(false);
  // Flag set while the user just clicked Run but the backend mutation +
  // refetch haven't flipped executionStatus to "Running" yet. Suppresses
  // the persisted-log fallback during that window so the previous run's
  // output doesn't flash on the screen.
  const [clearingForNewRun, setClearingForNewRun] = useState(false);
  const logRef = useRef<HTMLPreElement>(null);
  const esRef = useRef<EventSource | null>(null);

  // Auto-scroll to bottom whenever new lines come in.
  useEffect(() => {
    const el = logRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [lines]);

  // Open SSE when status is Running; close when terminal.
  useEffect(() => {
    if (!id || !gen) return;
    if (gen.executionStatus !== "Running") {
      // Show the persisted log if a finished run is being viewed.
      if (gen.executionStatus !== "Idle" && lines.length === 0) {
        setLines((gen.exitCode !== null ? [] : []));
      }
      return;
    }
    if (esRef.current) return;
    const es = new EventSource(`/api/generations/${id}/execution/stream`);
    esRef.current = es;
    setStreaming(true);
    es.onmessage = (e) => {
      try {
        const line = JSON.parse(e.data) as string;
        setLines((prev) => [...prev, line]);
      } catch {
        setLines((prev) => [...prev, e.data]);
      }
    };
    const finalize = () => {
      es.close();
      esRef.current = null;
      setStreaming(false);
      // Pull the final persisted record so we get exit code / terminal status.
      // Done on both `end` (clean close) and `error` (dropped connection) so a
      // failed run still unsticks the "Running" button.
      refetch();
    };
    es.addEventListener("end", finalize);
    es.onerror = finalize;
    return () => {
      es.close();
      esRef.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, gen?.executionStatus]);

  async function onRun() {
    if (!id) return;
    setLines([]);
    setClearingForNewRun(true);
    try {
      await execute.mutateAsync(id);
      // Re-fetch so executionStatus flips to Running and the SSE effect fires.
      await refetch();
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Execution failed to start.");
    } finally {
      setClearingForNewRun(false);
    }
  }

  const displayLines = useMemo(() => {
    // While a new run is being kicked off, OR the backend says we're Running,
    // show only the live streamed buffer. Falling back to the persisted log
    // here would briefly show the previous run's output.
    if (clearingForNewRun || gen?.executionStatus === "Running") return lines;
    if (gen?.executionLog) return gen.executionLog.split(/\r?\n/);
    return [];
  }, [lines, gen?.executionStatus, gen?.executionLog, clearingForNewRun]);

  if (isLoading || !gen) {
    return (
      <ScreenShell>
        <TopBar />
        <div style={{ padding: 28, color: "var(--text-3)" }}>Loading run…</div>
      </ScreenShell>
    );
  }

  const canRun = !!gen.finalScript?.trim() && gen.executionStatus !== "Running";

  return (
    <ScreenShell>
      <TopBar crumb="execution" />
      <div
        style={{
          padding: 28,
          display: "flex",
          flexDirection: "column",
          gap: 20,
          flex: 1,
          overflow: "hidden",
        }}
      >
        <StepIndicator step={9} />
        <div
          style={{
            display: "grid",
            gridTemplateColumns: "320px 1fr",
            gap: 24,
            flex: 1,
            minHeight: 0,
          }}
        >
          {/* ── LEFT — run controls + status ── */}
          <div style={{ display: "flex", flexDirection: "column", gap: 20, minHeight: 0 }}>
            <div
              className="mz-glass"
              style={{
                padding: 20,
                display: "flex",
                flexDirection: "column",
                gap: 14,
                flex: 1,
                minHeight: 0,
                overflow: "auto",
              }}
            >
              <div className="mz-eyebrow">Step 9 · Execution</div>
              <div style={{ color: "var(--text-3)", fontSize: 12.5, lineHeight: 1.5 }}>
                Mazda runs the merged spec via <code style={{ fontFamily: "var(--f-mono)" }}>
                npx playwright test</code> inside the API container and streams the terminal
                output live. <code style={{ fontFamily: "var(--f-mono)" }}>127.0.0.1</code> and{" "}
                <code style={{ fontFamily: "var(--f-mono)" }}>localhost</code> are auto-rewritten
                to <code style={{ fontFamily: "var(--f-mono)" }}>host.docker.internal</code> so
                CDP attach reaches a browser on your host.
              </div>

              <div className="mz-divider" />

              <SummaryRow label="Execution" value={<Pill tone={statusTone(gen.executionStatus)}>{gen.executionStatus}</Pill>} />
              <SummaryRow label="Exit code" value={gen.exitCode === null ? "—" : String(gen.exitCode)} />
              <SummaryRow
                label="Started"
                value={gen.executionStartedAt ? new Date(gen.executionStartedAt).toLocaleString() : "—"}
              />
              <SummaryRow
                label="Ended"
                value={gen.executionEndedAt ? new Date(gen.executionEndedAt).toLocaleString() : "—"}
              />
              {streaming && (
                <div
                  style={{
                    fontSize: 11,
                    color: "var(--orange-100)",
                    fontFamily: "var(--f-mono)",
                  }}
                >
                  ● streaming live…
                </div>
              )}

              <BtnPrimary
                full
                icon={Icon.spark()}
                onClick={onRun}
                disabled={!canRun || execute.isPending}
              >
                {gen.executionStatus === "Running"
                  ? "Running…"
                  : gen.executionStatus === "Idle"
                  ? "Run script"
                  : "Run again"}
              </BtnPrimary>
            </div>

            <div style={{ display: "flex", gap: 10 }}>
              <BtnGhost onClick={() => navigate(`/runs/${id}/merge`)}>Back to merge</BtnGhost>
              <BtnGhost full onClick={() => navigate("/")}>
                Studio
              </BtnGhost>
            </div>
          </div>

          {/* ── RIGHT — terminal ── */}
          <div style={{ display: "flex", flexDirection: "column", gap: 12, minHeight: 0 }}>
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
              <div className="mz-eyebrow">Console</div>
              <div style={{ fontFamily: "var(--f-mono)", fontSize: 11, color: "var(--text-4)" }}>
                {displayLines.length} {displayLines.length === 1 ? "line" : "lines"}
              </div>
            </div>
            <pre
              ref={logRef}
              className="mz-scroll"
              style={{
                flex: 1,
                minHeight: 0,
                margin: 0,
                padding: 16,
                background: "#08060a",
                border: "1px solid var(--glass-stroke)",
                borderRadius: 10,
                fontFamily: "var(--f-mono)",
                fontSize: 12,
                lineHeight: 1.5,
                color: "#dcd6c8",
                overflow: "auto",
                whiteSpace: "pre-wrap",
                wordBreak: "break-word",
              }}
            >
              {displayLines.length === 0
                ? "(no output yet — hit \"Run script\" to start)"
                : displayLines.join("\n")}
            </pre>
          </div>
        </div>
      </div>
    </ScreenShell>
  );
}

function SummaryRow({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12 }}>
      <span className="mz-eyebrow" style={{ fontSize: 9.5 }}>{label}</span>
      <span style={{ fontSize: 12.5, color: "var(--text-1)", textAlign: "right" }}>{value}</span>
    </div>
  );
}
