import { useEffect, useId, useMemo, useState, type CSSProperties, type ReactNode } from "react";
import { TopBar } from "@/shared/components/TopBar";
import { ScreenShell, BtnPrimary, BtnGhost, Pill } from "@/shared/components/Primitives";
import { ApiError } from "@/shared/api/client";
import {
  useLlmSettings,
  useSaveLlmSettings,
  useTestLlm,
  type LlmProvider,
  type LlmSettingsDto,
  type LlmTestResult,
  type UpdateLlmSettings,
} from "./api";

/** Form state: what will be saved. Empty fields fall back to .env. */
interface Form {
  provider: LlmProvider;
  openAiBaseUrl: string;
  openAiApiKey: string;
  clearOpenAiApiKey: boolean;
  openAiAnalyzerModel: string;
  openAiGeneratorModel: string;
  claudeBridgeUrl: string;
  claudeAnalyzerModel: string;
  claudeGeneratorModel: string;
  timeoutSeconds: string;
}

function toForm(d: LlmSettingsDto): Form {
  return {
    provider: d.provider || d.defaults.provider,
    openAiBaseUrl: d.openAiBaseUrl,
    openAiApiKey: "",
    clearOpenAiApiKey: false,
    openAiAnalyzerModel: d.openAiAnalyzerModel,
    openAiGeneratorModel: d.openAiGeneratorModel,
    claudeBridgeUrl: d.claudeBridgeUrl,
    claudeAnalyzerModel: d.claudeAnalyzerModel,
    claudeGeneratorModel: d.claudeGeneratorModel,
    timeoutSeconds: d.timeoutSeconds > 0 ? String(d.timeoutSeconds) : "",
  };
}

function toBody(f: Form): UpdateLlmSettings {
  const t = Number(f.timeoutSeconds);
  return {
    provider: f.provider,
    openAiBaseUrl: f.openAiBaseUrl.trim(),
    openAiApiKey: f.openAiApiKey.trim(),
    clearOpenAiApiKey: f.clearOpenAiApiKey,
    openAiAnalyzerModel: f.openAiAnalyzerModel.trim(),
    openAiGeneratorModel: f.openAiGeneratorModel.trim(),
    claudeBridgeUrl: f.claudeBridgeUrl.trim(),
    claudeAnalyzerModel: f.claudeAnalyzerModel.trim(),
    claudeGeneratorModel: f.claudeGeneratorModel.trim(),
    timeoutSeconds: Number.isFinite(t) && t > 0 ? Math.round(t) : 0,
  };
}

export function SettingsPage() {
  const q = useLlmSettings();
  const save = useSaveLlmSettings();
  const test = useTestLlm();
  const [form, setForm] = useState<Form | null>(null);
  const [saved, setSaved] = useState<Form | null>(null);
  const [testResult, setTestResult] = useState<LlmTestResult | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    if (q.data && !form) {
      const f = toForm(q.data);
      setForm(f);
      setSaved(f);
    }
  }, [q.data, form]);

  const dirty = useMemo(() => !!form && !!saved && JSON.stringify(form) !== JSON.stringify(saved), [form, saved]);
  const set = <K extends keyof Form>(k: K, v: Form[K]) => setForm((f) => (f ? { ...f, [k]: v } : f));

  const doSave = async (): Promise<boolean> => {
    if (!form) return false;
    setErr(null);
    try {
      const d = await save.mutateAsync(toBody(form));
      const f = toForm(d);
      setForm(f);
      setSaved(f);
      return true;
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : String(e));
      return false;
    }
  };

  const doTest = async () => {
    setTestResult(null);
    if (dirty && !(await doSave())) return;
    try {
      setTestResult(await test.mutateAsync());
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : String(e));
    }
  };

  const d = q.data;
  return (
    <ScreenShell>
      <TopBar />
      <div className="mz-scroll" style={{ flex: 1, padding: 28, display: "flex", flexDirection: "column", gap: 20, overflow: "auto" }}>
        <div>
          <div className="mz-eyebrow">Settings</div>
          <div style={{ fontFamily: "var(--f-display)", fontSize: 38, fontWeight: 500, letterSpacing: "-0.025em", marginTop: 4 }}>
            Language model
          </div>
          <div style={{ color: "var(--text-3)", marginTop: 4, fontSize: 13, maxWidth: 680 }}>
            Which model Mazda uses to analyse components, write functions, plan runs and fix round trips. These
            settings apply to the whole installation. An empty field uses the value from <code>.env</code>, shown
            greyed out.
          </div>
        </div>

        {q.isLoading && <Pill>Loading…</Pill>}
        {q.error && <Pill tone="err">Could not load the settings.</Pill>}

        {d && form && (
          <div className="mz-glass" style={{ padding: 22, borderRadius: 16, display: "flex", flexDirection: "column", gap: 18, maxWidth: 820 }}>
            <div role="radiogroup" aria-label="Provider" style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(260px, 1fr))", gap: 12 }}>
              <ProviderCard
                checked={form.provider === "claude-code"}
                onSelect={() => set("provider", "claude-code")}
                title="Claude Code on this computer"
                text="Uses your own Claude Code login (subscription). No API key: a small bridge on your computer runs claude for Mazda."
              />
              <ProviderCard
                checked={form.provider === "openai"}
                onSelect={() => set("provider", "openai")}
                title="API key (OpenAI-compatible)"
                text="Any OpenAI-compatible API: OpenAI, OpenRouter, Ollama, vLLM, DeepSeek, …"
              />
            </div>

            {form.provider === "claude-code" ? (
              <>
                <BridgeHelp tokenSet={d.defaults.claudeBridgeTokenSet} />
                <Field label="Bridge URL" hint="where Mazda reaches the bridge">
                  <input style={inputStyle} value={form.claudeBridgeUrl} placeholder={d.defaults.claudeBridgeUrl}
                    onChange={(e) => set("claudeBridgeUrl", e.target.value)} aria-label="Bridge URL" />
                </Field>
                <TwoModels
                  analyzer={form.claudeAnalyzerModel} generator={form.claudeGeneratorModel}
                  analyzerDefault={d.defaults.claudeAnalyzerModel} generatorDefault={d.defaults.claudeGeneratorModel}
                  hint="sonnet, opus, haiku or a full model id"
                  onAnalyzer={(v) => set("claudeAnalyzerModel", v)} onGenerator={(v) => set("claudeGeneratorModel", v)}
                />
              </>
            ) : (
              <>
                <Field label="API base URL">
                  <input style={inputStyle} value={form.openAiBaseUrl} placeholder={d.defaults.openAiBaseUrl}
                    onChange={(e) => set("openAiBaseUrl", e.target.value)} aria-label="API base URL" />
                </Field>
                <Field
                  label="API key"
                  hint={form.clearOpenAiApiKey ? "will use the key from .env"
                    : d.openAiApiKeySaved ? "a key is saved; type to replace it"
                    : d.defaults.openAiApiKeySet ? "using the key from .env" : "no key set"}
                >
                  <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
                    <input style={inputStyle} type="password" autoComplete="off" value={form.openAiApiKey}
                      placeholder={d.openAiApiKeySaved ? "••••••••  (saved)" : d.defaults.openAiApiKeySet ? "from .env" : "sk-…"}
                      onChange={(e) => setForm((f) => (f ? { ...f, openAiApiKey: e.target.value, clearOpenAiApiKey: false } : f))}
                      aria-label="API key" />
                    {d.openAiApiKeySaved && !form.clearOpenAiApiKey && (
                      <span style={{ flex: "none", whiteSpace: "nowrap" }}>
                        <BtnGhost onClick={() => setForm((f) => (f ? { ...f, openAiApiKey: "", clearOpenAiApiKey: true } : f))}>
                          Forget saved key
                        </BtnGhost>
                      </span>
                    )}
                  </div>
                </Field>
                <TwoModels
                  analyzer={form.openAiAnalyzerModel} generator={form.openAiGeneratorModel}
                  analyzerDefault={d.defaults.openAiAnalyzerModel} generatorDefault={d.defaults.openAiGeneratorModel}
                  hint="the provider's model names"
                  onAnalyzer={(v) => set("openAiAnalyzerModel", v)} onGenerator={(v) => set("openAiGeneratorModel", v)}
                />
              </>
            )}

            <Field label="Timeout per call (seconds)">
              <input style={{ ...inputStyle, maxWidth: 160 }} inputMode="numeric" value={form.timeoutSeconds}
                placeholder={String(d.defaults.timeoutSeconds)}
                onChange={(e) => set("timeoutSeconds", e.target.value.replace(/[^0-9]/g, ""))} aria-label="Timeout per call" />
            </Field>

            <div style={{ display: "flex", gap: 10, alignItems: "center", flexWrap: "wrap" }}>
              <BtnPrimary onClick={() => void doSave()} disabled={!dirty || save.isPending}>
                {save.isPending ? "Saving…" : dirty ? "Save" : "Saved"}
              </BtnPrimary>
              <BtnGhost onClick={() => void doTest()} disabled={test.isPending || save.isPending}>
                {test.isPending ? "Testing…" : dirty ? "Save and test" : "Test connection"}
              </BtnGhost>
              <span style={{ fontSize: 12, color: "var(--text-3)" }}>
                In use: {d.effective.provider === "claude-code" ? "Claude Code" : "API key"} · {d.effective.analyzerModel} / {d.effective.generatorModel}
              </span>
            </div>

            {!d.effective.ready && !dirty && <Pill tone="warn">{d.effective.notReadyReason}</Pill>}
            {err && <Pill tone="err">{err}</Pill>}
            {testResult && (
              testResult.ok
                ? <Pill tone="ok">{`Connected: ${testResult.model} answered “${(testResult.reply ?? "").slice(0, 40)}” in ${(testResult.ms / 1000).toFixed(1)} s`}</Pill>
                : <Pill tone="err">{testResult.error}</Pill>
            )}
          </div>
        )}
      </div>
    </ScreenShell>
  );
}

function ProviderCard({ checked, onSelect, title, text }: { checked: boolean; onSelect: () => void; title: string; text: string }) {
  const descId = useId();
  return (
    <button
      type="button"
      role="radio"
      aria-checked={checked}
      aria-label={title}
      aria-describedby={descId}
      onClick={onSelect}
      style={{
        textAlign: "left",
        padding: 14,
        borderRadius: 12,
        cursor: "pointer",
        background: checked ? "rgba(234,88,12,0.12)" : "var(--glass-1)",
        border: `1px solid ${checked ? "rgba(234,88,12,0.6)" : "var(--glass-stroke-2)"}`,
        color: "var(--text-1)",
        display: "flex",
        gap: 10,
        alignItems: "flex-start",
      }}
    >
      <span
        aria-hidden
        style={{
          marginTop: 2, width: 14, height: 14, borderRadius: 999, flex: "none",
          border: `2px solid ${checked ? "#ea580c" : "var(--text-4)"}`,
          background: checked ? "radial-gradient(#ea580c 0 40%, transparent 45%)" : "transparent",
        }}
      />
      <span style={{ display: "flex", flexDirection: "column", gap: 4 }}>
        <span style={{ fontSize: 14, fontWeight: 500 }}>{title}</span>
        <span id={descId} style={{ fontSize: 12, color: "var(--text-3)", lineHeight: 1.45 }}>{text}</span>
      </span>
    </button>
  );
}

function BridgeHelp({ tokenSet }: { tokenSet: boolean }) {
  return (
    <div style={{ fontSize: 12.5, color: "var(--text-2)", lineHeight: 1.6, background: "var(--glass-1)", border: "1px solid var(--glass-stroke)", borderRadius: 10, padding: "10px 14px" }}>
      Start the bridge on your computer and keep it running:
      <pre style={codeStyle}>node tools/claude-bridge/bridge.mjs</pre>
      {tokenSet ? (
        <span style={{ color: "var(--text-3)" }}>The bridge token is set in <code>.env</code>.</span>
      ) : (
        <>
          <span style={{ color: "#f0a89e" }}>The bridge token is not set.</span> Create it in the repository folder, then restart the API:
          <pre style={codeStyle}>{`echo "CLAUDE_BRIDGE_TOKEN=$(node -e "console.log(require('crypto').randomBytes(32).toString('hex'))")" >> .env
docker compose up -d api`}</pre>
        </>
      )}
    </div>
  );
}

function TwoModels(p: {
  analyzer: string; generator: string; analyzerDefault: string; generatorDefault: string; hint: string;
  onAnalyzer: (v: string) => void; onGenerator: (v: string) => void;
}) {
  return (
    <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(240px, 1fr))", gap: 12, alignItems: "end" }}>
      <Field label="Analyzer model" hint="analysis">
        <input style={inputStyle} value={p.analyzer} placeholder={p.analyzerDefault} onChange={(e) => p.onAnalyzer(e.target.value)} aria-label="Analyzer model" />
      </Field>
      <Field label="Generator model" hint="code, plans, fixes">
        <input style={inputStyle} value={p.generator} placeholder={p.generatorDefault} onChange={(e) => p.onGenerator(e.target.value)} aria-label="Generator model" />
      </Field>
      <div style={{ gridColumn: "1 / -1", fontSize: 11.5, color: "var(--text-4)", marginTop: -6 }}>Model names: {p.hint}.</div>
    </div>
  );
}

function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", gap: 8 }}>
        <span style={{ fontFamily: "var(--f-mono)", fontSize: 10.5, letterSpacing: "0.06em", color: "var(--text-3)", textTransform: "uppercase", whiteSpace: "nowrap" }}>
          {label}
        </span>
        {hint && <span style={{ fontFamily: "var(--f-mono)", fontSize: 10.5, color: "var(--text-4)", textAlign: "right" }}>{hint}</span>}
      </div>
      {children}
    </div>
  );
}

const inputStyle: CSSProperties = {
  width: "100%",
  padding: "10px 12px",
  background: "var(--glass-1)",
  border: "1px solid var(--glass-stroke-2)",
  borderRadius: 8,
  color: "var(--text-1)",
  fontFamily: "var(--f-sans)",
  fontSize: 13,
  outline: "none",
};

const codeStyle: CSSProperties = {
  margin: "6px 0",
  padding: "8px 10px",
  background: "rgba(0,0,0,0.35)",
  borderRadius: 8,
  fontFamily: "var(--f-mono)",
  fontSize: 12,
  whiteSpace: "pre-wrap",
  wordBreak: "break-all",
  color: "var(--text-1)",
};
