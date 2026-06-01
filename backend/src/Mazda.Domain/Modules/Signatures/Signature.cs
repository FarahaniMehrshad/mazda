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
    public string Type { get; private set; } = "component"; // form | wizard | grid | modal | uploader | tabs | page
    public string HumanDescription { get; private set; } = string.Empty;
    public string UserFlowsJson { get; private set; } = string.Empty;
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

    private readonly List<SignatureElement> _elements = new();
    public IReadOnlyList<SignatureElement> Elements => _elements;

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
    public void SetType(string type) { Type = type.Trim(); Touch(); }
    public void BumpVersion(string version) { Version = version.Trim(); Touch(); }
    public void SetHumanDescription(string? description) { HumanDescription = description?.Trim() ?? string.Empty; Touch(); }
    public void SetUserFlows(string? json) { UserFlowsJson = json ?? string.Empty; Touch(); }
    public void SetLastSetupPrompt(string? prompt) { LastSetupPrompt = prompt?.Trim() ?? string.Empty; Touch(); }
    public void SetLastTestPrompt(string? prompt) { LastTestPrompt = prompt?.Trim() ?? string.Empty; Touch(); }
    public void SetLastSetupScript(string? script) { LastSetupScript = script ?? string.Empty; Touch(); }
    public void SetLastTestScript(string? script) { LastTestScript = script ?? string.Empty; Touch(); }
    public void SetLastFinalScript(string? script) { LastFinalScript = script ?? string.Empty; Touch(); }

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
