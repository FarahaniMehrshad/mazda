import { useEffect, useMemo, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import Editor from "@monaco-editor/react";
import { ScreenShell, StepIndicator, BtnPrimary, BtnGhost, Pill } from "@/shared/components/Primitives";
import { TopBar } from "@/shared/components/TopBar";
import { useStepNavigator } from "@/shared/components/useStepNavigator";
import { useSignature } from "./api";
import {
  useSignatureFunctions,
  useGenerateFunctions,
  useCreateFunction,
  useUpdateFunction,
  useDeleteFunction,
  type FunctionInput,
} from "./functionsApi";
import type { SignatureFunctionDto } from "@/shared/api/types";
import { FunctionTestPanel } from "./FunctionTestPanel";
import {
  LabeledInput,
  LabeledSelect,
  extractParamPreview,
  prettifyJson,
} from "./functionForm";
import { ApiError } from "@/shared/api/client";

/**
 * Step 6 of the wizard — Functions.
 *
 * Two panes stacked vertically: state functions (readers that run during
 * gatherInformation) on top, action functions (the planner's building
 * blocks) below. Regenerate replaces the whole catalogue via the LLM;
 * per-function edit lets the author fix up individual bodies without
 * losing the rest.
 *
 * Once the catalogue is in place, Continue → routes to the Run page (step
 * 7), which is where the user turns a prompt into a plan and executes it.
 */
export function FunctionsPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const sig = useSignature(id);
  const fns = useSignatureFunctions(id);
  const generate = useGenerateFunctions(id);
  const remove = useDeleteFunction(id);

  const [editing, setEditing] = useState<SignatureFunctionDto | "new" | null>(null);
  const [testing, setTesting] = useState<SignatureFunctionDto | null>(null);
  const [err, setErr] = useState<string | null>(null);

  const navigator = useStepNavigator(id ? { mode: "signature", signatureId: id } : null);

  // Split the catalogue into the two panes. Sort by order (server sends
  // them ordered already, but paranoia is cheap).
  const { states, actions } = useMemo(() => {
    const list = (fns.data ?? []).slice().sort((a, b) => a.order - b.order);
    return {
      states: list.filter((f) => f.kind === "state"),
      actions: list.filter((f) => f.kind === "action"),
    };
  }, [fns.data]);

  const onGenerate = async () => {
    setErr(null);
    try {
      await generate.mutateAsync();
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : String(e));
    }
  };

  const onDelete = async (fn: SignatureFunctionDto) => {
    if (!confirm(`Delete function ${fn.name}?`)) return;
    setErr(null);
    try {
      await remove.mutateAsync(fn.id);
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : String(e));
    }
  };

  return (
    <ScreenShell>
      <TopBar />
      {/*
        `mz-stage` is a flex column and every child needs to declare itself
        as the scroll region — otherwise long content just clips at the
        viewport bottom. The outer div carries flex:1 + overflow:auto so
        the scrollbar rides the viewport edge; the inner div preserves the
        centred max-width layout for readable content.
      */}
      <div className="mz-scroll" style={{ flex: 1, overflow: "auto" }}>
        <div style={{ padding: "40px 48px 80px", maxWidth: 1200, margin: "0 auto" }}>
        <StepIndicator step={6} onStepClick={navigator} />

        <div style={{ marginTop: 32, display: "flex", justifyContent: "space-between", alignItems: "flex-end" }}>
          <div>
            <div style={{ fontSize: 11, fontFamily: "var(--f-mono)", color: "var(--text-3)", letterSpacing: "0.05em" }}>
              STEP 06 · FUNCTIONS
            </div>
            <div style={{ fontSize: 22, fontWeight: 500, marginTop: 6 }}>
              {sig.data?.name ?? "…"} — function catalogue
            </div>
            <div style={{ fontSize: 13, color: "var(--text-3)", marginTop: 6, maxWidth: 680 }}>
              High-level operations the planner LLM composes into plans at runtime. State functions
              run during gather; action functions execute the plan.
            </div>
          </div>
          <div style={{ display: "flex", gap: 10 }}>
            <BtnGhost onClick={() => navigate(`/signatures/${id}/learn`)}>Learn by watching</BtnGhost>
            <BtnGhost onClick={() => setEditing("new")}>+ Add function</BtnGhost>
            <BtnPrimary onClick={onGenerate} disabled={generate.isPending}>
              {generate.isPending ? "Regenerating…" : fns.data && fns.data.length > 0 ? "Regenerate all" : "Generate catalogue"}
            </BtnPrimary>
          </div>
        </div>

        {err && (
          <div style={{ marginTop: 20 }}>
            <Pill tone="err">{err}</Pill>
          </div>
        )}

        {fns.isLoading && <div style={{ marginTop: 40, color: "var(--text-3)" }}>Loading catalogue…</div>}
        {fns.data && fns.data.length === 0 && !generate.isPending && (
          <div
            style={{
              marginTop: 40,
              padding: 24,
              border: "1px dashed var(--glass-stroke-2)",
              borderRadius: 12,
              color: "var(--text-3)",
              fontSize: 13,
            }}
          >
            No functions yet. Click <b>Generate catalogue</b> to have the LLM propose one from the
            signature's elements + relationships + user flows.
          </div>
        )}

        <FunctionPane
          title="State functions"
          badge="reader"
          subtitle="Run automatically during gatherInformation. Return structured data the planner reads."
          list={states}
          onEdit={setEditing}
          onDelete={onDelete}
          onTest={setTesting}
        />
        <FunctionPane
          title="Action functions"
          badge="mutator"
          subtitle="Building blocks the planner sequences into a plan. One function call per plan step."
          list={actions}
          onEdit={setEditing}
          onDelete={onDelete}
          onTest={setTesting}
        />

        {fns.data && fns.data.length > 0 && (
          <div style={{ marginTop: 40, display: "flex", justifyContent: "flex-end" }}>
            <BtnPrimary onClick={() => navigate(`/signatures/${id}/run`)}>Continue to Run →</BtnPrimary>
          </div>
        )}
        </div>
      </div>

      {editing && (
        <FunctionEditor
          fn={editing === "new" ? null : editing}
          sigId={id!}
          nextOrder={(fns.data ?? []).reduce((m, f) => Math.max(m, f.order), -1) + 1}
          onClose={() => setEditing(null)}
        />
      )}

      {testing && (
        <FunctionTestPanel fn={testing} sigId={id!} onClose={() => setTesting(null)} />
      )}
    </ScreenShell>
  );
}

// ── Function pane ────────────────────────────────────────────────────

function FunctionPane({
  title,
  badge,
  subtitle,
  list,
  onEdit,
  onDelete,
  onTest,
}: {
  title: string;
  badge: string;
  subtitle: string;
  list: SignatureFunctionDto[];
  onEdit: (fn: SignatureFunctionDto) => void;
  onDelete: (fn: SignatureFunctionDto) => void;
  onTest: (fn: SignatureFunctionDto) => void;
}) {
  return (
    <div style={{ marginTop: 32 }}>
      <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
        <div style={{ fontSize: 13, fontWeight: 500, color: "var(--text-1)" }}>{title}</div>
        <Pill tone="neutral">{badge}</Pill>
        <div style={{ fontSize: 11, fontFamily: "var(--f-mono)", color: "var(--text-3)" }}>
          {list.length}
        </div>
      </div>
      <div style={{ fontSize: 12, color: "var(--text-3)", marginTop: 4 }}>{subtitle}</div>

      <div style={{ marginTop: 12, display: "flex", flexDirection: "column", gap: 8 }}>
        {list.map((fn) => (
          <FunctionRow
            key={fn.id}
            fn={fn}
            onEdit={() => onEdit(fn)}
            onDelete={() => onDelete(fn)}
            onTest={() => onTest(fn)}
          />
        ))}
        {list.length === 0 && (
          <div
            style={{
              fontSize: 12,
              color: "var(--text-3)",
              padding: "10px 14px",
              border: "1px dashed var(--glass-stroke-2)",
              borderRadius: 10,
            }}
          >
            No {title.toLowerCase()} yet.
          </div>
        )}
      </div>
    </div>
  );
}

function FunctionRow({
  fn,
  onEdit,
  onDelete,
  onTest,
}: {
  fn: SignatureFunctionDto;
  onEdit: () => void;
  onDelete: () => void;
  onTest: () => void;
}) {
  // Extract parameter names + types from the schema for a one-line preview
  // — spares the user from opening the editor to see the signature.
  const paramList = useMemo(() => extractParamPreview(fn.parametersJsonSchema), [fn.parametersJsonSchema]);
  return (
    <div
      style={{
        display: "grid",
        gridTemplateColumns: "1fr auto",
        gap: 16,
        alignItems: "center",
        padding: "12px 14px",
        borderRadius: 10,
        background: "var(--glass-1)",
        border: "1px solid var(--glass-stroke-2)",
      }}
    >
      <div>
        <div style={{ fontFamily: "var(--f-mono)", fontSize: 13, color: "var(--text-1)" }}>
          {fn.name}
          <span style={{ color: "var(--text-3)" }}>({paramList})</span>
        </div>
        {fn.description && (
          <div style={{ fontSize: 12, color: "var(--text-3)", marginTop: 4, lineHeight: 1.5 }}>
            {fn.description}
          </div>
        )}
      </div>
      <div style={{ display: "flex", gap: 8 }}>
        <BtnGhost onClick={onTest}>Test</BtnGhost>
        <BtnGhost onClick={onEdit}>Edit</BtnGhost>
        <BtnGhost onClick={onDelete}>Delete</BtnGhost>
      </div>
    </div>
  );
}

// ── Editor modal ─────────────────────────────────────────────────────

interface EditorFields extends FunctionInput {}

function FunctionEditor({
  fn,
  sigId,
  nextOrder,
  onClose,
}: {
  fn: SignatureFunctionDto | null;
  sigId: string;
  nextOrder: number;
  onClose: () => void;
}) {
  const create = useCreateFunction(sigId);
  const update = useUpdateFunction(sigId);
  const [err, setErr] = useState<string | null>(null);

  const [fields, setFields] = useState<EditorFields>(() =>
    fn
      ? {
          name: fn.name,
          kind: fn.kind,
          description: fn.description,
          parametersJsonSchema: fn.parametersJsonSchema,
          returnsJsonSchema: fn.returnsJsonSchema,
          preconditions: fn.preconditions,
          body: fn.body,
          order: fn.order,
        }
      : {
          name: "",
          kind: "action",
          description: "",
          parametersJsonSchema: JSON.stringify(
            { type: "object", properties: {}, required: [] },
            null,
            2,
          ),
          returnsJsonSchema: JSON.stringify({ type: "null" }, null, 2),
          preconditions: "",
          body: "",
          order: nextOrder,
        },
  );

  const set = <K extends keyof EditorFields>(k: K, v: EditorFields[K]) =>
    setFields((s) => ({ ...s, [k]: v }));

  const onSave = async () => {
    setErr(null);
    try {
      if (fn) {
        await update.mutateAsync({ fnId: fn.id, input: fields });
      } else {
        await create.mutateAsync(fields);
      }
      onClose();
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : String(e));
    }
  };

  const busy = create.isPending || update.isPending;

  return (
    <div
      role="dialog"
      aria-modal="true"
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
          width: "min(920px, 100%)",
          maxHeight: "90vh",
          overflow: "auto",
        }}
      >
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
          <div style={{ fontSize: 16, fontWeight: 500 }}>{fn ? `Edit ${fn.name}` : "New function"}</div>
          <BtnGhost onClick={onClose}>Cancel</BtnGhost>
        </div>

        <div style={{ marginTop: 20, display: "grid", gridTemplateColumns: "1fr 160px", gap: 12 }}>
          <LabeledInput
            label="Name"
            value={fields.name}
            onChange={(v) => set("name", v)}
            placeholder="AddTask"
          />
          <LabeledSelect
            label="Kind"
            value={fields.kind}
            options={[
              { value: "state", label: "state" },
              { value: "action", label: "action" },
            ]}
            onChange={(v) => set("kind", v as "state" | "action")}
          />
        </div>

        <LabeledInput
          label="Description"
          value={fields.description}
          onChange={(v) => set("description", v)}
          placeholder="One or two sentences explaining what this does."
          multiline
        />

        <LabeledInput
          label="Preconditions (optional)"
          value={fields.preconditions}
          onChange={(v) => set("preconditions", v)}
          placeholder="The task list must be the visible view."
        />

        <div style={{ marginTop: 16, display: "grid", gridTemplateColumns: "1fr 1fr", gap: 12 }}>
          <SchemaEditor
            label="parameters (JSON Schema)"
            value={fields.parametersJsonSchema}
            onChange={(v) => set("parametersJsonSchema", v)}
          />
          <SchemaEditor
            label="returns (JSON Schema)"
            value={fields.returnsJsonSchema}
            onChange={(v) => set("returnsJsonSchema", v)}
          />
        </div>

        <div style={{ marginTop: 16 }}>
          <div style={{ fontSize: 11, color: "var(--text-3)", marginBottom: 6, fontFamily: "var(--f-mono)" }}>
            body — TypeScript, wrapped as{" "}
            <code>async (page, args) =&gt; &#123; ... &#125;</code>. Params destructured from{" "}
            <code>args</code>. <code>__mzClick</code>, <code>__mzSelectOption</code>,{" "}
            <code>__mzPointerDrag</code>, <code>__mzPointerSwap</code> in scope.
          </div>
          <div style={{ border: "1px solid var(--glass-stroke-2)", borderRadius: 10, overflow: "hidden" }}>
            <Editor
              height="260px"
              defaultLanguage="typescript"
              theme="vs-dark"
              value={fields.body}
              onChange={(v) => set("body", v ?? "")}
              options={{ minimap: { enabled: false }, fontSize: 12, tabSize: 2 }}
            />
          </div>
        </div>

        {err && (
          <div style={{ marginTop: 12 }}>
            <Pill tone="err">{err}</Pill>
          </div>
        )}

        <div style={{ marginTop: 20, display: "flex", justifyContent: "flex-end", gap: 10 }}>
          <BtnGhost onClick={onClose}>Cancel</BtnGhost>
          <BtnPrimary onClick={onSave} disabled={busy}>
            {busy ? "Saving…" : "Save"}
          </BtnPrimary>
        </div>
      </div>
    </div>
  );
}

// ── Small inputs ─────────────────────────────────────────────────────

function SchemaEditor({
  label,
  value,
  onChange,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
}) {
  // Best-effort pretty-print on mount so the LLM's minified output is
  // legible. Falls back to as-is if the value isn't valid JSON.
  const [display, setDisplay] = useState(() => prettifyJson(value));
  useEffect(() => setDisplay(prettifyJson(value)), [value]);
  return (
    <div>
      <div style={{ fontSize: 11, color: "var(--text-3)", marginBottom: 6, fontFamily: "var(--f-mono)" }}>
        {label}
      </div>
      <div style={{ border: "1px solid var(--glass-stroke-2)", borderRadius: 10, overflow: "hidden" }}>
        <Editor
          height="180px"
          defaultLanguage="json"
          theme="vs-dark"
          value={display}
          onChange={(v) => {
            setDisplay(v ?? "");
            onChange(v ?? "");
          }}
          options={{ minimap: { enabled: false }, fontSize: 12, tabSize: 2 }}
        />
      </div>
    </div>
  );
}
