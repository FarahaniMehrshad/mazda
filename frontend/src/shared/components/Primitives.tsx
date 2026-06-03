import { type CSSProperties, type ReactNode } from "react";

interface ButtonProps {
  children: ReactNode;
  icon?: ReactNode;
  full?: boolean;
  onClick?: () => void;
  type?: "button" | "submit";
  disabled?: boolean;
}

export function BtnPrimary({ children, icon, full, onClick, type = "button", disabled }: ButtonProps) {
  const style: CSSProperties = {
    display: "inline-flex",
    alignItems: "center",
    gap: 8,
    padding: "9px 16px",
    borderRadius: 10,
    background: disabled
      ? "linear-gradient(180deg, #5a2308 0%, #3a1804 100%)"
      : "linear-gradient(180deg, #d65015 0%, #a8350a 100%)",
    border: `1px solid ${disabled ? "#7a2806" : "#ea7028"}`,
    color: "#fde7d6",
    fontSize: 12.5,
    fontWeight: 500,
    letterSpacing: "-0.005em",
    boxShadow: disabled
      ? "none"
      : "0 1px 0 rgba(255,255,255,0.18) inset, 0 8px 24px rgba(194,65,12,0.35)",
    cursor: disabled ? "not-allowed" : "pointer",
    opacity: disabled ? 0.7 : 1,
    width: full ? "100%" : "auto",
    justifyContent: "center",
    transition: "transform 80ms ease, box-shadow 200ms ease",
  };
  return (
    <button type={type} disabled={disabled} onClick={onClick} style={style}>
      {icon}
      <span>{children}</span>
    </button>
  );
}

export function BtnGhost({ children, icon, full, onClick, type = "button", disabled }: ButtonProps) {
  const style: CSSProperties = {
    display: "inline-flex",
    alignItems: "center",
    gap: 8,
    padding: "8px 14px",
    borderRadius: 10,
    background: "var(--glass-1)",
    border: "1px solid var(--glass-stroke-2)",
    color: "var(--text-1)",
    fontSize: 12.5,
    fontWeight: 500,
    cursor: disabled ? "not-allowed" : "pointer",
    backdropFilter: "blur(20px)",
    WebkitBackdropFilter: "blur(20px)",
    width: full ? "100%" : "auto",
    justifyContent: "center",
    opacity: disabled ? 0.5 : 1,
  };
  return (
    <button type={type} disabled={disabled} onClick={onClick} style={style}>
      {icon}
      <span>{children}</span>
    </button>
  );
}

type Tone = "neutral" | "orange" | "ok" | "warn" | "info" | "err";

interface PillProps {
  children: ReactNode;
  tone?: Tone;
  icon?: ReactNode;
}

const TONES: Record<Tone, { bg: string; bd: string; fg: string }> = {
  neutral: { bg: "var(--glass-1)", bd: "var(--glass-stroke-2)", fg: "var(--text-2)" },
  orange:  { bg: "rgba(194,65,12,0.14)", bd: "rgba(234,112,40,0.4)", fg: "#f7c69a" },
  ok:      { bg: "rgba(106,209,154,0.10)", bd: "rgba(106,209,154,0.35)", fg: "#a8e6c4" },
  warn:    { bg: "rgba(232,176,74,0.10)", bd: "rgba(232,176,74,0.35)", fg: "#f1d18a" },
  info:    { bg: "rgba(106,168,209,0.10)", bd: "rgba(106,168,209,0.35)", fg: "#a8cce6" },
  err:     { bg: "rgba(232,92,74,0.10)",  bd: "rgba(232,92,74,0.35)",  fg: "#f0a89e" },
};

export function Pill({ children, tone = "neutral", icon }: PillProps) {
  const t = TONES[tone];
  return (
    <span
      style={{
        display: "inline-flex",
        alignItems: "center",
        gap: 6,
        padding: "3px 9px",
        borderRadius: 999,
        background: t.bg,
        border: `1px solid ${t.bd}`,
        color: t.fg,
        fontSize: 10.5,
        fontFamily: "var(--f-mono)",
        letterSpacing: "0.04em",
      }}
    >
      {icon}
      {children}
    </span>
  );
}

export function Aurora() {
  return (
    <div className="mz-aurora" aria-hidden>
      <div className="spark" />
    </div>
  );
}

interface ScreenShellProps {
  children: ReactNode;
}

/** Wraps a route in the Mazda aurora + grid backdrop. */
export function ScreenShell({ children }: ScreenShellProps) {
  return (
    <div className="mz-artboard" style={{ width: "100vw", height: "100vh" }}>
      <Aurora />
      <div className="mz-grid" />
      <div className="mz-stage">{children}</div>
    </div>
  );
}

interface StepIndicatorProps {
  step: 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9;
  /**
   * Optional click handler. When provided, each step becomes a button-like
   * clickable target — cursor pointer, hover affordance, keyboard-focusable.
   * The handler receives the 1-based step number; it's up to the caller
   * (typically via `useStepNavigator`) to decide which steps actually
   * navigate vs. no-op.
   */
  onStepClick?: (step: number) => void;
}

const STEPS = [
  { n: "01", t: "Upload" },
  { n: "02", t: "Interactable" },
  { n: "03", t: "Informational" },
  { n: "04", t: "User flows" },
  { n: "05", t: "Setup" },
  { n: "06", t: "Analyze state" },
  { n: "07", t: "Test prompt" },
  { n: "08", t: "Merge & finalize" },
  { n: "09", t: "Execution" },
];

export function StepIndicator({ step, onStepClick }: StepIndicatorProps) {
  return (
    <div style={{ display: "flex", alignItems: "center", gap: 14 }}>
      {STEPS.map((s, i) => {
        const idx = i + 1;
        const on = idx === step;
        const done = idx < step;
        // Render the step body once, then conditionally wrap it in a button
        // when navigation is wired up. Keeps the visual mapping a one-source-
        // of-truth — clickable steps look identical to passive ones except
        // for the cursor, focus ring, and hover background.
        const body = (
          <div style={{ display: "flex", alignItems: "center", gap: 8, opacity: on || done ? 1 : 0.45 }}>
            <div
              style={{
                width: 22,
                height: 22,
                borderRadius: 999,
                border: `1px solid ${on ? "rgba(234,112,40,0.5)" : "var(--glass-stroke-2)"}`,
                background: on ? "rgba(194,65,12,0.18)" : done ? "rgba(106,209,154,0.10)" : "transparent",
                display: "grid",
                placeItems: "center",
                fontFamily: "var(--f-mono)",
                fontSize: 10,
                color: on ? "#f7c69a" : done ? "#a8e6c4" : "var(--text-3)",
              }}
            >
              {s.n}
            </div>
            <div
              style={{
                fontSize: 12.5,
                color: on ? "var(--text-1)" : "var(--text-3)",
                fontWeight: on ? 500 : 400,
              }}
            >
              {s.t}
            </div>
          </div>
        );

        return (
          <div key={s.n} style={{ display: "flex", alignItems: "center", gap: 14 }}>
            {onStepClick ? (
              <button
                type="button"
                onClick={() => onStepClick(idx)}
                aria-label={`Go to step ${idx}: ${s.t}`}
                aria-current={on ? "step" : undefined}
                style={{
                  background: "transparent",
                  border: "none",
                  padding: "4px 6px",
                  margin: "-4px -6px",
                  borderRadius: 8,
                  cursor: "pointer",
                  font: "inherit",
                  color: "inherit",
                  transition: "background 120ms ease",
                }}
                onMouseEnter={(e) => {
                  (e.currentTarget as HTMLButtonElement).style.background = "var(--glass-2)";
                }}
                onMouseLeave={(e) => {
                  (e.currentTarget as HTMLButtonElement).style.background = "transparent";
                }}
              >
                {body}
              </button>
            ) : (
              body
            )}
            {idx < STEPS.length && (
              <div style={{ width: 18, height: 1, background: "var(--glass-stroke-2)" }} />
            )}
          </div>
        );
      })}
    </div>
  );
}
