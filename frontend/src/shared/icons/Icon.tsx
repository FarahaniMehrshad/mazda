type IconFn = (size?: number) => JSX.Element;

export const Icon: Record<string, IconFn> = {
  folder: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4">
      <path d="M2 4.5C2 3.7 2.7 3 3.5 3H6l1.5 1.5h5C13.3 4.5 14 5.2 14 6v6c0 .8-.7 1.5-1.5 1.5h-9C2.7 13.5 2 12.8 2 12V4.5z" />
    </svg>
  ),
  play: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="currentColor">
      <path d="M4 3l9 5-9 5V3z" />
    </svg>
  ),
  pause: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="currentColor">
      <rect x="4" y="3" width="3" height="10" />
      <rect x="9" y="3" width="3" height="10" />
    </svg>
  ),
  arrow: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4">
      <path d="M3 8h10M9 4l4 4-4 4" />
    </svg>
  ),
  plus: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4">
      <path d="M8 3v10M3 8h10" />
    </svg>
  ),
  search: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4">
      <circle cx="7" cy="7" r="4.5" />
      <path d="M14 14l-3.2-3.2" />
    </svg>
  ),
  globe: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4">
      <circle cx="8" cy="8" r="5.5" />
      <path d="M2.5 8h11M8 2.5c1.5 1.5 2.2 3.4 2.2 5.5S9.5 12.5 8 13.5C6.5 12.5 5.8 10.6 5.8 8.5S6.5 4 8 2.5z" />
    </svg>
  ),
  check: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6">
      <path d="M3 8.5l3 3 7-7" />
    </svg>
  ),
  spark: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="currentColor">
      <path d="M8 1l1.3 4.7L14 7l-4.7 1.3L8 13l-1.3-4.7L2 7l4.7-1.3L8 1z" />
    </svg>
  ),
  code: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4">
      <path d="M5 4L2 8l3 4M11 4l3 4-3 4M9.5 3l-3 10" />
    </svg>
  ),
  upload: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4">
      <path d="M8 11V3M5 6l3-3 3 3M3 12v1.5h10V12" />
    </svg>
  ),
  download: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4">
      <path d="M8 3v8M5 8l3 3 3-3M3 13.5h10" />
    </svg>
  ),
  copy: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4">
      <rect x="5" y="5" width="9" height="9" rx="1.5" />
      <path d="M2.5 10V3c0-.6.4-1 1-1H10" />
    </svg>
  ),
  file: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4">
      <path d="M3.5 1.5h6L13 5v9.5H3.5V1.5z" />
      <path d="M9.5 1.5V5H13" />
    </svg>
  ),
  more: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="currentColor">
      <circle cx="3" cy="8" r="1.3" />
      <circle cx="8" cy="8" r="1.3" />
      <circle cx="13" cy="8" r="1.3" />
    </svg>
  ),
  clock: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4">
      <circle cx="8" cy="8" r="5.5" />
      <path d="M8 5v3.2L10 10" />
    </svg>
  ),
  cursor: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="currentColor">
      <path d="M3 2l3 11 2-4 4-2L3 2z" />
    </svg>
  ),
  keyboard: (s = 14) => (
    <svg width={s} height={s} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4">
      <rect x="1.5" y="4" width="13" height="8" rx="1.5" />
      <path d="M4 7h.01M6 7h.01M8 7h.01M10 7h.01M12 7h.01M5 10h6" />
    </svg>
  ),
};
