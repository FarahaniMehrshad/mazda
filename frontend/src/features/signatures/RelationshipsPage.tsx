import { useEffect, useMemo, useState } from "react";
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
import {
  useSignature,
  useAnalyzeRelationships,
  useSaveRelationships,
} from "./api";
import { ApiError } from "@/shared/api/client";
import type { RelationshipGraph } from "@/shared/api/types";
import { useStepNavigator } from "@/shared/components/useStepNavigator";

// Wider nodes than the user-flows graph because relationship labels embed
// selector tokens that are longer than state names. Same height.
const NODE_W = 240;
const NODE_H = 70;

function safeParse(json: string): RelationshipGraph | null {
  if (!json.trim()) return null;
  try {
    const parsed = JSON.parse(json);
    if (!Array.isArray(parsed.nodes) || !Array.isArray(parsed.edges)) return null;
    return parsed as RelationshipGraph;
  } catch {
    return null;
  }
}

// Pick a tint per node role so the viewer reads at a glance. Containers /
// sections (the pivot nodes the analyzer introduces) get a cool blue so
// they pop visually as "structure, not control".
function nodeStyleByRole(role: string) {
  switch (role) {
    case "interactive":
      return {
        gradient: "linear-gradient(180deg, rgba(40,18,8,0.95), rgba(20,9,4,0.95))",
        border: "rgba(234,112,40,0.45)",
        title: "#f7c69a",
      };
    case "informational":
      return {
        gradient: "linear-gradient(180deg, rgba(40,28,8,0.95), rgba(22,18,4,0.95))",
        border: "rgba(225,170,72,0.45)",
        title: "#f7dca0",
      };
    case "section":
    case "container":
      return {
        gradient: "linear-gradient(180deg, rgba(14,24,40,0.95), rgba(8,14,22,0.95))",
        border: "rgba(106,160,234,0.45)",
        title: "#aac9f7",
      };
    default:
      return {
        gradient: "linear-gradient(180deg, rgba(28,28,32,0.95), rgba(16,16,20,0.95))",
        border: "rgba(168,168,180,0.35)",
        title: "var(--text-1)",
      };
  }
}

// Edge palette by relation. Keeps "structural" edges (labels, grouped-under,
// list-item-of, sibling-of, refers-to) on a calm grey-blue so they don't
// overpower "behavioural" edges (controls, opens, closes, submits) which
// get the brand orange. mutually-exclusive / depends-on get a magenta to
// stand out as constraints.
function edgeStyleByRelation(relation: string) {
  switch (relation) {
    case "controls":
    case "opens":
    case "closes":
    case "submits":
      return { stroke: "rgba(234,112,40,0.7)", marker: "rgba(234,112,40,0.85)" };
    case "labels":
    case "grouped-under":
    case "list-item-of":
    case "value-of":
      return { stroke: "rgba(106,160,234,0.65)", marker: "rgba(106,160,234,0.85)" };
    case "refers-to":
    case "sibling-of":
      return { stroke: "rgba(106,209,154,0.65)", marker: "rgba(106,209,154,0.85)" };
    case "mutually-exclusive":
    case "depends-on":
      return { stroke: "rgba(214,108,196,0.7)", marker: "rgba(214,108,196,0.9)" };
    default:
      return { stroke: "rgba(168,168,180,0.5)", marker: "rgba(168,168,180,0.7)" };
  }
}

function laidOutGraph(graph: RelationshipGraph): { nodes: Node[]; edges: Edge[] } {
  const g = new dagre.graphlib.Graph();
  g.setDefaultEdgeLabel(() => ({}));
  // Left-to-right reads naturally for "X labels Y" / "X controls Y" stories.
  // Slightly larger ranksep than UserFlowsPage because we have more label
  // text on each edge.
  g.setGraph({ rankdir: "LR", nodesep: 36, ranksep: 72 });

  graph.nodes.forEach((n) => g.setNode(n.id, { width: NODE_W, height: NODE_H }));
  graph.edges.forEach((e) => g.setEdge(e.from, e.to));
  dagre.layout(g);

  const nodes: Node[] = graph.nodes.map((n) => {
    const pos = g.node(n.id);
    return {
      id: n.id,
      type: "mzRel",
      // dagre returns NaN for unconnected / unknown nodes; fall back to (0,0)
      // so React Flow doesn't choke. The user will see them clustered at the
      // origin but at least the page renders.
      position: {
        x: Number.isFinite(pos?.x) ? pos.x - NODE_W / 2 : 0,
        y: Number.isFinite(pos?.y) ? pos.y - NODE_H / 2 : 0,
      },
      data: {
        label: n.label || n.selector,
        selector: n.selector,
        role: n.role,
        description: n.description,
      },
    };
  });

  const edges: Edge[] = graph.edges.map((e) => {
    const style = edgeStyleByRelation(e.relation);
    return {
      id: e.id,
      source: e.from,
      target: e.to,
      label: e.label || e.relation,
      type: "smoothstep",
      style: { stroke: style.stroke, strokeWidth: 1.5 },
      labelStyle: { fill: "var(--text-2)", fontSize: 11, fontFamily: "var(--f-mono)" },
      labelBgStyle: { fill: "rgba(20,12,8,0.9)", stroke: "rgba(106,160,234,0.35)" },
      labelBgPadding: [6, 4] as [number, number],
      labelBgBorderRadius: 4,
      markerEnd: { type: MarkerType.ArrowClosed, color: style.marker, width: 16, height: 16 },
    };
  });

  return { nodes, edges };
}

function RelationshipNode({
  data,
}: {
  data: { label: string; selector: string; role: string; description?: string };
}) {
  const style = nodeStyleByRole(data.role);
  return (
    <div
      style={{
        width: NODE_W,
        minHeight: NODE_H,
        padding: "10px 14px",
        background: style.gradient,
        border: `1px solid ${style.border}`,
        borderRadius: 10,
        boxShadow: "0 8px 24px rgba(0,0,0,0.5)",
        color: "var(--text-1)",
        fontSize: 12.5,
        fontFamily: "var(--f-sans)",
        position: "relative",
      }}
      title={data.description ?? data.selector}
    >
      <Handle
        type="target"
        position={Position.Left}
        style={{ background: style.border, width: 8, height: 8, border: "none" }}
      />
      <Handle
        type="source"
        position={Position.Right}
        style={{ background: style.border, width: 8, height: 8, border: "none" }}
      />
      <div style={{ fontWeight: 500, color: style.title, marginBottom: 4 }}>
        {data.label}
      </div>
      <div
        style={{
          fontSize: 10,
          color: "var(--text-4)",
          fontFamily: "var(--f-mono)",
          lineHeight: 1.35,
          overflow: "hidden",
          textOverflow: "ellipsis",
          whiteSpace: "nowrap",
        }}
      >
        {data.selector}
      </div>
      {data.role && (
        <div
          style={{
            position: "absolute",
            top: 4,
            right: 6,
            fontSize: 9,
            fontFamily: "var(--f-mono)",
            color: "var(--text-4)",
            textTransform: "uppercase",
            letterSpacing: 0.5,
          }}
        >
          {data.role}
        </div>
      )}
    </div>
  );
}

const nodeTypes: NodeTypes = { mzRel: RelationshipNode };

export function RelationshipsPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { data: signature, isLoading } = useSignature(id);
  const analyze = useAnalyzeRelationships();
  const save = useSaveRelationships();
  // Must be called before any early-return so the hook count stays stable
  // across the loading → loaded transition (React error #310 otherwise).
  const onStepClick = useStepNavigator(
    signature ? { mode: "signature", signatureId: signature.id } : null,
  );

  const [draftJson, setDraftJson] = useState("");
  const [parseError, setParseError] = useState<string | null>(null);

  // First load → pull persisted graph JSON into the editor. ONLY runs while
  // the editor is empty, so a react-query refetch during typing can't wipe
  // the user's in-progress edit. The analyze mutation below pushes new
  // server output into the editor explicitly via setDraftJson.
  useEffect(() => {
    if (signature && !draftJson) {
      setDraftJson(signature.relationshipsJson || "");
    }
  }, [signature, draftJson]);

  const parsed = useMemo(() => safeParse(draftJson), [draftJson]);
  const graphView = useMemo(
    () => (parsed ? laidOutGraph(parsed) : { nodes: [], edges: [] }),
    [parsed],
  );

  async function onAnalyze() {
    if (!id) return;
    try {
      const updated = await analyze.mutateAsync(id);
      // Push the freshly-analyzed JSON into the editor so the user sees the
      // new graph immediately. The editor doesn't auto-sync on refetch
      // (would clobber in-progress edits), so we do it explicitly here.
      setDraftJson(updated.relationshipsJson || "");
      setParseError(null);
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Relationship analysis failed.");
    }
  }

  async function onSave() {
    if (!id || !parsed) {
      setParseError("JSON does not match the expected { nodes, edges } shape.");
      return;
    }
    try {
      await save.mutateAsync({ id, graphJson: draftJson });
      setParseError(null);
    } catch (err) {
      alert(err instanceof ApiError ? err.message : "Save failed.");
    }
  }

  function onContinue() {
    if (signature) navigate(`/signatures/${signature.id}/flows`);
  }

  function onBack() {
    if (signature) navigate(`/signatures/${signature.id}/informational`);
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
  const hasGraph = nodeCount > 0;

  return (
    <ScreenShell>
      <TopBar crumb={`${signature.name} · relationships`} />
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
        <StepIndicator step={4} onStepClick={onStepClick} />
        <div
          style={{
            display: "grid",
            gridTemplateColumns: "360px 1fr 1fr",
            gap: 24,
            flex: 1,
            minHeight: 0,
          }}
        >
          {/* ── LEFT — controls + summary ── */}
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
              <div className="mz-eyebrow">Step 4 · Element relationships</div>
              <div style={{ color: "var(--text-3)", fontSize: 12.5, lineHeight: 1.55 }}>
                Mazda reads the signature plus the source and infers a graph of the
                SEMANTIC relations between elements — which parent labels which
                children, which control affects which field, which row name
                identifies which delete button, mutually-exclusive pairs, etc.
                The test-generation step uses these edges to resolve relational
                phrasing like “delete next to row X” or “the X under &lt;heading&gt;”.
              </div>

              <div className="mz-divider" />

              <BtnPrimary
                full
                icon={Icon.spark()}
                onClick={onAnalyze}
                disabled={analyze.isPending}
              >
                {analyze.isPending
                  ? "Analyzing relationships…"
                  : hasGraph
                  ? "Re-run analyze"
                  : "Analyze relationships"}
              </BtnPrimary>

              <div className="mz-divider" />

              <SummaryRow label="Nodes" value={String(nodeCount)} />
              <SummaryRow label="Edges" value={String(edgeCount)} />
              <SummaryRow
                label="Signature"
                value={`${signature.elements.length} elements`}
              />

              <div className="mz-divider" />

              <div className="mz-eyebrow" style={{ fontSize: 9.5 }}>
                Edge legend
              </div>
              <LegendDot color="rgba(106,160,234,0.85)" label="labels / grouped-under / value-of" />
              <LegendDot color="rgba(234,112,40,0.85)" label="controls / opens / closes / submits" />
              <LegendDot color="rgba(106,209,154,0.85)" label="refers-to / sibling-of" />
              <LegendDot color="rgba(214,108,196,0.9)" label="mutually-exclusive / depends-on" />

              {parseError && (
                <div style={{ fontSize: 11.5, color: "var(--err)", fontFamily: "var(--f-mono)" }}>
                  {parseError}
                </div>
              )}
            </div>

            <div style={{ display: "flex", gap: 10 }}>
              <BtnGhost onClick={onBack}>Back to informational</BtnGhost>
              <BtnPrimary full icon={Icon.arrow()} onClick={onContinue}>
                Continue to flows
              </BtnPrimary>
            </div>
          </div>

          {/* ── MIDDLE — JSON editor ── */}
          <div style={{ display: "flex", flexDirection: "column", gap: 12, minHeight: 0 }}>
            <div
              style={{
                display: "flex",
                justifyContent: "space-between",
                alignItems: "baseline",
              }}
            >
              <div className="mz-eyebrow">Graph JSON</div>
              <div style={{ display: "flex", gap: 8 }}>
                <Pill tone="info">v{signature.version}</Pill>
                <BtnGhost onClick={onSave} disabled={save.isPending}>
                  {save.isPending ? "Saving…" : "Save edits"}
                </BtnGhost>
              </div>
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
              {hasGraph ? (
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
                  <Background color="rgba(106,160,234,0.10)" gap={24} />
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
                    No relationship graph yet. Click{" "}
                    <strong style={{ color: "var(--orange-100)" }}>
                      Analyze relationships
                    </strong>{" "}
                    to have Mazda infer the semantic edges between this
                    signature’s elements.
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

function LegendDot({ color, label }: { color: string; label: string }) {
  return (
    <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
      <div
        style={{
          width: 18,
          height: 2,
          background: color,
          borderRadius: 2,
        }}
      />
      <span style={{ fontSize: 11, color: "var(--text-3)", fontFamily: "var(--f-mono)" }}>
        {label}
      </span>
    </div>
  );
}
