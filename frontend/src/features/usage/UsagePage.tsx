import { useMemo, useState, type ReactNode } from "react";
import { TopBar } from "@/shared/components/TopBar";
import { ScreenShell, Pill } from "@/shared/components/Primitives";
import { CallTable, UsageTiles } from "./UsagePanel";
import {
  costNote,
  fmtInt,
  fmtTok,
  fmtUsd,
  fmtUsdExact,
  kindLabel,
  useUsageSummary,
  type UsageGroup,
} from "./api";

type Range = "today" | "7d" | "30d" | "all";
const RANGES: { id: Range; label: string }[] = [
  { id: "today", label: "Today" },
  { id: "7d", label: "7 days" },
  { id: "30d", label: "30 days" },
  { id: "all", label: "All time" },
];

function startOf(range: Range): Date | null {
  if (range === "all") return null;
  const d = new Date();
  d.setHours(0, 0, 0, 0);
  if (range === "7d") d.setDate(d.getDate() - 6);
  if (range === "30d") d.setDate(d.getDate() - 29);
  return d;
}

const dayKey = (d: Date) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;

/** Every LLM call Mazda made for you: totals, cost per day, per model, per kind of work, latest calls. */
export function UsagePage() {
  const [range, setRange] = useState<Range>("7d");
  const from = useMemo(() => startOf(range), [range]);
  const q = useUsageSummary(from);
  const s = q.data;

  return (
    <ScreenShell>
      <TopBar />
      <div className="mz-scroll" style={{ flex: 1, padding: 28, display: "flex", flexDirection: "column", gap: 20, overflow: "auto" }}>
        <div>
          <div className="mz-eyebrow">Usage</div>
          <div style={{ fontFamily: "var(--f-display)", fontSize: 38, fontWeight: 500, letterSpacing: "-0.025em", marginTop: 4 }}>
            LLM usage
          </div>
          <div style={{ color: "var(--text-3)", marginTop: 4, fontSize: 13, maxWidth: 720, lineHeight: 1.5 }}>
            Every call to the language model, exactly as the provider reported it. Claude Code reports its own cost at
            API list prices (on a subscription you are not billed per token). For API-key models the cost is estimated
            from the prices in Settings.
          </div>
        </div>

        <div role="radiogroup" aria-label="Period" style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
          {RANGES.map((r) => (
            <button key={r.id} type="button" role="radio" aria-checked={range === r.id} onClick={() => setRange(r.id)}
              style={{
                padding: "6px 14px", borderRadius: 999, fontSize: 12.5, cursor: "pointer",
                background: range === r.id ? "var(--glass-3)" : "var(--glass-1)",
                border: `1px solid ${range === r.id ? "var(--glass-stroke-2)" : "var(--glass-stroke)"}`,
                color: range === r.id ? "var(--text-1)" : "var(--text-3)",
              }}>
              {r.label}
            </button>
          ))}
        </div>

        {q.isLoading && <Pill>Loading…</Pill>}
        {q.error && <Pill tone="err">Could not load the usage.</Pill>}
        {s && s.totals.calls === 0 && <div style={{ fontSize: 13, color: "var(--text-3)" }}>No LLM calls in this period.</div>}

        {s && s.totals.calls > 0 && (
          <>
            <UsageTiles t={s.totals} />
            <div style={{ fontSize: 12, color: "var(--text-3)", marginTop: -10 }}>Cost {costNote(s.totals)}.</div>

            <Card title="Cost per day">
              <DayBars days={s.byDay} from={from} />
            </Card>

            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(520px, 1fr))", gap: 16 }}>
              <Card title="By model"><GroupTable groups={s.byModel} name="Model" mono /></Card>
              <Card title="By kind of work"><GroupTable groups={s.byKind} name="Work" label={kindLabel} /></Card>
            </div>

            <Card title={`Latest calls${s.recent.length < s.totals.calls ? ` (last ${s.recent.length})` : ""}`}>
              <CallTable calls={s.recent} showSubject />
            </Card>

            <Card title="Per day">
              <GroupTable groups={[...s.byDay].reverse()} name="Day" mono />
            </Card>
          </>
        )}
      </div>
    </ScreenShell>
  );
}

function Card({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="mz-glass" aria-label={title} style={{ padding: 16, borderRadius: 14, minWidth: 0 }}>
      <div style={{ fontSize: 13, fontWeight: 500, marginBottom: 10 }}>{title}</div>
      {children}
    </section>
  );
}

/** Totals per group: calls, tokens by kind, cost. */
function GroupTable({ groups, name, label, mono }: { groups: UsageGroup[]; name: string; label?: (k: string) => string; mono?: boolean }) {
  return (
    <div className="mz-scroll" style={{ overflowX: "auto" }}>
      <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 12 }}>
        <thead>
          <tr style={{ color: "var(--text-3)" }}>
            {[name, "Calls", "Input", "Cache read", "Cache write", "Output", "Cost"].map((h, i) => (
              <th key={h} style={{ padding: "6px 8px", fontWeight: 400, fontFamily: "var(--f-mono)", fontSize: 10.5, letterSpacing: "0.04em",
                textTransform: "uppercase", textAlign: i === 0 ? "left" : "right", whiteSpace: "nowrap" }}>{h}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {groups.map((g) => (
            <tr key={g.key} style={{ borderTop: "1px solid var(--glass-stroke)" }}>
              <td style={{ padding: "6px 8px", fontFamily: mono ? "var(--f-mono)" : undefined, whiteSpace: "nowrap" }}>{label ? label(g.key) : g.key || "—"}</td>
              {[g.totals.calls, g.totals.input, g.totals.cacheRead, g.totals.cacheWrite, g.totals.output].map((n, i) => (
                <td key={i} style={numCell}>{fmtInt(n)}</td>
              ))}
              <td style={numCell} title={fmtUsdExact(g.totals.costUsd)}>
                {fmtUsd(g.totals.costUsd)}
                {g.totals.unknownCostCalls > 0 && <span style={{ color: "var(--text-4)" }}> +?</span>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

const numCell = { padding: "6px 8px", textAlign: "right" as const, fontFamily: "var(--f-mono)", whiteSpace: "nowrap" as const };

const BAR = "#6ad19a"; // the LLM colour used in traces

/**
 * Cost per day as bars on a zero baseline, every day of the period shown (empty
 * days included). One series: no legend. Hover a bar for its exact numbers.
 */
function DayBars({ days, from }: { days: UsageGroup[]; from: Date | null }) {
  const [hover, setHover] = useState<number | null>(null);
  const series = useMemo(() => {
    const byKey = new Map(days.map((d) => [d.key, d]));
    // At least a week, so one day of data is a bar, not a slab.
    const weekAgo = new Date();
    weekAgo.setHours(0, 0, 0, 0);
    weekAgo.setDate(weekAgo.getDate() - 6);
    const earliest = from ?? (days.length ? new Date(days[0].key + "T00:00:00") : weekAgo);
    const first = earliest < weekAgo ? earliest : weekAgo;
    const out: { key: string; g: UsageGroup | null }[] = [];
    const d = new Date(first);
    d.setHours(0, 0, 0, 0);
    const end = new Date();
    for (let i = 0; i < 400 && d <= end; i++) {
      const k = dayKey(d);
      out.push({ key: k, g: byKey.get(k) ?? null });
      d.setDate(d.getDate() + 1);
    }
    return out;
  }, [days, from]);

  const max = Math.max(...series.map((s) => s.g?.totals.costUsd ?? 0), 0);
  const H = 140;
  const shown = hover !== null ? series[hover] : null;
  const labelEvery = Math.max(1, Math.ceil(series.length / 8));

  return (
    <div>
      <div style={{ height: 20, fontSize: 12, color: "var(--text-2)", fontFamily: "var(--f-mono)" }}>
        {shown
          ? `${shown.key} · ${fmtUsd(shown.g?.totals.costUsd ?? 0)} · ${fmtInt(shown.g?.totals.calls ?? 0)} calls · ${fmtTok(shown.g?.totals.total ?? 0)} tokens`
          : <span style={{ color: "var(--text-4)" }}>max {fmtUsd(max)} a day</span>}
      </div>
      <div style={{ display: "flex", alignItems: "flex-end", gap: 2, height: H, borderBottom: "1px solid var(--glass-stroke-2)" }}
        onMouseLeave={() => setHover(null)}>
        {series.map((s, i) => {
          const v = s.g?.totals.costUsd ?? 0;
          const h = max > 0 ? Math.max(v > 0 ? 3 : 0, Math.round((v / max) * (H - 8))) : 0;
          return (
            <div key={s.key} onMouseEnter={() => setHover(i)}
              title={`${s.key}: ${fmtUsdExact(v)}, ${fmtInt(s.g?.totals.calls ?? 0)} calls`}
              style={{ flex: 1, minWidth: 3, height: "100%", display: "flex", alignItems: "flex-end", justifyContent: "center", cursor: "default" }}>
              <div style={{ width: "100%", maxWidth: 40, height: h, background: BAR, opacity: hover === null || hover === i ? 0.9 : 0.45,
                borderRadius: "4px 4px 0 0" }} />
            </div>
          );
        })}
      </div>
      <div style={{ display: "flex", gap: 2, marginTop: 4 }}>
        {series.map((s, i) => (
          <div key={s.key} style={{ flex: 1, minWidth: 3, fontSize: 10, color: "var(--text-4)", fontFamily: "var(--f-mono)", whiteSpace: "nowrap", overflow: "visible", textAlign: series.length <= 31 ? "center" : "left" }}>
            {i % labelEvery === 0 ? s.key.slice(5) : ""}
          </div>
        ))}
      </div>
    </div>
  );
}
