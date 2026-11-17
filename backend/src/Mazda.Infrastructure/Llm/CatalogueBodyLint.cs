using System.Text;
using System.Text.RegularExpressions;

namespace Mazda.Infrastructure.Llm;

/// <summary>
/// Enforces the catalogue's NO POST-CONDITIONS rule on ACTION bodies: an
/// action performs interactions and then waits only for the network
/// responses they trigger. Assertions, timeouts and waits on what the UI
/// shows afterwards make an action fail although it worked; a wait for a
/// request that may not come costs its full timeout every time it doesn't.
///
/// And the "what the page shows now" rule on STATE bodies
/// (<see cref="StateIssues"/>): no waits before reading, no single-element
/// reads of elements that may be absent.
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

    /// <summary>A request the recording saw after interacting with one of these selectors.</summary>
    public sealed record ObservedRequest(IReadOnlyList<string> TargetSelectors, string Request);

    private static readonly Regex Snapshot = new(
        @"\.evaluateAll\s*\(|\.allTextContents\s*\(|\.allInnerTexts\s*\(|\.count\s*\(\s*\)",
        RegexOptions.Compiled);
    private static readonly Regex AnyWait = new(@"\.waitFor\s*\(|\bwaitForSelector\s*\(|\bwaitForResponse\s*\(", RegexOptions.Compiled);

    /// <summary>Human-readable violations; empty when the body is clean.</summary>
    public static IReadOnlyList<string> Issues(string body, IReadOnlyList<ObservedRequest>? observed = null)
    {
        var issues = new List<string>();
        var statements = Split(body);

        // Snapshot read, nothing waited for before it, then a throw: an early
        // run (previous step still rendering) reads an empty list and fails.
        for (var i = 0; i < statements.Count; i++)
        {
            if (!Snapshot.IsMatch(statements[i])) continue;
            if (statements.Take(i).Any(st => AnyWait.IsMatch(st))) break;
            if (statements.Skip(i).Any(st => Regex.IsMatch(st, @"\bthrow\b")))
                issues.Add($"reads the page without waiting first and throws on that snapshot: `{Shorten(statements[i])}` — wait for the list (or use a locator filter) before reading it");
            break;
        }

        // The recording showed a request after interacting with a control
        // this body uses, but the body never waits for any response.
        if (observed is { Count: > 0 } && !Regex.IsMatch(body, @"\bwaitForResponse\s*\("))
        {
            foreach (var o in observed)
            {
                var hit = o.TargetSelectors.FirstOrDefault(sel => sel.Length >= 12 && body.Contains(sel, StringComparison.Ordinal));
                if (hit is null) continue;
                issues.Add($"the recording shows `{o.Request}` after interacting with `{Shorten(hit)}`, but the body never awaits waitForResponse for it");
                break;
            }
        }
        foreach (var s in OptionalRequestWaits(statements))
            issues.Add($"waits for a request that may never come and swallows the timeout: `{Shorten(s)}` — whenever the app does not send it, every call pays the full timeout. Await only the request the interaction itself sends every time; the harness lets follow-up requests finish between steps");
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

    private static readonly Regex OptionalRequestWait = new(
        @"\bwaitFor(Response|Request)\s*\(.*\.catch\s*\(", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>`page.waitForResponse(…).catch(…)`: a wait for a request that may not come.</summary>
    private static IEnumerable<string> OptionalRequestWaits(IReadOnlyList<string> statements)
        => statements.Where(st => OptionalRequestWait.IsMatch(st));

    // ── State functions ──

    /// <summary>Waits that are fine only for what the function itself just revealed (a menu it opened).</summary>
    private static readonly Regex StateWait = new(
        @"\.waitFor\s*\(|\bwaitFor(Selector|Response|Request|LoadState|URL|Function|Timeout)\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Playwright reads that wait for their element (up to the action timeout):
    /// <c>await &lt;locator&gt;.textContent()</c> and friends. Calls inside a
    /// browser callback (`=&gt;`) are DOM reads and return at once.
    /// </summary>
    private static readonly Regex SingleRead = new(
        @"\bawait\s+(?<loc>(?:(?!=>|\bawait\b)[^;])+?)\.(?<m>textContent|innerText|innerHTML|getAttribute|inputValue|isChecked|isEnabled|isDisabled|isEditable)\s*\(",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex TrailingPick = new(@"\.(first|last)\(\)\s*$|\.nth\([^()]*\)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Violations of the state-function rule "report what the page shows
    /// now": a state function that waits for an element the page does not
    /// have right now (nothing selected, an empty list) pays the full timeout
    /// on every gather. Waits and single-element reads are fine once the
    /// function has interacted (opened a menu, hovered for a tooltip): they
    /// are about what it just revealed.
    /// </summary>
    public static IReadOnlyList<string> StateIssues(string body)
    {
        var issues = new List<string>();
        var statements = Split(body);
        var first = FirstInteraction(statements);
        var before = first < 0 ? statements : statements.Take(first).ToList();
        foreach (var st in before)
        {
            if (StateWait.IsMatch(st))
                issues.Add($"waits before reading: `{Shorten(st)}` — a state function reports what the page shows now; when the element is absent (an empty list, nothing selected) this wait runs out its whole timeout. Read directly with evaluateAll / allTextContents / count(), which return at once");
            foreach (Match m in SingleRead.Matches(st))
            {
                var loc = m.Groups["loc"].Value.Trim();
                if (Guarded(body, loc)) continue;
                issues.Add($"`{m.Groups["m"].Value}()` on `{Shorten(loc)}` waits up to the action timeout when that element is absent — check `await loc.count()` first, or read it inside evaluate / evaluateAll");
            }
        }
        return issues.Distinct().ToList();
    }

    /// <summary>The body counts the same locator (or its un-picked base) before reading it.</summary>
    private static bool Guarded(string body, string loc)
    {
        var compact = (string x) => Regex.Replace(x, @"\s+", "");
        var b = compact(body);
        var l = compact(loc).TrimStart('(');
        var baseLoc = TrailingPick.Replace(l, "");
        return b.Contains(l + ".count()", StringComparison.Ordinal)
            || (baseLoc.Length > 0 && b.Contains(baseLoc + ".count()", StringComparison.Ordinal));
    }

    /// <summary>
    /// Deterministic clean-up for state bodies the repair round left as they
    /// were: drops `await x.waitFor(…).catch(…)` statements before the first
    /// interaction. They can only delay (the `.catch` swallows the timeout),
    /// so removing them never changes what the function returns.
    /// </summary>
    public static string StripStateDeadWaits(string body)
    {
        var statements = Split(body);
        var first = FirstInteraction(statements);
        var kept = new List<string>();
        for (var i = 0; i < statements.Count; i++)
        {
            var st = statements[i];
            var dead = (first < 0 || i < first)
                && Regex.IsMatch(st, @"^\s*await\s+[^;]*\.waitFor(Selector)?\s*\([^;]*\)\s*\.catch\s*\(\s*\(\s*\)\s*=>\s*(\{\s*\}|undefined|null)\s*\)\s*;?\s*$", RegexOptions.Singleline);
            if (!dead) kept.Add(st);
        }
        return kept.Count == statements.Count ? body : string.Join("\n", kept.Select(k => k.Trim())).Trim();
    }

    private static int FirstInteraction(IReadOnlyList<string> statements)
    {
        for (var i = 0; i < statements.Count; i++)
            if (Interaction.IsMatch(statements[i])) return i;
        return -1;
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
