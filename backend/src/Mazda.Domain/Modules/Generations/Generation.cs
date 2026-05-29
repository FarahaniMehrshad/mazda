using Mazda.Domain.Common;

namespace Mazda.Domain.Modules.Generations;

public enum GenerationStatus { Pending, Generating, Ready, Failed }

/// <summary>
/// A request to turn (Signature + target URL + English prompt) into a Playwright .spec.ts file.
/// Replaces the original live-execution concept — the user runs the script locally.
/// </summary>
public class Generation : Entity
{
    public Guid OwnerId { get; private set; }
    public Guid SignatureId { get; private set; }
    public string TargetUrl { get; private set; } = null!;
    public string Prompt { get; private set; } = null!;
    public GenerationStatus Status { get; private set; } = GenerationStatus.Pending;
    public string? GeneratedScript { get; private set; }
    public string? FailureReason { get; private set; }
    public int TokensUsed { get; private set; }

    private Generation() { }

    public static Generation Create(Guid ownerId, Guid signatureId, string targetUrl, string prompt)
    {
        if (string.IsNullOrWhiteSpace(targetUrl)) throw new ArgumentException("Target URL required.");
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out _)) throw new ArgumentException("Target URL must be absolute.");
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt required.");

        return new Generation
        {
            OwnerId = ownerId,
            SignatureId = signatureId,
            TargetUrl = targetUrl.Trim(),
            Prompt = prompt.Trim()
        };
    }

    public void MarkGenerating() { Status = GenerationStatus.Generating; Touch(); }

    public void MarkReady(string script, int tokensUsed)
    {
        GeneratedScript = script;
        TokensUsed = tokensUsed;
        Status = GenerationStatus.Ready;
        Touch();
    }

    public void MarkFailed(string reason)
    {
        FailureReason = reason;
        Status = GenerationStatus.Failed;
        Touch();
    }
}
