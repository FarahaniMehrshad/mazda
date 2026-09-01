import { useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "./useAuth";
import { ApiError } from "@/shared/api/client";
import { MazdaWordmark } from "@/shared/components/Brand";
import { Aurora, BtnPrimary } from "@/shared/components/Primitives";

export function AuthPage() {
  const navigate = useNavigate();
  const { login, register } = useAuth();
  const [mode, setMode] = useState<"signin" | "register">("signin");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      if (mode === "signin") await login(email, password);
      else await register(email, password, displayName || undefined);
      navigate("/", { replace: true });
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Something went wrong.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mz-artboard" style={{ width: "100vw", height: "100vh" }}>
      <Aurora />
      <div className="mz-grid" />
      <div
        className="mz-stage"
        style={{ alignItems: "center", justifyContent: "center", display: "flex" }}
      >
        <div
          className="mz-glass-2 mz-fade-in"
          style={{
            width: 420,
            padding: 36,
            display: "flex",
            flexDirection: "column",
            gap: 24,
          }}
        >
          <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
            <MazdaWordmark size={18} glow />
            <div className="mz-eyebrow" style={{ marginTop: 4 }}>
              UI-driven browser automation
            </div>
          </div>

          <form onSubmit={onSubmit} style={{ display: "flex", flexDirection: "column", gap: 14 }}>
            {mode === "register" && (
              <Field
                label="Display name"
                value={displayName}
                onChange={setDisplayName}
                placeholder="optional"
              />
            )}
            <Field label="Email" value={email} onChange={setEmail} type="email" required />
            <Field
              label="Password"
              value={password}
              onChange={setPassword}
              type="password"
              required
              minLength={mode === "register" ? 8 : undefined}
            />

            {error && (
              <div
                style={{
                  fontSize: 12,
                  color: "var(--err)",
                  background: "rgba(232,92,74,0.08)",
                  border: "1px solid rgba(232,92,74,0.3)",
                  padding: "8px 12px",
                  borderRadius: 8,
                  fontFamily: "var(--f-mono)",
                }}
              >
                {error}
              </div>
            )}

            <BtnPrimary type="submit" full disabled={busy}>
              {busy ? "..." : mode === "signin" ? "Sign in" : "Create account"}
            </BtnPrimary>
          </form>

          <div style={{ textAlign: "center", fontSize: 12, color: "var(--text-3)" }}>
            {mode === "signin" ? "New to Mazda?" : "Already have an account?"}{" "}
            <button
              type="button"
              onClick={() => setMode(mode === "signin" ? "register" : "signin")}
              style={{
                background: "none",
                border: "none",
                color: "var(--orange-200)",
                cursor: "pointer",
                padding: 0,
                fontFamily: "inherit",
                fontSize: "inherit",
              }}
            >
              {mode === "signin" ? "Create one" : "Sign in"}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

interface FieldProps {
  label: string;
  value: string;
  onChange: (v: string) => void;
  type?: string;
  required?: boolean;
  minLength?: number;
  placeholder?: string;
}

function Field({ label, value, onChange, type = "text", required, minLength, placeholder }: FieldProps) {
  return (
    <label style={{ display: "flex", flexDirection: "column", gap: 6 }}>
      <span className="mz-eyebrow" style={{ fontSize: 9.5 }}>
        {label}
      </span>
      <input
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        required={required}
        minLength={minLength}
        placeholder={placeholder}
        style={{
          padding: "10px 12px",
          background: "var(--glass-1)",
          border: "1px solid var(--glass-stroke-2)",
          borderRadius: 8,
          color: "var(--text-1)",
          fontSize: 13.5,
          outline: "none",
          fontFamily: "var(--f-mono)",
        }}
      />
    </label>
  );
}
