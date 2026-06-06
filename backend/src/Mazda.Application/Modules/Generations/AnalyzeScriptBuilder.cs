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

    public static string Build(Signature signature, Generation generation, IReadOnlyList<Signature>? dependencies = null)
    {
        // Walk the root + every dependency. Informational elements get
        // captured from the full union so dep components' read-targets
        // (status pills, list rows, etc.) make it into the state JSON.
        // Selector duplicates across signatures are collapsed (root wins —
        // it's iterated first), since the analyze script would otherwise
        // run the same locator twice.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var informational = new List<SignatureElement>();
        foreach (var e in signature.Elements.Where(x => x.Category == ElementCategory.Informational).OrderBy(x => x.Order))
        {
            if (seen.Add(e.ElementSelector)) informational.Add(e);
        }
        if (dependencies is not null)
        {
            foreach (var dep in dependencies)
            {
                if (dep.Id == signature.Id) continue;
                foreach (var e in dep.Elements.Where(x => x.Category == ElementCategory.Informational).OrderBy(x => x.Order))
                {
                    if (seen.Add(e.ElementSelector)) informational.Add(e);
                }
            }
        }

        // Setup script is prepended verbatim — it defines `test` (typically via
        // base.extend) and produces the `page` fixture. Falls back to a minimal
        // default that goes to about:blank if the user hasn't generated a setup.
        var setup = string.IsNullOrWhiteSpace(generation.SetupScript)
            ? DefaultSetup
            : generation.SetupScript;

        var selectorsJson = JsonSerializer.Serialize(
            informational.Select(e => new[] { RewriteTemplatedSelector(e.ElementSelector) }).ToArray(),
            new JsonSerializerOptions { WriteIndented = false });

        // Optional scope selector — when set, every signature selector is
        // evaluated INSIDE the named container. Useful when the component
        // lives in a modal / popover / portal and we want to ignore matches
        // elsewhere on the page. The scope itself is also recorded in the
        // extraction blob (under a "_scope" key) so the downstream state
        // builder knows what it represents.
        var scope = string.IsNullOrWhiteSpace(generation.AnalyzeStateScope)
            ? null
            : generation.AnalyzeStateScope!.Trim();
        var scopeLiteral = scope is null ? "null" : JsonSerializer.Serialize(scope);

        var sb = new StringBuilder();
        sb.AppendLine(setup);
        sb.AppendLine();
        sb.AppendLine("test('mazda: extract current component state', async ({ page }) => {");
        sb.AppendLine("  // Selectors are the informational entries of the signature, in order.");
        sb.AppendLine("  // We capture count + per-match rendered text for each. Errors are caught");
        sb.AppendLine("  // per-selector so one bad locator doesn't kill the whole capture.");
        sb.AppendLine($"  const SCOPE_SELECTOR: string | null = {scopeLiteral};");
        sb.AppendLine("  // When SCOPE_SELECTOR is set, every locator is evaluated INSIDE that");
        sb.AppendLine("  // container so generic selectors (e.g. [data-testid=\"title\"]) only");
        sb.AppendLine("  // match elements within the scoped sub-tree — not the whole page.");
        sb.AppendLine("  // page.locator(...) and Locator.locator(...) share the same chaining API");
        sb.AppendLine("  // so the rest of the extraction loop doesn't care which one `root` is.");
        sb.AppendLine("  const root: any = SCOPE_SELECTOR ? page.locator(SCOPE_SELECTOR) : page;");
        sb.AppendLine($"  const selectors: string[][] = {selectorsJson};");
        sb.AppendLine("  const result: Record<string, any> = {};");
        sb.AppendLine("  if (SCOPE_SELECTOR) result._scope = SCOPE_SELECTOR;");
        sb.AppendLine("  for (const [sel] of selectors) {");
        sb.AppendLine("    try {");
        sb.AppendLine("      const loc = root.locator(sel);");
        sb.AppendLine("      const count = await loc.count();");
        sb.AppendLine("      const texts: string[] = [];");
        sb.AppendLine("      for (let i = 0; i < count; i++) {");
        sb.AppendLine("        try {");
        sb.AppendLine("          // captureRenderedText runs INSIDE the browser. It mimics");
        sb.AppendLine("          // innerText's visibility/block rules but ALSO reads the");
        sb.AppendLine("          // CURRENT value of every <input>/<textarea>/<select>");
        sb.AppendLine("          // descendant (innerText alone returns '' for those — they");
        sb.AppendLine("          // have no text children, only a DOM-level .value property).");
        sb.AppendLine("          // Without this, a container wrapping a populated input");
        sb.AppendLine("          // looks empty in the captured state, which makes the");
        sb.AppendLine("          // downstream LLM think the field is blank when it isn't.");
        sb.AppendLine("          const t = await loc.nth(i).evaluate((el: any) => {");
        sb.AppendLine("            // ── inline DOM walker ────────────────────────────────");
        sb.AppendLine("            const BLOCK = new Set(['address','article','aside','blockquote','dd','details','dialog','div','dl','dt','fieldset','figcaption','figure','footer','form','h1','h2','h3','h4','h5','h6','header','hgroup','hr','legend','li','main','nav','ol','output','p','pre','section','summary','table','tbody','td','tfoot','th','thead','tr','ul','br']);");
        sb.AppendLine("            const SKIP  = new Set(['script','style','noscript','template']);");
        sb.AppendLine("            function isHidden(node: any): boolean {");
        sb.AppendLine("              if (!node || node.nodeType !== 1) return false;");
        sb.AppendLine("              const cs = (node.ownerDocument && node.ownerDocument.defaultView)");
        sb.AppendLine("                ? node.ownerDocument.defaultView.getComputedStyle(node)");
        sb.AppendLine("                : null;");
        sb.AppendLine("              if (!cs) return false;");
        sb.AppendLine("              if (cs.display === 'none') return true;");
        sb.AppendLine("              if (cs.visibility === 'hidden' || cs.visibility === 'collapse') return true;");
        sb.AppendLine("              return false;");
        sb.AppendLine("            }");
        sb.AppendLine("            function readField(node: any): string | null {");
        sb.AppendLine("              const tag = node.tagName.toLowerCase();");
        sb.AppendLine("              if (tag === 'input') {");
        sb.AppendLine("                const type = (node.type || 'text').toLowerCase();");
        sb.AppendLine("                if (type === 'hidden' || type === 'submit' || type === 'reset' || type === 'button' || type === 'image' || type === 'file') return null;");
        sb.AppendLine("                if (type === 'checkbox' || type === 'radio') return node.checked ? '[x]' : '[ ]';");
        sb.AppendLine("                // password fields: don't leak the value");
        sb.AppendLine("                if (type === 'password') return node.value ? '••••••' : '';");
        sb.AppendLine("                return (node.value ?? '') === '' ? (node.placeholder ?? '') : String(node.value);");
        sb.AppendLine("              }");
        sb.AppendLine("              if (tag === 'textarea') {");
        sb.AppendLine("                return (node.value ?? '') === '' ? (node.placeholder ?? '') : String(node.value);");
        sb.AppendLine("              }");
        sb.AppendLine("              if (tag === 'select') {");
        sb.AppendLine("                const opts = node.selectedOptions ? Array.from(node.selectedOptions) : [];");
        sb.AppendLine("                if (opts.length === 0) return '';");
        sb.AppendLine("                return opts.map((o: any) => (o.textContent ?? o.value ?? '').trim()).filter(Boolean).join(', ');");
        sb.AppendLine("              }");
        sb.AppendLine("              // contenteditable host — fall back to its textContent");
        sb.AppendLine("              if (node.isContentEditable) return node.textContent || '';");
        sb.AppendLine("              return null;");
        sb.AppendLine("            }");
        sb.AppendLine("            function walk(node: any, out: string[]): void {");
        sb.AppendLine("              if (!node) return;");
        sb.AppendLine("              if (node.nodeType === 3) { // text node");
        sb.AppendLine("                if (node.textContent) out.push(node.textContent);");
        sb.AppendLine("                return;");
        sb.AppendLine("              }");
        sb.AppendLine("              if (node.nodeType !== 1) return; // not an element");
        sb.AppendLine("              const tag = node.tagName.toLowerCase();");
        sb.AppendLine("              if (SKIP.has(tag)) return;");
        sb.AppendLine("              if (isHidden(node)) return;");
        sb.AppendLine("              // <br/> renders as a newline");
        sb.AppendLine("              if (tag === 'br') { out.push('\\n'); return; }");
        sb.AppendLine("              // Form field: emit its value INSTEAD of recursing (its");
        sb.AppendLine("              // shadow / pseudo content has no usable text children).");
        sb.AppendLine("              // Wrap with spaces so adjacent labels in the DOM (which");
        sb.AppendLine("              // typically have no text node between them and the input)");
        sb.AppendLine("              // don't collapse into 'Name:Alice' — readers expect");
        sb.AppendLine("              // 'Name: Alice'. The post-processing collapses runs.");
        sb.AppendLine("              const field = readField(node);");
        sb.AppendLine("              if (field !== null) {");
        sb.AppendLine("                if (field) { out.push(' '); out.push(field); out.push(' '); }");
        sb.AppendLine("                return;");
        sb.AppendLine("              }");
        sb.AppendLine("              const isBlock = BLOCK.has(tag);");
        sb.AppendLine("              if (isBlock) out.push('\\n');");
        sb.AppendLine("              const children = node.childNodes;");
        sb.AppendLine("              for (let i = 0; i < children.length; i++) walk(children[i], out);");
        sb.AppendLine("              if (isBlock) out.push('\\n');");
        sb.AppendLine("            }");
        sb.AppendLine("            const out: string[] = [];");
        sb.AppendLine("            walk(el, out);");
        sb.AppendLine("            // Collapse runs of inline whitespace, drop empty lines,");
        sb.AppendLine("            // keep meaningful line breaks (block boundaries / <br>).");
        sb.AppendLine("            return out.join('')");
        sb.AppendLine("              .split('\\n')");
        sb.AppendLine("              .map((line: string) => line.replace(/[\\t ]+/g, ' ').trim())");
        sb.AppendLine("              .filter((line: string) => line.length > 0)");
        sb.AppendLine("              .join('\\n');");
        sb.AppendLine("          }, undefined, { timeout: 1500 });");
        sb.AppendLine("          texts.push(t);");
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
