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

public class SignatureElement : Entity
{
    public Guid SignatureId { get; private set; }
    public string ElementSelector { get; private set; } = null!;
    public string ElementType { get; private set; } = null!; // input | select | button | checkbox | ...
    public string ElementDescription { get; private set; } = null!;
    public int Order { get; private set; }

    private SignatureElement() { }

    public static SignatureElement Create(string selector, string type, string description, int order)
    {
        if (string.IsNullOrWhiteSpace(selector)) throw new ArgumentException("selector required.");
        if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("type required.");
        return new SignatureElement
        {
            ElementSelector = selector.Trim(),
            ElementType = type.Trim().ToLowerInvariant(),
            ElementDescription = description?.Trim() ?? string.Empty,
            Order = order
        };
    }
}
