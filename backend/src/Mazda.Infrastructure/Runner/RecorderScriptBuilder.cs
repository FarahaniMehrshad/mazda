using System.Reflection;
using System.Text;
using System.Text.Json;
using Mazda.Application.Common.Abstractions;

namespace Mazda.Infrastructure.Runner;

/// <summary>
/// Builds the long-lived Playwright spec behind "learn by watching".
///
/// The spec attaches to the user's tab exactly like every other harness
/// (shared <see cref="FunctionHarnessBuilder.AppendPreamble"/>: CDP connect +
/// pick-by-targetId), then:
///   • exposes a per-session binding the page script reports through,
///   • injects <c>Recorder/recorder-page.js</c> into the current document(s)
///     and every future one (addInitScript — survives full navigations),
///   • opens its own CDP session with the Network domain enabled and
///     correlates XHR/fetch to the last user action (3s window),
///   • follows main-frame navigations,
///   • stays alive (test timeout 0) until the runner drops the STOP file,
///     a cap is hit, or the tab closes — then flushes and disconnects.
///
/// Every event goes to stdout as one line: <c>__MZ_REC__ {json}</c>.
/// </summary>
internal static class RecorderScriptBuilder
{
    public const string LinePrefix = "__MZ_REC__ ";

    /// <summary>Recorder caps. A 30-minute session is plenty to demonstrate a component.</summary>
    public const int MaxEvents = 5_000;
    public const int MaxBytes = 20 * 1024 * 1024;
    public const int MaxMinutes = 30;

    /// <summary>
    /// playwright.config.ts for the recorder: no test timeout, no traces or
    /// screenshots (a 30-minute trace would be huge and is useless here), and
    /// a reporter that does not print steps — every evaluate/CDP call would
    /// otherwise show up as a line.
    /// </summary>
    public const string PlaywrightConfig = """
        import { defineConfig } from '@playwright/test';
        export default defineConfig({
          testDir: '.',
          timeout: 0,
          reporter: [['list']],
          use: {
            actionTimeout: 10_000,
            trace: 'off',
            screenshot: 'off',
            video: 'off',
          },
        });
        """;

    private static readonly Lazy<string> PageScript = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Mazda.Recorder.recorder-page.js")
            ?? throw new InvalidOperationException("Embedded recorder-page.js not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Build(RecordingStartSpec spec)
    {
        if (spec.Target.TargetId is null)
            throw new InvalidOperationException("Recording requires an attached tab (CDP targetId).");

        var shortId = spec.SessionId.ToString("N")[..12];
        var cfg = new
        {
            sessionId = spec.SessionId.ToString("N"),
            binding = "__mzRecEmit_" + shortId,
            allowedOrigins = spec.AllowedOrigins,
            maskAll = spec.MaskAllInputs,
            captureBodies = spec.CaptureBodies,
            maxEvents = MaxEvents,
            maxBytes = MaxBytes,
            maxMs = MaxMinutes * 60_000,
        };

        var sb = new StringBuilder();
        sb.AppendLine("import * as __mzFs from 'node:fs';");
        FunctionHarnessBuilder.AppendPreamble(sb, "mazda-recorder", spec.Target);
        sb.AppendLine($"  const CFG: any = {JsonSerializer.Serialize(cfg)};");
        sb.AppendLine($"  const PAGE_SRC: string = {JsonSerializer.Serialize(PageScript.Value)};");
        sb.Append(Body);
        sb.AppendLine("});");
        return sb.ToString();
    }

    // Body of the test function. Runs after the preamble has bound `browser`
    // and `page`. Plain string (not interpolated) so braces need no escaping.
    private const string Body = """
          test.setTimeout(0);
          const START = Date.now();
          const STOP_FILE = process.env.MAZDA_STOP_FILE || '';
          let seq = 0, bytes = 0;
          let stopRequested = false, endReason = 'stopped';
          let resolveStop: () => void = () => {};
          const stopPromise = new Promise<void>((r) => { resolveStop = r; });
          // Last user-action boundary reported by the page ('mark' events).
          let lastAction: { id: string; t: number } | null = null;

          const requestStop = (reason: string) => {
            if (stopRequested) return;
            stopRequested = true;
            endReason = reason;
            resolveStop();
          };

          const clip = (s: any, max: number) => {
            if (typeof s !== 'string') return undefined;
            return s.length > max ? s.slice(0, max) + '…[truncated]' : s;
          };

          const shrink = (data: any) => {
            if (!data || typeof data !== 'object') return data;
            const d: any = Array.isArray(data) ? data.slice(0, 10) : { ...data };
            delete d.reqBody; delete d.resBody;
            for (const k of Object.keys(d)) if (Array.isArray(d[k])) d[k] = d[k].slice(0, 10);
            return d;
          };

          const out = (kind: string, data: any, actionId: string | null = null, extra: any = {}) => {
            const evt: any = { seq: ++seq, t: Date.now() - START, kind, actionId, data, ...extra };
            let line = JSON.stringify(evt);
            if (line.length > 32000) {
              evt.data = shrink(data); evt.truncated = true;
              line = JSON.stringify(evt);
              if (line.length > 32000) { evt.data = { truncated: true }; line = JSON.stringify(evt); }
            }
            bytes += line.length;
            console.log('__MZ_REC__ ' + line);
            if (!stopRequested && (seq >= CFG.maxEvents || bytes >= CFG.maxBytes)) {
              requestStop('limit');
              out('limit', { reason: seq >= CFG.maxEvents ? 'events' : 'bytes', msg: 'Recording cap reached; stopping.' });
            }
          };

          const safeOrigin = (u: string) => { try { return new URL(u).origin; } catch { return null; } };
          const allowed = (u: string) => {
            const o = safeOrigin(u);
            if (!o) return false;
            return CFG.allowedOrigins.length === 0 || CFG.allowedOrigins.includes(o);
          };
          const correlate = () => {
            const now = Date.now();
            return lastAction && now - lastAction.t <= 3000 ? lastAction.id : null;
          };

          // ── Page → Node channel ──────────────────────────────────────
          await page.exposeBinding(CFG.binding, (_src: any, evt: any) => {
            if (!evt || typeof evt !== 'object') return;
            const kind = String(evt.kind || '');
            if (kind === 'mark') { lastAction = { id: String(evt.actionId), t: Date.now() }; return; }
            if (!['action', 'mutations', 'ambient'].includes(kind)) return;
            out(kind, evt.data ?? null, evt.actionId ?? null, { url: evt.url });
          });

          const pageCfg = { sessionId: CFG.sessionId, binding: CFG.binding, allowedOrigins: CFG.allowedOrigins, maskAll: CFG.maskAll };
          const installSrc = '(' + PAGE_SRC + ')(' + JSON.stringify(pageCfg) + ');';
          // Future documents (full navigations, reloads). Page-scoped, NOT
          // context-scoped: the default context holds every tab the user has.
          await page.addInitScript({ content: installSrc });
          // Current document(s).
          for (const f of page.frames()) {
            try { await f.evaluate(installSrc); }
            catch (e) { out('warn', { msg: 'Could not install recorder in a frame (cross-origin or detached).', url: safeOrigin(f.url()) }); }
          }

          // ── Network (own CDP session) ────────────────────────────────
          const cdp = await page.context().newCDPSession(page);
          const inflight = new Map<string, any>();
          cdp.on('Network.requestWillBeSent', (e: any) => {
            if (stopRequested) return;
            if (!['XHR', 'Fetch', 'Document'].includes(e.type)) return;
            if (!allowed(e.request.url)) return;
            if (inflight.size >= 500) return;
            inflight.set(e.requestId, {
              rid: e.requestId, method: e.request.method, url: e.request.url, type: e.type, start: Date.now(), actionId: correlate(),
              reqBody: CFG.captureBodies ? clip(e.request.postData, 2048) : undefined,
            });
          });
          cdp.on('Network.responseReceived', (e: any) => {
            const r = inflight.get(e.requestId);
            if (r) { r.status = e.response.status; r.mime = e.response.mimeType; }
          });
          cdp.on('Network.loadingFinished', async (e: any) => {
            const r = inflight.get(e.requestId);
            if (!r) return;
            inflight.delete(e.requestId);
            // (a request reported as pending earlier is reported again on completion, with its real duration)
            let resBody: string | undefined;
            if (CFG.captureBodies && r.type !== 'Document' && /json|text|xml|graphql/i.test(r.mime || '')) {
              try {
                const b: any = await cdp.send('Network.getResponseBody', { requestId: e.requestId });
                if (!b.base64Encoded) resBody = clip(b.body, 4096);
              } catch { /* body evicted or session gone */ }
            }
            out('network', { rid: r.rid, method: r.method, url: r.url, type: r.type, status: r.status, mime: r.mime,
                             ms: Date.now() - r.start, reqBody: r.reqBody, resBody }, r.actionId);
          });
          cdp.on('Network.loadingFailed', (e: any) => {
            const r = inflight.get(e.requestId);
            if (!r) return;
            inflight.delete(e.requestId);
            out('network', { rid: r.rid, method: r.method, url: r.url, type: r.type, status: 0, error: String(e.errorText || 'failed'),
                             ms: Date.now() - r.start, reqBody: r.reqBody }, r.actionId);
          });
          await cdp.send('Network.enable', { maxPostDataSize: 4096 });

          // ── Navigation / lifecycle ───────────────────────────────────
          page.on('framenavigated', (f: any) => {
            if (f !== page.mainFrame()) return;
            const u = f.url();
            if (!allowed(u)) { out('nav', { origin: safeOrigin(u), paused: true }, correlate()); return; }
            out('nav', { url: u }, correlate());
          });
          page.on('popup', (p: any) => out('warn', { msg: 'A popup / new tab opened; it is not recorded.', url: safeOrigin(p.url()) }));
          page.on('close', () => requestStop('tab-closed'));
          browser.on('disconnected', () => requestStop('disconnected'));

          out('ready', { url: page.url(), title: await page.title().catch(() => '') }, null, { url: page.url() });

          // Requests that hang (backend down, VPN dropped, browser connection
          // pool exhausted) never reach loadingFinished, so they'd silently
          // be missing from the recording. Report each once after 5s so the
          // live feed shows WHY the page looks stuck.
          const STUCK_MS = 5000;
          const poll = setInterval(() => {
            const nowMs = Date.now();
            for (const r of inflight.values()) {
              if (!r.warned && nowMs - r.start >= STUCK_MS) {
                r.warned = true;
                out('network', { rid: r.rid, method: r.method, url: r.url, type: r.type, pending: true, ms: nowMs - r.start,
                                 msg: 'No response yet — the server or the network may be stuck.' }, r.actionId);
              }
            }
            if (STOP_FILE && __mzFs.existsSync(STOP_FILE)) requestStop('stopped');
            if (!stopRequested && Date.now() - START >= CFG.maxMs) {
              requestStop('limit');
              out('limit', { reason: 'duration', msg: 'Maximum recording duration reached; stopping.' });
            }
          }, 500);

          await stopPromise;
          clearInterval(poll);

          // Flush page-side buffers (pending fill, open mutation window) while
          // the binding is still alive, then give in-flight bodies a moment.
          if (endReason !== 'tab-closed' && endReason !== 'disconnected') {
            for (const f of page.frames()) {
              try { await f.evaluate(() => { const w: any = window; if (w.__mzRec) w.__mzRec.stop(); }); } catch { }
            }
            await new Promise((r) => setTimeout(r, 400));
          }
          // Whatever is still in flight never completed during the recording.
          for (const r of inflight.values())
            out('network', { rid: r.rid, method: r.method, url: r.url, type: r.type, pending: true, neverCompleted: true,
                             ms: Date.now() - r.start }, r.actionId);
          inflight.clear();
          out('end', { reason: endReason });
          try { await cdp.detach(); } catch { }
          // connectOverCDP: close() only disconnects; the user's Chrome stays open.
          try { await browser.close(); } catch { }

        """;
}
