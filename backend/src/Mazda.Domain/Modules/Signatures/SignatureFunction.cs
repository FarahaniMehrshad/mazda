using Mazda.Domain.Common;

namespace Mazda.Domain.Modules.Signatures;

/// <summary>
/// The two categories a high-level function can belong to.
///
/// A signature's function CATALOGUE is Mazda's design-time contract for the
/// runtime planner. Splitting into State vs Action makes the runtime flow
/// unambiguous: state functions run automatically during gatherInformation,
/// action functions are what the planner LLM composes into a plan.
/// </summary>
public enum FunctionKind
{
    /// <summary>
    /// Read-only observer. Takes no parameters (or a minimal scope arg), returns
    /// structured JSON that describes some slice of the current UI. Runs
    /// automatically during gatherInformation — the planner LLM never invokes
    /// these itself; it consumes their aggregated result and then plans
    /// concrete Action calls with literal args grounded in that snapshot.
    /// </summary>
    State,

    /// <summary>
    /// Side-effect-producing operation. Typed parameters, no meaningful return.
    /// The planner LLM composes plans out of these — one function call per plan
    /// step, each with concrete literal args. Bodies are pure deterministic
    /// Playwright (no LLM calls at runtime), so the same plan against the same
    /// state always produces the same interactions.
    /// </summary>
    Action,
}

/// <summary>
/// A pre-baked, LLM-authored function that lives on a Signature. Each function
/// carries its OWN Playwright body — the runtime planner never sees or
/// synthesises code, it only sees the typed contract (name, parameters,
/// returns, description) and composes plans that reference these by name.
///
/// See <see cref="FunctionKind"/> for the state/action split.
///
/// Bodies are stored as strings, not TypeScript files on disk. At execution
/// time the runner stitches them into a per-run harness alongside the existing
/// <c>__mz*</c> helpers (which every body is free to call — the harness
/// injects them). Storing as strings keeps parity with how every other
/// script-shaped artifact on the signature is persisted.
/// </summary>
public class SignatureFunction : Entity
{
    public Guid SignatureId { get; private set; }

    /// <summary>
    /// PascalCase identifier, unique within a signature's catalogue. The
    /// planner LLM addresses functions by this name in the plan JSON — any
    /// collision would silently make one function unreachable.
    /// </summary>
    public string Name { get; private set; } = null!;

    public FunctionKind Kind { get; private set; }

    /// <summary>
    /// 1–3 sentence English description. Fed to the planner LLM as part of the
    /// catalogue — it is what the planner uses to decide which function fits
    /// the user's intent. Keep it about USER GOALS, not DOM mechanics.
    /// </summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>
    /// Full JSON Schema document describing the function's parameter object.
    /// ALWAYS a <c>type:"object"</c> schema, even when the function takes no
    /// arguments (the empty-object schema, not <c>null</c>). The interpreter
    /// validates plan-step args against this schema before invoking the body,
    /// which turns bad plans into clean rejection errors instead of Playwright
    /// exceptions ten call frames deep.
    /// </summary>
    public string ParametersJsonSchema { get; private set; } =
        "{\"type\":\"object\",\"properties\":{},\"required\":[]}";

    /// <summary>
    /// Full JSON Schema document describing the function's return value.
    /// Action functions return null (<c>{"type":"null"}</c>); state functions
    /// return a structured value the planner reads to compose future plans.
    /// State return schemas must be self-describing enough that a planner LLM
    /// reading only the schema knows what fields and enums to expect.
    /// </summary>
    public string ReturnsJsonSchema { get; private set; } = "{\"type\":\"null\"}";

    /// <summary>
    /// Optional 1-sentence English precondition — "the task list must be the
    /// visible view", "a row must be selected". Empty when the function is
    /// unconditional. The interpreter does not check preconditions at v1; they
    /// are context for the planner LLM only.
    /// </summary>
    public string Preconditions { get; private set; } = string.Empty;

    /// <summary>
    /// TypeScript snippet executed as the body of an async function whose
    /// signature is <c>async (page: Page, args: &lt;ParamShape&gt;) =&gt; &lt;Returns&gt;</c>.
    /// Every parameter from <see cref="ParametersJsonSchema"/> is available
    /// as a local with the schema's exact property name. The
    /// <c>__mzClick</c> / <c>__mzSelectOption</c> / <c>__mzPointerDrag</c>
    /// / <c>__mzPointerSwap</c> helpers are in scope and preferred over
    /// their raw Playwright equivalents.
    /// </summary>
    public string Body { get; private set; } = string.Empty;

    /// <summary>
    /// Display / stable order within the catalogue. Only affects UI listing
    /// and (as a tiebreaker) the deterministic gatherInformation call order
    /// for state functions.
    /// </summary>
    public int Order { get; private set; }

    private SignatureFunction() { }

    public static SignatureFunction Create(
        string name,
        FunctionKind kind,
        string description,
        string parametersJsonSchema,
        string returnsJsonSchema,
        string body,
        int order,
        string? preconditions = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name required.", nameof(name));
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("body required.", nameof(body));

        return new SignatureFunction
        {
            Name = name.Trim(),
            Kind = kind,
            Description = description?.Trim() ?? string.Empty,
            ParametersJsonSchema = NormalizeSchema(parametersJsonSchema,
                fallback: "{\"type\":\"object\",\"properties\":{},\"required\":[]}"),
            ReturnsJsonSchema = NormalizeSchema(returnsJsonSchema,
                fallback: "{\"type\":\"null\"}"),
            Preconditions = preconditions?.Trim() ?? string.Empty,
            Body = body,
            Order = order,
        };
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name required.", nameof(name));
        Name = name.Trim();
        Touch();
    }

    public void ChangeKind(FunctionKind kind) { Kind = kind; Touch(); }
    public void UpdateDescription(string? description) { Description = description?.Trim() ?? string.Empty; Touch(); }
    public void UpdatePreconditions(string? preconditions) { Preconditions = preconditions?.Trim() ?? string.Empty; Touch(); }
    public void UpdateOrder(int order) { Order = order; Touch(); }

    public void UpdateParametersSchema(string schema)
    {
        ParametersJsonSchema = NormalizeSchema(schema,
            fallback: "{\"type\":\"object\",\"properties\":{},\"required\":[]}");
        Touch();
    }

    public void UpdateReturnsSchema(string schema)
    {
        ReturnsJsonSchema = NormalizeSchema(schema, fallback: "{\"type\":\"null\"}");
        Touch();
    }

    public void UpdateBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("body required.", nameof(body));
        Body = body;
        Touch();
    }

    private static string NormalizeSchema(string schema, string fallback)
        => string.IsNullOrWhiteSpace(schema) ? fallback : schema.Trim();
}
