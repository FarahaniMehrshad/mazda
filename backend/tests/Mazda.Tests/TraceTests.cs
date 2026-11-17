using System.Text.Json;
using Mazda.Application.Common.Tracing;
using Xunit;

namespace Mazda.Tests;

/// <summary>Where the time went: line mapping, categories, and buckets that add up.</summary>
public class TraceTests
{
    // A spec as the runner writes it: injected helpers above, the body between markers.
    private const string Spec = """
        import { test, chromium } from '@playwright/test';
        async function __mzClick__impl(locator: any) { await locator.click(); }
        const __mzClick = (...a: any[]) => __mzTraceTest.step('__mzClick', () => __mzClick__impl(...a), { box: true });
        test('mazda fn-test', async () => {
          const __mz_fn_GetPanel = async (page: Page, args: Record<string, unknown>): Promise<any> => test.step('fn GetPanel', async () => {
            // __MZ_BODY__ GetPanel
            await __mzClick(page.locator('#open'));
            const t = await page.locator('#missing').textContent().catch(() => '');
            await page.waitForTimeout(1500);
            await page.waitForResponse((r) => r.url().includes('/api'));
            return t;
            // __MZ_BODY_END__
          });
        });
        """;

    // Body lines: 1 __mzClick, 2 textContent (times out), 3 waitForTimeout, 4 waitForResponse. Spec lines are 7..10.
    private static string Reporter(long t0) => JsonSerializer.Serialize(new
    {
        title = "mazda fn-test", begin = t0 + 2000, testStart = t0 + 2500, testEnd = t0 + 16500,
        steps = new object[]
        {
            new { id = 3, parent = 2, cat = "pw:api", title = "locator.click(#open)", start = t0 + 3010, ms = 480, file = "mazda.spec.ts", line = 2, err = (string?)null },
            new { id = 2, parent = 1, cat = "test.step", title = "__mzClick", start = t0 + 3000, ms = 500, file = "mazda.spec.ts", line = 7, err = (string?)null },
            new { id = 4, parent = 1, cat = "pw:api", title = "locator.textContent(#missing)", start = t0 + 3600, ms = 10000, file = "mazda.spec.ts", line = 8,
                  err = (string?)"TimeoutError: locator.textContent: Timeout 10000ms exceeded." },
            new { id = 5, parent = 1, cat = "pw:api", title = "page.waitForTimeout", start = t0 + 13600, ms = 1500, file = "mazda.spec.ts", line = 9, err = (string?)null },
            new { id = 6, parent = 1, cat = "pw:api", title = "page.waitForResponse", start = t0 + 15100, ms = 900, file = "mazda.spec.ts", line = 10, err = (string?)null },
            new { id = 1, parent = (int?)null, cat = "test.step", title = "fn GetPanel", start = t0 + 2900, ms = 13200, file = "mazda.spec.ts", line = 20, err = (string?)null },
        },
    });

    private static ScriptTraceResult Build(long t0 = 1_000_000)
        => ScriptTrace.Build(Reporter(t0), Spec, new RunnerTimings(t0, t0 + 100, t0 + 200, t0 + 17000));

    [Fact]
    public void Every_call_maps_to_the_body_line_that_made_it_and_helper_internals_go_to_the_calling_line()
    {
        var r = Build();
        var body = Assert.Single(r.Bodies.Values);
        Assert.Equal("GetPanel", body.Fn);
        Assert.Contains("textContent", body.Lines[1]);

        var click = r.Nodes.Single(n => n.Name.StartsWith("locator.click"));
        Assert.Equal(("GetPanel", 1), (click.Fn, click.Line));   // the helper's own line (2) is not in the body
        Assert.Equal(1, r.Nodes.Single(n => n.Name == "__mzClick").Line);
        var read = r.Nodes.Single(n => n.Name.StartsWith("locator.textContent"));
        Assert.Equal(2, read.Line);
        Assert.True(read.TimedOut);
        Assert.Equal(TraceCategory.Function, r.Nodes.Single(n => n.Name == "fn GetPanel").Category);
    }

    [Theory]
    [InlineData("page.waitForResponse", TraceCategory.Network)]
    [InlineData("page.goto(https://x)", TraceCategory.Network)]
    [InlineData("locator.click(#a)", TraceCategory.Action)]
    [InlineData("keyboard.press(Escape)", TraceCategory.Action)]
    [InlineData("locator.waitFor(#a)", TraceCategory.Wait)]
    [InlineData("page.waitForTimeout", TraceCategory.Sleep)]
    [InlineData("page.screenshot", TraceCategory.Capture)]
    [InlineData("browserType.connectOverCDP", TraceCategory.Startup)]
    [InlineData("locator.textContent(#a)", TraceCategory.Read)]
    [InlineData("locator.count(#a)", TraceCategory.Read)]
    public void Playwright_calls_are_classified_by_what_they_do(string title, string category)
        => Assert.Equal(category, ScriptTrace.CategoryOfCall(title));

    [Fact]
    public void Buckets_add_up_to_the_whole_and_name_the_bottlenecks()
    {
        var rec = new TraceRecorder("function-check", "GetPanel({})");
        using (rec.Span("check GetPanel"))
            rec.AddScript(Build(TraceRecorder.Now()));
        var doc = rec.Snapshot();
        var sum = TraceAnalyzer.Summarize(doc);

        Assert.InRange(sum.Buckets.Sum(b => b.Ms), doc.Ms - 2, doc.Ms + 2);
        var by = sum.Buckets.ToDictionary(b => b.Category, b => b.Ms);
        Assert.Equal(10000, by[TraceCategory.Timeout]);
        Assert.Equal(1500, by[TraceCategory.Sleep]);
        Assert.Equal(900, by[TraceCategory.Network]);
        Assert.True(sum.Buckets.Single(b => b.Category == TraceCategory.Network).Normal);
        Assert.True(by[TraceCategory.Startup] >= 2300);   // prepare + spawn→test + shut down

        var top = sum.Lines.First();
        Assert.Equal((2, 1, 1), (top.Line, top.Calls, top.TimedOut));
        Assert.Contains("textContent", top.Source);
        Assert.Equal(500, sum.Lines.Single(l => l.Line == 1).Ms);   // the helper once, not helper + its click

        var f = Assert.Single(sum.Functions);
        Assert.Equal(13200, f.Ms);
        Assert.Equal(13200 - 500 - 10000 - 1500 - 900, f.BetweenCallsMs);
    }

    [Fact]
    public void A_script_attaches_under_the_span_that_was_open_when_it_ran()
    {
        var rec = new TraceRecorder("roundtrip", "rt");
        using (rec.Span("round trip"))
        using (rec.Span("replay 1"))
            rec.AddScript(Build(TraceRecorder.Now()));
        var doc = rec.Snapshot();
        var replay = doc.Nodes.Single(n => n.Name == "replay 1");
        var script = doc.Nodes.Single(n => n.Name.StartsWith("script: "));
        Assert.Equal(replay.Id, script.Parent);
        Assert.Equal(doc.Nodes.Single(n => n.Name == "round trip").Id, replay.Parent);
        Assert.Equal(doc.Nodes.Count, doc.Nodes.Select(n => n.Id).Distinct().Count());
    }

    [Fact]
    public void A_run_without_a_reporter_file_still_shows_its_start_up()
    {
        var r = ScriptTrace.Build("{}", Spec, new RunnerTimings(0, 100, 200, 5000));
        Assert.Contains(r.Nodes, n => n.Name.StartsWith("start Playwright"));
        Assert.DoesNotContain(r.Nodes, n => n.Category == TraceCategory.Function);
    }
}
