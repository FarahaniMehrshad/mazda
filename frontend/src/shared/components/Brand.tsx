import { type CSSProperties, useId } from "react";

interface GlyphProps {
  size?: number;
  glow?: boolean;
}

export function MazdaGlyph({ size = 28, glow = false }: GlyphProps) {
  const id = useId().replace(/:/g, "");
  return (
    <svg
      width={size}
      height={size * 0.9}
      viewBox="0 0 64 58"
      fill="none"
      style={{
        overflow: "visible",
        filter: glow ? "drop-shadow(0 0 12px rgba(234,112,40,0.55))" : "none",
      }}
    >
      <defs>
        <linearGradient id={id} x1="0" x2="0" y1="0" y2="1">
          <stop offset="0%" stopColor="#f7c69a" />
          <stop offset="55%" stopColor="#e88a4c" />
          <stop offset="100%" stopColor="#7a2806" />
        </linearGradient>
      </defs>
      <rect x="31" y="6" width="2" height="46" rx="1" fill={`url(#${id})`} />
      <circle cx="32" cy="29" r="4.5" fill={`url(#${id})`} />
      <circle cx="32" cy="29" r="2" fill="#0d0907" />
      <path d="M30 22 L8 16 L12 22 L30 24 Z" fill={`url(#${id})`} opacity="0.95" />
      <path d="M30 28 L4 28 L10 32 L30 30 Z" fill={`url(#${id})`} opacity="0.85" />
      <path d="M30 34 L10 40 L14 38 L30 36 Z" fill={`url(#${id})`} opacity="0.7" />
      <path d="M34 22 L56 16 L52 22 L34 24 Z" fill={`url(#${id})`} opacity="0.95" />
      <path d="M34 28 L60 28 L54 32 L34 30 Z" fill={`url(#${id})`} opacity="0.85" />
      <path d="M34 34 L54 40 L50 38 L34 36 Z" fill={`url(#${id})`} opacity="0.7" />
      <path d="M28 52 L32 58 L36 52 L34 50 L32 53 L30 50 Z" fill={`url(#${id})`} />
    </svg>
  );
}

interface WordmarkProps {
  size?: number;
  glow?: boolean;
}

export function MazdaWordmark({ size = 18, glow = false }: WordmarkProps) {
  const style: CSSProperties = {
    fontFamily: "var(--f-display)",
    fontSize: size,
    fontWeight: 600,
    letterSpacing: "0.22em",
    color: "var(--text-1)",
    textTransform: "uppercase",
  };
  return (
    <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
      <MazdaGlyph size={size * 1.35} glow={glow} />
      <div style={style}>Mazda</div>
    </div>
  );
}
