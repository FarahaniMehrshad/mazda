import { useEffect, useState } from "react";
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
import { useSignature, useSaveSignature, useReanalyzeInteractive } from "./api";
import type { SignatureElementDto } from "@/shared/api/types";
import { ApiError } from "@/shared/api/client";

interface DraftShape {
  name: string;
  type: string;
  // Only the interactive subset of elements is edited here.
  elements: SignatureElementDto[];
}

const isInteractive = (e: SignatureElementDto) =>
  (e.category || "Interactive").toLowerCase() !== "informational";

export function EditorPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { data: signature, isLoading } = useSignature(id);
  const save = useSaveSignature();
  const reanalyze = useReanalyzeInteractive();

  const [draft, setDraft] = useState<string>("");
  const [parseError, setParseError] = useState<string | null>(null);

  useEffect(() => {
    if (signature && !draft) {
      const shape: DraftShape = {
        name: signature.name,
        type: signature.type,
        // Edit only the interactive slice; informational lives on step 3.
        elements: signature.elements.filter(isInteractive),
      };
      setDraft(JSON.stringify(shape, null, 2));
    }
  }, [signature, draft]);

  function tryParse(): DraftShape | null {
    try {
      const parsed = JSON.parse(draft);
      setParseError(null);
      return parsed;
    } catch (e) {
      setParseError((e as Error).message);
      return null;
    }
  }

  async function onSave() {
    if (!signature) return;
    const parsed = tryParse();
    if (!parsed) return;
    // Merge edited interactive elements with untouched informational ones.
    const informational = signature.elements.filter((e) => !isInteractive(e));
    const editedInteractive = (parsed.elements ?? []).map((e) => ({
      ...e,
      category: e.category || "Interactive",
    }));
    await save.mutateAsync({
      id: signature.id,
      name: parsed.name,
      type: parsed.type,
      source: signature.source,
      elements: [...editedInteractive, ...informational],
    });
  }

  function onContinue() {
    if (signature) navigate(`/signatures/${signature.id}/informational`);
  }

  async function onReanalyze() {
    if (!signature) return;
    if (
      !confirm(
        "Re-run the LLM on the original source? This will REPLACE the interactive elements below. Your informational elements (step 3) are preserved.",
      )
    )
      return;
    try {
      await reanalyze.mutateAsync(signature.id);
      // Force the draft to re-seed from the fresh signature on next render.
      // (The mount-time effect only seeds when `draft` is empty.)
      setDraft("");
      setParseError(null);
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Re-analysis failed.");
    }
  }

  if (isLoading || !signature) {
    return (
      <ScreenShell>
        <TopBar />
        <div style={{ padding: 28, color: "var(--text-3)" }}>Loading signature…</div>
      </ScreenShell>
    );
  }

  const interactive = signature.elements.filter(isInteractive);

  return (
    <ScreenShell>
      <TopBar crumb={`${signature.name} · interactable`} />
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
        <StepIndicator step={2} />
        <div
          style={{
            display: "grid",
            gridTemplateColumns: "1.2fr 1fr",
            gap: 24,
            flex: 1,
            minHeight: 0,
          }}
        >
          {/* ── LEFT — JSON editor (interactive only) ── */}
          <div style={{ display: "flex", flexDirection: "column", gap: 16, minHeight: 0 }}>
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
                  fontFamily: "var(--f-mono)",
                  fontSize: 11.5,
                  color: "var(--text-3)",
                  display: "flex",
                  justifyContent: "space-between",
                  alignItems: "center",
                }}
              >
                <span>{signature.source || "signature.json"} · interactive</span>
                <Pill tone="orange">v{signature.version}</Pill>
              </div>
              <div style={{ flex: 1, minHeight: 0 }}>
                <Editor
                  height="100%"
                  defaultLanguage="json"
                  theme="vs-dark"
                  value={draft}
                  onChange={(v) => setDraft(v ?? "")}
                  options={{
                    fontSize: 12.5,
                    fontFamily: "JetBrains Mono, monospace",
                    minimap: { enabled: false },
                    scrollBeyondLastLine: false,
                    tabSize: 2,
                  }}
                />
              </div>
            </div>
            {parseError && (
              <div
                style={{
                  fontSize: 12,
                  color: "var(--err)",
                  fontFamily: "var(--f-mono)",
                }}
              >
                JSON parse error: {parseError}
              </div>
            )}
          </div>

          {/* ── RIGHT — interactive elements preview only ── */}
          <div style={{ display: "flex", flexDirection: "column", gap: 16, minHeight: 0 }}>
            <div
              className="mz-scroll"
              style={{
                flex: 1,
                minHeight: 0,
                overflow: "auto",
                display: "flex",
                flexDirection: "column",
                gap: 16,
              }}
            >
              <ElementSection
                title={`Interactive · ${interactive.length}`}
                elements={interactive}
                empty="No interactive elements detected."
              />
            </div>

            <div style={{ display: "flex", gap: 10, justifyContent: "flex-end", flexWrap: "wrap" }}>
              <BtnGhost
                icon={Icon.spark()}
                onClick={onReanalyze}
                disabled={reanalyze.isPending || save.isPending}
              >
                {reanalyze.isPending ? "Re-analyzing…" : "Re-analyze interactive"}
              </BtnGhost>
              <BtnGhost onClick={onSave} disabled={save.isPending || reanalyze.isPending}>
                {save.isPending ? "Saving…" : "Save changes"}
              </BtnGhost>
              <BtnPrimary icon={Icon.arrow()} onClick={onContinue} disabled={reanalyze.isPending}>
                Continue to informational
              </BtnPrimary>
            </div>
          </div>
        </div>
      </div>
    </ScreenShell>
  );
}

function ElementSection({
  title,
  elements,
  empty,
}: {
  title: string;
  elements: SignatureElementDto[];
  empty: string;
}) {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
      <div className="mz-eyebrow">{title}</div>
      <div
        className="mz-glass"
        style={{ padding: 0, overflow: "hidden" }}
      >
        {elements.length === 0 ? (
          <div style={{ padding: 18, color: "var(--text-4)", fontSize: 12 }}>
            {empty}
          </div>
        ) : (
          elements.map((el, i) => <ElementRow key={i} index={i} el={el} />)
        )}
      </div>
    </div>
  );
}

function ElementRow({ index, el }: { index: number; el: SignatureElementDto }) {
  return (
    <div
      style={{
        padding: "14px 18px",
        borderBottom: "1px solid var(--glass-stroke)",
        display: "grid",
        gridTemplateColumns: "32px 1fr auto",
        gap: 12,
        alignItems: "center",
      }}
    >
      <div
        style={{
          fontFamily: "var(--f-mono)",
          fontSize: 10,
          color: "var(--text-4)",
        }}
      >
        {String(index + 1).padStart(2, "0")}
      </div>
      <div>
        <div
          style={{
            fontFamily: "var(--f-mono)",
            fontSize: 12.5,
            color: "var(--orange-100)",
          }}
        >
          {el.selector}
        </div>
        <div style={{ fontSize: 11.5, color: "var(--text-3)", marginTop: 2 }}>
          {el.description || <em style={{ opacity: 0.5 }}>no description</em>}
        </div>
        {el.logic && el.logic.trim().length > 0 && (
          <div
            style={{
              fontSize: 11,
              color: "var(--text-4)",
              marginTop: 6,
              paddingLeft: 8,
              borderLeft: "2px solid rgba(234,112,40,0.25)",
              lineHeight: 1.45,
              whiteSpace: "pre-wrap",
            }}
          >
            <span
              style={{
                fontFamily: "var(--f-mono)",
                fontSize: 9.5,
                color: "var(--orange-200)",
                letterSpacing: "0.04em",
                display: "block",
                marginBottom: 2,
              }}
            >
              LOGIC
            </span>
            {el.logic}
          </div>
        )}
      </div>
      <Pill>{el.elementType}</Pill>
    </div>
  );
}
