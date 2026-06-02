import { useEffect, useMemo, useRef, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import Editor from "@monaco-editor/react";
import {
  ReactFlow,
  Background,
  Handle,
  Position,
  MarkerType,
  type Node,
  type Edge,
  type NodeTypes,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import dagre from "@dagrejs/dagre";
import { TopBar } from "@/shared/components/TopBar";
import {
  ScreenShell,
  BtnPrimary,
  BtnGhost,
  Pill,
  StepIndicator,
} from "@/shared/components/Primitives";
import { Icon } from "@/shared/icons/Icon";
import { useSignature, useGenerateFlows, useSaveFlows } from "./api";
import { ApiError } from "@/shared/api/client";
import type { UserFlowsGraph } from "@/shared/api/types";

const NODE_W = 220;
const NODE_H = 64;

interface RawNode { id: string; label: string; description?: string }
interface RawEdge {
  id: string;
  from: string;
  to: string;
  action: string;
  elementSelector: string;
  label: string;
  description?: string;
  value?: string;
}

function safeParse(json: string): UserFlowsGraph | null {
  if (!json.trim()) return null;
  try {
    const parsed = JSON.parse(json);
    if (!Array.isArray(parsed.nodes) || !Array.isArray(parsed.edges)) return null;
    return parsed as UserFlowsGraph;
  } catch {
    return null;
  }
}

function laidOutGraph(graph: UserFlowsGraph): { nodes: Node[]; edges: Edge[] } {
  const g = new dagre.graphlib.Graph();
  g.setDefaultEdgeLabel(() => ({}));
  g.setGraph({ rankdir: "LR", nodesep: 36, ranksep: 60 });

  graph.nodes.forEach((n) => g.setNode(n.id, { width: NODE_W, height: NODE_H }));
  graph.edges.forEach((e) => g.setEdge(e.from, e.to));
  dagre.layout(g);

  const nodes: Node[] = graph.nodes.map((n) => {
    const pos = g.node(n.id);
    return {
      id: n.id,
      type: "mzState",
      position: { x: pos.x - NODE_W / 2, y: pos.y - NODE_H / 2 },
      data: { label: n.label, description: n.description },
    };
  });

  const edges: Edge[] = graph.edges.map((e) => ({
    id: e.id,
    source: e.from,
    target: e.to,
    label: e.label,
    type: "smoothstep",
    style: { stroke: "rgba(234,112,40,0.6)", strokeWidth: 1.5 },
    labelStyle: { fill: "var(--text-2)", fontSize: 11, fontFamily: "var(--f-mono)" },
    labelBgStyle: { fill: "rgba(20,12,8,0.9)", stroke: "rgba(234,112,40,0.35)" },
    labelBgPadding: [6, 4] as [number, number],
    labelBgBorderRadius: 4,
    markerEnd: { type: MarkerType.ArrowClosed, color: "rgba(234,112,40,0.85)", width: 18, height: 18 },
  }));

  return { nodes, edges };
}

function StateNode({ data }: { data: { label: string; description?: string } }) {
  return (
    <div
      style={{
        width: NODE_W,
        minHeight: NODE_H,
        padding: "10px 14px",
        background: "linear-gradient(180deg, rgba(40,18,8,0.95), rgba(20,9,4,0.95))",
        border: "1px solid rgba(234,112,40,0.4)",
        borderRadius: 10,
        boxShadow: "0 8px 24px rgba(0,0,0,0.5)",
        color: "var(--text-1)",
        fontSize: 12.5,
        fontFamily: "var(--f-sans)",
        position: "relative",
      }}
    >
      {/* Edge anchor points. Without these, edges have nothing to attach to. */}
      <Handle
        type="target"
        position={Position.Left}
        style={{ background: "rgba(234,112,40,0.7)", width: 8, height: 8, border: "none" }}
      />
      <Handle
        type="source"
        position={Position.Right}
        style={{ background: "rgba(234,112,40,0.7)", width: 8, height: 8, border: "none" }}
      />
      <div style={{ fontWeight: 500, color: "#f7c69a", marginBottom: 4 }}>
        {data.label}
      </div>
      {data.description && (
        <div
          style={{
            fontSize: 10.5,
            color: "var(--text-3)",
            fontFamily: "var(--f-mono)",
            lineHeight: 1.4,
          }}
        >
          {data.description}
        </div>
      )}
    </div>
  );
}

const nodeTypes: NodeTypes = { mzState: StateNode };

export function UserFlowsPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { data: signature, isLoading } = useSignature(id);
  const generate = useGenerateFlows();
  const save = useSaveFlows();

  const cypressInputRef = useRef<HTMLInputElement>(null);
  const [cypressName, setCypressName] = useState("");
  const [cypressSource, setCypressSource] = useState("");
  const [draftJson, setDraftJson] = useState("");
  const [parseError, setParseError] = useState<string | null>(null);

  // Initial load → pull persisted flows JSON into the editor.
  useEffect(() => {
    if (signature && !draftJson) {
      setDraftJson(signature.userFlowsJson || "");
    }
  }, [signature, draftJson]);

  const parsed = useMemo(() => safeParse(draftJson), [draftJson]);
  const graphView = useMemo(
    () => (parsed ? laidOutGraph(parsed) : { nodes: [], edges: [] }),
    [parsed]
  );

  async function onCypressFile(file: File) {
    if (file.size > 200_000) {
      alert("Cypress file too large — keep it under 200 KB.");
      return;
    }
    const text = await file.text();
    setCypressName(file.name);
    setCypressSource(text);
  }

  async function onGenerate() {
    if (!id) return;
    try {
      const updated = await generate.mutateAsync({
        id,
        cypressSource: cypressSource.trim() || undefined,
      });
      setDraftJson(updated.userFlowsJson || "");
      setParseError(null);
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Flow generation failed.");
    }
  }

  async function onSave() {
    if (!id || !parsed) {
      setParseError("JSON does not match the expected { nodes, edges } shape.");
      return;
    }
    try {
      await save.mutateAsync({ id, flowsJson: draftJson });
      setParseError(null);
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Save failed.");
    }
  }

  function onContinue() {
    if (id) navigate(`/runs/new?signature=${id}`);
  }

  if (isLoading || !signature) {
    return (
      <ScreenShell>
        <TopBar />
        <div style={{ padding: 28, color: "var(--text-3)" }}>Loading signature…</div>
      </ScreenShell>
    );
  }

  const nodeCount = parsed?.nodes.length ?? 0;
  const edgeCount = parsed?.edges.length ?? 0;
  const hasFlows = nodeCount > 0;

  return (
    <ScreenShell>
      <TopBar crumb={`${signature.name} · flows`} />
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
        <StepIndicator step={4} />
        <div
          style={{
            display: "grid",
            gridTemplateColumns: "360px 1fr 1fr",
            gap: 24,
            flex: 1,
            minHeight: 0,
          }}
        >
          {/* ── LEFT — controls ── */}
          <div style={{ display: "flex", flexDirection: "column", gap: 16, minHeight: 0 }}>
            <div
              className="mz-glass"
              style={{
                padding: 20,
                display: "flex",
                flexDirection: "column",
                gap: 14,
                flex: 1,
                minHeight: 0,
                overflow: "auto",
              }}
            >
              <div className="mz-eyebrow">Step 4 · User flows</div>
              <div style={{ color: "var(--text-3)", fontSize: 12.5, lineHeight: 1.55 }}>
                Mazda walks the signature and infers a graph of all plausible UI flows —
                states the user can be in, and the element-anchored actions that
                connect them. The Test prompt step then maps your English instruction
                onto a path through this graph.
              </div>

              <div className="mz-divider" />

              <div className="mz-eyebrow" style={{ fontSize: 9.5 }}>
                Optional: Cypress test as grounding
              </div>
              <div style={{ color: "var(--text-4)", fontSize: 11.5, lineHeight: 1.5 }}>
                If you have a Cypress integration test that exercises this UI, upload
                it — the analyzer will map each <code style={{ fontFamily: "var(--f-mono)" }}>cy.*</code> call
                onto an edge in the graph.
              </div>
              <input
                ref={cypressInputRef}
                type="file"
                accept=".ts,.tsx,.js,.jsx,.cy.ts,.cy.js,.txt"
                style={{ display: "none" }}
                onChange={(e) => {
                  const f = e.target.files?.[0];
                  if (f) onCypressFile(f);
                  e.target.value = "";
                }}
              />
              <BtnGhost
                full
                icon={Icon.file()}
                onClick={() => cypressInputRef.current?.click()}
              >
                {cypressName ? "Replace Cypress file" : "Upload Cypress test"}
              </BtnGhost>
              {cypressName && (
                <Pill tone="info" icon={Icon.file(10)}>
                  {cypressName}
                </Pill>
              )}

              <div className="mz-divider" />

              <BtnPrimary
                full
                icon={Icon.spark()}
                onClick={onGenerate}
                disabled={generate.isPending}
              >
                {generate.isPending
                  ? "Generating flows…"
                  : hasFlows
                  ? "Re-generate flows"
                  : "Generate flows"}
              </BtnPrimary>

              <div className="mz-divider" />

              <SummaryRow label="States" value={String(nodeCount)} />
              <SummaryRow label="Transitions" value={String(edgeCount)} />
              <SummaryRow label="Signature" value={`${signature.elements.length} elements`} />
              {parseError && (
                <div style={{ fontSize: 11.5, color: "var(--err)", fontFamily: "var(--f-mono)" }}>
                  {parseError}
                </div>
              )}
            </div>

            <div style={{ display: "flex", gap: 10 }}>
              <BtnGhost onClick={() => navigate(`/signatures/${id}/informational`)}>
                Back to informational
              </BtnGhost>
              <BtnPrimary
                full
                icon={Icon.arrow()}
                onClick={onContinue}
                disabled={!hasFlows}
              >
                Continue to setup
              </BtnPrimary>
            </div>
          </div>

          {/* ── MIDDLE — JSON editor ── */}
          <div style={{ display: "flex", flexDirection: "column", gap: 12, minHeight: 0 }}>
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
              <div className="mz-eyebrow">Flows JSON</div>
              <BtnGhost onClick={onSave} disabled={save.isPending}>
                {save.isPending ? "Saving…" : "Save edits"}
              </BtnGhost>
            </div>
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
              <div style={{ flex: 1, minHeight: 0 }}>
                <Editor
                  height="100%"
                  defaultLanguage="json"
                  theme="vs-dark"
                  value={draftJson}
                  onChange={(v) => setDraftJson(v ?? "")}
                  options={{
                    fontSize: 12,
                    fontFamily: "JetBrains Mono, monospace",
                    minimap: { enabled: false },
                    scrollBeyondLastLine: false,
                    tabSize: 2,
                  }}
                />
              </div>
            </div>
          </div>

          {/* ── RIGHT — graph viz ── */}
          <div style={{ display: "flex", flexDirection: "column", gap: 12, minHeight: 0 }}>
            <div className="mz-eyebrow">Graph view</div>
            <div
              className="mz-glass-2"
              style={{
                flex: 1,
                minHeight: 0,
                padding: 0,
                overflow: "hidden",
                position: "relative",
              }}
            >
              {hasFlows ? (
                <ReactFlow
                  nodes={graphView.nodes}
                  edges={graphView.edges}
                  nodeTypes={nodeTypes}
                  fitView
                  fitViewOptions={{ padding: 0.15 }}
                  nodesDraggable
                  nodesConnectable={false}
                  edgesFocusable={false}
                  proOptions={{ hideAttribution: true }}
                  style={{ background: "transparent" }}
                >
                  <Background color="rgba(234,112,40,0.12)" gap={24} />
                </ReactFlow>
              ) : (
                <div
                  style={{
                    height: "100%",
                    display: "grid",
                    placeItems: "center",
                    padding: 32,
                  }}
                >
                  <div
                    style={{
                      color: "var(--text-4)",
                      fontSize: 13,
                      textAlign: "center",
                      lineHeight: 1.6,
                      maxWidth: 320,
                    }}
                  >
                    No flows yet. Click{" "}
                    <strong style={{ color: "var(--orange-100)" }}>Generate flows</strong> to
                    have Mazda infer a graph from your signature.
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>
      </div>
    </ScreenShell>
  );
}

function SummaryRow({ label, value }: { label: string; value: string }) {
  return (
    <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12 }}>
      <span className="mz-eyebrow" style={{ fontSize: 9.5 }}>{label}</span>
      <span style={{ fontSize: 12.5, color: "var(--text-1)", textAlign: "right" }}>{value}</span>
    </div>
  );
}
