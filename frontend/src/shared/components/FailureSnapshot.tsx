import { useState } from "react";
import { ZoomableImage } from "@/shared/components/ImageViewer";
import { BtnGhost, Pill } from "@/shared/components/Primitives";
import type { ForensicsBundle, ForensicsRequest } from "@/shared/api/types";

/**
 * "Page at failure": what the browser looked like and what it was doing when a
 * step threw — screenshot, URL, failed requests, console, accessibility tree,
 * form values and the trimmed DOM. The data comes from the execute / test
 * harness (see the backend's `__mzCaptureForensics`).
 *
 * "Copy for LLM" copies the plain-text digest, which is the same text an MCP
 * agent receives from `get_run_failure`, so a human can paste exactly what an
 * agent would have seen.
 */
export function FailureSnapshot({ bundle, report }: { bundle: ForensicsBundle; report?: string | null }) {
  const [copied, setCopied] = useState(false);
  const text = report ?? bundle.report ?? null;

  const network = bundle.network ?? [];
  const bad = network.filter(isBad);
  const consoleProblems = (bundle.console ?? []).filter((c) => c.type === "error" || c.type === "warning");
  const filled = (bundle.fields ?? []).filter((f) => f.value || f.checked);

  const onCopy = async () => {
    if (!text) return;
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      /* clipboard can be blocked; the text is still visible below */
    }
  };

  return (
    <div
      style={{
        marginTop: 14,
        padding: 14,
        background: "var(--glass-1)",
        border: "1px solid var(--glass-stroke-2)",
        borderRadius: 8,
      }}
    >
      <div style={{ display: "flex", alignItems: "center", gap: 10, flexWrap: "wrap" }}>
        <b style={{ fontSize: 13 }}>Page at failure</b>
        <span style={{ fontSize: 11, fontFamily: "var(--f-mono)", color: "var(--text-3)" }}>
          {bundle.call}
          {bundle.stepId && bundle.stepId !== bundle.call ? ` [${bundle.stepId}]` : ""}
        </span>
        <span style={{ flex: 1 }} />
        {bad.length > 0 && <Pill tone="err">{bad.length} bad request{bad.length === 1 ? "" : "s"}</Pill>}
        {consoleProblems.length > 0 && <Pill tone="neutral">{consoleProblems.length} console</Pill>}
        {text && <BtnGhost onClick={onCopy}>{copied ? "Copied" : "Copy for LLM"}</BtnGhost>}
      </div>

      {bundle.url && (
        <div style={{ fontSize: 12, color: "var(--text-2)", marginTop: 8, wordBreak: "break-all" }}>
          <code>{bundle.url}</code>
          {bundle.title ? ` — “${bundle.title}”` : ""}
        </div>
      )}
      <div style={{ fontSize: 11, color: "var(--text-3)", marginTop: 4 }}>
        {[
          bundle.readyState && `readyState ${bundle.readyState}`,
          bundle.viewport && `${bundle.viewport.w}×${bundle.viewport.h}`,
          bundle.activeElement && `focused ${bundle.activeElement}`,
          bundle.prevStepId && `last ok step ${bundle.prevStepId}`,
        ]
          .filter(Boolean)
          .join(" · ")}
      </div>

      {bundle.screenshotJpegBase64 && (
        <div style={{ marginTop: 10, maxWidth: "100%", width: "fit-content" }}>
          <ZoomableImage
            alt="Page when the step failed"
            caption={bundle.url ? `Page when the step failed: ${bundle.url}` : "Page when the step failed"}
            src={`data:image/jpeg;base64,${bundle.screenshotJpegBase64}`}
            style={{ width: "auto", maxWidth: "100%", maxHeight: 360, border: "1px solid var(--glass-stroke-2)", borderRadius: 6 }}
          />
        </div>
      )}

      <Block title={`Network (${bundle.networkTotal ?? network.length} seen)`} open={bad.length > 0}>
        {network.length === 0 ? (
          <Muted>No XHR / fetch / document traffic captured.</Muted>
        ) : (
          <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
            {[...bad, ...network.filter((r) => !isBad(r))].map((r) => (
              <RequestRow key={r.n} r={r} />
            ))}
          </div>
        )}
      </Block>

      {(consoleProblems.length > 0 || (bundle.pageErrors?.length ?? 0) > 0) && (
        <Block title="Console" open>
          <Pre>
            {[
              ...consoleProblems.map((c) => `[${c.type}] ${c.text}`),
              ...(bundle.pageErrors ?? []).map((e) => `[pageerror] ${e.split("\n")[0]}`),
            ].join("\n")}
          </Pre>
        </Block>
      )}

      {(bundle.inflight?.length ?? 0) > 0 && (
        <Block title={`In flight at failure (${bundle.inflight!.length})`}>
          <Pre>{bundle.inflight!.join("\n")}</Pre>
        </Block>
      )}

      {filled.length > 0 && (
        <Block title={`Form values (${filled.length})`}>
          <Pre>
            {filled
              .map((f) => `${f.tag}${f.type ? `[${f.type}]` : ""} ${f.name ?? f.id ?? "?"} = ${f.value}`)
              .join("\n")}
          </Pre>
        </Block>
      )}

      {bundle.aria && (
        <Block title="Accessibility tree">
          <Pre>{bundle.aria}</Pre>
        </Block>
      )}

      {bundle.dom && (
        <Block title={`DOM (trimmed, ${bundle.dom.length.toLocaleString()} chars)`}>
          <Pre>{bundle.dom}</Pre>
        </Block>
      )}

      {bundle.redactionError && <Muted>{bundle.redactionError}</Muted>}
    </div>
  );
}

function isBad(r: ForensicsRequest) {
  return !!r.failure || (r.status ?? 0) >= 400;
}

function RequestRow({ r }: { r: ForensicsRequest }) {
  const bad = isBad(r);
  const outcome = r.failure ? `FAILED ${r.failure}` : r.status ?? "…";
  return (
    <details style={{ fontSize: 12 }}>
      <summary style={{ cursor: "pointer", fontFamily: "var(--f-mono)", color: bad ? "#f0a89e" : "var(--text-2)", wordBreak: "break-all" }}>
        <span style={{ color: "var(--text-3)" }}>[{r.step ?? "-"}]</span> {r.method} {r.url} → {outcome}
        {r.ms != null ? ` (${r.ms}ms)` : ""}
      </summary>
      {r.reqBody && (
        <>
          <Muted>request</Muted>
          <Pre>{r.reqBody}</Pre>
        </>
      )}
      {r.body && (
        <>
          <Muted>response{r.mime ? ` (${r.mime})` : ""}</Muted>
          <Pre>{r.body}</Pre>
        </>
      )}
      {!r.reqBody && !r.body && <Muted>No body captured.</Muted>}
    </details>
  );
}

function Block({ title, open, children }: { title: string; open?: boolean; children: React.ReactNode }) {
  return (
    <details open={open} style={{ marginTop: 10 }}>
      <summary style={{ fontSize: 12, color: "var(--text-2)", cursor: "pointer" }}>{title}</summary>
      <div style={{ marginTop: 6 }}>{children}</div>
    </details>
  );
}

function Pre({ children }: { children: React.ReactNode }) {
  return (
    <pre
      style={{
        margin: "4px 0 0",
        padding: 8,
        fontSize: 11,
        fontFamily: "var(--f-mono)",
        color: "var(--text-2)",
        background: "var(--glass-1)",
        border: "1px solid var(--glass-stroke-2)",
        borderRadius: 6,
        maxHeight: 280,
        overflow: "auto",
        whiteSpace: "pre-wrap",
        wordBreak: "break-word",
      }}
    >
      {children}
    </pre>
  );
}

function Muted({ children }: { children: React.ReactNode }) {
  return <div style={{ fontSize: 11, color: "var(--text-3)", marginTop: 4 }}>{children}</div>;
}
