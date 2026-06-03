using Mazda.Domain.Common;

namespace Mazda.Domain.Modules.Mcp;

/// <summary>
/// A signature exposed as an MCP tool. The tool's input schema is fixed
/// (setup_prompt + test_prompt, both strings). On invocation the server runs
/// the full wizard pipeline (setup → analyze state → test → merge → execute)
/// against the backing signature and returns the execution log + pass/fail
/// status as the tool result.
///
/// Tool NAMES are sanitized to MCP's required shape (lowercase, snake_case)
/// and must be unique per user — they're how the calling LLM addresses the
/// tool. The DisplayName is the human label shown in the Mazda UI.
/// </summary>
public class McpTool : Entity
{
    public Guid OwnerId { get; private set; }
    public Guid SignatureId { get; private set; }
    public string Name { get; private set; } = null!;
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>
    /// Tool description shown to the calling LLM. The signature's signature
    /// (interactable elements, informational elements, user flows) is appended
    /// to this at list-time, so this field is just the high-level intent.
    /// </summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>Default setup prompt used when the caller omits one. Empty string means "no default".</summary>
    public string DefaultSetupPrompt { get; private set; } = string.Empty;

    /// <summary>Default test prompt used when the caller omits one.</summary>
    public string DefaultTestPrompt { get; private set; } = string.Empty;

    /// <summary>
    /// When false the tool is hidden from tools/list and tools/call returns
    /// "method not found". Lets the user disable a tool without deleting it
    /// (and losing the configured defaults).
    /// </summary>
    public bool Enabled { get; private set; } = true;

    private McpTool() { }

    public static McpTool Create(
        Guid ownerId,
        Guid signatureId,
        string name,
        string displayName,
        string? description,
        string? defaultSetupPrompt,
        string? defaultTestPrompt,
        bool enabled)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner required.", nameof(ownerId));
        if (signatureId == Guid.Empty) throw new ArgumentException("Signature required.", nameof(signatureId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));

        return new McpTool
        {
            OwnerId = ownerId,
            SignatureId = signatureId,
            Name = name.Trim().ToLowerInvariant(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? name.Trim() : displayName.Trim(),
            Description = description?.Trim() ?? string.Empty,
            DefaultSetupPrompt = defaultSetupPrompt ?? string.Empty,
            DefaultTestPrompt = defaultTestPrompt ?? string.Empty,
            Enabled = enabled,
        };
    }

    public void Rename(string name, string displayName)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));
        Name = name.Trim().ToLowerInvariant();
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? name.Trim() : displayName.Trim();
        Touch();
    }

    public void Retarget(Guid signatureId)
    {
        if (signatureId == Guid.Empty) throw new ArgumentException("Signature required.", nameof(signatureId));
        SignatureId = signatureId;
        Touch();
    }

    public void SetDescription(string? description) { Description = description?.Trim() ?? string.Empty; Touch(); }
    public void SetDefaults(string? setupPrompt, string? testPrompt)
    {
        DefaultSetupPrompt = setupPrompt ?? string.Empty;
        DefaultTestPrompt = testPrompt ?? string.Empty;
        Touch();
    }
    public void SetEnabled(bool enabled) { Enabled = enabled; Touch(); }
}
