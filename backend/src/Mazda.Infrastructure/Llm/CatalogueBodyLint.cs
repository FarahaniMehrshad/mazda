using System.Text;
using System.Text.RegularExpressions;

namespace Mazda.Infrastructure.Llm;

/// <summary>
/// Enforces the catalogue's NO POST-CONDITIONS rule on ACTION bodies: an
/// action performs interactions and then waits only for the network
/// responses they trigger. Assertions, timeouts and waits on what the UI
/// shows afterwards make an action fail although it worked.
///
/// Pure string analysis (no TypeScript parser): bodies are split into
/// top-level statements, the last interaction is located, and anything
/// after it other than network waits is reported / stripped.
/// </summary>
internal static class CatalogueBodyLint
{
    private static readonly Regex Forbidden = new(
        @"\bexpect(\.soft|\.poll)?\s*\(|\.to(Be|Have|Contain|Match|Equal)\w*\s*\(|\bwaitForTimeout\s*\(|\bwaitForFunction\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex Interaction = new(
        @"__mz(Click|PointerDrag|PointerSwap|SelectOption)\s*\(|\.(click|dblclick|fill|press|pressSequentially|type|check|uncheck|selectOption|hover|dragTo|setInputFiles|tap)\s*\(|\bkeyboard\.(press|type|insertText)\s*\(",
        RegexOptions.Compiled);

    /// <summary>Waits on UI state (not network) — fine before a later interaction, never after the last one.</summary>
    private static readonly Regex UiWait = new(
        @"\.waitFor\s*\(|\bwaitForSelector\s*\(|\bwaitForFunction\s*\(|\bwaitForURL\s*\(|\bwaitForLoadState\s*\(|\bthrow\b",
        RegexOptions.Compiled);

    private static readonly Regex NetworkWait = new(@"\bwaitFor(Response|Request)\s*\(", RegexOptions.Compiled);

    public static bool IsAction(string kind) => string.Equals(kind, "action", StringComparison.OrdinalIgnoreCase);

    /// <summary>Human-readable violations; empty when the body is clean.</summary>
    public static IReadOnlyList<string> Issues(string body)
    {
        var issues = new List<string>();
        foreach (Match m in Forbidden.Matches(body))
        {
            var what = m.Value.TrimEnd('(', ' ');
            var msg = $"uses `{what}(…)`";
            if (!issues.Contains(msg)) issues.Add(msg);
        }
        foreach (var s in TrailingUiStatements(body))
            issues.Add($"waits on / checks the UI after its last interaction: `{Shorten(s)}`");
        return issues;
    }

    /// <summary>Drops the trailing UI waits / checks after the last interaction.</summary>
    public static string StripTrailingUiWaits(string body)
    {
        var statements = Split(body);
        var last = LastInteraction(statements);
        if (last < 0) return body;
        var kept = new List<string>();
        for (var i = 0; i < statements.Count; i++)
        {
            if (i > last && IsTrailingUiWait(statements[i])) continue;
            kept.Add(statements[i]);
        }
        return string.Join("\n", kept.Select(k => k.Trim())).Trim();
    }

    private static IEnumerable<string> TrailingUiStatements(string body)
    {
        var statements = Split(body);
        var last = LastInteraction(statements);
        if (last < 0) yield break;
        for (var i = last + 1; i < statements.Count; i++)
            if (IsTrailingUiWait(statements[i])) yield return statements[i];
    }

    private static bool IsTrailingUiWait(string statement)
    {
        if (!UiWait.IsMatch(statement)) return false;
        // `await Promise.all([page.waitForResponse(...), ...])` with no UI
        // wait in it is a network wait; one mixing both is still a UI wait.
        var withoutNetwork = NetworkWait.Replace(statement, "");
        return UiWait.IsMatch(withoutNetwork);
    }

    private static int LastInteraction(IReadOnlyList<string> statements)
    {
        for (var i = statements.Count - 1; i >= 0; i--)
            if (Interaction.IsMatch(statements[i])) return i;
        return -1;
    }

    /// <summary>
    /// Top-level statements: split at `;` and at a closing `}` that ends a
    /// block statement, outside strings, template literals, comments and
    /// brackets.
    /// </summary>
    internal static List<string> Split(string body)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        char quote = '\0';
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            sb.Append(c);
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < body.Length) { sb.Append(body[++i]); continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '/' && i + 1 < body.Length && body[i + 1] == '/')
            {
                while (i + 1 < body.Length && body[i + 1] != '\n') sb.Append(body[++i]);
                continue;
            }
            if (c is '\'' or '"' or '`') { quote = c; continue; }
            if (c is '(' or '[' or '{') { depth++; continue; }
            if (c is ')' or ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
                if (c == '}' && depth == 0)
                {
                    // `}` ending a block (if / for / try) — unless the
                    // expression continues: `}),`, `});`, `} else`, `} catch`.
                    var j = i + 1;
                    while (j < body.Length && char.IsWhiteSpace(body[j])) j++;
                    var rest = j < body.Length ? body[j..] : "";
                    if (rest.Length == 0 || !(rest[0] is ')' or ',' or ';' or '.' or ']')
                        && !rest.StartsWith("else", StringComparison.Ordinal)
                        && !rest.StartsWith("catch", StringComparison.Ordinal)
                        && !rest.StartsWith("finally", StringComparison.Ordinal))
                        Flush();
                }
                continue;
            }
            if (c == ';' && depth == 0) Flush();
        }
        Flush();
        return list;

        void Flush()
        {
            var s = sb.ToString().Trim();
            if (s.Length > 0 && s != ";") list.Add(s);
            sb.Clear();
        }
    }

    private static string Shorten(string s)
    {
        var one = Regex.Replace(s, @"\s+", " ");
        return one.Length > 140 ? one[..140] + "…" : one;
    }
}
