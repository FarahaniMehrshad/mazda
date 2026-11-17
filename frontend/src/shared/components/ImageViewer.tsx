import { useCallback, useEffect, useRef, useState, type CSSProperties } from "react";
import { createPortal } from "react-dom";

/**
 * Pictures you can open bigger.
 *
 *   <ZoomableImage src=… alt=… caption=… />                 one picture
 *   <ZoomableImage src=… gallery={items} index={i} />        one of several: ← / → flip through them
 *
 * The viewer fills the window with the picture fitted to it; clicking the
 * picture switches to its real size (scroll to look around) and back.
 * Esc, the × button or a click beside the picture closes it.
 */

export interface ViewerImage {
  src: string;
  caption: string;
}

export function ZoomableImage({
  src,
  alt,
  caption,
  gallery,
  index = 0,
  style,
}: {
  src: string;
  alt: string;
  caption?: string;
  /** All pictures the viewer can flip through; this one is `gallery[index]`. */
  gallery?: ViewerImage[];
  index?: number;
  style?: CSSProperties;
}) {
  const [open, setOpen] = useState(false);
  const items = gallery && gallery.length > 0 ? gallery : [{ src, caption: caption ?? alt }];
  const start = gallery && gallery.length > 0 ? Math.max(0, Math.min(index, gallery.length - 1)) : 0;
  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        title="Click to see it bigger"
        aria-label={`${alt}: open bigger`}
        style={{ display: "block", padding: 0, border: 0, background: "none", cursor: "zoom-in", width: style?.width ?? "100%", maxWidth: "100%" }}
      >
        <img alt={alt} src={src} style={{ display: "block", width: "100%", ...style }} />
      </button>
      {open && <ImageViewer images={items} index={start} onClose={() => setOpen(false)} />}
    </>
  );
}

export function ImageViewer({ images, index, onClose }: { images: ViewerImage[]; index: number; onClose: () => void }) {
  const [i, setI] = useState(index);
  const [actual, setActual] = useState(false);
  const closeRef = useRef<HTMLButtonElement>(null);
  const count = images.length;
  const go = useCallback((d: number) => {
    setI((x) => (x + d + count) % count);
    setActual(false);
  }, [count]);

  useEffect(() => {
    const before = document.activeElement as HTMLElement | null;
    closeRef.current?.focus();
    const overflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") { e.preventDefault(); onClose(); }
      else if (e.key === "ArrowRight" && count > 1) { e.preventDefault(); go(1); }
      else if (e.key === "ArrowLeft" && count > 1) { e.preventDefault(); go(-1); }
    };
    window.addEventListener("keydown", onKey);
    return () => {
      window.removeEventListener("keydown", onKey);
      document.body.style.overflow = overflow;
      before?.focus?.();
    };
  }, [onClose, go, count]);

  const img = images[i];
  if (!img) return null;
  const btn: CSSProperties = {
    background: "rgba(20,16,12,0.85)",
    color: "var(--text-1, #eee)",
    border: "1px solid var(--glass-stroke-2, #444)",
    borderRadius: 10,
    padding: "8px 14px",
    fontSize: 14,
    cursor: "pointer",
  };

  return createPortal(
    <div
      role="dialog"
      aria-modal="true"
      aria-label={img.caption}
      onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}
      style={{
        position: "fixed", inset: 0, zIndex: 1000, background: "rgba(0,0,0,0.85)", backdropFilter: "blur(4px)",
        display: "flex", flexDirection: "column", alignItems: "center", padding: "12px 16px", gap: 10,
      }}
    >
      <div style={{ display: "flex", alignItems: "center", gap: 10, width: "100%", maxWidth: 1600 }}>
        <span style={{ flex: 1, minWidth: 0, fontSize: 14, color: "var(--text-1, #eee)", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
          {img.caption}
          {count > 1 && <span style={{ color: "var(--text-3, #999)" }}>{`  ·  ${i + 1} / ${count}`}</span>}
        </span>
        <span style={{ fontSize: 12, color: "var(--text-3, #999)" }}>{actual ? "real size: click to fit" : "click the picture for real size"}</span>
        {count > 1 && <button type="button" style={btn} onClick={() => go(-1)} aria-label="Previous picture">←</button>}
        {count > 1 && <button type="button" style={btn} onClick={() => go(1)} aria-label="Next picture">→</button>}
        <button ref={closeRef} type="button" style={btn} onClick={onClose} aria-label="Close">×</button>
      </div>
      <div
        onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}
        style={{
          flex: 1, minHeight: 0, width: "100%", display: "flex",
          alignItems: actual ? "flex-start" : "center", justifyContent: actual ? "flex-start" : "center",
          overflow: actual ? "auto" : "hidden",
        }}
      >
        <img
          alt={img.caption}
          src={img.src}
          onClick={() => setActual((a) => !a)}
          style={actual
            ? { cursor: "zoom-out", margin: "auto", flex: "none" }
            : { cursor: "zoom-in", maxWidth: "100%", maxHeight: "100%", objectFit: "contain", borderRadius: 6, boxShadow: "0 10px 40px rgba(0,0,0,0.5)" }}
        />
      </div>
    </div>,
    document.body,
  );
}
