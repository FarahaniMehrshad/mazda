import { useEffect, useMemo, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import Editor from "@monaco-editor/react";
import { TopBar } from "@/shared/components/TopBar";
import {
  ScreenShell,
  BtnPrimary,
  BtnGhost,
  Pill,
  StepIndicator,
} from "@/shared/components/Primitives";
import { Icon } from "@/shared/icons/Icon";
import {
  useGeneration,
  useGenerateAnalyzeScript,
  useSaveAnalyzeScript,
  useRunAnalyze,
} from "./api";
import { ApiError } from "@/shared/api/client";

export function AnalyzeStatePage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { data: gen, isLoading } = useGeneration(id);
  const generate = useGenerateAnalyzeScript();
  const save = useSaveAnalyzeScript();
  const run = useRunAnalyze();

  // Local edit buffer for the analyze script. We seed it from the persisted
  // value the first time we get it, and re-sync whenever the persisted value
  // changes (e.g. after Generate / Run / external save).
  const [draft, setDraft] = useState<string>("");
  const [draftDirty, setDraftDirty] = useState(false);
  const persisted = gen?.analyzeStateScript ?? "";
  useEffect(() => {
    if (!draftDirty) setDraft(persisted);
  }, [persisted, draftDirty]);

  async function onGenerate() {
    if (!id) return;
    try {
      await generate.mutateAsync(id);
      setDraftDirty(false);
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Generate failed.");
    }
  }

  async function onSave() {
    if (!id) return;
    try {
      await save.mutateAsync({ id, script: draft });
      setDraftDirty(false);
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Save failed.");
    }
  }

  async function onRun() {
    if (!id) return;
    try {
      // If the user has unsaved edits, persist them first so the runner
      // executes what they see in the editor.
      if (draftDirty) await save.mutateAsync({ id, script: draft });
      await run.mutateAsync(id);
      setDraftDirty(false);
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Analyze run failed.");
    }
  }

  // Auto-persist any unsaved edits to the analyze script before navigating
  // away. Otherwise the dirty draft is lost the moment the user leaves the
  // page — which is the "analyze didn't persist" complaint.
  async function persistIfDirty() {
    if (!id || !draftDirty) return;
    try {
      await save.mutateAsync({ id, script: draft });
      setDraftDirty(false);
    } catch {
      // Surface but don't block navigation — the user has already chosen to leave.
      // (No alert here: avoid stacking modals while transitioning.)
    }
  }

  async function onContinue() {
    if (!id) return;
    await persistIfDirty();
    navigate(`/runs/${id}/test`);
  }

  async function onBack() {
    if (!id) return;
    await persistIfDirty();
    navigate(`/runs/${id}/setup`);
  }

  const prettyState = useMemo(() => {
    if (!gen?.analyzeStateJson) return "";
    try {
      return JSON.stringify(JSON.parse(gen.analyzeStateJson), null, 2);
    } catch {
      return gen.analyzeStateJson;
    }
  }, [gen?.analyzeStateJson]);

  if (isLoading || !gen) {
    return (
      <ScreenShell>
        <TopBar />
        <div style={{ padding: 28, color: "var(--text-3)" }}>Loading run…</div>
      </ScreenShell>
    );
  }

  const hasScript = !!persisted.trim();
  const hasState = !!gen.analyzeStateJson?.trim();
  const isBusy = generate.isPending || save.isPending || run.isPending;

  return (
    <ScreenShell>
      <TopBar crumb="analyze state" />
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
        <StepIndicator step={6} />
        <div
          style={{
            display: "grid",
            gridTemplateColumns: "340px 1fr 1fr",
            gap: 24,
            flex: 1,
            minHeight: 0,
          }}
        >
          {/* ── LEFT — actions + status ── */}
          <div style={{ display: "flex", flexDirection: "column", gap: 16, minHeight: 0 }}>
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
              <div className="mz-eyebrow">Step 6 · Analyze state</div>
              <div style={{ color: "var(--text-3)", fontSize: 12.5, lineHeight: 1.55 }}>
                Mazda builds a Playwright test that walks every <em>informational</em>
                element in the signature and captures its on-screen text. The
                test runs against the same setup as the main test. The captured
                text is then handed to the LLM, which produces a clean JSON of
                the current component state — used as ground-truth context in
                the next step.
              </div>

              <div className="mz-divider" />

              <SummaryBlock label="Script" ready={hasScript} />
              <SummaryBlock label="State JSON" ready={hasState} />

              <div className="mz-divider" />

              <BtnPrimary
                full
                icon={Icon.spark()}
                onClick={onGenerate}
                disabled={isBusy}
              >
                {generate.isPending
                  ? "Generating script…"
                  : hasScript
                  ? "Re-generate script"
                  : "Generate analyze script"}
              </BtnPrimary>

              <BtnGhost
                full
                icon={Icon.spark()}
                onClick={onRun}
                disabled={!hasScript || isBusy}
              >
                {run.isPending ? "Running…" : hasState ? "Re-run analyze" : "Run analyze"}
              </BtnGhost>

              {draftDirty && (
                <div style={{ fontSize: 11, color: "var(--orange-200)", fontFamily: "var(--f-mono)" }}>
                  ● unsaved edits
                </div>
              )}
            </div>

            <div style={{ display: "flex", gap: 10 }}>
              <BtnGhost onClick={onBack}>Back to setup</BtnGhost>
              <BtnPrimary
                full
                icon={Icon.arrow()}
                onClick={onContinue}
              >
                Continue to test prompt
              </BtnPrimary>
            </div>
          </div>

          {/* ── MIDDLE — analyze script (editable) ── */}
          <div style={{ display: "flex", flexDirection: "column", gap: 12, minHeight: 0 }}>
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
              <div className="mz-eyebrow">Analyze script</div>
              <BtnGhost onClick={onSave} disabled={!draftDirty || save.isPending}>
                {save.isPending ? "Saving…" : "Save edits"}
              </BtnGhost>
            </div>
            <div
              className="mz-glass-2"
              style={{
                flex: 1,
                minHeight: 0,
                padding: 0,
                overflow: "hidden",
                display: "flex",
                flexDirection: "column",
              }}
            >
              {hasScript ? (
                <Editor
                  height="100%"
                  defaultLanguage="typescript"
                  theme="vs-dark"
                  value={draft}
                  onChange={(v) => {
                    setDraft(v ?? "");
                    setDraftDirty(true);
                  }}
                  options={{
                    fontSize: 12,
                    fontFamily: "JetBrains Mono, monospace",
                    minimap: { enabled: false },
                    scrollBeyondLastLine: false,
                    tabSize: 2,
                  }}
                />
              ) : (
                <div
                  style={{
                    height: "100%",
                    display: "grid",
                    placeItems: "center",
                    padding: 24,
                    color: "var(--text-4)",
                    fontSize: 13,
                    textAlign: "center",
                  }}
                >
                  No analyze script yet. Hit{" "}
                  <strong style={{ color: "var(--orange-100)" }}>Generate</strong> on the
                  left.
                </div>
              )}
            </div>
          </div>

          {/* ── RIGHT — state JSON (read-only) ── */}
          <div style={{ display: "flex", flexDirection: "column", gap: 12, minHeight: 0 }}>
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
              <div className="mz-eyebrow">Component state · JSON</div>
              {hasState && <Pill tone="ok">ready</Pill>}
            </div>
            <div
              className="mz-glass-2"
              style={{
                flex: 1,
                minHeight: 0,
                padding: 0,
                overflow: "hidden",
                display: "flex",
                flexDirection: "column",
              }}
            >
              {hasState ? (
                <Editor
                  height="100%"
                  defaultLanguage="json"
                  theme="vs-dark"
                  value={prettyState}
                  options={{
                    readOnly: true,
                    fontSize: 12,
                    fontFamily: "JetBrains Mono, monospace",
                    minimap: { enabled: false },
                    scrollBeyondLastLine: false,
                    tabSize: 2,
                  }}
                />
              ) : (
                <div
                  style={{
                    height: "100%",
                    display: "grid",
                    placeItems: "center",
                    padding: 24,
                    color: "var(--text-4)",
                    fontSize: 13,
                    textAlign: "center",
                  }}
                >
                  No captured state yet. Run the analyze script to extract the
                  current UI state.
                </div>
              )}
            </div>
            {gen.analyzeStateRawOutput && (
              <details style={{ fontSize: 11, color: "var(--text-4)" }}>
                <summary style={{ cursor: "pointer", fontFamily: "var(--f-mono)" }}>
                  Raw runner output
                </summary>
                <pre
                  style={{
                    margin: "8px 0 0",
                    padding: 12,
                    background: "#08060a",
                    border: "1px solid var(--glass-stroke)",
                    borderRadius: 8,
                    maxHeight: 220,
                    overflow: "auto",
                    whiteSpace: "pre-wrap",
                    fontFamily: "var(--f-mono)",
                    fontSize: 11,
                    lineHeight: 1.5,
                    color: "#dcd6c8",
                  }}
                >
                  {gen.analyzeStateRawOutput}
                </pre>
              </details>
            )}
          </div>
        </div>
      </div>
    </ScreenShell>
  );
}

function SummaryBlock({ label, ready }: { label: string; ready: boolean }) {
  return (
    <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12 }}>
      <span style={{ fontSize: 12.5, color: "var(--text-2)" }}>{label}</span>
      <Pill tone={ready ? "ok" : "warn"}>{ready ? "ready" : "missing"}</Pill>
    </div>
  );
}
