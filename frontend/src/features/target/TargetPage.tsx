import { useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { TopBar } from "@/shared/components/TopBar";
import {
  ScreenShell,
  BtnPrimary,
  BtnGhost,
  Pill,
  StepIndicator,
} from "@/shared/components/Primitives";
import { Icon } from "@/shared/icons/Icon";
import { useSignatures } from "@/features/signatures/api";
import { useGenerateScript } from "@/features/generation/api";
import { ApiError } from "@/shared/api/client";

const SAMPLE_PROMPTS = [
  "Fill the form with realistic test data and submit it.",
  "Enter a Stripe test card (4242 4242 4242 4242, 12/34, 123) and complete checkout.",
  "Click each tab in turn, asserting the right panel appears.",
];

export function TargetPage() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const { data: signatures } = useSignatures();
  const generate = useGenerateScript();

  const initialSig = params.get("signature") ?? "";
  const [signatureId, setSignatureId] = useState(initialSig);
  const [url, setUrl] = useState("");
  const [prompt, setPrompt] = useState("");

  const selected = signatures?.find((s) => s.id === signatureId);

  async function onRun() {
    if (!signatureId || !url || !prompt) return;
    try {
      const gen = await generate.mutateAsync({ signatureId, targetUrl: url, prompt });
      navigate(`/runs/${gen.id}`);
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Generation failed.");
    }
  }

  return (
    <ScreenShell>
      <TopBar crumb={selected ? `run · ${selected.name}` : "run"} />
      <div
        style={{
          padding: 28,
          display: "grid",
          gridTemplateColumns: "1fr 520px",
          gap: 24,
          flex: 1,
          overflow: "auto",
        }}
        className="mz-scroll"
      >
        {/* ── LEFT — composition ── */}
        <div style={{ display: "flex", flexDirection: "column", gap: 20 }}>
          <StepIndicator step={3} />

          {/* Signature picker */}
          <div className="mz-glass" style={{ padding: 20 }}>
            <div
              style={{
                display: "flex",
                alignItems: "baseline",
                justifyContent: "space-between",
                marginBottom: 14,
              }}
            >
              <div className="mz-eyebrow">Step 1 · Choose signature</div>
            </div>

            {signatures && signatures.length > 0 ? (
              <select
                value={signatureId}
                onChange={(e) => setSignatureId(e.target.value)}
                style={{
                  width: "100%",
                  padding: "10px 12px",
                  background: "var(--glass-1)",
                  border: "1px solid var(--glass-stroke-2)",
                  borderRadius: 8,
                  color: "var(--text-1)",
                  fontFamily: "var(--f-mono)",
                  fontSize: 13,
                  outline: "none",
                }}
              >
                <option value="">— select a signature —</option>
                {signatures.map((s) => (
                  <option key={s.id} value={s.id} style={{ background: "#0d0907" }}>
                    {s.name} · {s.elements.length} nodes · v{s.version}
                  </option>
                ))}
              </select>
            ) : (
              <div
                style={{
                  color: "var(--text-3)",
                  fontSize: 13,
                  padding: 12,
                  background: "var(--glass-1)",
                  borderRadius: 8,
                }}
              >
                You don't have any signatures yet.{" "}
                <button
                  onClick={() => navigate("/signatures/new")}
                  style={{
                    background: "none",
                    border: "none",
                    color: "var(--orange-200)",
                    cursor: "pointer",
                    padding: 0,
                    fontFamily: "inherit",
                  }}
                >
                  Create one first.
                </button>
              </div>
            )}

            {selected && (
              <div style={{ marginTop: 12, display: "flex", gap: 6, flexWrap: "wrap" }}>
                <Pill tone="orange">{selected.type}</Pill>
                <Pill>{selected.elements.length} nodes</Pill>
                <Pill>v{selected.version}</Pill>
              </div>
            )}
          </div>

          {/* Target URL */}
          <div className="mz-glass" style={{ padding: 20 }}>
            <div className="mz-eyebrow" style={{ marginBottom: 14 }}>
              Step 2 · Target URL
            </div>
            <div
              style={{
                display: "flex",
                alignItems: "center",
                gap: 10,
                padding: "10px 14px",
                background: "var(--glass-1)",
                border: "1px solid var(--glass-stroke-2)",
                borderRadius: 10,
              }}
            >
              <span style={{ color: "var(--text-3)" }}>{Icon.globe()}</span>
              <input
                type="url"
                value={url}
                onChange={(e) => setUrl(e.target.value)}
                placeholder="https://app.example.com/checkout"
                style={{
                  flex: 1,
                  background: "transparent",
                  border: "none",
                  outline: "none",
                  color: "var(--text-1)",
                  fontFamily: "var(--f-mono)",
                  fontSize: 13,
                }}
              />
            </div>
          </div>

          {/* Prompt */}
          <div className="mz-glass" style={{ padding: 20, flex: 1, minHeight: 280 }}>
            <div
              style={{
                display: "flex",
                justifyContent: "space-between",
                alignItems: "baseline",
                marginBottom: 14,
              }}
            >
              <div className="mz-eyebrow">Step 3 · Tell Mazda what to do</div>
              <div style={{ fontFamily: "var(--f-mono)", fontSize: 11, color: "var(--text-4)" }}>
                {prompt.length} / 2000
              </div>
            </div>
            <textarea
              value={prompt}
              onChange={(e) => setPrompt(e.target.value.slice(0, 2000))}
              placeholder="In plain English: what should happen on that page?"
              style={{
                width: "100%",
                minHeight: 180,
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
            <div style={{ marginTop: 10, display: "flex", gap: 6, flexWrap: "wrap" }}>
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
                  }}
                >
                  {p.length > 48 ? p.slice(0, 48) + "…" : p}
                </button>
              ))}
            </div>
          </div>
        </div>

        {/* ── RIGHT — summary + go ── */}
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <div className="mz-eyebrow">Plan preview</div>
          <div
            className="mz-glass-2"
            style={{
              padding: 20,
              display: "flex",
              flexDirection: "column",
              gap: 14,
              flex: 1,
            }}
          >
            <SummaryRow label="Signature" value={selected?.name ?? "—"} />
            <SummaryRow label="Type" value={selected?.type ?? "—"} />
            <SummaryRow label="Target" value={url || "—"} mono />
            <SummaryRow label="Nodes available" value={String(selected?.elements.length ?? 0)} />
            <div className="mz-divider" />
            <div style={{ fontSize: 12, color: "var(--text-3)" }}>
              Mazda will generate a Playwright TypeScript spec you can run locally with{" "}
              <code style={{ fontFamily: "var(--f-mono)", color: "var(--orange-100)" }}>
                npx playwright test
              </code>
              .
            </div>
            <BtnPrimary
              full
              icon={Icon.spark()}
              onClick={onRun}
              disabled={!signatureId || !url || !prompt || generate.isPending}
            >
              {generate.isPending ? "Generating…" : "Generate Playwright script"}
            </BtnPrimary>
            <BtnGhost full onClick={() => navigate(-1)}>
              Back
            </BtnGhost>
          </div>
        </div>
      </div>
    </ScreenShell>
  );
}

function SummaryRow({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", gap: 12 }}>
      <span className="mz-eyebrow" style={{ fontSize: 9.5 }}>
        {label}
      </span>
      <span
        style={{
          fontSize: 12.5,
          color: "var(--text-1)",
          fontFamily: mono ? "var(--f-mono)" : "var(--f-sans)",
          textAlign: "right",
          overflow: "hidden",
          textOverflow: "ellipsis",
          whiteSpace: "nowrap",
          maxWidth: "60%",
        }}
      >
        {value}
      </span>
    </div>
  );
}
