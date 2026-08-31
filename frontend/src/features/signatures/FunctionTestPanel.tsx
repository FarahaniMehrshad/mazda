import { useMemo, useState } from "react";
import Editor from "@monaco-editor/react";
import { BtnGhost, BtnPrimary, Pill } from "@/shared/components/Primitives";
import { ApiError } from "@/shared/api/client";
import type { FunctionTestResultDto, SignatureFunctionDto } from "@/shared/api/types";
import { useTestFunction } from "./functionsApi";
import {
  LabeledInput,
  LabeledSelect,
  isSimpleParamSet,
  parseParams,
  prettifyJson,
} from "./functionForm";

/**
 * Run one catalogue function against the live CDP browser.
 *
 * This executes the SAVED body, not whatever is open in the editor — which is
 * why it is a modal off the catalogue row rather than a section inside
 * FunctionEditor, where it would quietly test something other than the code
 * on screen.
 *
 * Two failure shapes reach this component and they mean different things:
 * a rejected request (unreachable browser, invalid args) renders as an error
 * pill, while a body that ran and threw comes back as a normal result with
 * `ok: false` — seeing that message is the whole point of the affordance.
 */
export function FunctionTestPanel({
  fn,
  sigId,
  onClose,
}: {
  fn: SignatureFunctionDto;
  sigId: string;
  onClose: () => void;
}) {
  const test = useTestFunction(sigId);
  const params = useMemo(() => parseParams(fn.parametersJsonSchema), [fn.parametersJsonSchema]);
  const simple = useMemo(() => isSimpleParamSet(params), [params]);

  // Two ways to supply args. The generated form covers flat primitives — what
  // the catalogue generator actually emits — and raw JSON is both the fallback
  // for anything nested and an escape hatch when the form is in the way.
  const [raw, setRaw] = useState(!simple);
  const [values, setValues] = useState<Record<string, string>>({});
  const [rawJson, setRawJson] = useState("{}");
  const [result, setResult] = useState<FunctionTestResultDto | null>(null);
  const [err, setErr] = useState<string | null>(null);

  const buildArgs = (): string => {
    if (raw) return rawJson.trim() || "{}";
    const out: Record<string, unknown> = {};
    for (const p of params) {
      const v = values[p.name];
      // An untouched field is omitted rather than sent as "", which would
      // fail the server's type check on a number or boolean parameter.
      if (v === undefined || v === "") continue;
      if (p.type === "number" || p.type === "integer") {
        const n = Number(v);
        out[p.name] = Number.isNaN(n) ? v : n;
      } else if (p.type === "boolean") {
        out[p.name] = v === "true";
      } else {
        out[p.name] = v;
      }
    }
    return JSON.stringify(out);
  };

  const onRun = async () => {
    setErr(null);
    setResult(null);
    try {
      setResult(await test.mutateAsync({ fnId: fn.id, argsJson: buildArgs() }));
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : String(e));
    }
  };

  return (
    <div
      style={{
        position: "fixed",
        inset: 0,
        background: "rgba(0,0,0,0.6)",
        backdropFilter: "blur(4px)",
        display: "grid",
        placeItems: "center",
        zIndex: 100,
        padding: 24,
      }}
      onClick={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div
        style={{
          background: "var(--surface-1, #14100c)",
          border: "1px solid var(--glass-stroke-2)",
          borderRadius: 14,
          padding: 24,
          width: "min(760px, 100%)",
          maxHeight: "90vh",
          overflow: "auto",
        }}
      >
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
          <div style={{ fontSize: 16, fontWeight: 500, fontFamily: "var(--f-mono)" }}>
            Test {fn.name}
          </div>
          <BtnGhost onClick={onClose}>Close</BtnGhost>
        </div>

        <div style={{ fontSize: 12, color: "var(--text-3)", marginTop: 8, lineHeight: 1.6 }}>
          Runs the saved body against the Chrome you have attached over CDP.
          {fn.kind === "action" && (
            <span style={{ color: "var(--warn, #d9a441)" }}>
              {" "}
              This is an action — it changes the live page for real and cannot be undone.
            </span>
          )}
        </div>

        {fn.preconditions && (
          <div style={{ fontSize: 12, color: "var(--text-3)", marginTop: 10 }}>
            <b>Precondition:</b> {fn.preconditions}
          </div>
        )}

        <div style={{ marginTop: 20, display: "flex", alignItems: "center", gap: 12 }}>
          <div style={{ fontSize: 11, fontFamily: "var(--f-mono)", color: "var(--text-3)" }}>
            ARGUMENTS
          </div>
          {simple && params.length > 0 && (
            <BtnGhost onClick={() => setRaw(!raw)}>{raw ? "Use form" : "Edit as JSON"}</BtnGhost>
          )}
        </div>

        {params.length === 0 && !raw && (
          <div style={{ fontSize: 12, color: "var(--text-3)", marginTop: 8 }}>
            This function takes no arguments.
          </div>
        )}

        {!raw &&
          params.map((p) =>
            p.enumValues ? (
              <LabeledSelect
                key={p.name}
                label={`${p.name}${p.required ? " *" : ""} — ${p.type ?? "any"}`}
                value={values[p.name] ?? ""}
                options={[
                  { value: "", label: "—" },
                  ...p.enumValues.map((v) => ({ value: String(v), label: String(v) })),
                ]}
                onChange={(v) => setValues((s) => ({ ...s, [p.name]: v }))}
              />
            ) : p.type === "boolean" ? (
              <LabeledSelect
                key={p.name}
                label={`${p.name}${p.required ? " *" : ""} — boolean`}
                value={values[p.name] ?? ""}
                options={[
                  { value: "", label: "—" },
                  { value: "true", label: "true" },
                  { value: "false", label: "false" },
                ]}
                onChange={(v) => setValues((s) => ({ ...s, [p.name]: v }))}
              />
            ) : (
              <LabeledInput
                key={p.name}
                label={`${p.name}${p.required ? " *" : ""} — ${p.type ?? "any"}`}
                value={values[p.name] ?? ""}
                onChange={(v) => setValues((s) => ({ ...s, [p.name]: v }))}
                placeholder={p.description ?? ""}
              />
            )
          )}

        {raw && (
          <div
            style={{
              marginTop: 10,
              border: "1px solid var(--glass-stroke-2)",
              borderRadius: 10,
              overflow: "hidden",
            }}
          >
            <Editor
              height="140px"
              defaultLanguage="json"
              theme="vs-dark"
              value={rawJson}
              onChange={(v) => setRawJson(v ?? "")}
              options={{ minimap: { enabled: false }, fontSize: 12, tabSize: 2 }}
            />
          </div>
        )}

        <div style={{ marginTop: 20, display: "flex", justifyContent: "flex-end", gap: 10 }}>
          <BtnPrimary onClick={onRun} disabled={test.isPending}>
            {test.isPending ? "Running…" : "Run against browser"}
          </BtnPrimary>
        </div>

        {test.isPending && (
          <div style={{ fontSize: 12, color: "var(--text-3)", marginTop: 12 }}>
            Spawning Playwright and attaching over CDP — this takes a few seconds.
          </div>
        )}

        {err && <ErrorBlock label="Could not run" text={err} />}

        {result && <TestResultView result={result} />}
      </div>
    </div>
  );
}

/**
 * Multi-line failure text — a harness log tail, a Playwright stack, a
 * validation message.
 *
 * Deliberately NOT a `Pill`: that is a pill-shaped inline chip sized for a
 * one-word status, and feeding it a 2000-character runner log stretches it
 * into an ellipse with the text spilling out of its own border. Long output
 * needs a block that can wrap, scroll, and preserve the newlines the runner
 * emitted.
 */
function ErrorBlock({ label, text }: { label: string; text: string }) {
  return (
    <div
      style={{
        marginTop: 16,
        background: "rgba(232,92,74,0.10)",
        border: "1px solid rgba(232,92,74,0.35)",
        borderRadius: 10,
        overflow: "hidden",
      }}
    >
      <div
        style={{
          padding: "8px 12px",
          borderBottom: "1px solid rgba(232,92,74,0.22)",
          fontSize: 10.5,
          fontFamily: "var(--f-mono)",
          letterSpacing: "0.04em",
          textTransform: "uppercase",
          color: "#f0a89e",
        }}
      >
        {label}
      </div>
      <pre
        style={{
          margin: 0,
          padding: 12,
          fontSize: 11.5,
          lineHeight: 1.6,
          fontFamily: "var(--f-mono)",
          // The runner's own line breaks carry meaning; keep them, but wrap
          // the long ones instead of forcing a horizontal scrollbar.
          whiteSpace: "pre-wrap",
          overflowWrap: "anywhere",
          color: "var(--text-2)",
          maxHeight: 260,
          overflow: "auto",
        }}
      >
        {text}
      </pre>
    </div>
  );
}

function TestResultView({ result }: { result: FunctionTestResultDto }) {
  return (
    <div style={{ marginTop: 20, borderTop: "1px solid var(--glass-stroke-2)", paddingTop: 16 }}>
      <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
        <Pill tone={result.ok ? "ok" : "err"}>{result.ok ? "passed" : "failed"}</Pill>
        <div style={{ fontSize: 11, fontFamily: "var(--f-mono)", color: "var(--text-3)" }}>
          {result.durationMs} ms
        </div>
      </div>

      {result.pageUrl && (
        <div style={{ fontSize: 11, color: "var(--text-3)", marginTop: 10, wordBreak: "break-all" }}>
          Browser was on <code>{result.pageUrl}</code>
          {result.pageTitle ? ` — “${result.pageTitle}”` : ""}
        </div>
      )}

      {!result.ok && (
        <div>
          <ErrorBlock
            label={result.errorKind ?? "Error"}
            text={result.errorMessage ?? "(no message)"}
          />
          {result.errorStack && (
            <details style={{ marginTop: 8 }}>
              <summary style={{ fontSize: 11, color: "var(--text-3)", cursor: "pointer" }}>
                Stack
              </summary>
              <pre
                style={{
                  fontSize: 11,
                  color: "var(--text-3)",
                  whiteSpace: "pre-wrap",
                  marginTop: 6,
                }}
              >
                {result.errorStack}
              </pre>
            </details>
          )}
        </div>
      )}

      {result.ok && (
        <div style={{ marginTop: 12 }}>
          <div
            style={{
              fontSize: 11,
              fontFamily: "var(--f-mono)",
              color: "var(--text-3)",
              marginBottom: 6,
            }}
          >
            returned
          </div>
          {result.returnedJson ? (
            <div style={{ border: "1px solid var(--glass-stroke-2)", borderRadius: 10, overflow: "hidden" }}>
              <Editor
                height="200px"
                defaultLanguage="json"
                theme="vs-dark"
                value={prettifyJson(result.returnedJson)}
                options={{ minimap: { enabled: false }, fontSize: 12, readOnly: true }}
              />
            </div>
          ) : (
            <div style={{ fontSize: 12, color: "var(--text-3)" }}>
              Nothing — expected for an action function.
            </div>
          )}
        </div>
      )}

      <details style={{ marginTop: 12 }}>
        <summary style={{ fontSize: 11, color: "var(--text-3)", cursor: "pointer" }}>
          Runner log
        </summary>
        <pre
          style={{
            fontSize: 11,
            color: "var(--text-3)",
            whiteSpace: "pre-wrap",
            wordBreak: "break-word",
            marginTop: 6,
            maxHeight: 240,
            overflow: "auto",
          }}
        >
          {result.logTail}
        </pre>
      </details>
    </div>
  );
}
