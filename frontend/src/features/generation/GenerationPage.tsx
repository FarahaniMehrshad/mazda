import { useMemo } from "react";
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
import { useGeneration } from "./api";

export function GenerationPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { data: gen, isLoading } = useGeneration(id);

  const fileName = useMemo(() => {
    if (!gen) return "mazda-run.spec.ts";
    const slug = gen.prompt
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-+|-+$/g, "")
      .slice(0, 40) || "run";
    return `${slug}.spec.ts`;
  }, [gen]);

  function copy() {
    if (gen?.script) {
      navigator.clipboard.writeText(gen.script);
    }
  }

  function download() {
    if (!gen?.script) return;
    const blob = new Blob([gen.script], { type: "text/typescript" });
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

  return (
    <ScreenShell>
      <TopBar crumb={fileName} />
      <div
        style={{
          padding: 28,
          display: "grid",
          gridTemplateColumns: "1fr 360px",
          gap: 24,
          flex: 1,
          overflow: "hidden",
        }}
      >
        {/* ── LEFT — generated script ── */}
        <div style={{ display: "flex", flexDirection: "column", gap: 16, minHeight: 0 }}>
          <StepIndicator step={4} />
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
            <div
              style={{
                padding: "10px 16px",
                borderBottom: "1px solid var(--glass-stroke)",
                display: "flex",
                justifyContent: "space-between",
                alignItems: "center",
                fontFamily: "var(--f-mono)",
                fontSize: 11.5,
                color: "var(--text-3)",
              }}
            >
              <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
                <Pill tone="orange" icon={Icon.code(10)}>
                  playwright · typescript
                </Pill>
                <span>{fileName}</span>
              </div>
              <div style={{ display: "flex", gap: 6 }}>
                <button
                  onClick={copy}
                  style={ghostBtn}
                  title="Copy to clipboard"
                >
                  {Icon.copy()}
                </button>
                <button onClick={download} style={ghostBtn} title="Download">
                  {Icon.download()}
                </button>
              </div>
            </div>

            <div style={{ flex: 1, minHeight: 0 }}>
              {gen.status === "Ready" && gen.script ? (
                <Editor
                  height="100%"
                  defaultLanguage="typescript"
                  theme="vs-dark"
                  value={gen.script}
                  options={{
                    fontSize: 12.5,
                    fontFamily: "JetBrains Mono, monospace",
                    minimap: { enabled: false },
                    readOnly: true,
                    scrollBeyondLastLine: false,
                  }}
                />
              ) : gen.status === "Failed" ? (
                <div
                  style={{
                    padding: 32,
                    color: "var(--err)",
                    fontFamily: "var(--f-mono)",
                    fontSize: 12.5,
                  }}
                >
                  Generation failed: {gen.failureReason ?? "unknown reason"}
                </div>
              ) : (
                <div
                  style={{
                    padding: 32,
                    color: "var(--text-3)",
                    fontFamily: "var(--f-mono)",
                    fontSize: 12.5,
                  }}
                >
                  Still working on it…
                </div>
              )}
            </div>
          </div>
        </div>

        {/* ── RIGHT — run instructions ── */}
        <div style={{ display: "flex", flexDirection: "column", gap: 16, overflow: "auto" }} className="mz-scroll">
          <div className="mz-eyebrow">Run summary</div>
          <div className="mz-glass" style={{ padding: 18, display: "flex", flexDirection: "column", gap: 12 }}>
            <SummaryRow label="Status" value={<Pill tone={gen.status === "Ready" ? "ok" : gen.status === "Failed" ? "err" : "warn"}>{gen.status}</Pill>} />
            <SummaryRow label="Target" value={<code style={{ fontFamily: "var(--f-mono)", fontSize: 11.5 }}>{gen.targetUrl}</code>} />
            <SummaryRow label="Tokens" value={gen.tokensUsed.toLocaleString()} />
            <SummaryRow label="Created" value={new Date(gen.createdAt).toLocaleString()} />
          </div>

          <div className="mz-eyebrow">Prompt</div>
          <div
            className="mz-glass"
            style={{
              padding: 14,
              fontSize: 12.5,
              color: "var(--text-2)",
              lineHeight: 1.55,
            }}
          >
            {gen.prompt}
          </div>

          <div className="mz-eyebrow">Run locally</div>
          <div
            className="mz-glass"
            style={{
              padding: 14,
              fontFamily: "var(--f-mono)",
              fontSize: 12,
              color: "var(--text-2)",
              display: "flex",
              flexDirection: "column",
              gap: 8,
            }}
          >
            <Line>npm init playwright@latest</Line>
            <Line># put the file into the tests/ folder</Line>
            <Line>npx playwright test {fileName}</Line>
          </div>

          <div style={{ display: "flex", gap: 10 }}>
            <BtnGhost full onClick={() => navigate("/")}>
              Back to studio
            </BtnGhost>
            <BtnPrimary full icon={Icon.download()} onClick={download} disabled={!gen.script}>
              Download
            </BtnPrimary>
          </div>
        </div>
      </div>
    </ScreenShell>
  );
}

const ghostBtn: React.CSSProperties = {
  background: "var(--glass-1)",
  border: "1px solid var(--glass-stroke-2)",
  color: "var(--text-2)",
  borderRadius: 6,
  padding: "4px 8px",
  cursor: "pointer",
  display: "inline-flex",
  alignItems: "center",
  justifyContent: "center",
};

function SummaryRow({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12 }}>
      <span className="mz-eyebrow" style={{ fontSize: 9.5 }}>
        {label}
      </span>
      <span style={{ fontSize: 12.5, color: "var(--text-1)", textAlign: "right" }}>{value}</span>
    </div>
  );
}

function Line({ children }: { children: React.ReactNode }) {
  return <div style={{ whiteSpace: "pre-wrap" }}>{children}</div>;
}
