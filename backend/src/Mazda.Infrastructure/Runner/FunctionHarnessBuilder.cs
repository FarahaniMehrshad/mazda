using System.Text;
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
    /// <summary>Sentinel that the state gather harness prints just after the JSON payload.</summary>
    public const string StateEndSentinel = "__MZ_STATE_END__";
    /// <summary>Prefix for every per-step JSON event emitted by the execute harness.</summary>
    public const string EventLinePrefix = "__MZ_EVENT__ ";

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
        IReadOnlyList<Signature>? dependencies)
    {
        var stateFunctions = Enumerate(signature, dependencies)
            .Where(f => f.Kind == FunctionKind.State)
            .ToList();

        var sb = new StringBuilder();
        AppendPreamble(sb, testName: "mazda gather");
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
        sb.AppendLine("  for (const [__mz_name, __mz_fn] of __mz_state_calls) {");
        sb.AppendLine("    try {");
        sb.AppendLine("      __mz_state[__mz_name] = await __mz_fn(page, {});");
        sb.AppendLine("    } catch (__mz_err: any) {");
        sb.AppendLine("      __mz_state[__mz_name] = { __mz_error: String(__mz_err?.message ?? __mz_err) };");
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
        Plan plan)
    {
        var actionFunctions = Enumerate(signature, dependencies)
            .Where(f => f.Kind == FunctionKind.Action)
            .ToList();

        var sb = new StringBuilder();
        AppendPreamble(sb, testName: "mazda execute");
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

        sb.AppendLine("  const __mz_emit = (evt: unknown) => {");
        sb.AppendLine($"    console.log({JsString(EventLinePrefix)} + JSON.stringify(evt));");
        sb.AppendLine("  };");
        sb.AppendLine();

        // Fail-fast loop. `run-complete` fires only if every step succeeded;
        // on a failure we emit `step-failed` and `run-aborted` and return.
        // The interpreter reconstructs "skipped" entries for steps after the
        // failure using the plan and the events it observed.
        sb.AppendLine("  for (const __mz_step of __mz_plan.steps) {");
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
        sb.AppendLine("    } catch (__mz_err: any) {");
        sb.AppendLine("      __mz_emit({ type: 'step-failed', id: __mz_step.id, call: __mz_step.call, durationMs: Date.now() - __mz_t0,");
        sb.AppendLine("        error: { kind: 'RuntimeError', message: String(__mz_err?.message ?? __mz_err) } });");
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
    private static void AppendPreamble(StringBuilder sb, string testName)
    {
        sb.AppendLine("import { test, chromium } from '@playwright/test';");
        sb.AppendLine("import type { Page } from '@playwright/test';");
        sb.AppendLine();
        sb.AppendLine($"test({JsString(testName)}, async () => {{");
        sb.AppendLine("  const browser = await chromium.connectOverCDP('http://localhost:9222');");
        sb.AppendLine("  const ctx = browser.contexts()[0] ?? await browser.newContext();");
        sb.AppendLine("  const page = ctx.pages()[0] ?? await ctx.newPage();");
        sb.AppendLine();
    }

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
            sb.AppendLine($"  const {identifier} = async (page: Page, args: Record<string, unknown>): Promise<any> => {{");
            if (fn.ParamNames.Count > 0)
            {
                var names = string.Join(", ", fn.ParamNames);
                // `any` cast so a body destructuring numeric / boolean args
                // doesn't need per-param type annotations. Bodies validated
                // against the parameters schema at the interpreter boundary.
                sb.AppendLine($"    const {{ {names} }} = args as any;");
            }
            // Reindent each body line by two spaces so nested inside the async
            // function it reads naturally. Blank lines survive intact.
            foreach (var line in fn.Body.Split('\n'))
                sb.AppendLine("    " + line.TrimEnd('\r'));
            sb.AppendLine("  };");
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
