import { useEffect, useMemo, useState } from "react";
import { TopBar } from "@/shared/components/TopBar";
import {
  ScreenShell,
  BtnPrimary,
  BtnGhost,
  Pill,
} from "@/shared/components/Primitives";
import { Icon } from "@/shared/icons/Icon";
import { useSignatures } from "@/features/signatures/api";
import {
  useMcpTools,
  useCreateMcpTool,
  useUpdateMcpTool,
  useDeleteMcpTool,
  type McpToolInput,
} from "./api";
import { ApiError, tokenStorage } from "@/shared/api/client";
import type { McpToolDto, SignatureDto } from "@/shared/api/types";

// Sanitize the same way the backend does so the UI shows the live canonical
// name as the user types. Keeps the "name preview" honest.
function sanitizeName(raw: string): string {
  if (!raw) return "";
  let out = "";
  for (const ch of raw.trim().toLowerCase()) {
    if (/[a-z0-9]/.test(ch)) out += ch;
    else if (ch === " " || ch === "-" || ch === "_" || ch === ".") out += "_";
  }
  return out.replace(/^_+|_+$/g, "") || "tool";
}

export function McpServerPage() {
  const { data: tools, isLoading } = useMcpTools();
  const { data: signatures } = useSignatures();
  const del = useDeleteMcpTool();
  const [editing, setEditing] = useState<McpToolDto | null>(null);
  const [creating, setCreating] = useState(false);

  // MCP endpoint URL — same origin as the SPA, path /mcp. The same JWT used
  // for the web UI authenticates MCP clients.
  const mcpUrl = useMemo(() => {
    if (typeof window === "undefined") return "/mcp";
    return `${window.location.origin}/mcp`;
  }, []);

  return (
    <ScreenShell>
      <TopBar />
      <div
        className="mz-scroll"
        style={{
          flex: 1,
          padding: 28,
          display: "flex",
          flexDirection: "column",
          gap: 20,
          overflow: "auto",
        }}
      >
        {/* ── Header ── */}
        <div style={{ display: "flex", alignItems: "flex-end", justifyContent: "space-between", gap: 24 }}>
          <div>
            <div className="mz-eyebrow">The radio</div>
            <div
              style={{
                fontFamily: "var(--f-display)",
                fontSize: 38,
                fontWeight: 500,
                letterSpacing: "-0.025em",
                marginTop: 4,
              }}
            >
              MCP server
            </div>
            <div style={{ color: "var(--text-3)", marginTop: 4, fontSize: 13, maxWidth: 640 }}>
              Expose your signatures as tools to any MCP client (Claude Desktop,
              Claude Code, …). Each tool runs the full pipeline on call: setup
              → analyze → test → merge → execute.
            </div>
          </div>
          <BtnPrimary
            icon={Icon.plus()}
            disabled={!signatures || signatures.length === 0}
            onClick={() => setCreating(true)}
          >
            New tool
          </BtnPrimary>
        </div>

        <ConnectionPanel url={mcpUrl} />

        {isLoading && <EmptyHint text="Loading tools…" />}
        {tools && tools.length === 0 && (
          <EmptyHint
            text={
              signatures && signatures.length > 0
                ? "No MCP tools yet. Create one to expose a signature."
                : "Create a signature first — every MCP tool wraps one."
            }
          />
        )}

        {tools && tools.length > 0 && (
          <div
            style={{
              display: "grid",
              gridTemplateColumns: "repeat(auto-fill, minmax(360px, 1fr))",
              gap: 16,
            }}
          >
            {tools.map((t) => (
              <ToolCard
                key={t.id}
                tool={t}
                onEdit={() => setEditing(t)}
                onDelete={async () => {
                  if (!confirm(`Delete tool "${t.name}"?`)) return;
                  try {
                    await del.mutateAsync(t.id);
                  } catch (err) {
                    alert(err instanceof ApiError ? err.message : "Delete failed.");
                  }
                }}
              />
            ))}
          </div>
        )}
      </div>

      {(creating || editing) && signatures && (
        <ToolForm
          existing={editing ?? undefined}
          signatures={signatures}
          onClose={() => {
            setCreating(false);
            setEditing(null);
          }}
        />
      )}
    </ScreenShell>
  );
}

// ── Connection info ───────────────────────────────────────────────

function ConnectionPanel({ url }: { url: string }) {
  const [copied, setCopied] = useState<"url" | "token" | null>(null);
  const [reveal, setReveal] = useState(false);
  const token = tokenStorage.get() ?? "";

  // Do NOT inject any non-ASCII characters into the rendered token — MCP
  // clients that read the on-screen value (or users who hand-select it) will
  // try to put that text into a fetch `Headers` value, and code-points >255
  // (e.g. `…` U+2026) blow the Headers constructor up with
  // "character at index N has value 8230 which is greater than 255".
  // Mask with plain ASCII dots, or just reveal the whole thing — this page
  // is auth-gated to the very user the token belongs to.
  const display = token
    ? reveal
      ? `Bearer ${token}`
      : `Bearer ${token.slice(0, 12)}................${token.slice(-6)}`
    : "(not signed in)";

  async function copy(value: string, label: "url" | "token") {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(label);
      setTimeout(() => setCopied(null), 1500);
    } catch {
      // fall through — user can still select manually
    }
  }

  return (
    <div className="mz-glass" style={{ padding: 20, display: "flex", flexDirection: "column", gap: 14 }}>
      <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
        <span className="mz-dot" style={{ background: "var(--ok)", boxShadow: "0 0 8px var(--ok)" }} />
        <div className="mz-eyebrow" style={{ marginBottom: 0 }}>Connection</div>
      </div>
      <div style={{ color: "var(--text-3)", fontSize: 12.5, lineHeight: 1.55 }}>
        Streamable-HTTP transport, JSON-RPC. Point your MCP client at the URL
        below and authenticate with your Mazda JWT as a bearer token. Tools
        list updates live — no client restart needed when you add or toggle a tool.
      </div>

      <div style={{ display: "grid", gridTemplateColumns: "100px 1fr auto", gap: 10, alignItems: "center" }}>
        <span style={{ fontFamily: "var(--f-mono)", fontSize: 10.5, color: "var(--text-3)", letterSpacing: "0.06em" }}>
          ENDPOINT
        </span>
        <code style={mono}>{url}</code>
        <BtnGhost onClick={() => copy(url, "url")}>{copied === "url" ? "Copied" : "Copy"}</BtnGhost>

        <span style={{ fontFamily: "var(--f-mono)", fontSize: 10.5, color: "var(--text-3)", letterSpacing: "0.06em" }}>
          AUTH HEADER
        </span>
        <code style={mono}>{display}</code>
        <div style={{ display: "flex", gap: 6 }}>
          <BtnGhost onClick={() => setReveal((v) => !v)} disabled={!token}>
            {reveal ? "Hide" : "Show"}
          </BtnGhost>
          <BtnGhost onClick={() => token && copy(`Bearer ${token}`, "token")} disabled={!token}>
            {copied === "token" ? "Copied" : "Copy"}
          </BtnGhost>
        </div>
      </div>

      <div style={{ fontSize: 11, color: "var(--text-4)", lineHeight: 1.5 }}>
        Tip: use <strong>Copy</strong> to put the full bearer value on your
        clipboard — paste it into your MCP client's <code>Authorization</code>
        header. Don't hand-select the masked text above; it's truncated and
        meant for display only.
      </div>
    </div>
  );
}

const mono: React.CSSProperties = {
  fontFamily: "var(--f-mono)",
  fontSize: 12,
  color: "var(--text-2)",
  background: "var(--glass-1)",
  border: "1px solid var(--glass-stroke)",
  borderRadius: 8,
  padding: "8px 12px",
  overflow: "auto",
  whiteSpace: "nowrap",
  textOverflow: "ellipsis",
};

// ── Tool card ─────────────────────────────────────────────────────

function ToolCard({
  tool,
  onEdit,
  onDelete,
}: {
  tool: McpToolDto;
  onEdit: () => void;
  onDelete: () => void;
}) {
  const update = useUpdateMcpTool();
  async function toggle() {
    try {
      await update.mutateAsync({
        id: tool.id,
        input: {
          signatureId: tool.signatureId,
          name: tool.name,
          displayName: tool.displayName,
          description: tool.description,
          defaultSetupPrompt: tool.defaultSetupPrompt,
          defaultTestPrompt: tool.defaultTestPrompt,
          enabled: !tool.enabled,
        },
      });
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Update failed.");
    }
  }

  return (
    <div
      className="mz-glass"
      style={{
        padding: 18,
        display: "flex",
        flexDirection: "column",
        gap: 12,
      }}
    >
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", gap: 8 }}>
        <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
          <div style={{ fontSize: 15, fontWeight: 500, color: "var(--text-1)" }}>
            {tool.displayName || tool.name}
          </div>
          <code style={{ fontFamily: "var(--f-mono)", fontSize: 11.5, color: "var(--orange-100)" }}>
            {tool.name}
          </code>
        </div>
        <Pill tone={tool.enabled ? "ok" : "warn"}>{tool.enabled ? "enabled" : "disabled"}</Pill>
      </div>

      <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
        <Pill icon={Icon.file(10)}>{tool.signatureName}</Pill>
        {tool.defaultSetupPrompt && <Pill>default setup</Pill>}
        {tool.defaultTestPrompt && <Pill>default test</Pill>}
      </div>

      {tool.description && (
        <div style={{ fontSize: 12.5, color: "var(--text-3)", lineHeight: 1.5 }}>
          {tool.description.length > 200 ? tool.description.slice(0, 200) + "…" : tool.description}
        </div>
      )}

      <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
        <BtnGhost onClick={toggle} disabled={update.isPending}>
          {tool.enabled ? "Disable" : "Enable"}
        </BtnGhost>
        <BtnGhost onClick={onEdit}>Edit</BtnGhost>
        <BtnGhost onClick={onDelete}>Delete</BtnGhost>
      </div>
    </div>
  );
}

// ── Create / Edit modal ───────────────────────────────────────────

function ToolForm({
  existing,
  signatures,
  onClose,
}: {
  existing?: McpToolDto;
  signatures: SignatureDto[];
  onClose: () => void;
}) {
  const create = useCreateMcpTool();
  const update = useUpdateMcpTool();

  const [signatureId, setSignatureId] = useState(existing?.signatureId ?? signatures[0]?.id ?? "");
  const [displayName, setDisplayName] = useState(existing?.displayName ?? "");
  const [rawName, setRawName] = useState(existing?.name ?? "");
  const [description, setDescription] = useState(existing?.description ?? "");
  const [defaultSetupPrompt, setDefaultSetupPrompt] = useState(existing?.defaultSetupPrompt ?? "");
  const [defaultTestPrompt, setDefaultTestPrompt] = useState(existing?.defaultTestPrompt ?? "");
  const [enabled, setEnabled] = useState(existing?.enabled ?? true);

  // Auto-derive the technical name from the display name when the user hasn't
  // typed an explicit one yet.
  useEffect(() => {
    if (!existing && !rawName) {
      setRawName(displayName);
    }
  }, [displayName, existing, rawName]);

  const canonical = sanitizeName(rawName || displayName);
  const saving = create.isPending || update.isPending;

  async function onSave() {
    if (!signatureId) return alert("Pick a signature first.");
    if (!displayName.trim() && !rawName.trim()) return alert("Give the tool a name.");
    const input: McpToolInput = {
      signatureId,
      name: rawName.trim() || displayName.trim(),
      displayName: displayName.trim() || canonical,
      description: description.trim() || undefined,
      defaultSetupPrompt: defaultSetupPrompt.trim() || undefined,
      defaultTestPrompt: defaultTestPrompt.trim() || undefined,
      enabled,
    };
    try {
      if (existing) {
        await update.mutateAsync({ id: existing.id, input });
      } else {
        await create.mutateAsync(input);
      }
      onClose();
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Save failed.");
    }
  }

  return (
    <div
      onClick={onClose}
      style={{
        position: "fixed",
        inset: 0,
        background: "rgba(7,5,10,0.6)",
        backdropFilter: "blur(8px)",
        display: "grid",
        placeItems: "center",
        zIndex: 50,
        padding: 28,
      }}
    >
      <div
        className="mz-glass"
        onClick={(e) => e.stopPropagation()}
        style={{
          width: "min(720px, 100%)",
          maxHeight: "calc(100vh - 56px)",
          overflow: "auto",
          padding: 24,
          display: "flex",
          flexDirection: "column",
          gap: 16,
        }}
      >
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
          <div>
            <div className="mz-eyebrow">{existing ? "Edit tool" : "New tool"}</div>
            <div style={{ fontSize: 20, fontWeight: 500, marginTop: 2 }}>
              {existing?.displayName ?? "MCP tool"}
            </div>
          </div>
          <BtnGhost onClick={onClose}>Close</BtnGhost>
        </div>

        <Field label="Signature">
          <select
            value={signatureId}
            onChange={(e) => setSignatureId(e.target.value)}
            style={inputStyle}
          >
            {signatures.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name} {s.source ? `· ${s.source}` : ""}
              </option>
            ))}
          </select>
        </Field>

        <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 12 }}>
          <Field label="Display name">
            <input
              type="text"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
              placeholder="Checkout · happy path"
              maxLength={120}
              style={inputStyle}
            />
          </Field>
          <Field
            label="Tool name (MCP)"
            hint={
              canonical && canonical !== rawName
                ? `Sent as: ${canonical}`
                : "lowercase, snake_case"
            }
          >
            <input
              type="text"
              value={rawName}
              onChange={(e) => setRawName(e.target.value)}
              placeholder="checkout_happy_path"
              maxLength={80}
              style={inputStyle}
            />
          </Field>
        </div>

        <Field label="Description (shown to the calling LLM)">
          <textarea
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            rows={3}
            maxLength={10_000}
            placeholder="What this test verifies, when it should be run, anything else the LLM should know."
            style={{ ...inputStyle, resize: "vertical", minHeight: 70 }}
          />
        </Field>

        <Field
          label="Default setup_prompt (optional)"
          hint="If set, callers may omit setup_prompt. They can still override."
        >
          <textarea
            value={defaultSetupPrompt}
            onChange={(e) => setDefaultSetupPrompt(e.target.value)}
            rows={3}
            maxLength={50_000}
            placeholder="e.g. Connect via CDP to Chrome on localhost:9222 and navigate to /checkout."
            style={{ ...inputStyle, resize: "vertical", minHeight: 70 }}
          />
        </Field>

        <Field
          label="Default test_prompt (optional)"
          hint="If set, callers may omit test_prompt. They can still override."
        >
          <textarea
            value={defaultTestPrompt}
            onChange={(e) => setDefaultTestPrompt(e.target.value)}
            rows={3}
            maxLength={50_000}
            placeholder="e.g. Add an item to cart, proceed to checkout, assert the total matches."
            style={{ ...inputStyle, resize: "vertical", minHeight: 70 }}
          />
        </Field>

        <label style={{ display: "flex", alignItems: "center", gap: 10, fontSize: 13, color: "var(--text-2)" }}>
          <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
          Enabled — visible in tools/list and callable
        </label>

        <div style={{ display: "flex", gap: 10, justifyContent: "flex-end" }}>
          <BtnGhost onClick={onClose}>Cancel</BtnGhost>
          <BtnPrimary icon={Icon.spark()} onClick={onSave} disabled={saving}>
            {saving ? "Saving…" : existing ? "Save changes" : "Create tool"}
          </BtnPrimary>
        </div>
      </div>
    </div>
  );
}

function Field({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
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
          {label}
        </label>
        {hint && (
          <span style={{ fontFamily: "var(--f-mono)", fontSize: 10.5, color: "var(--text-4)" }}>
            {hint}
          </span>
        )}
      </div>
      {children}
    </div>
  );
}

const inputStyle: React.CSSProperties = {
  width: "100%",
  padding: "10px 12px",
  background: "var(--glass-1)",
  border: "1px solid var(--glass-stroke-2)",
  borderRadius: 8,
  color: "var(--text-1)",
  fontFamily: "var(--f-sans)",
  fontSize: 13,
  outline: "none",
};

function EmptyHint({ text }: { text: string }) {
  return (
    <div
      className="mz-glass"
      style={{
        padding: 24,
        textAlign: "center",
        color: "var(--text-3)",
        fontSize: 13,
      }}
    >
      {text}
    </div>
  );
}
