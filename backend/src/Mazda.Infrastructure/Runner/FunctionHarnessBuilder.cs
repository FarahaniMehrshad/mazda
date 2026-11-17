using System.Text;
using Mazda.Application.Common.Tracing;
using System.Text.Json;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Infrastructure.Runner;

/// <summary>
/// Stitches a signature's function catalogue into an ephemeral Playwright
/// spec file that can be executed by <see cref="IScriptRunner"/>. Two
/// harness shapes are produced, from the same catalogue:
///
///   • <see cref="BuildGatherHarness"/> — runs every state function once,
///     collects the returns into a JSON blob, and prints it between the
///     <c>__MZ_STATE_BEGIN__</c> / <c>__MZ_STATE_END__</c> sentinels so
///     <see cref="StateGatherer"/> can pluck it out of stdout.
///
///   • <see cref="BuildExecuteHarness"/> — runs a supplied
///     <see cref="Plan"/>, invoking the corresponding action function for
///     each step and streaming per-step events as <c>__MZ_EVENT__</c>
///     JSON lines. Fail-fast: on the first step error, the harness emits
///     a <c>step-failed</c> event and returns without executing later
///     steps.
///
/// The stitcher writes a single <c>chromium.connectOverCDP('http://localhost:9222')</c>
/// call at the top of each harness; <see cref="LocalProcessScriptRunner"/>
/// rewrites this to route through <c>host.docker.internal</c> and injects
/// the <c>__mzConnectViaHost</c> helper (same code path every analyze-state
/// script uses today). The same runner scans the file for
/// <c>__mzClick</c> / <c>__mzSelectOption</c> / <c>__mzPointerDrag</c> /
/// <c>__mzPointerSwap</c> references and injects only the helpers referenced
/// — so a function body can use them freely without any per-catalogue
/// declaration.
///
/// Dependencies (transitive signatures the root embeds) contribute their
/// state functions to gather harnesses and their action functions to
/// execute harnesses. Functions from a dependency are namespaced by the
/// dependency's signature name (<c>Cart.AddItem</c>) so name collisions
/// across catalogues don't silently overwrite each other. The root
/// signature's own functions stay unnamespaced.
/// </summary>
internal static class FunctionHarnessBuilder
{
    /// <summary>Sentinel that the state gather harness prints just before the JSON payload.</summary>
    public const string StateBeginSentinel = "__MZ_STATE_BEGIN__";

    /// <summary>How long one state function may take during a gather before it is recorded as timed out.</summary>
    public const int StateFunctionBudgetMs = 30_000;
    /// <summary>Sentinel that the state gather harness prints just after the JSON payload.</summary>
    public const string StateEndSentinel = "__MZ_STATE_END__";
    /// <summary>Prefix for every per-step JSON event emitted by the execute harness.</summary>
    public const string EventLinePrefix = "__MZ_EVENT__ ";

    /// <summary>Sentinel the single-function test harness prints just before its result payload.</summary>
    public const string TestBeginSentinel = "__MZ_FNTEST_BEGIN__";
    /// <summary>Sentinel the single-function test harness prints just after its result payload.</summary>
    public const string TestEndSentinel = "__MZ_FNTEST_END__";

    /// <summary>
    /// One function unpacked from a signature and ready for stitching. The
    /// namespaced <see cref="QualifiedName"/> is what the planner LLM writes
    /// in a plan step's `call`; for the root signature it equals
    /// <see cref="RawName"/>.
    /// </summary>
    private sealed record CataloguedFunction(
        string QualifiedName,          // e.g. "AddTask" (root) or "Cart.AddItem" (dep)
        string RawName,                // e.g. "AddTask" or "AddItem"
        FunctionKind Kind,
        IReadOnlyList<string> ParamNames,  // property names to destructure from `args`
        string Body);

    // ── Public entry points ──────────────────────────────────────────

    public static string BuildGatherHarness(
        Signature signature,
        IReadOnlyList<Signature>? dependencies,
        CdpTarget target)
    {
        var stateFunctions = Enumerate(signature, dependencies)
            .Where(f => f.Kind == FunctionKind.State)
            .ToList();

        var sb = new StringBuilder();
        AppendPreamble(sb, testName: "mazda gather", target);
        AppendFunctionDeclarations(sb, stateFunctions, prefix: "__mz_state_");

        // Gather loop: iterate every state function in declaration order,
        // stash the return under the qualified name, encode individual
        // failures rather than aborting. The planner LLM tolerates missing
        // keys far better than a silent early exit.
        sb.AppendLine("  const __mz_state: Record<string, unknown> = {};");
        sb.AppendLine("  const __mz_state_calls: Array<[string, (page: Page, args: Record<string, unknown>) => Promise<unknown>]> = [");
        foreach (var fn in stateFunctions)
        {
            sb.AppendLine($"    [{JsString(fn.QualifiedName)}, __mz_state_{Slug(fn.QualifiedName)}],");
        }
        sb.AppendLine("  ];");
        // Each state function gets its own time budget: one that waits for
        // something the page does not have (a settings panel when nothing is
        // selected) must cost its own entry, not the whole gather. The test
        // timeout is sized so every function can use its full budget.
        sb.AppendLine($"  const __mz_stateBudgetMs = {StateFunctionBudgetMs};");
        sb.AppendLine($"  test.setTimeout({stateFunctions.Count} * __mz_stateBudgetMs + 30_000);");
        sb.AppendLine("  for (const [__mz_name, __mz_fn] of __mz_state_calls) {");
        sb.AppendLine("    let __mz_timer: ReturnType<typeof setTimeout> | undefined;");
        sb.AppendLine("    try {");
        sb.AppendLine("      __mz_state[__mz_name] = await Promise.race([");
        sb.AppendLine("        __mz_fn(page, {}),");
        sb.AppendLine("        new Promise((_, reject) => { __mz_timer = setTimeout(() => reject(new Error(");
        sb.AppendLine("          `timed out after ${__mz_stateBudgetMs / 1000}s (it probably waits for something the page does not show right now)`)), __mz_stateBudgetMs); }),");
        sb.AppendLine("      ]);");
        sb.AppendLine("    } catch (__mz_err: any) {");
        sb.AppendLine("      __mz_state[__mz_name] = { __mz_error: String(__mz_err?.message ?? __mz_err) };");
        sb.AppendLine("    } finally {");
        sb.AppendLine("      if (__mz_timer) clearTimeout(__mz_timer);");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine();

        // Fenced emission. Runner captures stdout into a string; StateGatherer
        // greps for the sentinels and parses the payload between them. Doing
        // it this way keeps state emission independent of Playwright's
        // reporter — the same stdout can carry both the reporter's noise
        // and our payload without them stepping on each other.
        sb.AppendLine($"  console.log({JsString(StateBeginSentinel)});");
        sb.AppendLine("  console.log(JSON.stringify(__mz_state));");
        sb.AppendLine($"  console.log({JsString(StateEndSentinel)});");
        sb.AppendLine();

        AppendPostamble(sb);
        return sb.ToString();
    }

    public static string BuildExecuteHarness(
        Signature signature,
        IReadOnlyList<Signature>? dependencies,
        Plan plan,
        CdpTarget target)
    {
        var actionFunctions = Enumerate(signature, dependencies)
            .Where(f => f.Kind == FunctionKind.Action)
            .ToList();

        var sb = new StringBuilder();
        AppendPreamble(sb, testName: "mazda execute", target);
        AppendFunctionDeclarations(sb, actionFunctions, prefix: "__mz_action_");

        // Dispatch table: qualified name → handler. The planner LLM writes
        // `call` in the plan; the switch below looks it up. Missing entries
        // become a FunctionNotFound step-error rather than a compile-time
        // one, so a plan referencing a stale function name fails cleanly
        // during the step, not before the run even starts.
        sb.AppendLine("  const __mz_actions: Record<string, (page: Page, args: any) => Promise<void>> = {");
        foreach (var fn in actionFunctions)
        {
            sb.AppendLine($"    {JsString(fn.QualifiedName)}: __mz_action_{Slug(fn.QualifiedName)},");
        }
        sb.AppendLine("  };");
        sb.AppendLine();

        // Plan gets embedded as a JSON literal. JSON is a subset of JS, so
        // a plain `const __mz_plan = <json>;` compiles. We ship args as raw
        // JsonElement text (see BuildPlanLiteral) rather than reserializing
        // to preserve whatever shape the LLM emitted.
        sb.AppendLine($"  const __mz_plan = {BuildPlanLiteral(plan)};");
        sb.AppendLine();
        AppendSettleHelper(sb);
        sb.Append(ForensicsHelperTs);
        sb.AppendLine();

        sb.AppendLine("  const __mz_emit = (evt: unknown) => {");
        sb.AppendLine($"    console.log({JsString(EventLinePrefix)} + JSON.stringify(evt));");
        sb.AppendLine("  };");
        sb.AppendLine();

        // Fail-fast loop. `run-complete` fires only if every step succeeded;
        // on a failure we emit `step-failed` and `run-aborted` and return.
        // The interpreter reconstructs "skipped" entries for steps after the
        // failure using the plan and the events it observed.
        sb.AppendLine("  let __mz_prevId: string | null = null;");
        sb.AppendLine("  for (const __mz_step of __mz_plan.steps) {");
        // Let the previous step's effects land (requests answered, the UI
        // done re-rendering) before the next step looks at the page. Never
        // fails the run: it only waits, capped.
        sb.AppendLine("    await __mzSettle(page);");
        sb.AppendLine("    __mz_curStep = __mz_step.id;");
        sb.AppendLine("    const __mz_t0 = Date.now();");
        sb.AppendLine("    __mz_emit({ type: 'step-started', id: __mz_step.id, call: __mz_step.call, args: __mz_step.args ?? {} });");
        sb.AppendLine("    const __mz_handler = __mz_actions[__mz_step.call];");
        sb.AppendLine("    if (!__mz_handler) {");
        sb.AppendLine("      __mz_emit({ type: 'step-failed', id: __mz_step.id, call: __mz_step.call, durationMs: Date.now() - __mz_t0,");
        sb.AppendLine("        error: { kind: 'FunctionNotFound', message: `No action named \"${__mz_step.call}\" in this catalogue.` } });");
        sb.AppendLine("      __mz_emit({ type: 'run-aborted' });");
        sb.AppendLine("      await browser.close();");
        sb.AppendLine("      return;");
        sb.AppendLine("    }");
        sb.AppendLine("    try {");
        sb.AppendLine("      await __mz_handler(page, __mz_step.args ?? {});");
        sb.AppendLine("      __mz_emit({ type: 'step-ok', id: __mz_step.id, call: __mz_step.call, durationMs: Date.now() - __mz_t0 });");
        sb.AppendLine("      __mz_prevId = __mz_step.id;");
        sb.AppendLine("    } catch (__mz_err: any) {");
        // Cheap, time-boxed page probes so the failure says where the page was.
        sb.AppendLine("      let __mz_url: string | null = null, __mz_title: string | null = null;");
        sb.AppendLine("      try { __mz_url = page.url(); } catch {}");
        sb.AppendLine("      try { __mz_title = await Promise.race([page.title(), new Promise<string>((r) => setTimeout(() => r(''), 1500))]); } catch {}");
        // Full page snapshot (aria, trimmed DOM, form values, console, in-flight
        // requests, screenshot) written to MZ_FORENSICS_DIR for the runner to
        // collect. Bounded and fully guarded: it must never change the outcome.
        sb.AppendLine("      await __mzCaptureForensics(page, { stepId: __mz_step.id, call: __mz_step.call, args: __mz_step.args ?? {},");
        sb.AppendLine("        prevStepId: __mz_prevId, url: __mz_url, title: __mz_title,");
        sb.AppendLine("        error: { message: __mzPlain(__mz_err?.message ?? __mz_err), stack: __mzPlain(__mz_err?.stack).slice(0, 4000) } });");
        sb.AppendLine("      __mz_emit({ type: 'step-failed', id: __mz_step.id, call: __mz_step.call, durationMs: Date.now() - __mz_t0,");
        sb.AppendLine("        error: { kind: 'RuntimeError', message: __mzPlain(__mz_err?.message ?? __mz_err),");
        sb.AppendLine("          stack: __mzPlain(__mz_err?.stack).slice(0, 4000), pageUrl: __mz_url, pageTitle: __mz_title } });");
        sb.AppendLine("      __mz_emit({ type: 'run-aborted' });");
        sb.AppendLine("      await browser.close();");
        sb.AppendLine("      return;");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine();
        sb.AppendLine("  __mz_emit({ type: 'run-complete' });");
        AppendPostamble(sb);
        return sb.ToString();
    }

    /// <summary>
    /// Failure forensics for the execute harness. Listens for console output and
    /// page errors for the whole run, and on a failed step builds one bundle:
    /// URL / title, an accessibility snapshot, a trimmed DOM (scripts, styles,
    /// svg, comments stripped; long text clipped; password values blanked),
    /// current form-field values, console + page errors, in-flight XHR/fetch
    /// URLs and a JPEG screenshot. Every probe is time-boxed and individually
    /// guarded because the page may be closed or wedged at exactly the moment
    /// we need it. Written to <c>$MZ_FORENSICS_DIR/forensics.json</c>; the
    /// runner picks it up after the process exits. Lives in the harness, never
    /// in generated bodies.
    /// </summary>
    private const string ForensicsHelperTs = """
  const __mzFx = { console: [] as any[], pageErrors: [] as string[] };
  page.on('console', (m: any) => {
    try { __mzFx.console.push({ type: m.type(), text: String(m.text()).slice(0, 500) }); if (__mzFx.console.length > 100) __mzFx.console.shift(); } catch {}
  });
  page.on('pageerror', (e: any) => {
    try { __mzFx.pageErrors.push(String(e?.stack ?? e).slice(0, 800)); if (__mzFx.pageErrors.length > 50) __mzFx.pageErrors.shift(); } catch {}
  });
  // Playwright colours its call log with ANSI escapes, which show up as garbage
  // glyphs in the UI and are noise to an LLM.
  const __mzPlain = (s: any): string => String(s ?? '').replace(/\u001b\[[0-9;]*[A-Za-z]/g, '');
  const __mzBound = <T,>(p: Promise<T>, ms: number): Promise<T | null> =>
    Promise.race([p.catch(() => null as any), new Promise<null>((r) => setTimeout(() => r(null), ms))]);
  // Rolling network log for the whole run, each request tagged with the step
  // that was current when it started. Ring-capped. Bodies are fetched without
  // blocking the page and only for text-ish xhr/fetch/document responses.
  // Headers (cookies, Authorization) are deliberately never recorded.
  let __mz_curStep: string | null = null;
  const __mzNet: any[] = [];
  const __mzNetByReq = new WeakMap<object, any>();
  const __mzNetT0 = Date.now();
  let __mzNetSeq = 0;
  const __mzBodyTypes = ['xhr', 'fetch', 'document'];
  page.on('request', (r: any) => {
    try {
      const rec: any = { n: ++__mzNetSeq, step: __mz_curStep, method: r.method(), url: String(r.url()).slice(0, 500), type: r.resourceType(), t: Date.now() - __mzNetT0 };
      const pd = r.postData();
      if (pd) rec.reqBody = String(pd).slice(0, 2048);
      __mzNet.push(rec); __mzNetByReq.set(r, rec);
      if (__mzNet.length > 300) __mzNet.shift();
    } catch {}
  });
  page.on('response', (resp: any) => {
    try {
      const rec = __mzNetByReq.get(resp.request());
      if (rec) { rec.status = resp.status(); rec.mime = String(resp.headers()['content-type'] ?? '').slice(0, 80); }
    } catch {}
  });
  page.on('requestfinished', (r: any) => {
    try {
      const rec = __mzNetByReq.get(r);
      if (!rec) return;
      rec.ms = Date.now() - __mzNetT0 - rec.t;
      if (__mzBodyTypes.includes(rec.type) && /json|text|xml|javascript|html|x-www-form-urlencoded/i.test(rec.mime ?? '')) {
        __mzBound((async () => { const resp = await r.response(); return resp ? await resp.text() : null; })(), 1500)
          .then((t: any) => { if (t) rec.body = String(t).slice(0, 4096); }).catch(() => {});
      }
    } catch {}
  });
  page.on('requestfailed', (r: any) => {
    try {
      const rec = __mzNetByReq.get(r);
      if (rec) { rec.failure = r.failure()?.errorText ?? 'failed'; rec.ms = Date.now() - __mzNetT0 - rec.t; }
    } catch {}
  });
  const __mzAriaText = (n: any, depth = 0): string => {
    if (!n || depth > 25) return '';
    const label = [n.role, n.name ? JSON.stringify(String(n.name).slice(0, 120)) : '', n.value ? '= ' + JSON.stringify(String(n.value).slice(0, 80)) : '',
      n.checked !== undefined ? '[checked=' + n.checked + ']' : '', n.disabled ? '[disabled]' : '', n.focused ? '[focused]' : ''].filter(Boolean).join(' ');
    return '  '.repeat(depth) + '- ' + label + '\n' + (n.children ?? []).map((c: any) => __mzAriaText(c, depth + 1)).join('');
  };
  const __mzCaptureForensics = (p: any, ctx: any) => test.step('mz: forensics', async () => {
    try {
      const out: any = { capturedAt: new Date().toISOString(), ...ctx };
      const [aria, dom, shot] = await Promise.all([
        __mzBound((async () => {
          const loc: any = p.locator('body');
          if (typeof loc.ariaSnapshot === 'function') return String(await loc.ariaSnapshot({ timeout: 3000 }));
          const tree = await p.accessibility?.snapshot({ interestingOnly: true });
          return tree ? __mzAriaText(tree) : null;
        })(), 3500),
        __mzBound(p.evaluate(() => {
          const fields = Array.from(document.querySelectorAll('input,textarea,select')).slice(0, 100).map((el: any) => ({
            tag: el.tagName.toLowerCase(), type: el.type || null, name: el.name || null, id: el.id || null,
            value: el.type === 'password' ? '***' : String(el.value ?? '').slice(0, 200),
            checked: el.type === 'checkbox' || el.type === 'radio' ? !!el.checked : undefined,
          }));
          const root = document.documentElement.cloneNode(true) as HTMLElement;
          root.querySelectorAll('script,style,noscript,link,meta,svg,template,iframe').forEach((e) => e.remove());
          root.querySelectorAll('input[type=password]').forEach((e) => e.setAttribute('value', ''));
          const walker = document.createTreeWalker(root, NodeFilter.SHOW_COMMENT | NodeFilter.SHOW_TEXT);
          const kill: Node[] = [];
          while (walker.nextNode()) {
            const n = walker.currentNode;
            if (n.nodeType === 8) kill.push(n);
            else if ((n.nodeValue ?? '').length > 300) n.nodeValue = n.nodeValue!.slice(0, 300) + '…';
          }
          kill.forEach((n) => n.parentNode?.removeChild(n));
          root.querySelectorAll('*').forEach((el) => {
            for (const a of Array.from(el.attributes)) if (a.value.length > 300) el.setAttribute(a.name, a.value.slice(0, 300) + '…');
          });
          const ae: any = document.activeElement;
          return {
            html: root.outerHTML.slice(0, 150000), fields, readyState: document.readyState,
            viewport: { w: window.innerWidth, h: window.innerHeight }, scrollY: Math.round(window.scrollY),
            activeElement: ae && ae !== document.body ? (ae.tagName.toLowerCase() + (ae.id ? '#' + ae.id : '') + (ae.name ? '[name=' + ae.name + ']' : '')) : null,
          };
        }), 3500),
        __mzBound(p.screenshot({ type: 'jpeg', quality: 55, timeout: 3000 }), 4000),
      ]);
      out.aria = aria ? String(aria).slice(0, 60000) : null;
      if (dom) { const { html, ...rest } = dom as any; out.dom = html; Object.assign(out, rest); }
      // Keep what explains a failure: xhr/fetch/document traffic plus anything that failed or errored.
      out.network = __mzNet.filter((x) => __mzBodyTypes.includes(x.type) || x.failure || (x.status ?? 0) >= 400);
      out.networkTotal = __mzNet.length;
      out.console = __mzFx.console;
      out.pageErrors = __mzFx.pageErrors;
      out.inflight = Array.from(__mz_inflight).slice(0, 20).map((r: any) => { try { return r.method() + ' ' + String(r.url()).slice(0, 300); } catch { return '?'; } });
      const b64 = shot ? (shot as any).toString('base64') : null;
      out.screenshotJpegBase64 = b64 && b64.length <= 900000 ? b64 : null;
      const dir = process.env.MZ_FORENSICS_DIR;
      if (dir) {
        const fs: any = await import('node:fs');
        fs.mkdirSync(dir, { recursive: true });
        fs.writeFileSync(dir + '/forensics.json', JSON.stringify(out));
      }
    } catch { /* forensics must never change the run's outcome */ }
  });
""";

    /// <summary>
    /// <c>__mzSettle(page)</c>: wait until no XHR / fetch has been in flight
    /// for 300 ms and the DOM has been quiet for 300 ms, at most 5 s in
    /// total. A pause, not a check — it resolves either way. Long-polling or
    /// stuck requests only cost the cap.
    /// </summary>
    private static void AppendSettleHelper(StringBuilder sb)
    {
        sb.AppendLine("  const __mz_inflight = new Set<any>();");
        sb.AppendLine("  page.on('request', (r: any) => { if (['xhr', 'fetch'].includes(r.resourceType())) __mz_inflight.add(r); });");
        sb.AppendLine("  page.on('requestfinished', (r: any) => __mz_inflight.delete(r));");
        sb.AppendLine("  page.on('requestfailed', (r: any) => __mz_inflight.delete(r));");
        sb.AppendLine("  // A response is an answer: a body the page never reads never \"finishes\".");
        sb.AppendLine("  page.on('response', (res: any) => { try { __mz_inflight.delete(res.request()); } catch {} });");
        sb.AppendLine("  const __mzSettle = (p: Page, maxMs = 5000) => test.step('mz: settle', async () => {");
        sb.AppendLine("    const end = Date.now() + maxMs;");
        sb.AppendLine("    let quietSince = Date.now();");
        sb.AppendLine("    while (Date.now() < end) {");
        sb.AppendLine("      if (__mz_inflight.size > 0) quietSince = Date.now();");
        sb.AppendLine("      else if (Date.now() - quietSince >= 300) break;");
        sb.AppendLine("      await new Promise((r) => setTimeout(r, 50));");
        sb.AppendLine("    }");
        sb.AppendLine("    const left = Math.max(0, end - Date.now());");
        sb.AppendLine("    if (left === 0) return;");
        sb.AppendLine("    await p.evaluate((ms: number) => new Promise<void>((resolve) => {");
        sb.AppendLine("      let quiet: any = null;");
        sb.AppendLine("      const done = () => { obs.disconnect(); clearTimeout(quiet); clearTimeout(cap); resolve(); };");
        sb.AppendLine("      const obs = new MutationObserver(() => { clearTimeout(quiet); quiet = setTimeout(done, 300); });");
        sb.AppendLine("      obs.observe(document.documentElement, { subtree: true, childList: true, attributes: true, characterData: true });");
        sb.AppendLine("      quiet = setTimeout(done, 300);");
        sb.AppendLine("      const cap = setTimeout(done, ms);");
        sb.AppendLine("    }), left).catch(() => {});");
        sb.AppendLine("  });");
        sb.AppendLine();
    }

    /// <summary>File (under <c>MZ_FORENSICS_DIR</c>) the round-trip harness writes its observations to.</summary>
    public const string RoundTripFileName = "roundtrip.json";

    /// <summary>
    /// Harness for a round trip: open the recording's start URL, install the
    /// SAME page-side recorder used while the user demonstrated, run the
    /// plan's calls one by one, and record what each did: the recorder's
    /// mutation summaries, the requests it caused, console output, where the
    /// page ended up, an accessibility snapshot and before/after pictures.
    /// Observation only: judging happens in C#. Stops at the first throw.
    /// </summary>
    public static string BuildRoundTripHarness(
        Signature signature,
        IReadOnlyList<Signature>? dependencies,
        IReadOnlyList<Mazda.Application.Modules.RoundTrip.RoundTripCall> calls,
        CdpTarget target,
        string? startUrl,
        string recorderPageScript)
    {
        var all = Enumerate(signature, dependencies).ToList();
        var runTag = Guid.NewGuid().ToString("N")[..12];

        var sb = new StringBuilder();
        AppendPreamble(sb, testName: "mazda roundtrip", target);
        AppendSettleHelper(sb);
        sb.Append(PictureReadiness.Ts);
        sb.Append(ForensicsHelperTs);
        sb.AppendLine();
        AppendFunctionDeclarations(sb, all, prefix: "__mz_fn_");

        sb.AppendLine("  const __mz_fns: Record<string, (page: Page, args: any) => Promise<any>> = {");
        foreach (var fn in all)
            sb.AppendLine($"    {JsString(fn.QualifiedName)}: __mz_fn_{Slug(fn.QualifiedName)},");
        sb.AppendLine("  };");
        sb.AppendLine("  const __mz_calls: Array<{ call: string; args: any }> = [");
        foreach (var c in calls)
            sb.AppendLine($"    {{ call: {JsString(c.Call)}, args: {NormalizeArgsLiteral(c.ArgsJson)} }},");
        sb.AppendLine("  ];");
        sb.AppendLine($"  const __mz_rt = {{ binding: {JsString("__mzRtEmit_" + runTag)}, session: {JsString("rt-" + runTag)}, startUrl: {JsNullableString(startUrl)} }};");
        sb.AppendLine($"  const __MZ_PAGE_SRC: string = {JsonSerializer.Serialize(recorderPageScript)};");
        sb.Append(RoundTripLoopTs);
        AppendPostamble(sb);
        return sb.ToString();
    }

    private const string RoundTripLoopTs = """
  const __mz_say = (m: string) => console.log('__MZ_RT__ ' + m);
  const __mz_pause = (ms: number) => test.step(`mz: pause ${ms} ms`, () => new Promise((r) => setTimeout(r, ms)));
  // A picture only once the page is ready for it (PictureReadiness); `busy` says why it was not, when the wait ran out.
  const __mz_shot = async (which: string): Promise<{ jpeg: string | null; busy: string | null }> => {
    const busy = await test.step(`mz: wait for the page before the ${which} picture`, () => __mzReadyForPicture(page));
    if (busy) __mz_say(`the ${which} picture was taken while the page was still busy: ${busy}`);
    const jpeg = await test.step('mz: screenshot', async () => {
      const b: any = await __mzBound(page.screenshot({ type: 'jpeg', quality: 50, timeout: 3000 }), 4000);
      const s = b ? b.toString('base64') : null;
      return s && s.length <= 900000 ? s : null;
    });
    return { jpeg, busy };
  };

  // The recorder's page-side script, exactly as used while the user demonstrated:
  // the replay's mutation summaries then have the demonstration's shape.
  const __mz_events: any[] = [];
  await page.exposeBinding(__mz_rt.binding, (_src: any, evt: any) => {
    if (!evt || typeof evt !== 'object') return;
    const kind = String(evt.kind || '');
    if (kind !== 'mutations' && kind !== 'action') return;
    __mz_events.push({ step: __mz_curStep, kind, actionId: evt.actionId ?? null, data: evt.data ?? null });
  });
  const __mz_install = '(' + __MZ_PAGE_SRC + ')(' + JSON.stringify({ sessionId: __mz_rt.session, binding: __mz_rt.binding, allowedOrigins: [], maskAll: false }) + ');';
  await page.addInitScript({ content: __mz_install });
  const __mz_console: any[] = [];
  page.on('console', (m: any) => { try { if (__mz_curStep) __mz_console.push({ step: __mz_curStep, text: `[${m.type()}] ${String(m.text()).slice(0, 400)}` }); } catch {} });
  page.on('pageerror', (e: any) => { try { if (__mz_curStep) __mz_console.push({ step: __mz_curStep, text: `[pageerror] ${String(e?.message ?? e).slice(0, 400)}` }); } catch {} });

  if (__mz_rt.startUrl) {
    __mz_say('opening the start page');
    await test.step('mz: open the start page', async () => {
      try { await page.goto(__mz_rt.startUrl, { waitUntil: 'load', timeout: 20000 }); } catch (e: any) { __mz_say('could not open the start page: ' + __mzPlain(e?.message ?? e)); }
    });
  }
  for (const f of page.frames()) { try { await f.evaluate(__mz_install); } catch { /* cross-origin frame */ } }
  await __mzSettle(page);

  const __mz_out: any[] = [];
  let __mz_failed = false;
  for (let __mz_i = 0; __mz_i < __mz_calls.length; __mz_i++) {
    const __mz_c = __mz_calls[__mz_i];
    const rec: any = { call: __mz_c.call, ran: false, ok: false, durationMs: 0 };
    __mz_out.push(rec);
    if (__mz_failed) continue;
    await __mzSettle(page);
    { const shot = await __mz_shot(`step ${__mz_i + 1} "before"`); rec.before = shot.jpeg; rec.beforeBusy = shot.busy; }
    rec.ran = true;
    __mz_say(`step ${__mz_i + 1}/${__mz_calls.length}: ${__mz_c.call}`);
    __mz_curStep = 's' + __mz_i;
    const __mz_t0 = Date.now();
    try {
      const h = __mz_fns[__mz_c.call];
      if (!h) throw new Error(`No function named "${__mz_c.call}" in this catalogue.`);
      await h(page, __mz_c.args ?? {});
      rec.ok = true;
    } catch (__mz_err: any) {
      rec.error = { message: __mzPlain(__mz_err?.message ?? __mz_err), stack: __mzPlain(__mz_err?.stack).slice(0, 4000) };
      __mz_failed = true;
    }
    rec.durationMs = Date.now() - __mz_t0;
    // Let the step's requests land and the recorder close its observation window (300 ms of quiet).
    await __mzSettle(page);
    await __mz_pause(600);
    __mz_curStep = null;
    try { rec.urlAfter = page.url(); } catch {}
    { const shot = await __mz_shot(`step ${__mz_i + 1} "after"`); rec.after = shot.jpeg; rec.afterBusy = shot.busy; }
    const tree: any = await test.step('mz: aria snapshot', () => __mzBound((async () => {
      const t = await (page as any).accessibility?.snapshot({ interestingOnly: true });
      return t ? __mzAriaText(t) : null;
    })(), 3000));
    rec.aria = tree ? String(tree).slice(0, 20000) : null;
    __mz_say(`step ${__mz_i + 1}/${__mz_calls.length}: ${__mz_c.call} ${rec.ok ? 'done' : 'FAILED: ' + rec.error.message.split('\n')[0]}`);
  }

  // Flush whatever the recorder still holds, then hand everything over per step.
  for (const f of page.frames()) { try { await f.evaluate(() => { const w: any = window; if (w.__mzRec) w.__mzRec.stop(); }); } catch {} }
  await __mz_pause(200);
  for (let __mz_i = 0; __mz_i < __mz_out.length; __mz_i++) {
    const id = 's' + __mz_i;
    const rec = __mz_out[__mz_i];
    rec.mutations = __mz_events.filter((e) => e.step === id && e.kind === 'mutations').map((e) => e.data);
    rec.network = __mzNet.filter((x: any) => x.step === id)
      .map((x: any) => ({ method: x.method, url: x.url, reqBody: x.reqBody ?? null, status: x.status ?? null, type: x.type }));
    rec.console = __mz_console.filter((x) => x.step === id).map((x) => x.text).slice(0, 50);
  }
  {
    const dir = process.env.MZ_FORENSICS_DIR;
    if (dir) {
      const fs: any = await import('node:fs');
      fs.mkdirSync(dir, { recursive: true });
      fs.writeFileSync(dir + '/roundtrip.json', JSON.stringify({ steps: __mz_out }));
    }
  }
  __mz_say('replay finished');
""";

    /// <summary>
    /// Harness for the design-time "test this function" affordance: invoke ONE
    /// function with caller-supplied args and report what it returned or threw.
    ///
    /// Unlike gather and execute, this shape declares the WHOLE catalogue (state
    /// and action, root and dependencies) and calls only the target. Declaring
    /// everything costs a bigger file but means a body that leans on a sibling
    /// helper still runs, and there's one code path rather than a
    /// kind-and-dependency special case.
    ///
    /// The try/catch lives inside the harness on purpose: a throwing body is a
    /// legitimate test RESULT (<c>ok:false</c> with the message), not a harness
    /// failure. Only an unreachable browser or a spec that won't compile should
    /// surface as an error to the caller.
    /// </summary>
    public static string BuildTestHarness(
        Signature signature,
        IReadOnlyList<Signature>? dependencies,
        string qualifiedFunctionName,
        string argsJson,
        CdpTarget target)
    {
        var all = Enumerate(signature, dependencies).ToList();

        var sb = new StringBuilder();
        AppendPreamble(sb, testName: "mazda fn-test", target);
        // Page snapshot on failure, same as the execute harness (the settle
        // helper contributes the in-flight request set it reads).
        AppendSettleHelper(sb);
        sb.Append(ForensicsHelperTs);
        sb.AppendLine();
        AppendFunctionDeclarations(sb, all, prefix: "__mz_fn_");

        // Dispatch through a table keyed by qualified name rather than emitting
        // a direct call, so an unknown name produces the same structured
        // FunctionNotFound payload the execute harness gives instead of a
        // TypeScript compile error the user would have to read out of a log.
        sb.AppendLine("  const __mz_fns: Record<string, (page: Page, args: any) => Promise<any>> = {");
        foreach (var fn in all)
            sb.AppendLine($"    {JsString(fn.QualifiedName)}: __mz_fn_{Slug(fn.QualifiedName)},");
        sb.AppendLine("  };");
        sb.AppendLine();

        sb.AppendLine($"  const __mz_target = {JsString(qualifiedFunctionName)};");
        sb.AppendLine($"  const __mz_args = {NormalizeArgsLiteral(argsJson)};");
        sb.AppendLine("  const __mz_handler = __mz_fns[__mz_target];");
        sb.AppendLine("  const __mz_t0 = Date.now();");
        sb.AppendLine("  __mz_curStep = __mz_target;");
        sb.AppendLine("  let __mz_result: any;");
        sb.AppendLine("  if (!__mz_handler) {");
        sb.AppendLine("    __mz_result = { ok: false, durationMs: 0, error: { kind: 'FunctionNotFound',");
        sb.AppendLine("      message: `No function named \"${__mz_target}\" in this catalogue.`, stack: '' } };");
        sb.AppendLine("  } else {");
        sb.AppendLine("    try {");
        sb.AppendLine("      const __mz_returned = await __mz_handler(page, __mz_args);");
        sb.AppendLine("      __mz_result = { ok: true, durationMs: Date.now() - __mz_t0,");
        sb.AppendLine("        returned: __mz_returned === undefined ? null : __mz_returned };");
        sb.AppendLine("    } catch (__mz_err: any) {");
        sb.AppendLine("      __mz_result = { ok: false, durationMs: Date.now() - __mz_t0,");
        sb.AppendLine("        error: { kind: 'RuntimeError', message: __mzPlain(__mz_err?.message ?? __mz_err),");
        sb.AppendLine("                 stack: __mzPlain(__mz_err?.stack) } };");
        sb.AppendLine("      let __mz_url: string | null = null;");
        sb.AppendLine("      try { __mz_url = page.url(); } catch {}");
        sb.AppendLine("      await __mzCaptureForensics(page, { stepId: __mz_target, call: __mz_target, args: __mz_args, prevStepId: null, url: __mz_url, title: null,");
        sb.AppendLine("        error: { message: __mz_result.error.message, stack: __mz_result.error.stack.slice(0, 4000) } });");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine();

        // Page context alongside the result: a body that assumes the wrong view
        // fails in a way that only makes sense once you know where the browser
        // actually was. Cheap to capture, and it turns "click timed out" into
        // "click timed out, and you were on /settings".
        sb.AppendLine("  try {");
        sb.AppendLine("    __mz_result.pageUrl = page.url();");
        sb.AppendLine("    __mz_result.pageTitle = await page.title();");
        sb.AppendLine("  } catch { /* page may be gone; the result still stands */ }");
        sb.AppendLine();

        sb.AppendLine($"  console.log({JsString(TestBeginSentinel)});");
        sb.AppendLine("  console.log(JSON.stringify(__mz_result));");
        sb.AppendLine($"  console.log({JsString(TestEndSentinel)});");
        sb.AppendLine();

        AppendPostamble(sb);
        return sb.ToString();
    }

    /// <summary>
    /// Emit the caller's args as a JS object literal. Reuses the raw JSON text
    /// when it parses to an object (same reasoning as
    /// <see cref="BuildPlanLiteral"/> — preserve the shape as given); anything
    /// else degrades to <c>{}</c> so a malformed payload can't produce a spec
    /// that won't compile. The use case validates args before we get here, so
    /// this is belt-and-braces.
    /// </summary>
    private static string NormalizeArgsLiteral(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return "{}";
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "{}";
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    // ── Building blocks ──────────────────────────────────────────────

    /// <summary>
    /// Shared header: imports + Playwright test declaration + CDP connect +
    /// context/page acquisition. Deliberately connects to
    /// <c>localhost:9222</c> — <see cref="LocalProcessScriptRunner"/> rewrites
    /// that to <c>__mzConnectViaHost(chromium, 'host.docker.internal', 9222)</c>
    /// and injects the helper. This is the same convention every
    /// analyze-state script uses today, so it works out of the box on the
    /// existing runner without a special flag.
    /// </summary>
    internal static void AppendPreamble(StringBuilder sb, string testName, CdpTarget target)
    {
        sb.AppendLine("import { test, chromium } from '@playwright/test';");
        sb.AppendLine("import type { Page } from '@playwright/test';");
        sb.AppendLine();

        // Resolve the attached tab by CDP targetId. Matching on URL would be
        // ambiguous the moment two tabs share one (a second window on the same
        // app, an SPA where routes share an origin), and `pages()[0]` — what
        // every harness used before attachment existed — is simply whichever
        // target Chrome reports first, which is not the focused tab.
        //
        // Scans every context, not just contexts()[0]: normal tabs share one
        // default context, but incognito windows and other profiles get their
        // own, and a user can legitimately attach to one of those.
        sb.AppendLine("async function __mzPickPage(browser: any, targetId: string | null): Promise<Page> {");
        sb.AppendLine("  const found: Array<{ ctx: any; page: any }> = [];");
        sb.AppendLine("  for (const c of browser.contexts()) for (const p of c.pages()) found.push({ ctx: c, page: p });");
        sb.AppendLine("  if (targetId) {");
        sb.AppendLine("    for (const { ctx, page } of found) {");
        sb.AppendLine("      try {");
        sb.AppendLine("        const session = await ctx.newCDPSession(page);");
        sb.AppendLine("        const info = await session.send('Target.getTargetInfo');");
        sb.AppendLine("        await session.detach().catch(() => {});");
        sb.AppendLine("        if (info?.targetInfo?.targetId === targetId) return page;");
        sb.AppendLine("      } catch { /* target vanished mid-scan; keep looking */ }");
        sb.AppendLine("    }");
        // Deliberately throw rather than fall back to the first page: silently
        // driving a different tab than the one the user attached is worse than
        // a clear failure telling them to re-pick.
        sb.AppendLine("    throw new Error(");
        sb.AppendLine("      'The attached browser tab is no longer open (CDP targetId ' + targetId + '). ' +");
        sb.AppendLine("      'Pick a tab again from the CDP panel.');");
        sb.AppendLine("  }");
        sb.AppendLine("  if (found.length > 0) return found[0].page;");
        sb.AppendLine("  const ctx = browser.contexts()[0] ?? await browser.newContext();");
        sb.AppendLine("  return await ctx.newPage();");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine($"test({JsString(testName)}, async () => {{");
        // Emitted as localhost:<port> on purpose — LocalProcessScriptRunner's
        // RewriteHosts matches that shape and swaps in the __mzConnectViaHost
        // helper (which spoofs the Host header Chrome insists on). Its regex
        // captures the port, so a non-default port survives the rewrite.
        sb.AppendLine("  const { browser, page } = await test.step('mz: connect to the browser', async () => {");
        sb.AppendLine($"    const browser = await chromium.connectOverCDP('http://localhost:{target.Port}');");
        sb.AppendLine($"    const page = await __mzPickPage(browser, {JsNullableString(target.TargetId)});");
        sb.AppendLine("    return { browser, page };");
        sb.AppendLine("  });");
        sb.AppendLine();
    }

    /// <summary>JS string literal, or bare <c>null</c> for a missing value.</summary>
    private static string JsNullableString(string? s)
        => string.IsNullOrWhiteSpace(s) ? "null" : JsString(s);

    /// <summary>
    /// Shared footer: close the CDP session (does NOT close the user's
    /// Chrome — connectOverCDP's Browser.close only disconnects the
    /// protocol session) and close the test function.
    /// </summary>
    private static void AppendPostamble(StringBuilder sb)
    {
        sb.AppendLine("  await browser.close();");
        sb.AppendLine("});");
    }

    /// <summary>
    /// Emits one <c>const</c> declaration per function in the list, using the
    /// convention <c>{prefix}{Slug(qualifiedName)}</c> for the JS identifier.
    /// Each function is wrapped as
    /// <c>async (page, args) =&gt; { const { p1, p2 } = args; &lt;body&gt; }</c>
    /// so bodies can reference the parameter names bare (as the catalogue
    /// generator's system prompt promises them).
    /// </summary>
    private static void AppendFunctionDeclarations(
        StringBuilder sb,
        IReadOnlyList<CataloguedFunction> functions,
        string prefix)
    {
        foreach (var fn in functions)
        {
            var identifier = $"{prefix}{Slug(fn.QualifiedName)}";
            // Explicit `Record<string, unknown>` for `args` so bodies that
            // don't take parameters still typecheck when the harness passes
            // the empty object. The destructuring below re-narrows into the
            // actual param names.
            // Every call is a named step ("fn <name>") so the trace can tell calls apart, and the body sits
            // between markers so the trace maps each Playwright call back to the body line that made it.
            sb.AppendLine($"  const {identifier} = async (page: Page, args: Record<string, unknown>): Promise<any> => test.step({JsString(ScriptTrace.FunctionStepPrefix + fn.QualifiedName)}, async () => {{");
            if (fn.ParamNames.Count > 0)
            {
                var names = string.Join(", ", fn.ParamNames);
                // `any` cast so a body destructuring numeric / boolean args
                // doesn't need per-param type annotations. Bodies validated
                // against the parameters schema at the interpreter boundary.
                sb.AppendLine($"    const {{ {names} }} = args as any;");
            }
            sb.AppendLine("    " + ScriptTrace.BodyStartMarker + fn.QualifiedName);
            // Reindent each body line by four spaces so nested inside the async
            // function it reads naturally. Blank lines survive intact.
            foreach (var line in fn.Body.Split('\n'))
                sb.AppendLine("    " + line.TrimEnd('\r'));
            sb.AppendLine("    " + ScriptTrace.BodyEndMarker);
            sb.AppendLine("  });");
            sb.AppendLine();
        }
    }

    /// <summary>
    /// Serialise a <see cref="Plan"/> as a JS-literal expression. Args are
    /// re-serialised from the raw JSON string the LLM produced (see
    /// <see cref="PlanStep.ArgsJson"/>); reasoning is dropped because the
    /// harness doesn't need it.
    /// </summary>
    private static string BuildPlanLiteral(Plan plan)
    {
        var steps = plan.Steps.Select(s => new
        {
            id = s.Id,
            call = s.Call,
            args = JsonDocument.Parse(string.IsNullOrWhiteSpace(s.ArgsJson) ? "{}" : s.ArgsJson).RootElement,
        }).ToList();
        return JsonSerializer.Serialize(new { steps }, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>
    /// Flatten root signature + optional dependencies into a single list of
    /// <see cref="CataloguedFunction"/>, with dependency-owned functions
    /// namespaced by the dependency's signature name.
    /// </summary>
    private static IEnumerable<CataloguedFunction> Enumerate(
        Signature root,
        IReadOnlyList<Signature>? dependencies)
    {
        foreach (var fn in root.Functions.OrderBy(f => f.Order))
        {
            yield return new CataloguedFunction(
                QualifiedName: fn.Name,
                RawName: fn.Name,
                Kind: fn.Kind,
                ParamNames: ExtractParamNames(fn.ParametersJsonSchema),
                Body: fn.Body);
        }
        if (dependencies is null) yield break;
        foreach (var dep in dependencies)
        {
            var ns = SanitizeNamespace(dep.Name);
            foreach (var fn in dep.Functions.OrderBy(f => f.Order))
            {
                yield return new CataloguedFunction(
                    QualifiedName: $"{ns}.{fn.Name}",
                    RawName: fn.Name,
                    Kind: fn.Kind,
                    ParamNames: ExtractParamNames(fn.ParametersJsonSchema),
                    Body: fn.Body);
            }
        }
    }

    /// <summary>
    /// Pull the top-level property names out of a JSON Schema doc. We only
    /// look at <c>properties</c> — never at nested schemas, oneOf/anyOf,
    /// etc. — because the runtime contract for bodies is "destructure the
    /// declared top-level params". A malformed schema returns an empty list,
    /// which means bodies simply don't get a destructuring statement (they
    /// can still access args as <c>args.foo</c> if they really want to).
    /// </summary>
    private static IReadOnlyList<string> ExtractParamNames(string schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(schemaJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
            if (!root.TryGetProperty("properties", out var props)
                || props.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<string>();
            }
            var names = new List<string>();
            foreach (var prop in props.EnumerateObject())
            {
                // Only include names that are valid JS identifiers — otherwise
                // the destructuring statement wouldn't compile. Bodies for
                // exotic keys can still reach into `args` directly.
                if (IsValidJsIdent(prop.Name)) names.Add(prop.Name);
            }
            return names;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsValidJsIdent(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (!(char.IsLetter(s[0]) || s[0] == '_' || s[0] == '$')) return false;
        for (var i = 1; i < s.Length; i++)
        {
            var c = s[i];
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '$')) return false;
        }
        return true;
    }

    /// <summary>
    /// Turn a qualified function name (which may contain '.') into a valid
    /// JS identifier suitable for use as a local variable name. Non-ident
    /// chars become '_'. Collisions across dependency namespaces are avoided
    /// because the dependency's sanitized name is baked into the slug too.
    /// </summary>
    private static string Slug(string name)
    {
        var sb = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else sb.Append('_');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Sanitize a signature name for use as a namespace prefix. Same rules
    /// as <see cref="Slug"/>, applied to whitespace-collapsed input.
    /// </summary>
    private static string SanitizeNamespace(string name)
    {
        var trimmed = new string(name.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var slug = Slug(trimmed);
        return string.IsNullOrEmpty(slug) ? "dep" : slug;
    }

    /// <summary>
    /// Produce a JS single-quoted string literal for <paramref name="s"/>,
    /// escaping backslashes and single quotes. We use single quotes for
    /// consistency with the rest of the emitted harness.
    /// </summary>
    private static string JsString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('\'');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('\'');
        return sb.ToString();
    }
}
