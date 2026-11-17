using Mazda.Infrastructure.Llm;
using Xunit;

namespace Mazda.Tests;

/// <summary>
/// The "no dead waits" rules: waits that run out their timeout whenever the
/// awaited thing does not happen (an absent element, a request that was not sent).
/// </summary>
public class CatalogueBodyLintTests
{
    // ── State functions ──

    [Fact]
    public void State_wait_before_reading_a_list_is_flagged_and_stripped()
    {
        const string body = """
            await page.locator('ul.items > li').first().waitFor({ timeout: 5000 }).catch(() => {});
            return await page.locator('ul.items > li').evaluateAll(rows => rows.map((r, i) => ({ index: i, name: (r.textContent ?? '').trim() })));
            """;
        var issues = CatalogueBodyLint.StateIssues(body);
        Assert.Single(issues);
        Assert.Contains("waits before reading", issues[0]);

        var stripped = CatalogueBodyLint.StripStateDeadWaits(body);
        Assert.DoesNotContain("waitFor", stripped);
        Assert.StartsWith("return await page.locator('ul.items > li').evaluateAll", stripped);
        Assert.Empty(CatalogueBodyLint.StateIssues(stripped));
    }

    [Fact]
    public void State_single_element_read_of_a_maybe_absent_element_is_flagged()
    {
        const string body = """
            return {
              busy: await page.locator('.spinner').count() > 0,
              detail: ((await page.locator('div.details[data-x="1"]').first().textContent().catch(() => '')) ?? '').trim(),
            };
            """;
        var issues = CatalogueBodyLint.StateIssues(body);
        Assert.Single(issues);
        Assert.Contains("textContent()", issues[0]);
        Assert.Contains("div.details", issues[0]);
    }

    [Fact]
    public void State_read_guarded_by_count_is_fine()
    {
        const string body = """
            const loc = page.locator('.panel .title').first();
            const title = (await loc.count()) > 0 ? ((await loc.textContent()) ?? '').trim() : '';
            const other = page.locator('.side .name');
            const name = (await other.count()) > 0 ? await other.first().innerText() : '';
            return { title, name };
            """;
        Assert.Empty(CatalogueBodyLint.StateIssues(body));
    }

    [Fact]
    public void State_reads_inside_browser_callbacks_are_fine()
    {
        const string body = """
            return await page.locator('tr.row').evaluateAll(rows => rows.map(r => ({
              id: r.getAttribute('data-id'),
              text: r.querySelector('td')?.textContent ?? '',
            })));
            """;
        Assert.Empty(CatalogueBodyLint.StateIssues(body));
    }

    [Fact]
    public void State_waits_after_its_own_interaction_are_fine()
    {
        const string body = """
            const menu = page.locator('.menu');
            const opened = !(await menu.isVisible().catch(() => false));
            if (opened) {
              await __mzClick(page.locator('button.open-menu'));
              await menu.waitFor({ state: 'visible', timeout: 10000 });
            }
            const out = [] as string[];
            const items = menu.locator('.item');
            const count = await items.count();
            for (let i = 0; i < count; i++) {
              await items.nth(i).hover();
              const tip = page.locator('.tooltip').last();
              await tip.waitFor({ state: 'visible', timeout: 1500 }).catch(() => {});
              out.push(((await tip.textContent().catch(() => '')) ?? '').trim());
            }
            if (opened) await page.keyboard.press('Escape');
            return out;
            """;
        Assert.Empty(CatalogueBodyLint.StateIssues(body));
        Assert.Equal(body.Trim(), CatalogueBodyLint.StripStateDeadWaits(body).Trim());
    }

    [Fact]
    public void State_wait_that_can_throw_is_flagged_but_not_stripped()
    {
        // Without the .catch it changes the outcome (throws), so only the repair round may change it.
        const string body = """
            await page.locator('.list > .item').first().waitFor();
            return await page.locator('.list > .item').allTextContents();
            """;
        Assert.NotEmpty(CatalogueBodyLint.StateIssues(body));
        Assert.Equal(body, CatalogueBodyLint.StripStateDeadWaits(body));
    }

    // ── Actions ──

    [Fact]
    public void Action_optional_request_wait_is_flagged()
    {
        const string body = """
            const saved = page.waitForResponse(r => r.request().method() === 'POST' && new URL(r.url()).pathname === '/api/items');
            const refreshed = page.waitForResponse(r => new URL(r.url()).pathname === '/api/items/list', { timeout: 15000 }).catch(() => undefined);
            await __mzPointerDrag(page, page.locator('.src').nth(index), page.locator('.dst'));
            await saved;
            await refreshed;
            """;
        var issues = CatalogueBodyLint.Issues(body);
        Assert.Single(issues);
        Assert.Contains("may never come", issues[0]);
        Assert.Contains("/api/items/list", issues[0]);
    }

    [Fact]
    public void Action_waiting_for_its_own_request_is_fine()
    {
        const string body = """
            const saved = page.waitForResponse(r => r.request().method() === 'POST' && new URL(r.url()).pathname === '/api/items');
            await __mzClick(page.locator('button.save'));
            await saved;
            """;
        Assert.Empty(CatalogueBodyLint.Issues(body));
    }
}
