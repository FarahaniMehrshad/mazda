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
import { useSignature, useSaveSignature } from "./api";
import type { SignatureElementDto } from "@/shared/api/types";

interface DraftShape {
  name: string;
  type: string;
  elements: SignatureElementDto[];
}

export function EditorPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { data: signature, isLoading } = useSignature(id);
  const save = useSaveSignature();

  const [draft, setDraft] = useState<string>("");
  const [parseError, setParseError] = useState<string | null>(null);

  useEffect(() => {
    if (signature && !draft) {
      const shape: DraftShape = {
        name: signature.name,
        type: signature.type,
        elements: signature.elements,
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
    await save.mutateAsync({
      id: signature.id,
      name: parsed.name,
      type: parsed.type,
      source: signature.source,
      elements: parsed.elements ?? [],
    });
  }

  function onContinue() {
    if (signature) navigate(`/signatures/${signature.id}/flows`);
  }

  if (isLoading || !signature) {
    return (
      <ScreenShell>
        <TopBar />
        <div style={{ padding: 28, color: "var(--text-3)" }}>Loading signature…</div>
      </ScreenShell>
    );
  }

  return (
    <ScreenShell>
      <TopBar crumb={signature.name} />
      <div
        style={{
          padding: 28,
          display: "grid",
          gridTemplateColumns: "1.2fr 1fr",
          gap: 24,
          flex: 1,
          overflow: "hidden",
        }}
      >
        {/* ── LEFT — JSON editor ── */}
        <div style={{ display: "flex", flexDirection: "column", gap: 16, minHeight: 0 }}>
          <StepIndicator step={2} />
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
              <span>{signature.source || "signature.json"}</span>
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

        {/* ── RIGHT — element list preview ── */}
        <div style={{ display: "flex", flexDirection: "column", gap: 16, minHeight: 0 }}>
          <div className="mz-eyebrow">Detected elements · {signature.elements.length}</div>
          <div
            className="mz-glass mz-scroll"
            style={{
              flex: 1,
              minHeight: 0,
              padding: 0,
              overflow: "auto",
            }}
          >
            {signature.elements.map((el, i) => (
              <ElementRow key={i} index={i} el={el} />
            ))}
            {signature.elements.length === 0 && (
              <div style={{ padding: 24, color: "var(--text-4)", fontSize: 13 }}>
                No elements detected yet.
              </div>
            )}
          </div>

          <div style={{ display: "flex", gap: 10, justifyContent: "flex-end" }}>
            <BtnGhost onClick={onSave} disabled={save.isPending}>
              {save.isPending ? "Saving…" : "Save changes"}
            </BtnGhost>
            <BtnPrimary icon={Icon.arrow()} onClick={onContinue}>
              Continue to flows
            </BtnPrimary>
          </div>
        </div>
      </div>
    </ScreenShell>
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
      </div>
      <Pill>{el.elementType}</Pill>
    </div>
  );
}
