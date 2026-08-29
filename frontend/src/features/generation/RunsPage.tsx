import { useNavigate } from "react-router-dom";
import { TopBar } from "@/shared/components/TopBar";
import { ScreenShell, Pill } from "@/shared/components/Primitives";
import { useRecentGenerations } from "./api";

export function RunsPage() {
  const navigate = useNavigate();
  const { data: runs = [], isLoading } = useRecentGenerations(50);

  return (
    <ScreenShell>
      <TopBar />
      <div className="mz-scroll" style={{ flex: 1, padding: 28, overflow: "auto" }}>
        <div>
          <div className="mz-eyebrow">History</div>
          <div
            style={{
              fontFamily: "var(--f-display)",
              fontSize: 38,
              fontWeight: 500,
              letterSpacing: "-0.025em",
              marginTop: 4,
            }}
          >
            Runs
          </div>
        </div>

        <div style={{ marginTop: 20, display: "flex", flexDirection: "column", gap: 10 }}>
          {isLoading && <div style={{ color: "var(--text-3)" }}>Loading…</div>}
          {!isLoading && runs.length === 0 && (
            <div
              className="mz-glass"
              style={{ padding: 40, textAlign: "center", color: "var(--text-4)" }}
            >
              No runs yet.
            </div>
          )}
          {runs.map((r) => (
            <div
              key={r.id}
              onClick={() => navigate(`/runs/${r.id}`)}
              className="mz-glass"
              style={{
                padding: 16,
                display: "grid",
                gridTemplateColumns: "1fr 120px 180px 90px",
                gap: 16,
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
                  {r.prompt || "(empty run)"}
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
                  {/*
                    Runtime stage under the function-catalogue architecture.
                    Each blob populates in order — the trail below tells the
                    user how far the pipeline got.
                  */}
                  {r.executionEnvelopeJson
                    ? "executed"
                    : r.planJson
                    ? "planned"
                    : r.gatheredStateJson
                    ? "gathered"
                    : "draft"}
                </div>
              </div>
              <div style={{ fontFamily: "var(--f-mono)", fontSize: 11, color: "var(--text-3)" }}>
                {r.tokensUsed.toLocaleString()} tok
              </div>
              <div style={{ fontFamily: "var(--f-mono)", fontSize: 11, color: "var(--text-3)" }}>
                {new Date(r.createdAt).toLocaleString()}
              </div>
              <Pill
                tone={r.status === "Ready" ? "ok" : r.status === "Failed" ? "err" : "warn"}
              >
                {r.status}
              </Pill>
            </div>
          ))}
        </div>
      </div>
    </ScreenShell>
  );
}
