import { useEffect, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { TopBar } from "@/shared/components/TopBar";
import {
  ScreenShell,
  BtnPrimary,
  BtnGhost,
  StepIndicator,
} from "@/shared/components/Primitives";
import { Icon } from "@/shared/icons/Icon";
import { useGeneration, useGenerateSetup, useSaveSetupScript } from "./api";
import { ScriptEditor } from "./ScriptEditor";
import { ApiError } from "@/shared/api/client";
import { useStepNavigator } from "@/shared/components/useStepNavigator";

const SAMPLE_PROMPTS = [
  "Open https://app.example.com/checkout and wait for the cart icon to appear.",
  "Connect to a Chrome already running on localhost:9222 via CDP and use the first existing context's first page.",
  "Launch a headed Chromium with viewport 1920x1080, navigate to https://staging.example.com, and accept the cookie banner.",
];

export function SetupPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { data: gen, isLoading } = useGeneration(id);
  const generate = useGenerateSetup();
  const save = useSaveSetupScript();
  // Must be called before any early-return so the hook count stays stable
  // across the loading → loaded transition (React error #310 otherwise).
  const onStepClick = useStepNavigator(
    id && gen ? { mode: "run", runId: id, signatureId: gen.signatureId } : null,
  );
  const [prompt, setPrompt] = useState("");

  useEffect(() => {
    if (gen?.setupPrompt) setPrompt(gen.setupPrompt);
  }, [gen?.setupPrompt]);

  async function onGenerate() {
    if (!id || !prompt.trim()) return;
    try {
      await generate.mutateAsync({ id, setupPrompt: prompt });
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Setup generation failed.");
    }
  }

  async function onSave(script: string) {
    if (!id) return;
    try {
      await save.mutateAsync({ id, script });
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Save failed.");
    }
  }

  if (isLoading || !gen) {
    return (
      <ScreenShell>
        <TopBar />
        <div style={{ padding: 28, color: "var(--text-3)" }}>Loading run…</div>
      </ScreenShell>
    );
  }

  const canContinue = !!gen.setupScript && gen.setupScript.trim().length > 0;

  return (
    <ScreenShell>
      <TopBar crumb="setup" />
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
        <StepIndicator step={5} onStepClick={onStepClick} />
        <div
          style={{
            display: "grid",
            gridTemplateColumns: "440px 1fr",
            gap: 24,
            flex: 1,
            minHeight: 0,
          }}
        >
        {/* ── LEFT — setup prompt ── */}
        <div style={{ display: "flex", flexDirection: "column", gap: 20, minHeight: 0 }}>
          <div
            className="mz-glass"
            style={{
              padding: 20,
              display: "flex",
              flexDirection: "column",
              gap: 12,
              flex: 1,
              minHeight: 0,
            }}
          >
            <div className="mz-eyebrow">Step 5 · Setup</div>
            <div style={{ color: "var(--text-3)", fontSize: 12.5, lineHeight: 1.5 }}>
              Describe what should happen <em>before</em> the test runs. Mazda turns this
              into Playwright setup code — hooks, fixtures, or a CDP attach.
            </div>
            <textarea
              value={prompt}
              onChange={(e) => setPrompt(e.target.value)}
              placeholder="e.g. Connect to Chrome on localhost:9222 via CDP and use the first page."
              style={{
                width: "100%",
                flex: 1,
                minHeight: 220,
                resize: "vertical",
                padding: 14,
                background: "var(--glass-1)",
                border: "1px solid var(--glass-stroke-2)",
                borderRadius: 10,
                color: "var(--text-1)",
                fontFamily: "var(--f-sans)",
                fontSize: 13.5,
                outline: "none",
              }}
            />
            <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
              {SAMPLE_PROMPTS.map((p) => (
                <button
                  key={p}
                  onClick={() => setPrompt(p)}
                  style={{
                    fontSize: 11,
                    fontFamily: "var(--f-mono)",
                    padding: "4px 10px",
                    borderRadius: 999,
                    background: "var(--glass-1)",
                    border: "1px solid var(--glass-stroke)",
                    color: "var(--text-3)",
                    cursor: "pointer",
                    textAlign: "left",
                  }}
                >
                  {p.length > 60 ? p.slice(0, 60) + "…" : p}
                </button>
              ))}
            </div>
            <div style={{ fontFamily: "var(--f-mono)", fontSize: 11, color: "var(--text-4)", textAlign: "right" }}>
              {prompt.length.toLocaleString()} chars
            </div>
            <BtnPrimary
              full
              icon={Icon.spark()}
              onClick={onGenerate}
              disabled={!prompt.trim() || generate.isPending}
            >
              {generate.isPending
                ? "Generating setup…"
                : gen.setupScript
                ? "Re-generate setup"
                : "Generate setup script"}
            </BtnPrimary>
          </div>

          <div style={{ display: "flex", gap: 10 }}>
            <BtnGhost onClick={() => navigate(`/signatures/${gen.signatureId}/flows`)}>
              Back to flows
            </BtnGhost>
            <BtnPrimary
              full
              icon={Icon.arrow()}
              onClick={() => navigate(`/runs/${id}/analyze-state`)}
              disabled={!canContinue}
            >
              Continue to analyze state
            </BtnPrimary>
          </div>
        </div>

        {/* ── RIGHT — generated setup script ── */}
        <div style={{ display: "flex", flexDirection: "column", gap: 12, minHeight: 0 }}>
          <div className="mz-eyebrow">Generated setup script</div>
          {gen.setupScript || generate.isPending ? (
            <ScriptEditor
              fileName="setup.ts"
              initialValue={gen.setupScript}
              onSave={onSave}
              saving={save.isPending}
              busy={generate.isPending}
              busyLabel="Generating setup…"
            />
          ) : (
            <div
              className="mz-glass"
              style={{
                flex: 1,
                display: "grid",
                placeItems: "center",
                padding: 32,
              }}
            >
              <div
                style={{
                  color: "var(--text-4)",
                  fontSize: 13,
                  textAlign: "center",
                  lineHeight: 1.6,
                  maxWidth: 380,
                }}
              >
                Describe your setup on the left and hit{" "}
                <strong style={{ color: "var(--orange-100)" }}>Generate</strong> — the
                script will appear here for you to review.
              </div>
            </div>
          )}
        </div>
        </div>
      </div>
    </ScreenShell>
  );
}
