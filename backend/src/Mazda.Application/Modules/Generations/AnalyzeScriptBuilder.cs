using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mazda.Domain.Modules.Signatures;
using Mazda.Domain.Modules.Generations;

namespace Mazda.Application.Modules.Generations;

/// <summary>
/// Builds the analyze-state Playwright script DETERMINISTICALLY from the
/// signature + the setup script. No LLM call — every informational element
/// becomes one extraction entry; the script `console.log`s a JSON blob bracketed
/// by sentinel markers, which the caller parses out of stdout.
/// </summary>
public static class AnalyzeScriptBuilder
{
    public const string StartMarker = "__MAZDA_STATE__";
    public const string EndMarker = "__MAZDA_STATE_END__";

    public static string Build(Signature signature, Generation generation)
    {
        var informational = signature.Elements
            .Where(e => e.Category == ElementCategory.Informational)
            .OrderBy(e => e.Order)
            .ToList();

        // Setup script is prepended verbatim — it defines `test` (typically via
        // base.extend) and produces the `page` fixture. Falls back to a minimal
        // default that goes to about:blank if the user hasn't generated a setup.
        var setup = string.IsNullOrWhiteSpace(generation.SetupScript)
            ? DefaultSetup
            : generation.SetupScript;

        var selectorsJson = JsonSerializer.Serialize(
            informational.Select(e => new[] { RewriteTemplatedSelector(e.ElementSelector) }).ToArray(),
            new JsonSerializerOptions { WriteIndented = false });

        var sb = new StringBuilder();
        sb.AppendLine(setup);
        sb.AppendLine();
        sb.AppendLine("test('mazda: extract current component state', async ({ page }) => {");
        sb.AppendLine("  // Selectors are the informational entries of the signature, in order.");
        sb.AppendLine("  // We capture count + per-match innerText for each. Errors are caught");
        sb.AppendLine("  // per-selector so one bad locator doesn't kill the whole capture.");
        sb.AppendLine($"  const selectors: string[][] = {selectorsJson};");
        sb.AppendLine("  const result: Record<string, any> = {};");
        sb.AppendLine("  for (const [sel] of selectors) {");
        sb.AppendLine("    try {");
        sb.AppendLine("      const loc = page.locator(sel);");
        sb.AppendLine("      const count = await loc.count();");
        sb.AppendLine("      const texts: string[] = [];");
        sb.AppendLine("      for (let i = 0; i < count; i++) {");
        sb.AppendLine("        try {");
        sb.AppendLine("          const t = await loc.nth(i).innerText({ timeout: 1500 });");
        sb.AppendLine("          texts.push(t.trim());");
        sb.AppendLine("        } catch {");
        sb.AppendLine("          // Hidden / detached node — skip without failing the whole run.");
        sb.AppendLine("          texts.push('');");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("      result[sel] = { count, texts };");
        sb.AppendLine("    } catch (e: any) {");
        sb.AppendLine("      result[sel] = { error: String(e?.message ?? e) };");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine($"  console.log('{StartMarker}' + JSON.stringify(result) + '{EndMarker}');");
        sb.AppendLine("});");

        return sb.ToString();
    }

    /// <summary>Pull the JSON blob between the sentinel markers out of a captured stdout.</summary>
    public static string? ExtractStateJson(string capturedOutput)
    {
        if (string.IsNullOrEmpty(capturedOutput)) return null;
        var start = capturedOutput.IndexOf(StartMarker, StringComparison.Ordinal);
        if (start < 0) return null;
        var contentStart = start + StartMarker.Length;
        var end = capturedOutput.IndexOf(EndMarker, contentStart, StringComparison.Ordinal);
        if (end < 0) return null;
        return capturedOutput.Substring(contentStart, end - contentStart).Trim();
    }

    /// <summary>
    /// Signature selectors may carry `{i}` / `{n}` / `{key}` placeholders
    /// in two places — positional pseudo-classes (`:nth-of-type({i})`,
    /// `:nth-child({i})`) and inside attribute values
    /// (`[data-testid="row-{i}-foo"]`). Passing those literal strings to
    /// `page.locator(sel)` makes Playwright match nothing because the
    /// `{...}` characters are taken at face value. We rewrite each
    /// selector here so the analyze-state runner can find every instance:
    ///
    ///   • Positional pseudos with a `{placeholder}` are STRIPPED. The
    ///     generated script already iterates the matched set via
    ///     `loc.nth(i)`, so dropping `:nth-of-type({i})` and counting
    ///     `loc.count()` preserves the "i-th of each" semantics exactly.
    ///
    ///   • Templated attribute values are rewritten to CSS substring
    ///     operators: `[data-testid="row-{i}-foo"]` becomes
    ///     `[data-testid^="row-"][data-testid$="-foo"]`, which matches
    ///     every row. When `{i}` is at the start or end of the value,
    ///     only the `$=` or `^=` half is emitted; if both prefix and
    ///     suffix are empty (`"{i}"`) the constraint collapses to an
    ///     attribute-presence check `[data-testid]`.
    ///
    /// Both rewrites operate on string form, not a real CSS AST, so they
    /// can't perfectly handle pathological selectors with nested quoted
    /// braces. Those would have been broken upstream anyway — the
    /// analyzer system prompt forbids `:nth-of-type` in the first place.
    /// </summary>
    internal static string RewriteTemplatedSelector(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var s = raw;

        // 1) Strip positional pseudo-classes whose argument is a
        //    `{placeholder}`. We're permissive about which pseudo —
        //    nth-of-type / nth-child plus their `last-` variants.
        s = Regex.Replace(
            s,
            @":(?:nth-of-type|nth-child|nth-last-of-type|nth-last-child)\(\s*\{[^}]+\}\s*\)",
            "");

        // 2) Rewrite templated attribute values like
        //    `[name="prefix-{i}-suffix"]` → `[name^="prefix-"][name$="-suffix"]`.
        //    A value that's exactly `"{i}"` collapses to `[name]`.
        s = Regex.Replace(
            s,
            "\\[(?<attr>[\\w:-]+)\\s*=\\s*\"(?<prefix>[^\"]*?)\\{[^}]+\\}(?<suffix>[^\"]*?)\"\\]",
            m =>
            {
                var attr = m.Groups["attr"].Value;
                var prefix = m.Groups["prefix"].Value;
                var suffix = m.Groups["suffix"].Value;
                var hasPrefix = prefix.Length > 0;
                var hasSuffix = suffix.Length > 0;
                return (hasPrefix, hasSuffix) switch
                {
                    (true, true) => $"[{attr}^=\"{prefix}\"][{attr}$=\"{suffix}\"]",
                    (true, false) => $"[{attr}^=\"{prefix}\"]",
                    (false, true) => $"[{attr}$=\"{suffix}\"]",
                    _ => $"[{attr}]",
                };
            });

        // 3) Collapse the double-spaces left behind when a pseudo-class
        //    sat between two descendant combinators (e.g.
        //    `.foo :nth-of-type({i}) .bar` → `.foo  .bar` → `.foo .bar`).
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();

        return s;
    }

    // Minimal fallback when the user hasn't yet generated a setup script.
    // Lands on about:blank so the test runs without erroring — every selector
    // will simply return count=0 with no texts.
    private const string DefaultSetup = """
        import { test as base, expect } from '@playwright/test';
        export const test = base;
        export { expect };
        """;
}
