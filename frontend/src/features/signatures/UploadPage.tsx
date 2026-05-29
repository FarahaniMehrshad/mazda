import { useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { TopBar } from "@/shared/components/TopBar";
import {
  ScreenShell,
  BtnPrimary,
  BtnGhost,
  Pill,
  StepIndicator,
} from "@/shared/components/Primitives";
import { Icon } from "@/shared/icons/Icon";
import { useAnalyzeComponent } from "./api";
import { ApiError } from "@/shared/api/client";

const ELIGIBLE_EXT = /\.(tsx|jsx|ts|js|txt)$/i;
const MAX_SINGLE_FILE_BYTES = 1_000_000;
// Backend caps Source at 300,000 chars; leave headroom for path headers.
const MAX_BUNDLE_CHARS = 290_000;
// Backend caps Description at 50,000 chars.
const MAX_DESCRIPTION_CHARS = 50_000;

function isEligible(name: string) {
  return ELIGIBLE_EXT.test(name);
}

async function bundleFiles(files: File[]): Promise<{ source: string; label: string } | null> {
  const eligible = files.filter((f) => isEligible(f.name));
  if (eligible.length === 0) return null;

  const parts: string[] = [];
  let total = 0;
  let skipped = 0;
  for (const f of eligible) {
    const rel = (f as File & { webkitRelativePath?: string }).webkitRelativePath || f.name;
    const text = await f.text();
    const chunk = `// === FILE: ${rel} ===\n${text}\n`;
    if (total + chunk.length > MAX_BUNDLE_CHARS) {
      skipped++;
      continue;
    }
    parts.push(chunk);
    total += chunk.length;
  }
  if (parts.length === 0) return null;

  const firstRel = (eligible[0] as File & { webkitRelativePath?: string }).webkitRelativePath;
  const folderName = firstRel ? firstRel.split("/")[0] : "";
  const label =
    folderName && eligible.length > 1
      ? `${folderName}/ (${parts.length} files${skipped ? `, ${skipped} skipped` : ""})`
      : eligible[0].name;

  return { source: parts.join("\n"), label };
}

export function UploadPage() {
  const navigate = useNavigate();
  const analyze = useAnalyzeComponent();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const folderInputRef = useRef<HTMLInputElement>(null);
  const descInputRef = useRef<HTMLInputElement>(null);
  const [fileName, setFileName] = useState("");
  const [source, setSource] = useState("");
  const [descriptionFile, setDescriptionFile] = useState("");
  const [description, setDescription] = useState("");
  const [dragging, setDragging] = useState(false);

  async function onFile(file: File) {
    if (file.size > MAX_SINGLE_FILE_BYTES) {
      alert("File too large — keep it under 1 MB.");
      return;
    }
    const text = await file.text();
    if (text.length > MAX_BUNDLE_CHARS) {
      alert(`File too large for the analyzer (${text.length.toLocaleString()} chars, limit ${MAX_BUNDLE_CHARS.toLocaleString()}).`);
      return;
    }
    setFileName(file.name);
    setSource(text);
  }

  async function onFiles(files: FileList | null) {
    if (!files || files.length === 0) return;
    if (files.length === 1) {
      await onFile(files[0]);
      return;
    }
    const bundled = await bundleFiles(Array.from(files));
    if (!bundled) {
      alert("No supported files (.tsx/.jsx/.ts/.js/.txt) found in selection.");
      return;
    }
    setFileName(bundled.label);
    setSource(bundled.source);
  }

  function onDrop(e: React.DragEvent) {
    e.preventDefault();
    setDragging(false);
    if (e.dataTransfer.files.length > 0) onFiles(e.dataTransfer.files);
  }

  async function onDescriptionFile(file: File) {
    if (!/\.(md|markdown|txt)$/i.test(file.name)) {
      alert("Description must be a .md / .markdown / .txt file.");
      return;
    }
    const text = await file.text();
    if (text.length > MAX_DESCRIPTION_CHARS) {
      alert(`Description too large (${text.length.toLocaleString()} chars, limit ${MAX_DESCRIPTION_CHARS.toLocaleString()}).`);
      return;
    }
    setDescriptionFile(file.name);
    setDescription(text);
  }

  async function runAnalysis() {
    if (!source.trim()) return;
    try {
      const sig = await analyze.mutateAsync({
        fileName: fileName || "Component.tsx",
        source,
        description: description.trim() || undefined,
      });
      navigate(`/signatures/${sig.id}`);
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Analysis failed.");
    }
  }

  return (
    <ScreenShell>
      <TopBar crumb="new signature" />
      <div
        style={{
          padding: 28,
          display: "grid",
          gridTemplateColumns: "1fr 1fr",
          gap: 24,
          flex: 1,
          overflow: "hidden",
        }}
      >
        {/* ── LEFT — dropzone ── */}
        <div style={{ display: "flex", flexDirection: "column", gap: 20, minHeight: 0 }}>
          <StepIndicator step={1} />

          <div
            className="mz-glass-2"
            onDragOver={(e) => {
              e.preventDefault();
              setDragging(true);
            }}
            onDragLeave={() => setDragging(false)}
            onDrop={onDrop}
            style={{
              flex: 1,
              padding: 32,
              display: "flex",
              flexDirection: "column",
              alignItems: "center",
              justifyContent: "center",
              gap: 18,
              border: dragging
                ? "1px dashed rgba(234,112,40,0.6)"
                : "1px dashed var(--glass-stroke-2)",
              transition: "border-color 160ms ease, background 160ms ease",
              background: dragging ? "rgba(194,65,12,0.06)" : "var(--glass-2)",
              minHeight: 320,
            }}
          >
            <div
              style={{
                width: 64,
                height: 64,
                borderRadius: 16,
                background: "linear-gradient(135deg, rgba(194,65,12,0.25), rgba(122,40,6,0.4))",
                border: "1px solid rgba(234,112,40,0.45)",
                display: "grid",
                placeItems: "center",
                color: "var(--orange-200)",
                boxShadow: "0 0 32px rgba(194,65,12,0.25)",
              }}
            >
              {Icon.upload(28)}
            </div>
            <div style={{ textAlign: "center" }}>
              <div style={{ fontFamily: "var(--f-display)", fontSize: 22, fontWeight: 500 }}>
                Drop a component file or folder
              </div>
              <div style={{ color: "var(--text-3)", marginTop: 4, fontSize: 13 }}>
                .tsx / .jsx / .ts / .js — we'll extract every testid-anchored element.
              </div>
            </div>
            <input
              ref={fileInputRef}
              type="file"
              accept=".tsx,.jsx,.ts,.js,.txt"
              style={{ display: "none" }}
              onChange={(e) => {
                onFiles(e.target.files);
                e.target.value = "";
              }}
            />
            <input
              ref={folderInputRef}
              type="file"
              // @ts-expect-error — non-standard but widely supported
              webkitdirectory=""
              directory=""
              multiple
              style={{ display: "none" }}
              onChange={(e) => {
                onFiles(e.target.files);
                e.target.value = "";
              }}
            />
            <input
              ref={descInputRef}
              type="file"
              accept=".md,.markdown,.txt"
              style={{ display: "none" }}
              onChange={(e) => {
                const f = e.target.files?.[0];
                if (f) onDescriptionFile(f);
                e.target.value = "";
              }}
            />
            <div style={{ display: "flex", gap: 10, flexWrap: "wrap", justifyContent: "center" }}>
              <BtnGhost icon={Icon.file()} onClick={() => fileInputRef.current?.click()}>
                Choose file
              </BtnGhost>
              <BtnGhost icon={Icon.folder()} onClick={() => folderInputRef.current?.click()}>
                Choose folder
              </BtnGhost>
              <BtnGhost icon={Icon.file()} onClick={() => descInputRef.current?.click()}>
                {descriptionFile ? "Replace description" : "Add description (.md)"}
              </BtnGhost>
            </div>
            <div
              style={{
                display: "flex",
                gap: 8,
                flexWrap: "wrap",
                justifyContent: "center",
              }}
            >
              {fileName && (
                <Pill tone="orange" icon={Icon.file(10)}>
                  {fileName}
                </Pill>
              )}
              {descriptionFile && (
                <Pill tone="info" icon={Icon.file(10)}>
                  {descriptionFile}
                </Pill>
              )}
            </div>
            <div
              style={{
                fontSize: 11,
                color: "var(--text-4)",
                textAlign: "center",
                maxWidth: 380,
              }}
            >
              Optional .md file describing how the component works — it'll be passed to the analyzer as extra context.
            </div>
          </div>
        </div>

        {/* ── RIGHT — preview + analyze ── */}
        <div style={{ display: "flex", flexDirection: "column", gap: 16, minHeight: 0 }}>
          <div className="mz-eyebrow">Source preview</div>
          <div
            className="mz-glass"
            style={{
              flex: 1,
              minHeight: 0,
              fontFamily: "var(--f-mono)",
              fontSize: 12,
              color: "var(--text-2)",
              padding: 16,
              overflow: "auto",
              whiteSpace: "pre",
              lineHeight: 1.55,
            }}
          >
            {source || (
              <span style={{ color: "var(--text-4)" }}>
                // Component source will appear here once you pick a file.
              </span>
            )}
          </div>
          <div style={{ display: "flex", gap: 10, justifyContent: "flex-end" }}>
            <BtnGhost onClick={() => navigate(-1)}>Cancel</BtnGhost>
            <BtnPrimary
              icon={Icon.spark()}
              onClick={runAnalysis}
              disabled={!source.trim() || analyze.isPending}
            >
              {analyze.isPending ? "Analyzing…" : "Analyze component"}
            </BtnPrimary>
          </div>
        </div>
      </div>
    </ScreenShell>
  );
}
