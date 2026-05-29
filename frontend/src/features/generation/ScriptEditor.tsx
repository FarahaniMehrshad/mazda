import { useEffect, useState } from "react";
import Editor from "@monaco-editor/react";
import { BtnGhost, Pill } from "@/shared/components/Primitives";
import { Icon } from "@/shared/icons/Icon";

interface Props {
  fileName: string;
  initialValue: string | null | undefined;
  onSave: (value: string) => Promise<void> | void;
  saving?: boolean;
  busy?: boolean;
  busyLabel?: string;
}

export function ScriptEditor({ fileName, initialValue, onSave, saving, busy, busyLabel }: Props) {
  const [value, setValue] = useState<string>(initialValue ?? "");
  const [dirty, setDirty] = useState(false);

  useEffect(() => {
    // Re-sync when the upstream script changes (e.g. after a fresh LLM call).
    setValue(initialValue ?? "");
    setDirty(false);
  }, [initialValue]);

  function onChange(next: string | undefined) {
    setValue(next ?? "");
    setDirty(true);
  }

  async function handleSave() {
    await onSave(value);
    setDirty(false);
  }

  return (
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
          {dirty && <Pill tone="warn">unsaved</Pill>}
        </div>
        <BtnGhost
          icon={Icon.spark()}
          onClick={handleSave}
          disabled={!dirty || saving || !value.trim()}
        >
          {saving ? "Saving…" : "Save edits"}
        </BtnGhost>
      </div>

      <div style={{ flex: 1, minHeight: 0, position: "relative" }}>
        {busy && (
          <div
            style={{
              position: "absolute",
              inset: 0,
              background: "rgba(13,9,7,0.6)",
              backdropFilter: "blur(2px)",
              display: "grid",
              placeItems: "center",
              zIndex: 10,
              fontFamily: "var(--f-mono)",
              fontSize: 12.5,
              color: "var(--orange-100)",
            }}
          >
            {busyLabel ?? "Working…"}
          </div>
        )}
        <Editor
          height="100%"
          defaultLanguage="typescript"
          theme="vs-dark"
          value={value}
          onChange={onChange}
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
  );
}
