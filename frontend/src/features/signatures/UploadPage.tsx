import { useEffect, useRef, useState } from "react";
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
import { useAnalyzeComponent, useSaveSignature, useUpdateSignatureDescription } from "./api";
import { ApiError } from "@/shared/api/client";

/**
 * Files we silently drop because `file.text()` on them returns replacement-
 * character garbage that pollutes the bundle and doesn't help the LLM. This
 * is INTENTIONALLY tiny — the user-facing filter is the Exclude patterns
 * field; this denylist exists only to keep clearly-binary blobs out.
 *
 * Text formats that LOOK binary at a glance but are actually text (SVG,
 * JSON, YAML, …) are NOT here. SVG in particular is XML, the LLM can read
 * it, and the user can still exclude it via the field if they want to.
 */
const BINARY_EXT =
  /\.(png|jpe?g|gif|webp|avif|ico|bmp|tiff?|woff2?|ttf|otf|eot|mp[34]|webm|mov|avi|mkv|flac|wav|ogg|zip|tar|gz|bz2|7z|rar|pdf|exe|dll|so|dylib|class|jar|wasm|node|psd|ai|sketch|fig|xcf)$/i;

const MAX_SINGLE_FILE_BYTES = 1_000_000;
// Backend caps Source at 1,000,000 chars. We mirror the cap here exactly —
// path headers are already counted into each chunk's size in bundleFiles,
// so there is no need to reserve extra headroom.
const MAX_BUNDLE_CHARS = 1_000_000;
// Backend caps Description at 50,000 chars.
const MAX_DESCRIPTION_CHARS = 50_000;

/**
 * Patterns that are filtered out of a folder bundle by default. Playwright
 * spec files are the most common case — they live next to the components
 * they test and bloat the bundle with assertions that aren't part of the
 * component's source-of-truth behavior.
 *
 * The user can edit this list in the upload UI. Comma- or newline-separated.
 */
const DEFAULT_EXCLUDE_PATTERNS = "*.ts, *.spec.ts, *.spec.tsx, *.cy.ts, mock*, *.mock, *.css, *.scss";

function isEligible(name: string) {
  // Denylist, not allowlist — we want everything text-decodable to flow
  // into the bundle so the user controls inclusion via Exclude patterns.
  return !BINARY_EXT.test(name);
}

/**
 * A compiled exclude pattern. `usesPath` decides which value we match
 * against — gitignore-style: a slash anywhere in the pattern means the
 * pattern is a PATH glob (matched against the file's relative path);
 * otherwise it's a NAME glob (matched against the basename only). This is
 * the convention users expect — `*.spec.ts` should match `Foo.spec.ts`
 * AND `src/Foo.spec.ts`, but `dist/` should only match the `dist`
 * directory and not a file literally named "dist".
 */
type ExcludePattern = { rx: RegExp; usesPath: boolean; source: string };

/**
 * Compile a user-entered patterns string into a list of matchers. Supports:
 *   • `*`  — any sequence of characters (including `/`)
 *   • `?`  — any single character
 *   • all other characters are matched literally
 * Patterns are case-insensitive. Empty entries (extra commas / blank lines)
 * are ignored. Invalid regex pieces can't happen because we escape every
 * non-wildcard character before assembling the body.
 */
function compileExcludePatterns(input: string): ExcludePattern[] {
  return input
    .split(/[,\n]/)
    .map((s) => s.trim())
    .filter(Boolean)
    .map((pattern) => {
      const usesPath = pattern.includes("/");
      const body = pattern
        // Escape every regex special EXCEPT * and ? — those become the
        // wildcard mappings below. (`-` is only special inside [] so it's
        // safe here.)
        .replace(/[.+^${}()|[\]\\]/g, "\\$&")
        .replace(/\?/g, ".")
        .replace(/\*/g, ".*");
      return {
        rx: new RegExp(`^${body}$`, "i"),
        usesPath,
        source: pattern,
      };
    });
}

function isExcluded(picked: PickedFile, patterns: ExcludePattern[]): boolean {
  if (patterns.length === 0) return false;
  return patterns.some((p) =>
    p.usesPath ? p.rx.test(picked.relPath) : p.rx.test(picked.file.name),
  );
}

/**
 * A picked file with its display path relative to the root the user selected.
 * Carrying the path explicitly is necessary because:
 *   - File.webkitRelativePath is only populated for the <input webkitdirectory>
 *     code path. Files obtained from a DataTransferItem's entry API have an
 *     empty webkitRelativePath, so we have to reconstruct the path ourselves
 *     while we walk the directory tree.
 */
type PickedFile = { file: File; relPath: string };

/**
 * Recursively walk a webkitGetAsEntry-style FileSystemEntry, collecting every
 * file underneath it (at any depth) into `out`. Required because dropping a
 * folder onto a dropzone gives you a DataTransferItem that resolves to a
 * FileSystemDirectoryEntry — NOT a flat list of files. Without this walk,
 * nested files are silently lost on drop.
 *
 * The entry types are minimally typed locally — the DOM lib's
 * FileSystemEntry/FileSystemDirectoryEntry interfaces are sufficient for our
 * needs but vendor-prefixed in older browsers, so we keep the surface small.
 */
type FsEntry = {
  isFile: boolean;
  isDirectory: boolean;
  name: string;
  file?: (cb: (f: File) => void, err?: (e: unknown) => void) => void;
  createReader?: () => {
    readEntries: (cb: (entries: FsEntry[]) => void, err?: (e: unknown) => void) => void;
  };
};

async function walkEntry(entry: FsEntry, prefix: string, out: PickedFile[]): Promise<void> {
  if (entry.isFile && entry.file) {
    const file = await new Promise<File>((res, rej) => entry.file!(res, rej));
    out.push({ file, relPath: prefix + file.name });
    return;
  }
  if (entry.isDirectory && entry.createReader) {
    const reader = entry.createReader();
    // readEntries returns AT MOST 100 entries per call; keep calling until
    // it returns an empty batch. Forgetting this loop is the #1 bug in
    // hand-rolled directory walkers — large folders look fine in tests but
    // truncate silently in production.
    const children: FsEntry[] = [];
    for (;;) {
      const batch = await new Promise<FsEntry[]>((res, rej) =>
        reader.readEntries(res, rej),
      );
      if (batch.length === 0) break;
      children.push(...batch);
    }
    for (const child of children) {
      await walkEntry(child, prefix + entry.name + "/", out);
    }
  }
}

/**
 * Extract every file from a drop event, recursively descending into any
 * directories that were dropped. Falls back to the flat FileList if the
 * Entry API isn't available (very old browsers, drag from outside the OS
 * file manager).
 */
async function pickFromDataTransfer(dt: DataTransfer): Promise<PickedFile[]> {
  const out: PickedFile[] = [];

  // Prefer the items API so we can recurse into folders.
  if (dt.items && dt.items.length > 0) {
    const entries: FsEntry[] = [];
    for (let i = 0; i < dt.items.length; i++) {
      const item = dt.items[i];
      // webkitGetAsEntry is the de-facto cross-browser name; the standardised
      // getAsEntry alias is still not universal as of mid-2025.
      const getEntry = (
        item as DataTransferItem & {
          webkitGetAsEntry?: () => FsEntry | null;
          getAsEntry?: () => FsEntry | null;
        }
      ).webkitGetAsEntry?.bind(item) ??
        (item as DataTransferItem & { getAsEntry?: () => FsEntry | null }).getAsEntry?.bind(item);
      const entry = getEntry?.();
      if (entry) {
        entries.push(entry);
      } else {
        // Item isn't a file (text/plain MIME, etc.) — try the file path anyway.
        const f = item.getAsFile?.();
        if (f) out.push({ file: f, relPath: f.name });
      }
    }
    for (const e of entries) await walkEntry(e, "", out);
    return out;
  }

  // Final fallback: dataTransfer.files only ever contains TOP-LEVEL files;
  // nested files in a dropped folder are LOST on this path. We end up here
  // only when the items API is completely unavailable.
  for (const f of Array.from(dt.files)) out.push({ file: f, relPath: f.name });
  return out;
}

/**
 * Wrap a FileList from an <input type="file"> (with or without webkitdirectory)
 * into our PickedFile shape. webkitRelativePath is already set by the browser
 * for directory inputs.
 */
function pickFromFileList(files: FileList): PickedFile[] {
  return Array.from(files).map((f) => ({
    file: f,
    relPath: (f as File & { webkitRelativePath?: string }).webkitRelativePath || f.name,
  }));
}

type BundleResult = {
  source: string;
  label: string;
  /** Raw count of files the user dropped / picked, BEFORE any filtering. */
  totalPicked: number;
  /** Files matching BINARY_EXT — dropped silently because file.text() on
   *  them would return replacement-character garbage. */
  binary: number;
  /** Files that passed the binary denylist (totalPicked - binary). */
  totalEligible: number;
  /** Files that survived eligibility AND exclude patterns AND the size cap. */
  included: number;
  /** Eligible files dropped because adding them would breach MAX_BUNDLE_CHARS. */
  skipped: number;
  /** Eligible files dropped by exclude patterns. */
  excluded: number;
  /** Paths skipped by the size cap (for debugging "where did my file go?"). */
  skippedPaths: string[];
  /** Paths dropped by the user's exclude patterns. */
  excludedPaths: string[];
};

async function bundleFiles(
  picked: PickedFile[],
  excludePatterns: ExcludePattern[],
): Promise<BundleResult | null> {
  const totalPicked = picked.length;
  const eligible = picked
    .filter((p) => isEligible(p.file.name))
    // Sort by relative path so the bundle order is deterministic and the
    // LLM sees a coherent tree (parent files first, then children).
    .sort((a, b) => a.relPath.localeCompare(b.relPath));
  const binary = totalPicked - eligible.length;
  if (eligible.length === 0) return null;

  const afterExcludes: PickedFile[] = [];
  const excludedPaths: string[] = [];
  for (const p of eligible) {
    if (isExcluded(p, excludePatterns)) excludedPaths.push(p.relPath);
    else afterExcludes.push(p);
  }
  const excluded = excludedPaths.length;

  if (afterExcludes.length === 0) {
    // Every eligible file was filtered out by the user's exclude patterns.
    // Return a zero-result bundle (rather than null) so the UI can
    // distinguish "no source files at all" from "patterns excluded
    // everything" — the second case needs a different message.
    return {
      source: "",
      label: "",
      totalPicked,
      binary,
      totalEligible: eligible.length,
      included: 0,
      skipped: 0,
      excluded,
      skippedPaths: [],
      excludedPaths,
    };
  }

  const parts: string[] = [];
  let total = 0;
  const skippedPaths: string[] = [];
  for (const p of afterExcludes) {
    const text = await p.file.text();
    const chunk = `// === FILE: ${p.relPath} ===\n${text}\n`;
    if (total + chunk.length > MAX_BUNDLE_CHARS) {
      skippedPaths.push(p.relPath);
      continue;
    }
    parts.push(chunk);
    total += chunk.length;
  }
  const skipped = skippedPaths.length;
  if (parts.length === 0) {
    return {
      source: "",
      label: "",
      totalPicked,
      binary,
      totalEligible: eligible.length,
      included: 0,
      skipped,
      excluded,
      skippedPaths,
      excludedPaths,
    };
  }

  // Folder name = the segment shared by every eligible file's relPath. When
  // the user dropped multiple unrelated files, this falls back to a count.
  const firstRel = afterExcludes[0].relPath;
  const folderName = firstRel.includes("/") ? firstRel.split("/")[0] : "";
  const included = parts.length;
  const label =
    folderName && afterExcludes.length > 1
      ? `${folderName}/ (${included} files${skipped ? `, ${skipped} skipped` : ""}${excluded ? `, ${excluded} excluded` : ""})`
      : afterExcludes[0].file.name;

  return {
    source: parts.join("\n"),
    label,
    totalPicked,
    binary,
    totalEligible: eligible.length,
    included,
    skipped,
    excluded,
    skippedPaths,
    excludedPaths,
  };
}

export function UploadPage() {
  const navigate = useNavigate();
  const analyze = useAnalyzeComponent();
  const save = useSaveSignature();
  const updateDescription = useUpdateSignatureDescription();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const folderInputRef = useRef<HTMLInputElement>(null);
  const descInputRef = useRef<HTMLInputElement>(null);
  const [fileName, setFileName] = useState("");
  const [source, setSource] = useState("");
  const [descriptionFile, setDescriptionFile] = useState("");
  const [description, setDescription] = useState("");
  const [name, setName] = useState("");
  const [dragging, setDragging] = useState(false);

  // Picked files persist across pattern edits — this is what makes the
  // "Exclude patterns" field interactive. We re-bundle from this list
  // whenever either the files OR the patterns change. Single-file (non-
  // folder) picks store one entry here too, and the rebundle effect
  // detects them as a special case so the user-visible source isn't
  // wrapped in `// === FILE: … ===` headers.
  const [pickedFiles, setPickedFiles] = useState<PickedFile[]>([]);
  const [excludePatterns, setExcludePatterns] = useState(DEFAULT_EXCLUDE_PATTERNS);
  const [bundleInfo, setBundleInfo] = useState<{
    /** Raw count of files in the drop / pick. */
    totalPicked: number;
    /** Files dropped by the binary denylist (images, fonts, archives, …). */
    binary: number;
    included: number;
    excluded: number;
    skipped: number;
    totalEligible: number;
    /** "all excluded" — every eligible file was filtered out by patterns. */
    allExcluded: boolean;
    /** Paths dropped by the size cap — surfaced so the user can verify
     *  whether a specific missing file fell here. */
    skippedPaths: string[];
    /** Paths dropped by the user's exclude patterns. */
    excludedPaths: string[];
  } | null>(null);

  // Soft caps mirror the backend validators so we fail in the UI before a round-trip.
  const MAX_NAME_CHARS = 80;

  // Re-bundle the picked files whenever the list OR the exclude patterns
  // change. A run-id ref guards against the race where the user types
  // quickly in the patterns box and a slow earlier bundle resolves AFTER
  // a faster later one — we ignore stale results so the UI always shows
  // the bundle for the latest input.
  const bundleRunIdRef = useRef(0);
  useEffect(() => {
    if (pickedFiles.length === 0) {
      setFileName("");
      setSource("");
      setBundleInfo(null);
      return;
    }
    const runId = ++bundleRunIdRef.current;
    void (async () => {
      // Single-file path: skip excludes entirely. The user explicitly chose
      // one file; honouring `*.spec.ts` here would silently throw it away.
      if (pickedFiles.length === 1 && !pickedFiles[0].relPath.includes("/")) {
        const file = pickedFiles[0].file;
        if (!isEligible(file.name)) {
          // file.text() on a binary returns replacement-character garbage;
          // bail explicitly so the user sees what's wrong.
          alert("Binary files (images / fonts / archives) can't be analyzed. Pick a text-based source file.");
          setPickedFiles([]);
          return;
        }
        if (file.size > MAX_SINGLE_FILE_BYTES) {
          alert("File too large — keep it under 1 MB.");
          setPickedFiles([]);
          return;
        }
        const text = await file.text();
        if (runId !== bundleRunIdRef.current) return;
        if (text.length > MAX_BUNDLE_CHARS) {
          alert(
            `File too large for the analyzer (${text.length.toLocaleString()} chars, limit ${MAX_BUNDLE_CHARS.toLocaleString()}).`,
          );
          setPickedFiles([]);
          return;
        }
        setFileName(file.name);
        setSource(text);
        setBundleInfo(null);
        return;
      }

      const compiled = compileExcludePatterns(excludePatterns);
      const bundled = await bundleFiles(pickedFiles, compiled);
      if (runId !== bundleRunIdRef.current) return;
      if (!bundled) {
        // No supported extensions in the picked set at all.
        setFileName("");
        setSource("");
        setBundleInfo(null);
        return;
      }
      setFileName(bundled.label);
      setSource(bundled.source);
      setBundleInfo({
        totalPicked: bundled.totalPicked,
        binary: bundled.binary,
        included: bundled.included,
        excluded: bundled.excluded,
        skipped: bundled.skipped,
        totalEligible: bundled.totalEligible,
        allExcluded: bundled.included === 0 && bundled.excluded > 0,
        skippedPaths: bundled.skippedPaths,
        excludedPaths: bundled.excludedPaths,
      });
    })();
  }, [pickedFiles, excludePatterns]);

  async function onPicked(picked: PickedFile[]) {
    if (picked.length === 0) return;
    // Early eligibility check just to surface a useful alert when the user
    // dropped a folder with NO supported files. The actual bundling
    // happens in the effect above.
    const anyEligible = picked.some((p) => isEligible(p.file.name));
    if (!anyEligible) {
      // Every file in the selection matched BINARY_EXT (images, fonts,
      // archives, …). Nothing text-decodable to bundle.
      alert("Selection contained only binary files (images / fonts / archives). Nothing to analyze.");
      return;
    }
    setPickedFiles(picked);
  }

  async function onFiles(files: FileList | null) {
    if (!files || files.length === 0) return;
    await onPicked(pickFromFileList(files));
  }

  async function onDrop(e: React.DragEvent) {
    e.preventDefault();
    setDragging(false);
    // Use the entry API instead of .files so nested files inside dropped
    // folders are actually collected (see pickFromDataTransfer). The previous
    // implementation read e.dataTransfer.files directly and silently lost
    // every file living inside a subdirectory.
    const picked = await pickFromDataTransfer(e.dataTransfer);
    await onPicked(picked);
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
    if (description.length > MAX_DESCRIPTION_CHARS) {
      alert(`Description too long (${description.length.toLocaleString()} chars, limit ${MAX_DESCRIPTION_CHARS.toLocaleString()}).`);
      return;
    }
    if (name.length > MAX_NAME_CHARS) {
      alert(`Name too long (${name.length} chars, limit ${MAX_NAME_CHARS}).`);
      return;
    }
    try {
      const sig = await analyze.mutateAsync({
        fileName: fileName || "Component.tsx",
        source,
        description: description.trim() || undefined,
        // Send only when the user actually typed something — empty means
        // "let the analyzer suggest a name".
        name: name.trim() || undefined,
      });
      navigate(`/signatures/${sig.id}`);
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Analysis failed.");
    }
  }

  // No usable source (third-party library, closed bundle): create an empty
  // signature and let MAZDA learn it by watching the user operate the UI.
  async function createForLearning() {
    const trimmed = name.trim();
    if (!trimmed) {
      alert("Give the component a name first — there's no source to suggest one from.");
      return;
    }
    if (trimmed.length > MAX_NAME_CHARS) {
      alert(`Name too long (${trimmed.length} chars, limit ${MAX_NAME_CHARS}).`);
      return;
    }
    try {
      const sig = await save.mutateAsync({ name: trimmed, source: "recorded", type: "component", elements: [] });
      if (description.trim()) await updateDescription.mutateAsync({ id: sig.id, description: description.trim() });
      navigate(`/signatures/${sig.id}/learn`);
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Could not create the signature.");
    }
  }

  return (
    <ScreenShell>
      <TopBar crumb="new signature" />
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
        {/* Steps go ABOVE the grid as a full-width row so the indicator's
            intrinsic width never has to fit inside a 1fr column. Otherwise
            with minWidth: 0 on the columns it would visibly overflow into
            the right column and land on top of the "Source preview" eyebrow. */}
        <StepIndicator step={1} />

        <div
          style={{
            display: "grid",
            gridTemplateColumns: "1fr 1fr",
            gap: 24,
            flex: 1,
            minHeight: 0,
          }}
        >
        {/* ── LEFT — dropzone + details ── */}
        {/* The whole column can scroll if the viewport is short — the dropzone
            shrinks to a sane min, and the Details panel stays at its natural
            size so the inputs never get clipped. */}
        <div style={{ display: "flex", flexDirection: "column", gap: 16, minHeight: 0, minWidth: 0, overflow: "auto" }}>
          <div
            className="mz-glass-2"
            onDragOver={(e) => {
              e.preventDefault();
              setDragging(true);
            }}
            onDragLeave={() => setDragging(false)}
            onDrop={onDrop}
            style={{
              // 0 0 auto so the dropzone NEVER shrinks when the column has
              // other content (bundle-info panel + Details panel). Previously
              // `flex: "1 1 auto"` plus `minHeight: 240` made the dropzone
              // collapse to ~240px after a successful drop, which read as
              // "the icon shrank" even though the icon itself is fixed-size.
              // Now the dropzone keeps its full visual weight; if the column
              // runs out of room the parent's `overflow: auto` scrolls.
              flex: "0 0 auto",
              padding: 40,
              display: "flex",
              flexDirection: "column",
              alignItems: "center",
              justifyContent: "center",
              gap: 16,
              border: dragging
                ? "1px dashed rgba(234,112,40,0.6)"
                : "1px dashed var(--glass-stroke-2)",
              transition: "border-color 160ms ease, background 160ms ease",
              background: dragging ? "rgba(194,65,12,0.06)" : "var(--glass-2)",
              minHeight: 380,
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
                Any text-based source — components, templates, configs.
                Refine the bundle with the Exclude patterns field below.
              </div>
            </div>
            <input
              ref={fileInputRef}
              type="file"
              // No accept filter — we accept any text-decodable file. The
              // OS file picker stays open to everything; the BINARY_EXT
              // denylist + the user's Exclude patterns are the real filter.
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
            </div>
            <div
              style={{
                display: "flex",
                flexDirection: "column",
                gap: 6,
                alignItems: "center",
              }}
            >
              {fileName && (
                <Pill tone="orange" icon={Icon.file(10)}>
                  {fileName}
                </Pill>
              )}
            </div>
          </div>

          {/* ── Bundle breakdown — counts + expandable path lists ── */}
          {/* Lives OUTSIDE the dropzone so expanding the skipped / excluded
              path lists doesn't stretch the dropzone's dashed border. The
              previous layout nested this inside the dropzone, which made
              clicking "Show N excluded" balloon the drop-target area into
              a sprawling info panel. */}
          {bundleInfo && (
            <div
              className="mz-glass"
              style={{
                flex: "0 0 auto",
                padding: 16,
                display: "flex",
                flexDirection: "column",
                gap: 10,
                fontFamily: "var(--f-mono)",
                fontSize: 11,
                color: bundleInfo.allExcluded ? "var(--err, #e07a5f)" : "var(--text-3)",
                minWidth: 0,
              }}
            >
              {/* Headline. */}
              <div style={{ textAlign: "center" }}>
                {bundleInfo.totalPicked.toLocaleString()} file
                {bundleInfo.totalPicked === 1 ? "" : "s"} found in selection
              </div>

              {/* Breakdown grid. Coloured so non-zero "excluded by patterns" /
                  "skipped (size cap)" stand out. */}
              <div
                style={{
                  display: "grid",
                  gridTemplateColumns: "1fr auto",
                  gap: "2px 14px",
                  maxWidth: 360,
                  margin: "0 auto",
                  textAlign: "left",
                  width: "100%",
                }}
              >
                <div style={{ color: "var(--text-3)" }}>Included in bundle</div>
                <div
                  style={{
                    color: bundleInfo.included > 0 ? "var(--ok, #66c98d)" : "var(--text-4)",
                    textAlign: "right",
                  }}
                >
                  {bundleInfo.included.toLocaleString()}
                </div>

                <div style={{ color: "var(--text-3)" }}>Excluded by patterns</div>
                <div
                  style={{
                    color: bundleInfo.excluded > 0 ? "var(--orange-200)" : "var(--text-4)",
                    textAlign: "right",
                  }}
                >
                  {bundleInfo.excluded.toLocaleString()}
                </div>

                <div style={{ color: "var(--text-3)" }}>Skipped (size cap)</div>
                <div
                  style={{
                    color: bundleInfo.skipped > 0 ? "var(--err, #e07a5f)" : "var(--text-4)",
                    textAlign: "right",
                  }}
                >
                  {bundleInfo.skipped.toLocaleString()}
                </div>

                {bundleInfo.binary > 0 && (
                  <>
                    <div style={{ color: "var(--text-3)" }}>Binary (images / fonts / …)</div>
                    <div style={{ color: "var(--text-4)", textAlign: "right" }}>
                      {bundleInfo.binary.toLocaleString()}
                    </div>
                  </>
                )}
              </div>

              {bundleInfo.allExcluded && (
                <div style={{ textAlign: "center", color: "var(--err, #e07a5f)" }}>
                  All {bundleInfo.totalEligible} eligible files were excluded by your patterns.
                </div>
              )}

              {(bundleInfo.skippedPaths.length > 0 || bundleInfo.excludedPaths.length > 0) && (
                <div style={{ display: "flex", flexDirection: "column", gap: 6, textAlign: "left", minWidth: 0 }}>
                  {bundleInfo.skippedPaths.length > 0 && (
                    <PathListDetails
                      label={`Show ${bundleInfo.skippedPaths.length} skipped (size cap)`}
                      paths={bundleInfo.skippedPaths}
                      summaryColor="var(--orange-200)"
                      footer={
                        <>
                          Bundle limit is {MAX_BUNDLE_CHARS.toLocaleString()} chars.
                          Narrow the folder, or tighten Exclude patterns, to free room for these files.
                        </>
                      }
                    />
                  )}
                  {bundleInfo.excludedPaths.length > 0 && (
                    <PathListDetails
                      label={`Show ${bundleInfo.excludedPaths.length} excluded by patterns`}
                      paths={bundleInfo.excludedPaths}
                      summaryColor="var(--text-3)"
                    />
                  )}
                </div>
              )}
            </div>
          )}

          {/* ── Details — optional custom name + description ── */}
          {/* Both fields are optional. When the user leaves Name empty, the
              backend falls back to the LLM-suggested name. Description seeds
              the signature's HumanDescription (and is passed as extra context
              to the analyzer); it can be typed directly or loaded from a .md /
              .markdown / .txt file via the small "Load from file" button. */}
          <div
            className="mz-glass"
            style={{
              flex: "0 0 auto",
              padding: 18,
              display: "flex",
              flexDirection: "column",
              gap: 14,
            }}
          >
            <div className="mz-eyebrow">Details (optional)</div>

            <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
              <label
                style={{
                  fontFamily: "var(--f-mono)",
                  fontSize: 10.5,
                  letterSpacing: "0.06em",
                  color: "var(--text-3)",
                  textTransform: "uppercase",
                }}
              >
                Signature name
              </label>
              <input
                type="text"
                value={name}
                onChange={(e) => setName(e.target.value)}
                maxLength={MAX_NAME_CHARS}
                placeholder="Leave blank — Mazda will suggest one from the source"
                style={{
                  width: "100%",
                  padding: "10px 12px",
                  background: "var(--glass-1)",
                  border: "1px solid var(--glass-stroke-2)",
                  borderRadius: 8,
                  color: "var(--text-1)",
                  fontFamily: "var(--f-sans)",
                  fontSize: 13,
                  outline: "none",
                }}
              />
            </div>

            {/* ── Exclude patterns — folder/multi-file uploads only ── */}
            {/* Hidden when a single file was picked: excludes don't apply
                there (the user explicitly chose that one file). The field
                stays mounted so the value the user typed survives across
                pick events. */}
            <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
              <label
                style={{
                  fontFamily: "var(--f-mono)",
                  fontSize: 10.5,
                  letterSpacing: "0.06em",
                  color: "var(--text-3)",
                  textTransform: "uppercase",
                }}
              >
                Exclude patterns
              </label>
              <input
                type="text"
                value={excludePatterns}
                onChange={(e) => setExcludePatterns(e.target.value)}
                placeholder="e.g. *.spec.ts, *.test.tsx, fixtures/"
                spellCheck={false}
                style={{
                  width: "100%",
                  padding: "10px 12px",
                  background: "var(--glass-1)",
                  border: "1px solid var(--glass-stroke-2)",
                  borderRadius: 8,
                  color: "var(--text-1)",
                  fontFamily: "var(--f-mono)",
                  fontSize: 12,
                  outline: "none",
                }}
              />
              <div
                style={{
                  fontFamily: "var(--f-sans)",
                  fontSize: 11,
                  color: "var(--text-4)",
                  lineHeight: 1.5,
                }}
              >
                Glob patterns, comma- or newline-separated. <code style={{ fontFamily: "var(--f-mono)" }}>*</code>{" "}
                matches any chars, <code style={{ fontFamily: "var(--f-mono)" }}>?</code> matches one.
                Patterns without a <code style={{ fontFamily: "var(--f-mono)" }}>/</code> match the file name;
                patterns containing <code style={{ fontFamily: "var(--f-mono)" }}>/</code> match the full path.
                Only applies to folder / multi-file uploads.
              </div>
            </div>

            <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", gap: 8 }}>
                <label
                  style={{
                    fontFamily: "var(--f-mono)",
                    fontSize: 10.5,
                    letterSpacing: "0.06em",
                    color: "var(--text-3)",
                    textTransform: "uppercase",
                  }}
                >
                  Description
                </label>
                <button
                  onClick={() => descInputRef.current?.click()}
                  style={{
                    fontSize: 10.5,
                    fontFamily: "var(--f-mono)",
                    padding: "3px 8px",
                    borderRadius: 999,
                    background: "var(--glass-1)",
                    border: "1px solid var(--glass-stroke)",
                    color: "var(--text-3)",
                    cursor: "pointer",
                  }}
                >
                  {descriptionFile ? `↻ ${descriptionFile}` : "Load from .md"}
                </button>
              </div>
              <textarea
                value={description}
                onChange={(e) => {
                  setDescription(e.target.value);
                  // User is editing manually — drop the "loaded from file" badge
                  // so the small chip doesn't lie about the current contents.
                  if (descriptionFile) setDescriptionFile("");
                }}
                placeholder="How this component works — purpose, important states, anything the analyzer should know about. Passed to the LLM as extra context."
                rows={5}
                style={{
                  width: "100%",
                  resize: "vertical",
                  minHeight: 96,
                  padding: "10px 12px",
                  background: "var(--glass-1)",
                  border: "1px solid var(--glass-stroke-2)",
                  borderRadius: 8,
                  color: "var(--text-1)",
                  fontFamily: "var(--f-sans)",
                  fontSize: 13,
                  lineHeight: 1.5,
                  outline: "none",
                }}
              />
              <div
                style={{
                  fontFamily: "var(--f-mono)",
                  fontSize: 10.5,
                  color: description.length > MAX_DESCRIPTION_CHARS ? "var(--err, #e07a5f)" : "var(--text-4)",
                  textAlign: "right",
                }}
              >
                {description.length.toLocaleString()} / {MAX_DESCRIPTION_CHARS.toLocaleString()} chars
              </div>
            </div>
          </div>
        </div>

        {/* ── RIGHT — preview + analyze ── */}
        {/* minWidth: 0 is critical — grid items default to min-content sizing, so a
            single very-long line in the source preview would otherwise blow the
            column past `1fr` and shove the action row (with `justify-content:
            flex-end`) off the right edge of the viewport. With min-width: 0 the
            column stays at 1fr and the `overflow: auto` on the preview pane
            below is what actually handles horizontal scrolling. */}
        <div style={{ display: "flex", flexDirection: "column", gap: 16, minHeight: 0, minWidth: 0 }}>
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
            <BtnGhost
              onClick={createForLearning}
              disabled={save.isPending || analyze.isPending}
            >
              {save.isPending ? "Creating…" : "No source? Create & learn by watching"}
            </BtnGhost>
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
      </div>
    </ScreenShell>
  );
}

/**
 * Collapsible path list. Native <details>/<summary> so we don't have to
 * track open state ourselves. The inner panel caps its height and scrolls
 * internally — without that cap a long list (200+ paths) would push the
 * entire layout. Paths use `overflowWrap: anywhere` instead of
 * `wordBreak: break-all` so long Angular-style nested paths wrap at any
 * point but the breaks are visually softer.
 */
function PathListDetails({
  label,
  paths,
  summaryColor,
  footer,
}: {
  label: string;
  paths: string[];
  summaryColor: string;
  footer?: React.ReactNode;
}) {
  return (
    <details style={{ minWidth: 0 }}>
      <summary
        style={{
          cursor: "pointer",
          color: summaryColor,
          fontSize: 10.5,
          fontFamily: "var(--f-mono)",
          letterSpacing: "0.04em",
        }}
      >
        {label} ▾
      </summary>
      <div
        className="mz-scroll"
        style={{
          marginTop: 4,
          padding: "6px 8px",
          background: "var(--glass-1)",
          border: "1px solid var(--glass-stroke)",
          borderRadius: 6,
          maxHeight: 140,
          overflow: "auto",
          fontSize: 10.5,
          lineHeight: 1.45,
          color: "var(--text-3)",
          minWidth: 0,
        }}
      >
        {paths.map((p) => (
          <div key={p} style={{ overflowWrap: "anywhere" }}>{p}</div>
        ))}
        {footer && (
          <div style={{ marginTop: 6, color: "var(--text-4)", fontStyle: "italic", overflowWrap: "anywhere" }}>
            {footer}
          </div>
        )}
      </div>
    </details>
  );
}
