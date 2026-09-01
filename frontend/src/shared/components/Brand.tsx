import { type CSSProperties } from "react";

const GLYPH_SRC = "/mazda-glyph.png?v=3";
/** Natural aspect of the extracted banner mark (width / height). */
const GLYPH_ASPECT = 641 / 591;

interface GlyphProps {
  /** Glyph height in pixels. Width follows the logo aspect ratio. */
  size?: number;
  glow?: boolean;
}

export function MazdaGlyph({ size = 28, glow = false }: GlyphProps) {
  const height = size;
  const width = size * GLYPH_ASPECT;
  return (
    <img
      src={GLYPH_SRC}
      alt=""
      width={width}
      height={height}
      draggable={false}
      style={{
        width,
        height,
        display: "block",
        objectFit: "contain",
        overflow: "visible",
        filter: glow ? "drop-shadow(0 0 12px rgba(234,112,40,0.55))" : "none",
        userSelect: "none",
      }}
    />
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
