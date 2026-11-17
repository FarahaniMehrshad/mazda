namespace Mazda.Infrastructure.Runner;

/// <summary>
/// "Is the page ready for a picture?": the wait every before/after picture
/// goes through, while recording and while replaying, so both sides are
/// photographed the same way.
///
/// The page is ready when, at the same moment:
/// <list type="bullet">
///   <item>no request of any kind (documents, scripts, styles, images, fonts,
///         XHR / fetch) is in flight, and none finished in the last 400 ms.
///         An XHR / fetch counts as finished once its response arrives (a body
///         the page never reads never "finishes"); the others must load fully.
///         WebSockets, event streams and media are long-lived and never count;
///         a request open for over 10 s is treated as long-polling and stops
///         counting;</item>
///   <item>the document has finished loading and its fonts are loaded;</item>
///   <item>no visible image is still loading;</item>
///   <item>nothing visible says it is busy: <c>aria-busy="true"</c>, an
///         indeterminate progress bar, or a spinner / loader / skeleton;</item>
///   <item>no finite animation or transition is running (a menu still fading
///         in); infinite ones are ignored;</item>
///   <item>the DOM has not changed for 300 ms.</item>
/// </list>
/// Then two animation frames, so what changed is painted. It waits at most
/// <see cref="MaxWaitMs"/>; past that the picture is taken anyway, and the
/// wait returns why the page was still busy so the picture can be labelled.
///
/// Needs <c>page</c> in scope. Defines <c>__mzReadyForPicture(page, maxMs?, stop?)</c>,
/// which resolves to null when the page was ready, else a short reason.
/// </summary>
public static class PictureReadiness
{
    public const int MaxWaitMs = 15_000;

    public const string Ts = """
  // ── Ready for a picture (see PictureReadiness.cs) ──
  const __mzPicNet = new Map<any, number>();
  const __mzPicLongLived = ['websocket', 'eventsource', 'media'];
  let __mzPicNetActivity = 0;
  page.on('request', (r: any) => {
    try { if (!__mzPicLongLived.includes(r.resourceType())) { __mzPicNet.set(r, Date.now()); __mzPicNetActivity = Date.now(); } } catch {}
  });
  const __mzPicNetDone = (r: any) => { if (__mzPicNet.delete(r)) __mzPicNetActivity = Date.now(); };
  page.on('requestfinished', __mzPicNetDone);
  page.on('requestfailed', __mzPicNetDone);
  // An XHR / fetch is answered when its response arrives: a body the page never reads (or a stream)
  // is never "finished". What the page does with the answer is covered by the page-side checks.
  page.on('response', (res: any) => {
    try { const r = res.request(); if (['xhr', 'fetch'].includes(r.resourceType())) __mzPicNetDone(r); } catch {}
  });
  const __mzPicBound = <T,>(p: Promise<T>, ms: number): Promise<T | null> =>
    Promise.race([p.catch(() => null as any), new Promise<null>((r) => setTimeout(() => r(null), ms))]);
  const __mzPicShort = (u: string) => { try { const x = new URL(u); return x.pathname.length > 60 ? x.pathname.slice(0, 60) + '…' : x.pathname; } catch { return String(u).slice(0, 60); } };
  // Page side: why the page is not ready yet ([] = ready).
  const __mzPicPage = (p: any): Promise<string[]> => p.evaluate(() => {
    const w: any = window, d: any = document;
    if (!w.__mzPicObs && d.documentElement) {
      w.__mzPicLast = performance.now();
      w.__mzPicObs = new MutationObserver(() => { w.__mzPicLast = performance.now(); });
      w.__mzPicObs.observe(d.documentElement, { subtree: true, childList: true, attributes: true, characterData: true });
    }
    const why: string[] = [];
    if (d.readyState !== 'complete') why.push('the document is still loading');
    if (d.fonts && d.fonts.status !== 'loaded') why.push('fonts are loading');
    const vw = innerWidth, vh = innerHeight;
    const visible = (el: Element) => {
      const r = el.getBoundingClientRect();
      if (r.width < 2 || r.height < 2 || r.bottom <= 0 || r.right <= 0 || r.top >= vh || r.left >= vw) return false;
      const s = getComputedStyle(el);
      return s.visibility !== 'hidden' && s.display !== 'none' && Number(s.opacity) > 0.05;
    };
    let images = 0;
    for (const img of Array.from(d.images) as HTMLImageElement[])
      if ((img.currentSrc || img.src) && !img.complete && visible(img)) images++;
    if (images) why.push(`${images} image(s) still loading`);
    const busyWord = /(^|[\s_-])(spinner|loading|loader|skeleton|shimmer)([\s_-]|$)/i;
    let busy = 0;
    for (const el of Array.from(d.querySelectorAll('[aria-busy="true"], [role="progressbar"]:not([aria-valuenow]), [class*="spinner" i], [class*="load" i], [class*="skeleton" i], [class*="shimmer" i]')) as Element[]) {
      const cls = typeof (el as any).className === 'string' ? (el as any).className : (el.getAttribute('class') || '');
      const marked = el.getAttribute('aria-busy') === 'true' || el.getAttribute('role') === 'progressbar' || busyWord.test(cls);
      if (marked && visible(el)) { busy++; if (busy > 20) break; }
    }
    if (busy) why.push(`${busy} loading indicator(s) on screen`);
    let anims = 0;
    try {
      for (const a of (d.getAnimations ? d.getAnimations() : []) as any[]) {
        if (a.playState !== 'running') continue;
        const end = a.effect?.getComputedTiming?.().endTime;
        if (typeof end === 'number' && isFinite(end)) anims++;
      }
    } catch {}
    if (anims) why.push(`${anims} animation(s) running`);
    if (performance.now() - (w.__mzPicLast ?? 0) < 300) why.push('the page is still changing');
    return why;
  });
  const __mzReadyForPicture = async (p: any, maxMs = 15000, stop?: () => boolean): Promise<string | null> => {
    const end = Date.now() + maxMs;
    let why: string[] = [];
    for (;;) {
      const now = Date.now();
      const pending: string[] = [];
      for (const [r, t] of __mzPicNet) {
        if (now - t > 10000) continue;   // long-polling: never "finishes"
        try { pending.push(`${r.method()} ${__mzPicShort(r.url())}`); } catch { pending.push('a request'); }
      }
      why = [];
      if (pending.length) why.push(`${pending.length} request(s) pending (${pending.slice(0, 3).join(', ')}${pending.length > 3 ? ', …' : ''})`);
      else if (now - __mzPicNetActivity < 400) why.push('requests just finished');
      const pageWhy = await __mzPicBound(__mzPicPage(p), 2000);
      if (pageWhy === null) why.push('the page did not answer (navigating?)');
      else why.push(...pageWhy);
      if (why.length === 0) break;
      if (Date.now() >= end || (stop && stop())) return why.join('; ');
      await new Promise((r) => setTimeout(r, 100));
    }
    // Two frames: what changed is painted before the picture.
    await __mzPicBound(p.evaluate(() => new Promise<void>((r) => requestAnimationFrame(() => requestAnimationFrame(() => r())))), 1000);
    return null;
  };

""";
}
