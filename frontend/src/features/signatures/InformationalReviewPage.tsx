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
import { useSignature, useSaveSignature, useAnalyzeInformational } from "./api";
import { ApiError } from "@/shared/api/client";
import type { SignatureElementDto } from "@/shared/api/types";
import { useStepNavigator } from "@/shared/components/useStepNavigator";

interface DraftShape {
  name: string;
  type: string;
  // Only the informational subset is edited here.
  elements: SignatureElementDto[];
}

const isInformational = (e: SignatureElementDto) =>
  (e.category || "").toLowerCase() === "informational";

export function InformationalReviewPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { data: signature, isLoading } = useSignature(id);
  const save = useSaveSignature();
  const analyze = useAnalyzeInformational();
  // Must be called before any early-return so the hook count stays stable
  // across the loading → loaded transition (React error #310 otherwise).
  const onStepClick = useStepNavigator(
    signature ? { mode: "signature", signatureId: signature.id } : null,
  );

  // The draft is reset whenever the signature's informational element set
  // changes (e.g. after a deep-analyze pass).
  const informationalKey = signature
    ? signature.elements
        .filter(isInformational)
        .map((e) => `${e.selector}|${e.elementType}`)
        .join(",")
    : "";

  const [draft, setDraft] = useState<string>("");
  const [parseError, setParseError] = useState<string | null>(null);
  const [lastSyncedKey, setLastSyncedKey] = useState<string>("");

  useEffect(() => {
    if (!signature) return;
    if (informationalKey !== lastSyncedKey) {
      const shape: DraftShape = {
        name: signature.name,
        type: signature.type,
        elements: signature.elements.filter(isInformational),
      };
      setDraft(JSON.stringify(shape, null, 2));
      setLastSyncedKey(informationalKey);
    }
  }, [signature, informationalKey, lastSyncedKey]);

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
    const interactive = signature.elements.filter((e) => !isInformational(e));
    const editedInformational = (parsed.elements ?? []).map((e) => ({
      ...e,
      category: e.category || "Informational",
    }));
    await save.mutateAsync({
      id: signature.id,
      name: parsed.name,
      type: parsed.type,
      source: signature.source,
      elements: [...interactive, ...editedInformational],
    });
  }

  async function onDeepAnalyze() {
    if (!id) return;
    try {
      await analyze.mutateAsync(id);
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Deep analyze failed.");
    }
  }

  function onContinue() {
    if (signature) navigate(`/signatures/${signature.id}/relationships`);
  }

  function onBack() {
    if (signature) navigate(`/signatures/${signature.id}`);
  }

  if (isLoading || !signature) {
    return (
      <ScreenShell>
        <TopBar />
        <div style={{ padding: 28, color: "var(--text-3)" }}>Loading signature…</div>
      </ScreenShell>
    );
  }

  const informational = signature.elements.filter(isInformational);

  return (
    <ScreenShell>
      <TopBar crumb={`${signature.name} · informational`} />
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
        <StepIndicator step={3} onStepClick={onStepClick} />
        <div
          style={{
            display: "grid",
            gridTemplateColumns: "1.2fr 1fr",
            gap: 24,
            flex: 1,
            minHeight: 0,
          }}
        >
          {/* ── LEFT — JSON editor (informational only) ── */}
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
                <span>{signature.source || "signature.json"} · informational</span>
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

          {/* ── RIGHT — actions + informational preview ── */}
          <div style={{ display: "flex", flexDirection: "column", gap: 16, minHeight: 0 }}>
            <div
              className="mz-glass"
              style={{
                padding: 16,
                display: "flex",
                flexDirection: "column",
                gap: 12,
              }}
            >
              <div className="mz-eyebrow">Deep analysis</div>
              <div style={{ color: "var(--text-3)", fontSize: 12.5, lineHeight: 1.5 }}>
                A dedicated pass over the component focused on read targets
                only — lists, repeating containers, status displays, dynamic
                values, conditional text. Replaces the current informational
                subset; interactive elements stay untouched.
              </div>
              <BtnPrimary
                full
                icon={Icon.spark()}
                onClick={onDeepAnalyze}
                disabled={analyze.isPending}
              >
                {analyze.isPending
                  ? "Analyzing…"
                  : informational.length === 0
                  ? "Run deep analyze"
                  : "Re-run deep analyze"}
              </BtnPrimary>
            </div>

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
                title={`Informational · ${informational.length}`}
                elements={informational}
                empty="No informational elements yet. Hit Deep analyze, or paste a JSON list in the editor."
              />
            </div>

            <div style={{ display: "flex", gap: 10, justifyContent: "flex-end" }}>
              <BtnGhost onClick={onBack}>Back to interactable</BtnGhost>
              <BtnGhost onClick={onSave} disabled={save.isPending}>
                {save.isPending ? "Saving…" : "Save changes"}
              </BtnGhost>
              <BtnPrimary icon={Icon.arrow()} onClick={onContinue}>
                Continue to relationships
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
      <div className="mz-glass" style={{ padding: 0, overflow: "hidden" }}>
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
