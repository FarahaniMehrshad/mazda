import { useMemo, useState } from "react";
import {
  useAddSignatureDependency,
  useRemoveSignatureDependency,
  useSignatureDependencies,
  useSignatures,
} from "./api";
import { BtnGhost, Pill } from "@/shared/components/Primitives";
import { Icon } from "@/shared/icons/Icon";
import { ApiError } from "@/shared/api/client";

/**
 * Panel rendered on the signature editor (Interactable page) that lets the
 * user declare which OTHER signatures this one embeds / uses. Generation
 * pipelines union every transitive dep's elements into the LLM context, so
 * a test prompt can reach into an embedded component's controls without the
 * user having to re-declare those selectors per signature.
 *
 * Add / remove are immediate (mutations) — there is no separate save step.
 * Cycle detection happens on the backend; we surface the validation error
 * here as an inline message rather than overwriting the picker state.
 */
export function DependenciesPanel({ signatureId }: { signatureId: string }) {
  const { data: deps = [], isLoading } = useSignatureDependencies(signatureId);
  const { data: allSignatures = [] } = useSignatures();
  const add = useAddSignatureDependency(signatureId);
  const remove = useRemoveSignatureDependency(signatureId);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [pickerError, setPickerError] = useState<string | null>(null);

  // Signatures eligible to be added: every signature OTHER than this one,
  // minus those already declared as a direct dep. (Transitive deps are NOT
  // excluded — declaring a redundant edge is fine and the resolver dedupes;
  // showing them as eligible just adds noise.)
  const declared = useMemo(() => new Set(deps.map((d) => d.id)), [deps]);
  const eligible = useMemo(
    () =>
      allSignatures
        .filter((s) => s.id !== signatureId && !declared.has(s.id))
        .sort((a, b) => a.name.localeCompare(b.name)),
    [allSignatures, signatureId, declared],
  );

  async function onAdd(dependsOnId: string) {
    setPickerError(null);
    try {
      await add.mutateAsync(dependsOnId);
      setPickerOpen(false);
    } catch (err) {
      // Cycle errors come back as 400 with a human-readable message — show
      // inline in the picker so the user can pick a different signature
      // without losing context.
      setPickerError(err instanceof ApiError ? err.message : "Failed to add dependency.");
    }
  }

  async function onRemove(depId: string) {
    try {
      await remove.mutateAsync(depId);
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Failed to remove dependency.");
    }
  }

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
      <div
        style={{
          display: "flex",
          justifyContent: "space-between",
          alignItems: "baseline",
          gap: 8,
        }}
      >
        <div className="mz-eyebrow">Dependencies · {deps.length}</div>
        <BtnGhost
          icon={Icon.spark(10)}
          onClick={() => {
            setPickerError(null);
            setPickerOpen((v) => !v);
          }}
          disabled={eligible.length === 0 && !pickerOpen}
        >
          {pickerOpen ? "Cancel" : "Add"}
        </BtnGhost>
      </div>

      <div className="mz-glass" style={{ padding: 0, overflow: "hidden" }}>
        {isLoading ? (
          <div style={{ padding: 14, color: "var(--text-4)", fontSize: 12 }}>Loading…</div>
        ) : deps.length === 0 ? (
          <div
            style={{
              padding: "14px 18px",
              color: "var(--text-4)",
              fontSize: 12,
              lineHeight: 1.55,
            }}
          >
            No dependencies. If this component embeds / uses another component
            whose Signature you've already created, add it here and its
            elements will flow into generated test scripts automatically.
          </div>
        ) : (
          deps.map((d) => (
            <div
              key={d.id}
              style={{
                padding: "12px 18px",
                borderBottom: "1px solid var(--glass-stroke)",
                display: "grid",
                gridTemplateColumns: "1fr auto auto",
                gap: 10,
                alignItems: "center",
              }}
            >
              <div>
                <div
                  style={{
                    fontFamily: "var(--f-sans)",
                    fontSize: 13,
                    color: "var(--text-1)",
                  }}
                >
                  {d.name}
                </div>
                <div
                  style={{
                    fontFamily: "var(--f-mono)",
                    fontSize: 10.5,
                    color: "var(--text-4)",
                    marginTop: 2,
                  }}
                >
                  {d.type} · {d.elementCount} element{d.elementCount === 1 ? "" : "s"}
                </div>
              </div>
              <Pill>{d.type}</Pill>
              <button
                onClick={() => onRemove(d.id)}
                disabled={remove.isPending}
                title="Remove dependency"
                style={{
                  background: "transparent",
                  border: "1px solid var(--glass-stroke)",
                  borderRadius: 6,
                  padding: "4px 10px",
                  color: "var(--text-3)",
                  fontFamily: "var(--f-mono)",
                  fontSize: 11,
                  cursor: "pointer",
                }}
              >
                Remove
              </button>
            </div>
          ))
        )}
      </div>

      {pickerOpen && (
        <div
          className="mz-glass-2"
          style={{
            padding: 0,
            overflow: "hidden",
            border: "1px solid rgba(234,112,40,0.4)",
          }}
        >
          <div
            style={{
              padding: "10px 14px",
              borderBottom: "1px solid var(--glass-stroke)",
              fontFamily: "var(--f-mono)",
              fontSize: 10.5,
              color: "var(--text-3)",
              letterSpacing: "0.06em",
              textTransform: "uppercase",
            }}
          >
            Pick a signature to depend on
          </div>
          {pickerError && (
            <div
              style={{
                padding: "10px 14px",
                color: "var(--err, #e07a5f)",
                fontSize: 12,
                fontFamily: "var(--f-mono)",
                borderBottom: "1px solid var(--glass-stroke)",
                background: "rgba(224,122,95,0.06)",
                lineHeight: 1.5,
              }}
            >
              {pickerError}
            </div>
          )}
          {eligible.length === 0 ? (
            <div style={{ padding: 14, color: "var(--text-4)", fontSize: 12 }}>
              No other signatures available. Create one first, then come back.
            </div>
          ) : (
            <div style={{ maxHeight: 240, overflow: "auto" }}>
              {eligible.map((s) => (
                <button
                  key={s.id}
                  onClick={() => onAdd(s.id)}
                  disabled={add.isPending}
                  style={{
                    width: "100%",
                    background: "transparent",
                    border: "none",
                    borderBottom: "1px solid var(--glass-stroke)",
                    color: "var(--text-1)",
                    textAlign: "left",
                    padding: "10px 14px",
                    cursor: add.isPending ? "wait" : "pointer",
                    display: "flex",
                    justifyContent: "space-between",
                    alignItems: "center",
                    gap: 10,
                  }}
                >
                  <span style={{ fontSize: 13 }}>{s.name}</span>
                  <span
                    style={{
                      fontFamily: "var(--f-mono)",
                      fontSize: 10.5,
                      color: "var(--text-4)",
                    }}
                  >
                    {s.type} · {s.nodeCount} el
                  </span>
                </button>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
