import { useNavigate } from "react-router-dom";
import { TopBar } from "@/shared/components/TopBar";
import {
  ScreenShell,
  BtnPrimary,
  BtnGhost,
  Pill,
} from "@/shared/components/Primitives";
import { Icon } from "@/shared/icons/Icon";
import { useSignatures } from "@/features/signatures/api";
import { useRecentGenerations } from "@/features/generation/api";
import { MazdaGlyph } from "@/shared/components/Brand";

export function StudioPage() {
  const navigate = useNavigate();
  const { data: signatures = [] } = useSignatures();
  const { data: runs = [] } = useRecentGenerations(8);

  return (
    <ScreenShell>
      <TopBar />
      <div
        className="mz-scroll"
        style={{
          flex: 1,
          padding: "40px 28px 28px",
          display: "flex",
          flexDirection: "column",
          gap: 32,
          overflow: "auto",
        }}
      >
        {/* ── Hero ── */}
        <div
          className="mz-glass-2 mz-fade-in"
          style={{
            padding: "48px 40px",
            display: "grid",
            gridTemplateColumns: "1fr auto",
            gap: 32,
            alignItems: "center",
            position: "relative",
            overflow: "hidden",
          }}
        >
          <div style={{ display: "flex", flexDirection: "column", gap: 16, maxWidth: 640 }}>
            <div className="mz-eyebrow">The studio</div>
            <div
              style={{
                fontFamily: "var(--f-display)",
                fontSize: 56,
                fontWeight: 500,
                letterSpacing: "-0.03em",
                lineHeight: 1.02,
              }}
            >
              The one
              <br />
              <span
                style={{
                  background: "linear-gradient(135deg, #f7c69a 0%, #ea7028 60%, #c2410c 100%)",
                  WebkitBackgroundClip: "text",
                  backgroundClip: "text",
                  color: "transparent",
                }}
              >
                who knows.
              </span>
            </div>
            <div style={{ color: "var(--text-2)", fontSize: 15, lineHeight: 1.55 }}>
              Hand Mazda a component, a URL, and a sentence in plain English.
              <br />
              Get back a Playwright spec you can run on your own machine.
            </div>
            <div style={{ display: "flex", gap: 10, marginTop: 8 }}>
              <BtnPrimary icon={Icon.spark()} onClick={() => navigate("/signatures/new")}>
                New signature
              </BtnPrimary>
              <BtnGhost icon={Icon.folder()} onClick={() => navigate("/signatures")}>
                Browse the shelf
              </BtnGhost>
            </div>
          </div>
          <div
            style={{
              opacity: 0.6,
              filter: "drop-shadow(0 0 36px rgba(234,112,40,0.5))",
            }}
          >
            <MazdaGlyph size={200} glow />
          </div>
        </div>

        {/* ── Two columns: pinned signatures + recent runs ── */}
        <div
          style={{
            display: "grid",
            gridTemplateColumns: "1fr 1fr",
            gap: 24,
          }}
        >
          {/* Pinned signatures */}
          <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
            <SectionHeader title="Pinned signatures" cta="View all" onCta={() => navigate("/signatures")} />
            {signatures.length === 0 ? (
              <EmptyTile text="No signatures yet. Upload a component to create your first." />
            ) : (
              <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
                {signatures.slice(0, 5).map((s) => (
                  <div
                    key={s.id}
                    onClick={() => navigate(`/signatures/${s.id}`)}
                    className="mz-glass"
                    style={{
                      padding: 14,
                      display: "grid",
                      gridTemplateColumns: "32px 1fr auto",
                      gap: 12,
                      alignItems: "center",
                      cursor: "pointer",
                    }}
                  >
                    <div
                      style={{
                        width: 32,
                        height: 32,
                        borderRadius: 8,
                        background: "linear-gradient(135deg, rgba(194,65,12,0.25), rgba(122,40,6,0.4))",
                        border: "1px solid rgba(234,112,40,0.4)",
                        display: "grid",
                        placeItems: "center",
                        color: "var(--orange-200)",
                      }}
                    >
                      {Icon.code(14)}
                    </div>
                    <div>
                      <div style={{ fontSize: 13.5, color: "var(--text-1)" }}>{s.name}</div>
                      <div style={{ fontSize: 11, color: "var(--text-3)", fontFamily: "var(--f-mono)" }}>
                        {s.elements.length} nodes · v{s.version}
                      </div>
                    </div>
                    <Pill tone="orange">{s.type}</Pill>
                  </div>
                ))}
              </div>
            )}
          </div>

          {/* Recent runs */}
          <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
            <SectionHeader title="Recent runs" cta="See all" onCta={() => navigate("/runs")} />
            {runs.length === 0 ? (
              <EmptyTile text="No runs yet. Generate your first Playwright spec." />
            ) : (
              <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
                {runs.slice(0, 5).map((r) => (
                  <div
                    key={r.id}
                    onClick={() => navigate(`/runs/${r.id}`)}
                    className="mz-glass"
                    style={{
                      padding: 14,
                      display: "grid",
                      gridTemplateColumns: "1fr auto",
                      gap: 12,
                      alignItems: "center",
                      cursor: "pointer",
                    }}
                  >
                    <div style={{ overflow: "hidden" }}>
                      <div
                        style={{
                          fontSize: 13,
                          color: "var(--text-1)",
                          overflow: "hidden",
                          textOverflow: "ellipsis",
                          whiteSpace: "nowrap",
                        }}
                      >
                        {r.testPrompt || r.setupPrompt || "(empty run)"}
                      </div>
                      <div
                        style={{
                          fontSize: 11,
                          color: "var(--text-3)",
                          fontFamily: "var(--f-mono)",
                          overflow: "hidden",
                          textOverflow: "ellipsis",
                          whiteSpace: "nowrap",
                          marginTop: 2,
                        }}
                      >
                        {r.finalScript ? "final ready" : r.testScript ? "test ready" : r.setupScript ? "setup ready" : "draft"}
                      </div>
                    </div>
                    <Pill
                      tone={
                        r.status === "Ready"
                          ? "ok"
                          : r.status === "Failed"
                          ? "err"
                          : "warn"
                      }
                    >
                      {r.status}
                    </Pill>
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>
      </div>
    </ScreenShell>
  );
}

function SectionHeader({
  title,
  cta,
  onCta,
}: {
  title: string;
  cta?: string;
  onCta?: () => void;
}) {
  return (
    <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
      <div className="mz-eyebrow">{title}</div>
      {cta && (
        <button
          onClick={onCta}
          style={{
            background: "none",
            border: "none",
            color: "var(--orange-200)",
            cursor: "pointer",
            fontFamily: "var(--f-mono)",
            fontSize: 11,
            letterSpacing: "0.04em",
          }}
        >
          {cta} →
        </button>
      )}
    </div>
  );
}

function EmptyTile({ text }: { text: string }) {
  return (
    <div
      className="mz-glass"
      style={{
        padding: 36,
        textAlign: "center",
        color: "var(--text-4)",
        fontSize: 12.5,
      }}
    >
      {text}
    </div>
  );
}
