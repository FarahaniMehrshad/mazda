using Mazda.Domain.Common;

namespace Mazda.Domain.Modules.Mcp;

/// <summary>
/// A signature exposed as an MCP tool, so an external agent can ask MAZDA to
/// drive that component's real UI.
///
/// The input schema is a single `prompt` string. On invocation the server runs
/// the function-catalogue pipeline — gather state, plan, execute — against the
/// backing signature and returns the execution envelope as the tool result.
///
/// The schema used to be a setup_prompt / test_prompt pair, from the
/// architecture where a run was two LLM calls (fixtures, then assertions).
/// That split stopped meaning anything when the catalogue replaced runtime
/// script generation; the transport still accepts the old pair and joins it,
/// but nothing inward carries the distinction.
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
    /// Tool description shown to the calling LLM. The backing signature's
    /// FUNCTION CATALOGUE is appended at list-time, so this field carries just
    /// the high-level intent.
    /// </summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>
    /// Prompt used when the caller omits one. Empty means "no default", and
    /// the tool then requires `prompt` on every call.
    ///
    /// This replaced a setup/test pair left over from the architecture where
    /// a run was two LLM calls (fixtures, then assertions). Under the function
    /// catalogue there is one prompt and one planner call; the pair was being
    /// concatenated with a blank line before anything looked at it.
    /// </summary>
    public string DefaultPrompt { get; private set; } = string.Empty;

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
        string? defaultPrompt,
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
            DefaultPrompt = defaultPrompt?.Trim() ?? string.Empty,
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
    public void SetDefaultPrompt(string? prompt)
    {
        DefaultPrompt = prompt?.Trim() ?? string.Empty;
        Touch();
    }
    public void SetEnabled(bool enabled) { Enabled = enabled; Touch(); }
}
