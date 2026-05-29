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
import { useGeneration, useGenerateTest, useSaveTestScript } from "./api";
import { ScriptEditor } from "./ScriptEditor";
import { ApiError } from "@/shared/api/client";

const SAMPLE_PROMPTS = [
  "Fill the form with realistic test data and submit it.",
  "Click each tab in turn, asserting the right panel appears.",
  "Add a new mapping row, edit its output name to 'Total Amount', then save.",
];

export function TestPromptPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { data: gen, isLoading } = useGeneration(id);
  const generate = useGenerateTest();
  const save = useSaveTestScript();
  const [prompt, setPrompt] = useState("");

  useEffect(() => {
    if (gen?.testPrompt) setPrompt(gen.testPrompt);
  }, [gen?.testPrompt]);

  async function onGenerate() {
    if (!id || !prompt.trim()) return;
    try {
      await generate.mutateAsync({ id, testPrompt: prompt });
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Test generation failed.");
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

  const canContinue = !!gen.testScript && gen.testScript.trim().length > 0;

  return (
    <ScreenShell>
      <TopBar crumb="test prompt" />
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
        <StepIndicator step={4} />
        <div
          style={{
            display: "grid",
            gridTemplateColumns: "440px 1fr",
            gap: 24,
            flex: 1,
            minHeight: 0,
          }}
        >
        {/* ── LEFT — test prompt ── */}
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
            <div className="mz-eyebrow">Step 4 · Test prompt</div>
            <div style={{ color: "var(--text-3)", fontSize: 12.5, lineHeight: 1.5 }}>
              Tell Mazda what the test should actually <em>do</em>. The setup you wrote in
              the previous step provides the <code style={{ fontFamily: "var(--f-mono)" }}>page</code>.
            </div>
            <textarea
              value={prompt}
              onChange={(e) => setPrompt(e.target.value.slice(0, 4000))}
              placeholder="e.g. Add a row, set the output type to Number, and assert the chip shows '1 mapped'."
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
              {prompt.length} / 4000
            </div>
            <BtnPrimary
              full
              icon={Icon.spark()}
              onClick={onGenerate}
              disabled={!prompt.trim() || generate.isPending}
            >
              {generate.isPending
                ? "Generating test…"
                : gen.testScript
                ? "Re-generate test"
                : "Generate test script"}
            </BtnPrimary>
          </div>

          <div style={{ display: "flex", gap: 10 }}>
            <BtnGhost onClick={() => navigate(`/runs/${id}/setup`)}>Back to setup</BtnGhost>
            <BtnPrimary
              full
              icon={Icon.arrow()}
              onClick={() => navigate(`/runs/${id}/merge`)}
              disabled={!canContinue}
            >
              Continue to merge
            </BtnPrimary>
          </div>
        </div>

        {/* ── RIGHT — generated test script ── */}
        <div style={{ display: "flex", flexDirection: "column", gap: 12, minHeight: 0 }}>
          <div className="mz-eyebrow">Generated test script</div>
          {gen.testScript || generate.isPending ? (
            <ScriptEditor
              fileName="test.ts"
              initialValue={gen.testScript}
              onSave={onSave}
              saving={save.isPending}
              busy={generate.isPending}
              busyLabel="Generating test…"
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
                Write the test instructions on the left and hit{" "}
                <strong style={{ color: "var(--orange-100)" }}>Generate</strong>.
              </div>
            </div>
          )}
        </div>
        </div>
      </div>
    </ScreenShell>
  );
}
