using Mazda.Domain.Common;

namespace Mazda.Domain.Modules.Generations;

public enum GenerationStatus { Pending, Generating, Ready, Failed }

/// <summary>
/// A multi-step request to turn (Signature + setup instructions + test prompt) into
/// a single Playwright .spec.ts file. The user drives the wizard:
///   1. SetupPrompt  → LLM → SetupScript  (user can edit)
///   2. TestPrompt   → LLM → TestScript   (user can edit)
///   3. (Setup + Test) → LLM merge → FinalScript (user can edit)
/// </summary>
public class Generation : Entity
{
    public Guid OwnerId { get; private set; }
    public Guid SignatureId { get; private set; }

    public string SetupPrompt { get; private set; } = string.Empty;
    public string? SetupScript { get; private set; }

    public string TestPrompt { get; private set; } = string.Empty;
    public string? TestScript { get; private set; }

    public string? FinalScript { get; private set; }

    public GenerationStatus Status { get; private set; } = GenerationStatus.Pending;
    public string? FailureReason { get; private set; }
    public int TokensUsed { get; private set; }

    private Generation() { }

    public static Generation Create(Guid ownerId, Guid signatureId)
    {
        if (signatureId == Guid.Empty) throw new ArgumentException("Signature required.");
        return new Generation
        {
            OwnerId = ownerId,
            SignatureId = signatureId,
        };
    }

    public void SetSetupPrompt(string prompt) { SetupPrompt = (prompt ?? string.Empty).Trim(); Touch(); }
    public void SetSetupScript(string script) { SetupScript = script ?? string.Empty; Touch(); }

    public void SetTestPrompt(string prompt) { TestPrompt = (prompt ?? string.Empty).Trim(); Touch(); }
    public void SetTestScript(string script) { TestScript = script ?? string.Empty; Touch(); }

    public void SetFinalScript(string script) { FinalScript = script ?? string.Empty; Touch(); }

    public void MarkGenerating() { Status = GenerationStatus.Generating; FailureReason = null; Touch(); }
    public void MarkReady(int additionalTokens = 0) { Status = GenerationStatus.Ready; TokensUsed += additionalTokens; Touch(); }
    public void MarkFailed(string reason) { Status = GenerationStatus.Failed; FailureReason = reason; Touch(); }
}
