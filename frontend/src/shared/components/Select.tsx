import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState, type CSSProperties, type KeyboardEvent, type ReactNode } from "react";
import { createPortal } from "react-dom";

/**
 * MAZDA's dropdown. Use this — never a native `<select>`.
 *
 * Why not a native select: the option list of a `<select>` is drawn by the
 * OS/browser, not the page. It ignores almost all CSS, so on our dark theme it
 * pops up white (or with white chrome), and focus rings differ per platform.
 * `scripts/check-ui.mjs` fails the build if a native `<select>` appears
 * anywhere outside this file.
 *
 * Behaviour matches a native select:
 *   • Click, Enter, Space, ArrowDown/Up on the trigger opens the list.
 *   • ArrowUp/Down, Home/End move the highlight; typing jumps to the first
 *     option starting with the typed letters; Enter/Space selects; Escape or
 *     Tab closes; clicking outside closes.
 *   • ARIA: trigger is a combobox button (aria-haspopup="listbox",
 *     aria-expanded), the popup is a listbox with options and
 *     aria-activedescendant.
 *
 * The popup renders in a portal on <body> with fixed positioning, so it is
 * never clipped by an `overflow: hidden` parent or a modal, and opens upward
 * when there isn't room below.
 */
export interface SelectOption<V extends string = string> {
  value: V;
  label: ReactNode;
  /** Plain-text label used for type-ahead and the trigger tooltip. Defaults to `label` when it is a string. */
  text?: string;
  /** Secondary text shown under the label in the list. */
  hint?: ReactNode;
  disabled?: boolean;
}

export interface SelectProps<V extends string = string> {
  value: V | "" | null | undefined;
  options: SelectOption<V>[];
  onChange: (value: V) => void;
  placeholder?: string;
  disabled?: boolean;
  /** Accessible name when there is no visible <label htmlFor={id}>. */
  ariaLabel?: string;
  id?: string;
  /** Message shown in the popup when `options` is empty. */
  emptyText?: string;
  style?: CSSProperties;
}

const POPUP_MAX_HEIGHT = 280;

export function Select<V extends string = string>({
  value,
  options,
  onChange,
  placeholder = "Select…",
  disabled,
  ariaLabel,
  id,
  emptyText = "No options.",
  style,
}: SelectProps<V>) {
  const autoId = useId();
  const triggerId = id ?? `mz-select-${autoId}`;
  const listId = `${triggerId}-list`;
  const triggerRef = useRef<HTMLButtonElement>(null);
  const popupRef = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const [highlight, setHighlight] = useState(-1);
  const [pos, setPos] = useState<{ left: number; width: number; top?: number; bottom?: number; maxHeight: number } | null>(null);
  const typeahead = useRef({ text: "", at: 0 });

  const selectedIndex = options.findIndex((o) => o.value === value);
  const selected = selectedIndex >= 0 ? options[selectedIndex] : null;
  const textOf = (o: SelectOption<V>) => o.text ?? (typeof o.label === "string" ? o.label : String(o.value));

  const firstEnabled = useCallback(
    (from: number, step: 1 | -1) => {
      for (let i = from; i >= 0 && i < options.length; i += step) if (!options[i].disabled) return i;
      return -1;
    },
    [options],
  );

  const openList = useCallback(() => {
    if (disabled) return;
    setHighlight(selectedIndex >= 0 ? selectedIndex : firstEnabled(0, 1));
    setOpen(true);
  }, [disabled, selectedIndex, firstEnabled]);

  const close = useCallback((refocus = true) => {
    setOpen(false);
    if (refocus) triggerRef.current?.focus();
  }, []);

  const choose = useCallback(
    (i: number) => {
      const o = options[i];
      if (!o || o.disabled) return;
      if (o.value !== value) onChange(o.value);
      close();
    },
    [options, value, onChange, close],
  );

  // Position the popup under (or above) the trigger; follow scroll / resize.
  useLayoutEffect(() => {
    if (!open) return;
    const place = () => {
      const r = triggerRef.current?.getBoundingClientRect();
      if (!r) return;
      const below = window.innerHeight - r.bottom - 8;
      const above = r.top - 8;
      const up = below < Math.min(POPUP_MAX_HEIGHT, 160) && above > below;
      setPos({
        left: r.left,
        width: r.width,
        top: up ? undefined : r.bottom + 4,
        bottom: up ? window.innerHeight - r.top + 4 : undefined,
        maxHeight: Math.max(120, Math.min(POPUP_MAX_HEIGHT, up ? above : below)),
      });
    };
    place();
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
  }, [open]);

  // Outside click closes.
  useEffect(() => {
    if (!open) return;
    const onPointerDown = (e: PointerEvent) => {
      const t = e.target as Node;
      if (triggerRef.current?.contains(t) || popupRef.current?.contains(t)) return;
      close(false);
    };
    document.addEventListener("pointerdown", onPointerDown, true);
    return () => document.removeEventListener("pointerdown", onPointerDown, true);
  }, [open, close]);

  // Keep the highlighted option in view.
  useEffect(() => {
    if (!open || highlight < 0) return;
    popupRef.current?.querySelector<HTMLElement>(`[data-index="${highlight}"]`)?.scrollIntoView({ block: "nearest" });
  }, [open, highlight]);

  const onKeyDown = (e: KeyboardEvent<HTMLButtonElement>) => {
    if (disabled) return;
    const k = e.key;
    if (!open) {
      if (k === "ArrowDown" || k === "ArrowUp" || k === "Enter" || k === " ") {
        e.preventDefault();
        openList();
      }
      return;
    }
    if (k === "Escape") { e.preventDefault(); close(); return; }
    if (k === "Tab") { close(false); return; }
    if (k === "Enter" || k === " ") { e.preventDefault(); if (highlight >= 0) choose(highlight); return; }
    if (k === "ArrowDown") { e.preventDefault(); const n = firstEnabled(highlight + 1, 1); if (n >= 0) setHighlight(n); return; }
    if (k === "ArrowUp") { e.preventDefault(); const n = firstEnabled(Math.max(0, highlight - 1), -1); if (n >= 0) setHighlight(n); return; }
    if (k === "Home") { e.preventDefault(); setHighlight(firstEnabled(0, 1)); return; }
    if (k === "End") { e.preventDefault(); setHighlight(firstEnabled(options.length - 1, -1)); return; }
    if (k.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) {
      const now = Date.now();
      const ta = typeahead.current;
      ta.text = now - ta.at < 700 ? ta.text + k.toLowerCase() : k.toLowerCase();
      ta.at = now;
      const i = options.findIndex((o) => !o.disabled && textOf(o).toLowerCase().startsWith(ta.text));
      if (i >= 0) setHighlight(i);
    }
  };

  const triggerText = selected ? textOf(selected) : undefined;

  return (
    <>
      <button
        ref={triggerRef}
        id={triggerId}
        type="button"
        role="combobox"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={open ? listId : undefined}
        aria-activedescendant={open && highlight >= 0 ? `${listId}-${highlight}` : undefined}
        aria-label={ariaLabel}
        disabled={disabled}
        title={triggerText}
        onClick={() => (open ? close() : openList())}
        onKeyDown={onKeyDown}
        className="mz-select-trigger"
        data-open={open || undefined}
        style={style}
      >
        <span className="mz-select-value" data-placeholder={selected ? undefined : true}>
          {selected ? selected.label : placeholder}
        </span>
        <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden="true" className="mz-select-chevron">
          <path d="M1.5 3.5 5 7l3.5-3.5" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>

      {open &&
        pos &&
        createPortal(
          <div
            ref={popupRef}
            id={listId}
            role="listbox"
            aria-labelledby={triggerId}
            className="mz-select-popup"
            style={{ left: pos.left, width: pos.width, top: pos.top, bottom: pos.bottom, maxHeight: pos.maxHeight }}
            // Keep focus on the trigger so keyboard handling stays in one place.
            onMouseDown={(e) => e.preventDefault()}
          >
            {options.length === 0 && <div className="mz-select-empty">{emptyText}</div>}
            {options.map((o, i) => (
              <div
                key={o.value}
                id={`${listId}-${i}`}
                data-index={i}
                role="option"
                aria-selected={i === selectedIndex}
                aria-disabled={o.disabled || undefined}
                className="mz-select-option"
                data-highlighted={i === highlight || undefined}
                data-selected={i === selectedIndex || undefined}
                onMouseEnter={() => !o.disabled && setHighlight(i)}
                onClick={() => choose(i)}
              >
                <span>{o.label}</span>
                {o.hint && <span className="mz-select-hint">{o.hint}</span>}
              </div>
            ))}
          </div>,
          document.body,
        )}
    </>
  );
}
