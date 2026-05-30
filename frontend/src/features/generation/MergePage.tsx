import { useMemo } from "react";
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
import { useGeneration, useMergeGeneration, useSaveFinalScript } from "./api";
import { ScriptEditor } from "./ScriptEditor";
import { ApiError } from "@/shared/api/client";

export function MergePage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { data: gen, isLoading } = useGeneration(id);
  const merge = useMergeGeneration();
  const save = useSaveFinalScript();

  const fileName = useMemo(() => {
    if (!gen) return "mazda-run.spec.ts";
    const slug = gen.testPrompt
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-+|-+$/g, "")
      .slice(0, 40) || "run";
    return `${slug}.spec.ts`;
  }, [gen]);

  async function onMerge() {
    if (!id) return;
    try {
      await merge.mutateAsync(id);
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Merge failed.");
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

  function copy() {
    if (gen?.finalScript) navigator.clipboard.writeText(gen.finalScript);
  }

  function download() {
    if (!gen?.finalScript) return;
    const blob = new Blob([gen.finalScript], { type: "text/typescript" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = fileName;
    a.click();
    URL.revokeObjectURL(url);
  }

  if (isLoading || !gen) {
    return (
      <ScreenShell>
        <TopBar />
        <div style={{ padding: 28, color: "var(--text-3)" }}>Loading run…</div>
      </ScreenShell>
    );
  }

  const canMerge = !!gen.setupScript?.trim() && !!gen.testScript?.trim();

  return (
    <ScreenShell>
      <TopBar crumb={fileName} />
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
            gridTemplateColumns: "440px 1fr",
            gap: 24,
            flex: 1,
            minHeight: 0,
          }}
        >
        {/* ── LEFT — summary + merge action ── */}
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
            // eslint-disable-next-line react/no-unknown-property
          >
            <div className="mz-eyebrow">Step 6 · Merge & finalize</div>
            <div style={{ color: "var(--text-3)", fontSize: 12.5, lineHeight: 1.5 }}>
              Mazda will reconcile imports, fixtures, and the test body into a single
              ready-to-run <code style={{ fontFamily: "var(--f-mono)" }}>.spec.ts</code>.
              Review and edit the result before downloading.
            </div>

            <div className="mz-divider" />

            <SummaryBlock label="Setup script" ready={!!gen.setupScript?.trim()} />
            <SummaryBlock label="Test script" ready={!!gen.testScript?.trim()} />
            <SummaryBlock label="Final script" ready={!!gen.finalScript?.trim()} />

            <div className="mz-divider" />

            <SummaryRow label="Status" value={<Pill tone={gen.status === "Ready" ? "ok" : gen.status === "Failed" ? "err" : "warn"}>{gen.status}</Pill>} />
            <SummaryRow label="Tokens used" value={gen.tokensUsed.toLocaleString()} />
            <SummaryRow label="Updated" value={new Date(gen.updatedAt).toLocaleString()} />
            {gen.failureReason && (
              <div style={{ fontSize: 12, color: "var(--err)", fontFamily: "var(--f-mono)" }}>
                {gen.failureReason}
              </div>
            )}

            <BtnPrimary
              full
              icon={Icon.spark()}
              onClick={onMerge}
              disabled={!canMerge || merge.isPending}
            >
              {merge.isPending
                ? "Merging…"
                : gen.finalScript
                ? "Re-merge"
                : "Merge setup + test"}
            </BtnPrimary>
          </div>

          <div style={{ display: "flex", gap: 10 }}>
            <BtnGhost onClick={() => navigate(`/runs/${id}/test`)}>Back to test</BtnGhost>
            <BtnGhost
              icon={Icon.download()}
              onClick={download}
              disabled={!gen.finalScript}
            >
              Download
            </BtnGhost>
            <BtnPrimary
              full
              icon={Icon.arrow()}
              onClick={() => navigate(`/runs/${id}/execute`)}
              disabled={!gen.finalScript?.trim()}
            >
              Continue to execution
            </BtnPrimary>
          </div>
        </div>

        {/* ── RIGHT — merged spec ── */}
        <div style={{ display: "flex", flexDirection: "column", gap: 12, minHeight: 0 }}>
          <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
            <div className="mz-eyebrow">Final spec</div>
            {gen.finalScript && (
              <button
                onClick={copy}
                style={{
                  background: "none",
                  border: "none",
                  color: "var(--orange-200)",
                  cursor: "pointer",
                  fontFamily: "var(--f-mono)",
                  fontSize: 11,
                  letterSpacing: "0.04em",
                }}
              >
                Copy to clipboard →
              </button>
            )}
          </div>
          {gen.finalScript || merge.isPending ? (
            <ScriptEditor
              fileName={fileName}
              initialValue={gen.finalScript}
              onSave={onSave}
              saving={save.isPending}
              busy={merge.isPending}
              busyLabel="Merging…"
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
                Hit <strong style={{ color: "var(--orange-100)" }}>Merge</strong> to
                combine your setup and test into one ready-to-run spec.
              </div>
            </div>
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

function SummaryRow({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12 }}>
      <span className="mz-eyebrow" style={{ fontSize: 9.5 }}>{label}</span>
      <span style={{ fontSize: 12.5, color: "var(--text-1)", textAlign: "right" }}>{value}</span>
    </div>
  );
}
