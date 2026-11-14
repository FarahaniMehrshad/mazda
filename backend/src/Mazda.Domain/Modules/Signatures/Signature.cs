using Mazda.Domain.Common;

namespace Mazda.Domain.Modules.Signatures;

/// <summary>
/// A Signature is Mazda's "lens" on a component — the JSON description
/// of every interactive element with its CSS selector, type, and plain-English
/// description. See screen 03 in the design.
/// </summary>
public class Signature : Entity
{
    public Guid OwnerId { get; private set; }
    public string Name { get; private set; } = null!;
    public string Version { get; private set; } = "0.1.0";
    public string Source { get; private set; } = string.Empty;
    // The actual bundled component source code that was originally fed to the
    // analyzer. Stored so deep-rescan steps (re-analyze interactable, re-analyze
    // informational) can re-invoke the LLM without making the user re-upload.
    // `Source` above is a short display label (filename / "folder/ (N files)");
    // this is the raw text. Empty on signatures created before the field existed.
    public string SourceCode { get; private set; } = string.Empty;
    public string Type { get; private set; } = "component"; // form | wizard | grid | modal | uploader | tabs | page
    public string HumanDescription { get; private set; } = string.Empty;
    public string UserFlowsJson { get; private set; } = string.Empty;
    /// <summary>
    /// Element-relationship graph: serialized
    /// <c>{ "nodes": [ ... ], "edges": [ ... ] }</c> JSON produced by the
    /// relationship-analysis step. Captures the semantic connections that
    /// hide in the DOM tree — a parent container whose heading labels its
    /// children, a "select X related to Y" relationship, mutual-exclusion
    /// pairs, controls-the-state-of edges, etc. Empty until the step runs.
    /// Generation pipelines feed it to the test LLM as additional grounding.
    /// </summary>
    public string RelationshipsJson { get; private set; } = string.Empty;

    private readonly List<SignatureElement> _elements = new();
    public IReadOnlyList<SignatureElement> Elements => _elements;

    /// <summary>
    /// IDs of signatures THIS signature depends on. When a generation pipeline
    /// runs against this signature, the elements + descriptions of every
    /// transitive dependency are merged into the LLM context — see
    /// <c>IEffectiveSignatureResolver</c>. Stored as ID-only (not full
    /// navigation properties) because dependency rows are tiny lookup joins;
    /// the resolver does its own follow-up load.
    /// </summary>
    private readonly List<SignatureDependency> _dependencies = new();
    public IReadOnlyList<SignatureDependency> Dependencies => _dependencies;
    public IReadOnlyList<Guid> DependencyIds =>
        _dependencies.Select(d => d.DependsOnSignatureId).ToList();

    /// <summary>
    /// High-level FUNCTION CATALOGUE authored during the Functions wizard step.
    /// Two kinds live in this list (see <see cref="FunctionKind"/>): state
    /// readers that run automatically during gatherInformation, and actions
    /// the planner LLM composes at runtime. Kept sorted by <c>Order</c> for
    /// stable display and a deterministic gatherInformation call sequence.
    /// Cleared by <see cref="InvalidateDerivedCaches"/> because every body
    /// hard-references signature selectors — an element rename or removal
    /// would silently break bodies otherwise.
    /// </summary>
    private readonly List<SignatureFunction> _functions = new();
    public IReadOnlyList<SignatureFunction> Functions => _functions;

    private Signature() { }

    public static Signature Create(Guid ownerId, string name, string? source, string? type, string? humanDescription = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.");
        return new Signature
        {
            OwnerId = ownerId,
            Name = name.Trim(),
            Source = source?.Trim() ?? string.Empty,
            Type = string.IsNullOrWhiteSpace(type) ? "component" : type.Trim(),
            HumanDescription = humanDescription?.Trim() ?? string.Empty
        };
    }

    public void Rename(string name) { Name = name.Trim(); Touch(); }
    public void SetSourceCode(string? code) { SourceCode = code ?? string.Empty; Touch(); }
    public void SetType(string type) { Type = type.Trim(); Touch(); }
    public void BumpVersion(string version) { Version = version.Trim(); Touch(); }
    public void SetHumanDescription(string? description) { HumanDescription = description?.Trim() ?? string.Empty; Touch(); }
    public void SetUserFlows(string? json) { UserFlowsJson = json ?? string.Empty; Touch(); }
    public void SetRelationships(string? json) { RelationshipsJson = json ?? string.Empty; Touch(); }

    /// <summary>
    /// Wipe every signature-level cache that was derived from the element set.
    /// Called when the elements meaningfully change (selector renames, adds,
    /// removes, description rewrites). Without this the user sees flows, a
    /// relationship graph and function bodies that reference selectors which no
    /// longer exist — or worse, runs them.
    /// </summary>
    public void InvalidateDerivedCaches(DerivedCaches scope = DerivedCaches.All)
    {
        if (scope == DerivedCaches.None) return;
        if (scope.HasFlag(DerivedCaches.Flows)) UserFlowsJson = string.Empty;
        // The relationship graph is BUILT from selectors + descriptions, so an
        // element edit (rename / add / remove) makes the persisted graph stale
        // the same way it makes flows / analyze-state stale.
        if (scope.HasFlag(DerivedCaches.Relationships)) RelationshipsJson = string.Empty;
        // Function bodies hard-reference signature selectors verbatim. A
        // selector rename or removal invalidates them the same way it does
        // the relationship graph and analyze-state cache — safer to clear
        // and force a regenerate than to ship stale bodies that fail at
        // runtime with cryptic Playwright errors.
        if (scope.HasFlag(DerivedCaches.Functions)) _functions.Clear();
        Touch();
    }

    public void ReplaceElements(IEnumerable<SignatureElement> elements)
    {
        _elements.Clear();
        _elements.AddRange(elements);
        Touch();
    }

    public void AddElement(SignatureElement element)
    {
        _elements.Add(element);
        Touch();
    }

    /// <summary>
    /// Replace the entire function catalogue in one go. Used by both the
    /// generator (fresh LLM output → wholesale replacement) and manual
    /// re-orderings from the editor. Individual edits go through the
    /// <see cref="SignatureFunction"/> mutators directly.
    /// </summary>
    public void ReplaceFunctions(IEnumerable<SignatureFunction> functions)
    {
        _functions.Clear();
        _functions.AddRange(functions);
        Touch();
    }

    public void AddFunction(SignatureFunction function)
    {
        _functions.Add(function);
        Touch();
    }

    public void RemoveFunction(Guid functionId)
    {
        var existing = _functions.FirstOrDefault(f => f.Id == functionId);
        if (existing is null) return;
        _functions.Remove(existing);
        Touch();
    }

    /// <summary>
    /// Declare that this signature depends on another. Idempotent — adding a
    /// dependency that's already declared is a no-op. Cycle detection is
    /// performed at the application layer because the domain doesn't have
    /// access to the transitive dependency graph of the OTHER signature.
    /// </summary>
    public void AddDependency(Guid dependsOnSignatureId)
    {
        if (dependsOnSignatureId == Id)
            throw new InvalidOperationException("A signature cannot depend on itself.");
        if (dependsOnSignatureId == Guid.Empty)
            throw new ArgumentException("dependsOnSignatureId required.", nameof(dependsOnSignatureId));
        if (_dependencies.Any(d => d.DependsOnSignatureId == dependsOnSignatureId)) return;
        _dependencies.Add(new SignatureDependency(Id, dependsOnSignatureId));
        // Adding/removing a dep changes the effective signature for every
        // future generation — same invalidation story as element edits.
        InvalidateDerivedCaches();
    }

    public void RemoveDependency(Guid dependsOnSignatureId)
    {
        var existing = _dependencies.FirstOrDefault(d => d.DependsOnSignatureId == dependsOnSignatureId);
        if (existing is null) return;
        _dependencies.Remove(existing);
        InvalidateDerivedCaches();
    }
}

/// <summary>
/// Join row: signature S depends on signature D. Composite key
/// (SignatureId, DependsOnSignatureId). Cascade-deleted when either side
/// is removed.
/// </summary>
public class SignatureDependency
{
    public Guid SignatureId { get; private set; }
    public Guid DependsOnSignatureId { get; private set; }

    private SignatureDependency() { }

    public SignatureDependency(Guid signatureId, Guid dependsOnSignatureId)
    {
        if (signatureId == Guid.Empty) throw new ArgumentException("signatureId required.", nameof(signatureId));
        if (dependsOnSignatureId == Guid.Empty) throw new ArgumentException("dependsOnSignatureId required.", nameof(dependsOnSignatureId));
        SignatureId = signatureId;
        DependsOnSignatureId = dependsOnSignatureId;
    }
}

/// <summary>Which signature-level caches <see cref="Signature.InvalidateDerivedCaches"/> wipes.</summary>
[Flags]
public enum DerivedCaches
{
    None = 0,
    Flows = 1,
    Relationships = 2,
    Functions = 4,
    All = Flows | Relationships | Functions,
}

/// <summary>
/// Where an element came from. Provenance only — it does not change how the
/// element is used, but re-analysis steps that rebuild elements from source
/// code must keep <see cref="Recorded"/> ones, because the source analyzers
/// cannot see them (that is why they were recorded in the first place).
/// </summary>
public enum ElementOrigin
{
    /// <summary>Extracted from uploaded source code by an analyzer.</summary>
    Source,
    /// <summary>Learned from a "learn by watching" recording session.</summary>
    Recorded,
    /// <summary>Added by hand in the editor.</summary>
    Manual,
}

/// <summary>
/// Whether the element is something the user acts ON (interactive) or
/// something the test reads / asserts against / sorts by (informational).
/// </summary>
public enum ElementCategory
{
    Interactive,
    Informational
}

public class SignatureElement : Entity
{
    public Guid SignatureId { get; private set; }
    public string ElementSelector { get; private set; } = null!;
    public ElementCategory Category { get; private set; } = ElementCategory.Interactive;
    public string ElementType { get; private set; } = null!; // input | select | button | text | status | value | ...
    public string ElementDescription { get; private set; } = null!;

    /// <summary>
    /// Optional STRUCTURAL / LOGICAL description that goes beyond "what is this".
    /// Captures lifecycle (when the element exists, when it's created/destroyed),
    /// cardinality relationships to other elements (N+1 placements per N cards,
    /// one badge per row, etc.), index semantics (what does .nth(i) mean for
    /// THIS element relative to its siblings), and any non-obvious dependency
    /// on UI state. Empty when the element is self-explanatory.
    /// </summary>
    public string ElementLogic { get; private set; } = string.Empty;

    public int Order { get; private set; }

    public ElementOrigin Origin { get; private set; } = ElementOrigin.Source;

    private SignatureElement() { }

    public static SignatureElement Create(string selector, ElementCategory category, string type, string description, int order, string? logic = null, ElementOrigin origin = ElementOrigin.Source)
    {
        if (string.IsNullOrWhiteSpace(selector)) throw new ArgumentException("selector required.");
        if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("type required.");
        return new SignatureElement
        {
            ElementSelector = selector.Trim(),
            Category = category,
            ElementType = type.Trim().ToLowerInvariant(),
            ElementDescription = description?.Trim() ?? string.Empty,
            ElementLogic = logic?.Trim() ?? string.Empty,
            Order = order,
            Origin = origin
        };
    }
}
