import { useNavigate } from "react-router-dom";
import { TopBar } from "@/shared/components/TopBar";
import {
  ScreenShell,
  BtnPrimary,
  BtnGhost,
  Pill,
} from "@/shared/components/Primitives";
import { Icon } from "@/shared/icons/Icon";
import { useSignatures, useDeleteSignature } from "./api";
import type { SignatureDto } from "@/shared/api/types";

export function LibraryPage() {
  const navigate = useNavigate();
  const { data: signatures, isLoading, error } = useSignatures();

  return (
    <ScreenShell>
      <TopBar />
      <div
        style={{
          flex: 1,
          padding: 28,
          display: "flex",
          flexDirection: "column",
          gap: 20,
          overflow: "auto",
        }}
        className="mz-scroll"
      >
        {/* ── Header ── */}
        <div style={{ display: "flex", alignItems: "flex-end", justifyContent: "space-between" }}>
          <div>
            <div className="mz-eyebrow">The shelf</div>
            <div
              style={{
                fontFamily: "var(--f-display)",
                fontSize: 38,
                fontWeight: 500,
                letterSpacing: "-0.025em",
                marginTop: 4,
              }}
            >
              Signatures
            </div>
            <div style={{ color: "var(--text-3)", marginTop: 4, fontSize: 13 }}>
              Every saved lens on a component. Pick one to start a run.
            </div>
          </div>
          <div style={{ display: "flex", gap: 8 }}>
            <BtnGhost icon={Icon.search()}>Search</BtnGhost>
            <BtnPrimary icon={Icon.plus()} onClick={() => navigate("/signatures/new")}>
              New signature
            </BtnPrimary>
          </div>
        </div>

        {isLoading && <EmptyHint text="Reading the shelf…" />}
        {error && <EmptyHint text={`Couldn't load: ${(error as Error).message}`} tone="err" />}

        {signatures && signatures.length === 0 && (
          <EmptyHint
            text="Empty shelf. Upload a component to start your first signature."
            cta={
              <BtnPrimary icon={Icon.upload()} onClick={() => navigate("/signatures/new")}>
                Upload a component
              </BtnPrimary>
            }
          />
        )}

        {signatures && signatures.length > 0 && (
          <div
            style={{
              display: "grid",
              gridTemplateColumns: "repeat(auto-fill, minmax(320px, 1fr))",
              gap: 16,
            }}
          >
            {signatures.map((s) => (
              <SignatureCard key={s.id} sig={s} />
            ))}
          </div>
        )}
      </div>
    </ScreenShell>
  );
}

function SignatureCard({ sig }: { sig: SignatureDto }) {
  const navigate = useNavigate();
  const del = useDeleteSignature();

  return (
    <div
      className="mz-glass mz-fade-in"
      style={{
        padding: 18,
        display: "flex",
        flexDirection: "column",
        gap: 12,
        cursor: "pointer",
        transition: "transform 120ms ease, box-shadow 200ms ease",
      }}
      onClick={() => navigate(`/signatures/${sig.id}`)}
    >
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start" }}>
        <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
          <div
            style={{
              width: 38,
              height: 38,
              borderRadius: 10,
              background: "linear-gradient(135deg, rgba(194,65,12,0.25), rgba(122,40,6,0.4))",
              border: "1px solid rgba(234,112,40,0.4)",
              display: "grid",
              placeItems: "center",
              color: "var(--orange-200)",
            }}
          >
            {Icon.code(18)}
          </div>
          <div>
            <div style={{ fontWeight: 500, fontSize: 14, color: "var(--text-1)" }}>{sig.name}</div>
            <div style={{ fontSize: 11, color: "var(--text-3)", fontFamily: "var(--f-mono)" }}>
              v{sig.version} · {sig.type}
            </div>
          </div>
        </div>
        <button
          onClick={(e) => {
            e.stopPropagation();
            if (confirm(`Delete signature "${sig.name}"?`)) del.mutate(sig.id);
          }}
          style={{
            background: "none",
            border: "none",
            color: "var(--text-4)",
            cursor: "pointer",
            padding: 4,
          }}
        >
          {Icon.more()}
        </button>
      </div>

      {sig.source && (
        <div
          style={{
            fontSize: 12,
            color: "var(--text-3)",
            fontFamily: "var(--f-mono)",
            background: "var(--glass-1)",
            padding: "6px 10px",
            borderRadius: 6,
            overflow: "hidden",
            textOverflow: "ellipsis",
            whiteSpace: "nowrap",
          }}
        >
          {sig.source}
        </div>
      )}

      <div className="mz-divider" />

      <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
        <Pill tone="orange" icon={Icon.spark(10)}>
          {sig.nodeCount} nodes
        </Pill>
        <Pill>{new Date(sig.updatedAt).toLocaleDateString()}</Pill>
      </div>
    </div>
  );
}

interface EmptyHintProps {
  text: string;
  cta?: React.ReactNode;
  tone?: "neutral" | "err";
}

function EmptyHint({ text, cta, tone = "neutral" }: EmptyHintProps) {
  return (
    <div
      className="mz-glass"
      style={{
        padding: 48,
        textAlign: "center",
        color: tone === "err" ? "var(--err)" : "var(--text-3)",
        display: "flex",
        flexDirection: "column",
        gap: 16,
        alignItems: "center",
      }}
    >
      <div>{text}</div>
      {cta}
    </div>
  );
}
