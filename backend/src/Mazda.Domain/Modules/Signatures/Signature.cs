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
    // Most-recent setup / test prompts used against this signature. New
    // generations created off the signature pre-fill from these.
    public string LastSetupPrompt { get; private set; } = string.Empty;
    public string LastTestPrompt { get; private set; } = string.Empty;
    // Most-recent generated setup / test SCRIPTS for this signature. Same
    // pre-fill story — restored onto fresh generations so the wizard isn't
    // empty after a reload.
    public string LastSetupScript { get; private set; } = string.Empty;
    public string LastTestScript { get; private set; } = string.Empty;
    public string LastFinalScript { get; private set; } = string.Empty;
    public string LastAnalyzeStateScript { get; private set; } = string.Empty;
    // Most-recent analyze-state RUN OUTPUT for this signature. The script alone
    // isn't useful on a fresh generation — the captured raw output + the LLM-built
    // semantic JSON are the actual ground-truth payload the test-generation step
    // consumes. Persisting them across generations means a user can keep coming
    // back without having to re-Run after every minor edit elsewhere.
    public string LastAnalyzeStateRawOutput { get; private set; } = string.Empty;
    public string LastAnalyzeStateJson { get; private set; } = string.Empty;
    // Most recently used analyze-state scope selector. New generations
    // pre-fill from this so the user doesn't have to re-type the scope per
    // run; persisted on signature because the scope is almost always a
    // property of the COMPONENT (a modal selector, a card root) not the
    // run.
    public string LastAnalyzeStateScope { get; private set; } = string.Empty;

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
    public void SetLastSetupPrompt(string? prompt) { LastSetupPrompt = prompt?.Trim() ?? string.Empty; Touch(); }
    public void SetLastTestPrompt(string? prompt) { LastTestPrompt = prompt?.Trim() ?? string.Empty; Touch(); }
    public void SetLastSetupScript(string? script) { LastSetupScript = script ?? string.Empty; Touch(); }
    public void SetLastTestScript(string? script) { LastTestScript = script ?? string.Empty; Touch(); }
    public void SetLastFinalScript(string? script) { LastFinalScript = script ?? string.Empty; Touch(); }
    public void SetLastAnalyzeStateScript(string? script) { LastAnalyzeStateScript = script ?? string.Empty; Touch(); }
    public void SetLastAnalyzeStateScope(string? scope) { LastAnalyzeStateScope = scope?.Trim() ?? string.Empty; Touch(); }

    /// <summary>
    /// Wipe every signature-level cache that was derived from the element set.
    /// Called when the elements meaningfully change (selector renames, adds,
    /// removes, description rewrites). Without this, future generations
    /// pre-fill from these "Last used" buffers and the user sees scripts /
    /// state JSON / user flows that reference selectors that no longer exist
    /// — or worse, runs them.
    ///
    /// Kept narrow: prompts (LastSetupPrompt, LastTestPrompt) are user-authored
    /// and stay; LastSetupScript is usually just "navigate to URL" and is left
    /// alone for the same reason. Everything else that mentions selectors goes.
    /// </summary>
    public void InvalidateDerivedCaches()
    {
        LastTestScript = string.Empty;
        LastFinalScript = string.Empty;
        LastAnalyzeStateScript = string.Empty;
        LastAnalyzeStateRawOutput = string.Empty;
        LastAnalyzeStateJson = string.Empty;
        UserFlowsJson = string.Empty;
        // The relationship graph is BUILT from selectors + descriptions, so an
        // element edit (rename / add / remove) makes the persisted graph stale
        // the same way it makes flows / analyze-state stale.
        RelationshipsJson = string.Empty;
        Touch();
    }
    public void SetLastAnalyzeStateOutput(string? raw, string? json)
    {
        LastAnalyzeStateRawOutput = raw ?? string.Empty;
        LastAnalyzeStateJson = json ?? string.Empty;
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

    private SignatureElement() { }

    public static SignatureElement Create(string selector, ElementCategory category, string type, string description, int order, string? logic = null)
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
            Order = order
        };
    }
}
