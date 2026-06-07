using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Mazda.Application.Common.Abstractions;
using Microsoft.Extensions.Logging;

namespace Mazda.Infrastructure.Runner;

/// <summary>
/// Spawns "npx playwright test" against a freshly-materialized .spec.ts file
/// in a per-run tmp directory. Streams stdout + stderr into IExecutionRegistry
/// line-by-line, returning the terminal result when the process exits.
///
/// Auto-rewrites loopback hosts (127.0.0.1 / localhost) to host.docker.internal
/// so CDP-attach scripts written from the user's perspective still reach a
/// browser running on the Docker host.
/// </summary>
public sealed class LocalProcessScriptRunner(
    IExecutionRegistry registry,
    ILogger<LocalProcessScriptRunner> log) : IScriptRunner
{
    private static readonly Regex LoopbackHost = new(
        @"\b(?:127\.0\.0\.1|localhost)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string PlaywrightConfig = """
        import { defineConfig } from '@playwright/test';
        export default defineConfig({
          testDir: '.',
          timeout: 60_000,
          // Print each step (await page.click(...), expect(...), etc.) as it runs so
          // a hang shows the LAST step it was stuck on rather than just "timeout".
          reporter: [['list', { printSteps: true }]],
          use: {
            // Per-action timeout (default is 30s = half the test budget). 10s is short
            // enough to fail fast on a stuck locator and surface a precise stack trace
            // pointing at the offending line.
            actionTimeout: 10_000,
            navigationTimeout: 15_000,
            trace: 'retain-on-failure',
            screenshot: 'only-on-failure',
            video: 'off',
          },
        });
        """;

    private static readonly string PackageJson = """
        {
          "name": "mazda-run",
          "version": "0.0.0",
          "private": true,
          "type": "module"
        }
        """;

    public async Task<RunnerCompletion> RunAsync(Guid generationId, string script, CancellationToken ct = default)
    {
        var workdirRoot = Environment.GetEnvironmentVariable("MAZDA_RUNNER_WORKDIR") ?? "/var/mazda/runs";
        var runDir = Path.Combine(workdirRoot, generationId.ToString("N"));
        Directory.CreateDirectory(runDir);

        var rewritten = RewriteHosts(script);
        await File.WriteAllTextAsync(Path.Combine(runDir, "mazda.spec.ts"), rewritten, ct);
        await File.WriteAllTextAsync(Path.Combine(runDir, "playwright.config.ts"), PlaywrightConfig, ct);
        await File.WriteAllTextAsync(Path.Combine(runDir, "package.json"), PackageJson, ct);

        // Node ESM resolution doesn't look at the global node_modules. Symlink the
        // run dir's node_modules to the global one so `@playwright/test` resolves.
        var localNodeModules = Path.Combine(runDir, "node_modules");
        var globalNodeModules = Environment.GetEnvironmentVariable("MAZDA_GLOBAL_NODE_MODULES")
                                ?? "/usr/lib/node_modules";
        if (!Directory.Exists(localNodeModules) && !File.Exists(localNodeModules))
        {
            try { File.CreateSymbolicLink(localNodeModules, globalNodeModules); }
            catch (Exception ex) { log.LogWarning(ex, "Could not symlink node_modules → {Global}", globalNodeModules); }
        }

        registry.Append(generationId, $"$ cd {runDir}");
        registry.Append(generationId, "$ npx playwright test mazda.spec.ts --reporter=list");

        var fullLog = new StringBuilder();
        void Emit(string line)
        {
            // Strip ANSI escape sequences before storing/streaming. Two reasons:
            //   1. NO_COLOR + FORCE_COLOR env hints do NOT actually scrub
            //      everything — Jest's expect() emits inverse-video markers
            //      (\x1b[7m … \x1b[27m) to highlight diffing characters
            //      regardless of color env vars; Playwright's list reporter
            //      occasionally slips through codes too.
            //   2. The browser sees these through SSE → JSON.stringify → <pre>
            //      and renders the ESC byte as nothing, leaving garbage like
            //      `[33m`, `[7m`, `[27m` interleaved with the actual text.
            var stripped = StripAnsi(line);
            registry.Append(generationId, stripped);
            fullLog.AppendLine(stripped);
        }

        var psi = new ProcessStartInfo
        {
            FileName = "npx",
            WorkingDirectory = runDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Only NO_COLOR. Setting FORCE_COLOR to *any* value (even "0")
            // makes Node ignore NO_COLOR and emit a warning to stderr —
            // exactly the warning the user saw in the broken log.
            Environment = { ["NO_COLOR"] = "1" },
        };
        psi.ArgumentList.Add("playwright");
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add("mazda.spec.ts");
        // No --reporter flag here: the CLI form `list` would override the config's
        // reporter array and drop `printSteps: true`. Let playwright.config.ts win.

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Emit(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Emit(e.Data); };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            var msg = $"runner: failed to start npx — {ex.Message}";
            Emit(msg);
            // NOTE: do NOT call registry.Complete here. The handler will call it
            // AFTER the terminal status has been persisted, so the SSE end event
            // only fires once the DB reflects the final state.
            return new RunnerCompletion(RunnerExitKind.Errored, -1, fullLog.ToString());
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            Emit("runner: cancelled by user");
            return new RunnerCompletion(RunnerExitKind.Cancelled, -1, fullLog.ToString());
        }

        var kind = proc.ExitCode switch
        {
            0 => RunnerExitKind.Passed,
            1 => RunnerExitKind.FailedTest, // Playwright returns 1 on test failure.
            _ => RunnerExitKind.Errored,
        };

        // Point the user at any trace zips Playwright left behind on failure.
        // `trace: 'retain-on-failure'` writes to <runDir>/test-results/.../trace.zip.
        if (kind != RunnerExitKind.Passed)
        {
            var resultsDir = Path.Combine(runDir, "test-results");
            if (Directory.Exists(resultsDir))
            {
                foreach (var tracePath in Directory.EnumerateFiles(resultsDir, "trace.zip", SearchOption.AllDirectories))
                {
                    Emit($"runner: trace saved → {tracePath}");
                    Emit("runner: open with `npx playwright show-trace " + tracePath + "` (run on host)");
                }
            }
        }
        Emit($"runner: exit {proc.ExitCode} ({kind.ToString().ToLowerInvariant()})");

        // Caller (StartExecutionHandler) is responsible for calling
        // registry.Complete AFTER it has persisted the terminal status so the
        // SSE end event arrives at the client only once the DB reflects it.
        return new RunnerCompletion(kind, proc.ExitCode, fullLog.ToString());
    }

    public async Task<RunnerCompletion> RunOnceAsync(string script, CancellationToken ct = default)
    {
        // Isolated run dir per call — analyze-state extraction runs concurrent
        // with the wizard's main run flow potentially, so don't collide.
        var workdirRoot = Environment.GetEnvironmentVariable("MAZDA_RUNNER_WORKDIR") ?? "/var/mazda/runs";
        var runDir = Path.Combine(workdirRoot, "once-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDir);

        var rewritten = RewriteHosts(script);
        await File.WriteAllTextAsync(Path.Combine(runDir, "mazda.spec.ts"), rewritten, ct);
        await File.WriteAllTextAsync(Path.Combine(runDir, "playwright.config.ts"), PlaywrightConfig, ct);
        await File.WriteAllTextAsync(Path.Combine(runDir, "package.json"), PackageJson, ct);

        var localNodeModules = Path.Combine(runDir, "node_modules");
        var globalNodeModules = Environment.GetEnvironmentVariable("MAZDA_GLOBAL_NODE_MODULES")
                                ?? "/usr/lib/node_modules";
        if (!Directory.Exists(localNodeModules) && !File.Exists(localNodeModules))
        {
            try { File.CreateSymbolicLink(localNodeModules, globalNodeModules); }
            catch (Exception ex) { log.LogWarning(ex, "Could not symlink node_modules → {Global}", globalNodeModules); }
        }

        var fullLog = new StringBuilder();
        void Capture(string line)
        {
            // No registry interaction — just buffer for the caller. We still
            // strip ANSI so the captured log is clean for downstream parsing.
            fullLog.AppendLine(StripAnsi(line));
        }

        var psi = new ProcessStartInfo
        {
            FileName = "npx",
            WorkingDirectory = runDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { ["NO_COLOR"] = "1" },
        };
        psi.ArgumentList.Add("playwright");
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add("mazda.spec.ts");

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Capture(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Capture(e.Data); };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            Capture($"runner: failed to start npx — {ex.Message}");
            return new RunnerCompletion(RunnerExitKind.Errored, -1, fullLog.ToString());
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            Capture("runner: cancelled by user");
            return new RunnerCompletion(RunnerExitKind.Cancelled, -1, fullLog.ToString());
        }

        var kind = proc.ExitCode switch
        {
            0 => RunnerExitKind.Passed,
            1 => RunnerExitKind.FailedTest,
            _ => RunnerExitKind.Errored,
        };
        Capture($"runner: exit {proc.ExitCode} ({kind.ToString().ToLowerInvariant()})");
        return new RunnerCompletion(kind, proc.ExitCode, fullLog.ToString());
    }

    // Injected helper. Solves the CDP-via-host.docker.internal triple-bind:
    //   1. Chrome v111+ rejects /json/version requests whose Host header isn't
    //      localhost/127.0.0.1. We send Host: localhost:<port> on the probe.
    //   2. Chrome's response advertises the WS URL using the Host we sent —
    //      i.e. ws://localhost:<port>/devtools/... — which from inside the
    //      container resolves to the container's own loopback, where nothing
    //      is listening. We rewrite the ws URL host to host.docker.internal.
    //   3. Playwright's connectOverCDP accepts a ws:// URL directly, so we
    //      hand it the rewritten URL and skip Playwright's own HTTP probe.
    private const string CdpHelperBlock = """

// === Mazda runner: CDP-via-host.docker.internal helper (auto-injected) ===
import * as __mzHttp from 'node:http';
async function __mzConnectViaHost(c: any, host: string, port: number): Promise<any> {
  const payload = await new Promise<string>((resolve, reject) => {
    const req = __mzHttp.request({
      host, port, path: '/json/version', method: 'GET',
      headers: { Host: 'localhost:' + port },
    }, (res: any) => {
      let body = '';
      res.on('data', (chunk: any) => { body += chunk.toString(); });
      res.on('end', () => resolve(body));
    });
    req.on('error', reject);
    req.end();
  });
  const json = JSON.parse(payload);
  const wsUrl = String(json.webSocketDebuggerUrl)
    .replace(/^ws:\/\/[^/:]+(:\d+)?/, 'ws://' + host + ':' + port);
  // Chrome's WebSocket endpoint applies the same Host-header check as
  // /json/version. Spoof Host: localhost:<port> on the upgrade too.
  return await c.connectOverCDP({
    endpointURL: wsUrl,
    headers: { Host: 'localhost:' + port },
  });
}
// === end auto-injected ===

""";

    // Pointer-event drag helper. Use for drag-and-drop on UIs whose drag is
    // wired via pointer/mouse events (Angular CDK, @dnd-kit, react-dnd,
    // react-beautiful-dnd, custom mousedown/move/up handlers) — anything where
    // the source element does NOT carry HTML5 `draggable="true"`.
    //   - Moves to the source's center, presses, nudges a few pixels (to cross
    //     the library's minimum-drag threshold), then smoothly moves to the
    //     target's center in 25 steps so the library's pointermove handler
    //     fires along the path and runs its hit-testing / drop-zone logic.
    //   - Releases at the target. Awaits one rAF before returning so any
    //     framework re-render commits before the test re-measures positions.
    private const string PointerDragHelperBlock = """

// === Mazda runner: pointer-event drag helper (auto-injected) ===
// Selectors that frameworks commonly use to mark "this child is the drag handle
// — pointerdown must happen here for the drag to start". When the caller passes
// in a whole card and the card contains exactly one of these, the helper uses
// the handle's center instead of the card's center. Saves the test from having
// to know the handle selector when the card has only one obvious one.
const __mzDragHandleSelector =
  '[cdkDragHandle], [cdk-drag-handle], .cdk-drag-handle, ' +
  '[data-drag-handle], [data-rbd-drag-handle-draggable-id], ' +
  '.draggable-area, .drag-handle, .handle';
async function __mzResolveDragSource(source: any): Promise<any> {
  // Try to find a single, obvious drag handle inside the source. If exactly
  // one match exists, use it; otherwise, fall back to the source itself.
  // (Multiple matches are ambiguous — caller should pass the handle directly.)
  try {
    const handle = source.locator(__mzDragHandleSelector);
    if ((await handle.count()) === 1) return handle.first();
  } catch { /* ignore: source may not be a Locator with .locator() */ }
  return source;
}
async function __mzPointerDrag(page: any, source: any, target: any): Promise<void> {
  const src = await __mzResolveDragSource(source);
  const a = await src.boundingBox();
  const b = await target.boundingBox();
  if (!a || !b) {
    throw new Error('__mzPointerDrag: source or target has no bounding box (off-screen or not visible?)');
  }
  const sx = a.x + a.width / 2;
  const sy = a.y + a.height / 2;
  const tx = b.x + b.width / 2;
  const ty = b.y + b.height / 2;
  await page.mouse.move(sx, sy);
  await page.mouse.down();
  // Cross the library's minimum-drag threshold with a small initial move.
  // CDK / @dnd-kit ignore a single jump-to-target — they only start tracking
  // once the pointer has moved a few pixels.
  await page.mouse.move(sx + 8, sy + 8, { steps: 5 });
  // Smooth path so the library's pointermove handler runs continuously and
  // emits its over/enter/leave callbacks at intermediate drop zones.
  await page.mouse.move(tx, ty, { steps: 25 });
  await page.mouse.up();
  // Yield once so any synchronous framework re-render commits before the
  // caller re-measures DOM positions.
  await page.evaluate(() => new Promise(r => requestAnimationFrame(() => r(null))));
}
// === end auto-injected ===

""";

    // True swap of two items at positions i and j in a sortable list, where the
    // underlying library treats a drop as INSERT-AT-POSITION (the case for most
    // sortable libraries: CDK cdkDropList, @dnd-kit useSortable, react-dnd
    // sortable, react-beautiful-dnd, etc.). A single drag from i to j in those
    // libraries is a REORDER, not a swap: every item between i and j shifts by
    // one. To exchange only items-at-i and items-at-j and leave everything else
    // in place, two drags are needed (one if they are adjacent).
    //
    // Protocol for non-adjacent (lo = min(i,j), hi = max(i,j)):
    //   step 1: drag from hi to lo   → originally-at-hi sits at lo,
    //                                   originally-at-lo sits at lo+1.
    //   step 2: drag from lo+1 to hi → originally-at-lo sits at hi.
    // Adjacent (hi == lo+1): a single insert-at-position IS the swap.
    // `locator.click()` runs Playwright's actionability gauntlet — visible,
    // attached, stable, enabled, not obscured, hit-tested at the resolved
    // coordinates. Those checks save countless flaky tests on the happy path
    // but HARD-FAIL on elements modern UIs are full of:
    //   • visibility:hidden / opacity:0 / display:none toggle-on-hover icons,
    //   • pointer-events:none ancestors that route clicks elsewhere,
    //   • absolute-positioned overlays that catch the click,
    //   • the screen-reader-only <input type="file"> behind a styled uploader,
    //   • the native <input type="checkbox"> behind a custom checkbox.
    //
    // The LLM that generated the test can't predict at gen time which buttons
    // will be hidden at runtime — the Signature doesn't reliably indicate it.
    // So every UI click in the generated test goes through __mzClick, which:
    //   1. Tries `.click()` first (short 2s timeout). On success the normal
    //      Playwright safety net applies and we return.
    //   2. On any failure — actionability timeout, intercepted-by-overlay,
    //      pointer-events:none, etc. — falls back to dispatchEvent('click').
    //      That bypasses BOTH hit-testing AND CSS visibility, dispatching a
    //      real synthetic click event directly on the element. Angular
    //      (click), React onClick, Vue @click, plain DOM listeners all
    //      observe and run the same as for a real click.
    private const string ClickHelperBlock = """

// === Mazda runner: robust-click helper (auto-injected) ===
async function __mzClick(locator: any): Promise<void> {
  try {
    await locator.click({ timeout: 2000 });
  } catch {
    // Fall back to a synthetic click. dispatchEvent doesn't care about
    // visibility, pointer-events, overlays, or animation stability — it
    // fires the event directly on the underlying element. The framework's
    // bound click handler runs the same as for a real click.
    await locator.dispatchEvent('click');
  }
}
// === end auto-injected ===

""";

    private const string PointerSwapHelperBlock = """

// === Mazda runner: sortable-list swap helper (auto-injected) ===
async function __mzPointerSwap(page: any, items: any, i: number, j: number): Promise<void> {
  if (i === j) return;
  const lo = Math.min(i, j);
  const hi = Math.max(i, j);
  if (hi === lo + 1) {
    // Adjacent — a single insert-at-position IS the swap.
    await __mzPointerDrag(page, items.nth(hi), items.nth(lo));
    return;
  }
  // Non-adjacent — two-step protocol. After step 1 the originally-at-lo item
  // sits at lo+1; step 2 puts it where originally-at-hi used to be.
  await __mzPointerDrag(page, items.nth(hi), items.nth(lo));
  await __mzPointerDrag(page, items.nth(lo + 1), items.nth(hi));
}
// === end auto-injected ===

""";

    // Framework-agnostic select / combobox driver. Solves the recurring bug
    // where a manually-written three-step pattern —
    //     await __mzClick(trigger);
    //     await page.keyboard.type(text);
    //     await __mzClick(page.getByText(text, { exact: true }).first());
    // — resolves the trigger's currently-selected label, a recently-selected
    // pill, or unrelated copy elsewhere on the page INSTEAD of the option
    // inside the popup. `getByText` searches the whole document; the popup is
    // just one of many places the same string might be rendered.
    //
    // The helper scopes the option search to popup containers that became
    // VISIBLE only AFTER the trigger was clicked. We don't hard-code library
    // selectors (.cdk-overlay-container / .ant-select-dropdown / etc.) — the
    // strategy works for portaled (Angular CDK overlay, Radix portal, Vue
    // Teleport, MUI Popper at document root) and inline-mounted dropdowns
    // alike, because both kinds use position:absolute|fixed for popper
    // placement.
    //
    // Within the resolved popup the option is chosen by, in order:
    //   (a) [role="option" | "menuitem" | "treeitem"] with exact text — the
    //       most reliable signal across libraries that bother with ARIA.
    //   (b) The smallest sub-tree whose trimmed textContent equals the
    //       target — handles bare <div>/<li> custom dropdowns without ARIA.
    //   (c) The smallest sub-tree that contains the target text (case-
    //       insensitive) — last-resort fallback for highlight-match markup
    //       like "<b>Q</b>A mssql" where the option's leaf node isn't an
    //       exact textContent match.
    //
    // The click goes through __mzClick so dispatchEvent fallback still
    // applies if Playwright actionability rejects a normal click (overlay
    // sibling intercepts, animation mid-transition, etc.).
    private const string SelectOptionHelperBlock = """

// === Mazda runner: select-option helper (auto-injected) ===
async function __mzSelectOption(page: any, trigger: any, text: string): Promise<void> {
  const tag = '__mz_opt_' + Math.random().toString(36).slice(2, 10);

  // STEP 1 — pre-baseline. Mark every currently-VISIBLE floating element
  // (position:fixed/absolute) so we can later detect newcomers. We mark
  // ONLY visible ones: a popup that was already in the DOM but kept
  // display:none / opacity:0 / 0×0 stays un-marked, so when it toggles
  // visible on open it shows up as "new" alongside fully-portaled popups
  // that mount on open. We clear any stale markers from a prior failed
  // call before re-marking, so a half-cleaned baseline doesn't poison
  // this run.
  await page.evaluate(() => {
    document.querySelectorAll('[data-mz-popup-pre]').forEach((el: Element) =>
      el.removeAttribute('data-mz-popup-pre'));
    const all = document.body.querySelectorAll('*');
    for (let i = 0; i < all.length; i++) {
      const el = all[i] as HTMLElement;
      const cs = getComputedStyle(el);
      if (cs.position !== 'fixed' && cs.position !== 'absolute') continue;
      if (cs.display === 'none' || cs.visibility === 'hidden') continue;
      if (parseFloat(cs.opacity) === 0) continue;
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) continue;
      el.setAttribute('data-mz-popup-pre', '1');
    }
  });

  // STEP 2 — open the dropdown via the robust-click helper.
  await __mzClick(trigger);

  // STEP 3 — type the option text. Most custom comboboxes auto-focus an
  // internal search input on open, so page.keyboard.type lands on the
  // filter. Skipped when the caller passes an empty string (rare: pick
  // by text without typing first).
  if (text) await page.keyboard.type(text);

  // STEP 4 — locate the option INSIDE a newly-visible floating container,
  // then tag it with a unique attribute so the click in step 5 can address
  // it via a normal Locator (preserving __mzClick's safety net).
  await page.waitForFunction(
    (args: { target: string; tag: string }) => {
      const target = args.target;
      const tag = args.tag;
      const lower = target.toLowerCase();
      const isVisible = (el: Element) => {
        const cs = getComputedStyle(el);
        if (cs.display === 'none' || cs.visibility === 'hidden') return false;
        if (parseFloat(cs.opacity) === 0) return false;
        const r = el.getBoundingClientRect();
        return r.width > 0 && r.height > 0;
      };

      // Newly-visible floating containers — the popup(s) that opened.
      const containers: Element[] = [];
      const all = document.body.querySelectorAll('*');
      for (let i = 0; i < all.length; i++) {
        const el = all[i];
        if (el.hasAttribute('data-mz-popup-pre')) continue;
        const cs = getComputedStyle(el);
        if (cs.position !== 'fixed' && cs.position !== 'absolute') continue;
        if (!isVisible(el)) continue;
        containers.push(el);
      }
      if (!containers.length) return false;

      // Gather option candidates across containers, classified by match
      // quality so we can prefer ARIA-exact > text-exact > text-contains.
      const exactMatches: Element[] = [];
      const partialMatches: Element[] = [];
      const ariaMatches: Element[] = [];
      for (const c of containers) {
        const within = c.querySelectorAll('*');
        for (let i = 0; i < within.length; i++) {
          const el = within[i];
          if (!isVisible(el)) continue;
          const t = (el.textContent || '').trim();
          if (!t) continue;
          const role = el.getAttribute('role') || '';
          const isAria = role === 'option' || role === 'menuitem' || role === 'treeitem';
          if (t === target || t.toLowerCase() === lower) {
            exactMatches.push(el);
            if (isAria) ariaMatches.push(el);
          } else if (target && t.toLowerCase().includes(lower)) {
            partialMatches.push(el);
          }
        }
      }

      // Pick the smallest sub-tree in the chosen set — i.e. the candidate
      // that has no descendant also in the set. That's the leaf-most
      // option, which is the most reliable click target.
      const smallest = (arr: Element[]) =>
        arr.find(el => !arr.some(o => o !== el && el.contains(o))) || arr[0];

      let best: Element | null = null;
      if (ariaMatches.length) best = smallest(ariaMatches);
      else if (exactMatches.length) best = smallest(exactMatches);
      else if (partialMatches.length) best = smallest(partialMatches);

      if (!best) return false;
      best.setAttribute('data-mz-tag', tag);
      return true;
    },
    { target: text, tag },
    { timeout: 5000, polling: 100 }
  );

  // STEP 5 — click the tagged option via the robust-click helper.
  await __mzClick(page.locator('[data-mz-tag="' + tag + '"]'));

  // STEP 6 — clean up markers so subsequent calls start fresh.
  await page.evaluate((tag: string) => {
    document.querySelectorAll('[data-mz-popup-pre]').forEach((el: Element) =>
      el.removeAttribute('data-mz-popup-pre'));
    document.querySelectorAll('[data-mz-tag="' + tag + '"]').forEach((el: Element) =>
      el.removeAttribute('data-mz-tag'));
  }, tag);
}
// === end auto-injected ===

""";

    // ANSI escape stripper. Covers the three sequence families Playwright /
    // Jest / Node ever actually emit:
    //   - CSI (most common): ESC '[' <params> <intermediates> <final 0x40-0x7E>
    //     — matches SGR colour codes ([33m, [39m, [7m, [27m, ...),
    //       cursor moves, erase-line, etc.
    //   - OSC (set-window-title, hyperlinks): ESC ']' <body> (BEL | ESC '\')
    //   - Lone two-byte ESC sequences (rare ESC + single char forms).
    //
    // Also strips bare CSI bodies (e.g. "[27m") that occasionally appear
    // without their leading ESC byte — when something upstream eats the ESC
    // before we see the line.
    private static readonly Regex AnsiSequences = new(
        "(?:" +
        "\\u001B\\[[\\x30-\\x3F]*[\\x20-\\x2F]*[\\x40-\\x7E]" +
        "|\\u001B\\][^\\x07\\u001B]*(?:\\x07|\\u001B\\\\)" +
        "|\\u001B[@-Z\\\\\\-_]" +
        ")" +
        "|\\[\\d+(?:;\\d+)*m",
        RegexOptions.Compiled);

    private static string StripAnsi(string s) =>
        string.IsNullOrEmpty(s) ? s : AnsiSequences.Replace(s, string.Empty);


    private static string RewriteHosts(string source)
    {
        // NOTE: we do NOT blanket-rewrite localhost → host.docker.internal anymore.
        // That mangled unrelated URLs (e.g. a TARGET_PREFIX the script matches
        // against the page's actual url). Only connectOverCDP calls get rewritten;
        // everything else (page.goto, prefix matchers, comments) stays verbatim.

        var rewritten = source;
        var changed = false;

        // 1 — string form: chromium.connectOverCDP('http://(localhost|127.0.0.1|host.docker.internal):9222')
        rewritten = Regex.Replace(
            rewritten,
            @"(\w+)\.connectOverCDP\s*\(\s*(['""`])https?://(?:localhost|127\.0\.0\.1|host\.docker\.internal):(\d+)[^'""`]*\2\s*\)",
            m =>
            {
                changed = true;
                var receiver = m.Groups[1].Value;
                var port = m.Groups[3].Value;
                return $"__mzConnectViaHost({receiver}, 'host.docker.internal', {port})";
            },
            RegexOptions.IgnoreCase);

        // 2 — object form: chromium.connectOverCDP({ endpointURL: '...', ... }).
        //   Uses a one-level brace-balancer to handle nested headers: { Host: ... }.
        rewritten = Regex.Replace(
            rewritten,
            @"(\w+)\.connectOverCDP\s*\(\s*\{(?:[^{}]|\{[^{}]*\})*\bendpointURL\s*:\s*['""`]https?://(?:localhost|127\.0\.0\.1|host\.docker\.internal):(\d+)[^'""`]*['""`](?:[^{}]|\{[^{}]*\})*\}\s*\)",
            m =>
            {
                changed = true;
                var receiver = m.Groups[1].Value;
                var port = m.Groups[2].Value;
                return $"__mzConnectViaHost({receiver}, 'host.docker.internal', {port})";
            },
            RegexOptions.IgnoreCase);

        // 3 — figure out which helpers the test actually uses, and inject only those.
        //   Swap depends on Drag, so requesting Swap implicitly requests Drag too.
        //   SelectOption depends on Click, so requesting Select implicitly
        //   requests Click too.
        var needsCdpHelper = changed;
        var needsSwapHelper = Regex.IsMatch(rewritten, @"\b__mzPointerSwap\s*\(");
        var needsDragHelper = needsSwapHelper
            || Regex.IsMatch(rewritten, @"\b__mzPointerDrag\s*\(");
        var needsSelectHelper = Regex.IsMatch(rewritten, @"\b__mzSelectOption\s*\(");
        var needsClickHelper = needsSelectHelper
            || Regex.IsMatch(rewritten, @"\b__mzClick\s*\(");

        if (!needsCdpHelper && !needsDragHelper && !needsSwapHelper && !needsClickHelper && !needsSelectHelper)
            return rewritten;

        // Inject after the last top-level import statement so type imports stay
        // valid and our helpers can reference imported types if they ever need to.
        var importMatches = Regex.Matches(rewritten, @"^[ \t]*import\s.*$", RegexOptions.Multiline);
        int insertPos = 0;
        if (importMatches.Count > 0)
        {
            var last = importMatches[importMatches.Count - 1];
            insertPos = last.Index + last.Length;
        }

        var injection = string.Empty;
        if (needsCdpHelper) injection += CdpHelperBlock;
        // Order is presentational only — these are top-level `async function`
        // declarations and are all hoisted together — but reading top-to-
        // bottom is easier when callers come after callees.
        if (needsClickHelper) injection += ClickHelperBlock;
        if (needsDragHelper) injection += PointerDragHelperBlock;
        if (needsSwapHelper) injection += PointerSwapHelperBlock;
        if (needsSelectHelper) injection += SelectOptionHelperBlock;
        return rewritten.Insert(insertPos, injection);
    }
}
