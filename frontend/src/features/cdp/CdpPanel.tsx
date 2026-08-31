import { useEffect, useState } from "react";
import { createPortal } from "react-dom";
import { BtnGhost, BtnPrimary, Pill } from "@/shared/components/Primitives";
import { ApiError } from "@/shared/api/client";
import type { CdpStatusDto, CdpTabDto } from "@/shared/api/types";
import { useAttachTab, useCdpStatus, useDetachTab, useSetCdpPort } from "./cdpApi";

/**
 * Status chip for the TopBar. Answers, at a glance, the question every run
 * depends on: is a browser reachable, and which tab will MAZDA drive?
 *
 * Three states worth distinguishing — attached, reachable-but-unattached
 * (falls back to whichever page Chrome lists first), and unreachable.
 */
export function CdpStatusChip() {
  const [open, setOpen] = useState(false);
  const { data } = useCdpStatus();

  const tone = !data?.available
    ? "err"
    : data.attachedTabMissing
      ? "warn"
      : data.attachedTargetId
        ? "ok"
        : "warn";

  const label = !data
    ? "cdp …"
    : !data.available
      ? "no browser"
      : data.attachedTabMissing
        ? "tab closed"
        : data.attachedTargetId
          ? truncate(data.attachedTitle || data.attachedUrl || "attached", 28)
          : "no tab picked";

  return (
    <>
      <button
        onClick={() => setOpen(true)}
        title="Choose which Chrome tab MAZDA drives"
        style={{
          display: "inline-flex",
          alignItems: "center",
          gap: 8,
          padding: "4px 10px",
          borderRadius: 999,
          background: "var(--glass-1)",
          border: "1px solid var(--glass-stroke-2)",
          color: "var(--text-2)",
          fontSize: 11,
          fontFamily: "var(--f-mono)",
          cursor: "pointer",
        }}
      >
        <span
          aria-hidden
          style={{
            width: 7,
            height: 7,
            borderRadius: 999,
            background: DOT[tone],
            boxShadow: `0 0 6px ${DOT[tone]}`,
          }}
        />
        {label}
      </button>
      {open && <CdpPanel onClose={() => setOpen(false)} />}
    </>
  );
}

const DOT: Record<string, string> = {
  ok: "#6ad19a",
  warn: "#e8b04a",
  err: "#e85c4a",
};

function CdpPanel({ onClose }: { onClose: () => void }) {
  const { data, isLoading, refetch, isFetching } = useCdpStatus();
  const attach = useAttachTab();
  const detach = useDetachTab();
  const setPort = useSetCdpPort();

  const [portDraft, setPortDraft] = useState("");
  const [err, setErr] = useState<string | null>(null);

  // Seed the port field once the real value arrives, but don't clobber what
  // the user is mid-way through typing on every 10s poll.
  useEffect(() => {
    if (data && portDraft === "") setPortDraft(String(data.port));
  }, [data, portDraft]);

  const run = async (fn: () => Promise<unknown>) => {
    setErr(null);
    try {
      await fn();
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : String(e));
    }
  };

  // Portalled to <body> on purpose. The chip lives inside TopBar, which sets
  // `backdrop-filter: blur(20px)` — and a filtered ancestor becomes the
  // containing block for its `position: fixed` descendants. Rendered in place,
  // this overlay's `inset: 0` resolved to the TOP BAR's box (1100x62) instead
  // of the viewport, so the dialog was clipped to a 62px-tall strip.
  return createPortal(
    <div
      style={{
        position: "fixed",
        inset: 0,
        background: "rgba(0,0,0,0.6)",
        backdropFilter: "blur(4px)",
        // flex + auto margins rather than `place-items: center`: a centred grid
        // item that overflows its container has its top edge clipped and
        // unreachable, which bites on short viewports.
        display: "flex",
        justifyContent: "center",
        overflowY: "auto",
        zIndex: 200,
        padding: 24,
      }}
      onClick={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div
        style={{
          background: "var(--surface-1, #14100c)",
          border: "1px solid var(--glass-stroke-2)",
          borderRadius: 14,
          padding: 24,
          width: "min(720px, 100%)",
          height: "fit-content",
          margin: "auto",
        }}
      >
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
          <div style={{ fontSize: 16, fontWeight: 500 }}>Browser attachment</div>
          <BtnGhost onClick={onClose}>Close</BtnGhost>
        </div>

        <div style={{ fontSize: 12, color: "var(--text-3)", marginTop: 8, lineHeight: 1.6 }}>
          The tab you pick here is what gather, execute and function tests all drive. With no tab
          picked, MAZDA uses whichever page Chrome happens to list first — which is not necessarily
          the one you are looking at.
        </div>

        {/* ── Port ── */}
        <div style={{ marginTop: 20, display: "flex", alignItems: "center", gap: 10 }}>
          <div style={{ fontSize: 11, fontFamily: "var(--f-mono)", color: "var(--text-3)" }}>
            PORT
          </div>
          <input
            value={portDraft}
            onChange={(e) => setPortDraft(e.target.value.replace(/[^0-9]/g, ""))}
            style={{
              width: 90,
              padding: "6px 10px",
              background: "var(--glass-1)",
              border: "1px solid var(--glass-stroke-2)",
              borderRadius: 8,
              color: "var(--text-1)",
              fontSize: 12,
              fontFamily: "var(--f-mono)",
            }}
          />
          <BtnGhost
            onClick={() => run(() => setPort.mutateAsync(Number(portDraft || "9222")))}
            disabled={setPort.isPending || !portDraft || Number(portDraft) === data?.port}
          >
            {setPort.isPending ? "Saving…" : "Save port"}
          </BtnGhost>
          <BtnGhost onClick={() => refetch()} disabled={isFetching}>
            {isFetching ? "Checking…" : "Refresh"}
          </BtnGhost>
        </div>

        {isLoading && (
          <div style={{ marginTop: 20, fontSize: 12, color: "var(--text-3)" }}>
            Checking for a debuggable browser…
          </div>
        )}

        {/* ── Unreachable ── */}
        {data && !data.available && (
          <div
            style={{
              marginTop: 20,
              background: "rgba(232,92,74,0.10)",
              border: "1px solid rgba(232,92,74,0.35)",
              borderRadius: 10,
              padding: 14,
            }}
          >
            <div
              style={{
                fontSize: 10.5,
                fontFamily: "var(--f-mono)",
                letterSpacing: "0.04em",
                color: "#f0a89e",
                marginBottom: 8,
              }}
            >
              NO BROWSER REACHABLE
            </div>
            <div style={{ fontSize: 12, color: "var(--text-2)", lineHeight: 1.6 }}>
              {data.reason}
            </div>
            <div style={{ fontSize: 11, color: "var(--text-3)", marginTop: 10, lineHeight: 1.6 }}>
              Start Chrome with both flags — the address flag matters, because MAZDA runs in a
              container and connects from outside the loopback interface:
              <pre
                style={{
                  margin: "8px 0 0",
                  padding: 10,
                  background: "var(--glass-1)",
                  border: "1px solid var(--glass-stroke-2)",
                  borderRadius: 8,
                  fontSize: 11,
                  whiteSpace: "pre-wrap",
                  overflowWrap: "anywhere",
                  color: "var(--text-2)",
                }}
              >
                {`chrome --remote-debugging-port=${data.port} --remote-debugging-address=0.0.0.0`}
              </pre>
            </div>
          </div>
        )}

        {/* ── Stale attachment ── */}
        {data?.available && data.attachedTabMissing && (
          <div style={{ marginTop: 20 }}>
            <Pill tone="warn">
              The attached tab is closed — pick another, or runs will fail
            </Pill>
          </div>
        )}

        {/* ── Tabs ── */}
        {data?.available && (
          <>
            <div style={{ marginTop: 22, display: "flex", alignItems: "center", gap: 12 }}>
              <div style={{ fontSize: 11, fontFamily: "var(--f-mono)", color: "var(--text-3)" }}>
                TABS
              </div>
              <Pill tone="neutral">{data.browserVersion ?? "chrome"}</Pill>
              {data.attachedTargetId && (
                <BtnGhost onClick={() => run(() => detach.mutateAsync())} disabled={detach.isPending}>
                  Detach
                </BtnGhost>
              )}
            </div>

            {data.tabs.length === 0 && (
              <div style={{ fontSize: 12, color: "var(--text-3)", marginTop: 10 }}>
                Chrome is running but has no ordinary page open.
              </div>
            )}

            <div style={{ marginTop: 10, display: "flex", flexDirection: "column", gap: 8 }}>
              {data.tabs.map((t) => (
                <TabRow
                  key={t.targetId}
                  tab={t}
                  busy={attach.isPending}
                  onAttach={() => run(() => attach.mutateAsync(t.targetId))}
                />
              ))}
            </div>
          </>
        )}

        {err && (
          <div style={{ marginTop: 16 }}>
            <Pill tone="err">{err}</Pill>
          </div>
        )}
      </div>
    </div>,
    document.body
  );
}

function TabRow({
  tab,
  busy,
  onAttach,
}: {
  tab: CdpTabDto;
  busy: boolean;
  onAttach: () => void;
}) {
  return (
    <div
      style={{
        display: "grid",
        gridTemplateColumns: "1fr auto",
        gap: 16,
        alignItems: "center",
        padding: "10px 14px",
        borderRadius: 10,
        background: tab.attached ? "rgba(106,209,154,0.08)" : "var(--glass-1)",
        border: `1px solid ${tab.attached ? "rgba(106,209,154,0.35)" : "var(--glass-stroke-2)"}`,
      }}
    >
      <div style={{ minWidth: 0 }}>
        <div
          style={{
            fontSize: 13,
            color: "var(--text-1)",
            overflow: "hidden",
            textOverflow: "ellipsis",
            whiteSpace: "nowrap",
          }}
        >
          {tab.title || "(untitled)"}
        </div>
        <div
          style={{
            fontSize: 11,
            fontFamily: "var(--f-mono)",
            color: "var(--text-3)",
            marginTop: 3,
            overflow: "hidden",
            textOverflow: "ellipsis",
            whiteSpace: "nowrap",
          }}
        >
          {tab.url}
        </div>
      </div>
      {tab.attached ? (
        <Pill tone="ok">attached</Pill>
      ) : (
        <BtnPrimary onClick={onAttach} disabled={busy}>
          Attach
        </BtnPrimary>
      )}
    </div>
  );
}

function truncate(s: string, n: number) {
  return s.length <= n ? s : s.slice(0, n - 1) + "…";
}
