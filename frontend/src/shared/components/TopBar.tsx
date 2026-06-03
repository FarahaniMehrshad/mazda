import { useNavigate, useLocation } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { MazdaWordmark } from "./Brand";
import { useAuth } from "@/features/auth/useAuth";
import { api } from "@/shared/api/client";

interface MetaResponse {
  analyzerModel: string;
  generatorModel: string;
}

function useAnalyzerModel(): string {
  const { data } = useQuery({
    queryKey: ["meta"],
    queryFn: () => api.get<MetaResponse>("/api/meta"),
    staleTime: Infinity,
  });
  return data?.analyzerModel ?? "loading";
}

interface TopBarProps {
  crumb?: string;
}

const NAV = [
  { label: "Studio", path: "/" },
  { label: "Signatures", path: "/signatures" },
  { label: "Runs", path: "/runs" },
  { label: "MCP", path: "/mcp" },
];

export function TopBar({ crumb }: TopBarProps) {
  const navigate = useNavigate();
  const location = useLocation();
  const { user, logout } = useAuth();
  const analyzerModel = useAnalyzerModel();

  const isActive = (path: string) =>
    path === "/" ? location.pathname === "/" : location.pathname.startsWith(path);

  const initials = user?.displayName
    ? user.displayName.split(/\s+/).map((p) => p[0]).slice(0, 2).join("").toUpperCase()
    : "??";

  return (
    <div
      style={{
        display: "flex",
        alignItems: "center",
        justifyContent: "space-between",
        padding: "16px 28px",
        borderBottom: "1px solid var(--glass-stroke)",
        background: "rgba(7,5,10,0.55)",
        backdropFilter: "blur(20px)",
        WebkitBackdropFilter: "blur(20px)",
        flex: "0 0 auto",
      }}
    >
      <div style={{ display: "flex", alignItems: "center", gap: 24 }}>
        <div onClick={() => navigate("/")} style={{ cursor: "pointer" }}>
          <MazdaWordmark size={14} />
        </div>
        <div style={{ height: 18, width: 1, background: "var(--glass-stroke-2)" }} />
        <div style={{ display: "flex", gap: 4 }}>
          {NAV.map((item) => {
            const active = isActive(item.path);
            return (
              <button
                key={item.path}
                onClick={() => navigate(item.path)}
                style={{
                  padding: "6px 12px",
                  borderRadius: 8,
                  fontSize: 12.5,
                  color: active ? "var(--text-1)" : "var(--text-3)",
                  background: active ? "var(--glass-2)" : "transparent",
                  border: active ? "1px solid var(--glass-stroke-2)" : "1px solid transparent",
                  fontWeight: active ? 500 : 400,
                  cursor: "pointer",
                }}
              >
                {item.label}
              </button>
            );
          })}
        </div>
        {crumb && (
          <>
            <div style={{ color: "var(--text-4)", fontSize: 12 }}>/</div>
            <div style={{ fontSize: 12.5, color: "var(--text-2)", fontFamily: "var(--f-mono)" }}>
              {crumb}
            </div>
          </>
        )}
      </div>

      <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
        <div
          style={{
            display: "flex",
            alignItems: "center",
            gap: 8,
            padding: "5px 10px 5px 8px",
            borderRadius: 999,
            border: "1px solid var(--glass-stroke)",
            background: "var(--glass-1)",
            fontSize: 11.5,
            color: "var(--text-2)",
            fontFamily: "var(--f-mono)",
          }}
        >
          <span
            className="mz-dot"
            style={{ background: "var(--ok)", boxShadow: "0 0 8px var(--ok)" }}
          />
          <span>{analyzerModel} · ready</span>
        </div>
        <button
          onClick={logout}
          title="Sign out"
          style={{
            width: 28,
            height: 28,
            borderRadius: 999,
            background: "linear-gradient(135deg, #c2410c, #7a2806)",
            display: "grid",
            placeItems: "center",
            fontSize: 11,
            fontWeight: 600,
            color: "#fde7d6",
            border: "1px solid var(--glass-stroke-2)",
            cursor: "pointer",
          }}
        >
          {initials}
        </button>
      </div>
    </div>
  );
}
