using Mazda.Domain.Common;

namespace Mazda.Domain.Modules.Generations;

public enum GenerationStatus { Pending, Generating, Ready, Failed }

public enum ExecutionStatus { Idle, Running, Passed, FailedTest, Errored, Cancelled }

/// <summary>
/// How a generation was created. Wizard = the user drove the UI wizard step by
/// step. Mcp = the entire pipeline was kicked off in one shot by an MCP tool
/// invocation. The Runs page surfaces both; the badge/source is useful for
/// audit ("which of my runs were driven by an MCP client?").
/// </summary>
public enum GenerationSource { Wizard, Mcp }

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

    /// <summary>How this generation was created. Wizard by default.</summary>
    public GenerationSource Source { get; private set; } = GenerationSource.Wizard;

    /// <summary>When Source == Mcp, the tool that triggered the run.</summary>
    public Guid? McpToolId { get; private set; }

    public string SetupPrompt { get; private set; } = string.Empty;
    public string? SetupScript { get; private set; }

    // Analyze-state phase: a deterministic Playwright script that reads informational
    // elements off the live UI, captured raw output from running it, and an
    // LLM-built semantic JSON describing the current component state.
    public string? AnalyzeStateScript { get; private set; }
    public string? AnalyzeStateRawOutput { get; private set; }
    public string? AnalyzeStateJson { get; private set; }

    public string TestPrompt { get; private set; } = string.Empty;
    public string? TestScript { get; private set; }

    public string? FinalScript { get; private set; }

    public GenerationStatus Status { get; private set; } = GenerationStatus.Pending;
    public string? FailureReason { get; private set; }
    public int TokensUsed { get; private set; }

    public ExecutionStatus ExecutionStatus { get; private set; } = ExecutionStatus.Idle;
    public string? ExecutionLog { get; private set; }
    public int? ExitCode { get; private set; }
    public DateTime? ExecutionStartedAt { get; private set; }
    public DateTime? ExecutionEndedAt { get; private set; }

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

    /// <summary>Mark this generation as MCP-driven for audit/UI display. Idempotent.</summary>
    public void AttributeToMcp(Guid toolId)
    {
        if (toolId == Guid.Empty) throw new ArgumentException("Tool id required.", nameof(toolId));
        Source = GenerationSource.Mcp;
        McpToolId = toolId;
        Touch();
    }

    public void SetSetupPrompt(string prompt) { SetupPrompt = (prompt ?? string.Empty).Trim(); Touch(); }
    public void SetSetupScript(string script) { SetupScript = script ?? string.Empty; Touch(); }

    public void SetAnalyzeStateScript(string script) { AnalyzeStateScript = script ?? string.Empty; Touch(); }
    public void SetAnalyzeStateOutput(string raw, string json) { AnalyzeStateRawOutput = raw ?? string.Empty; AnalyzeStateJson = json ?? string.Empty; Touch(); }

    public void SetTestPrompt(string prompt) { TestPrompt = (prompt ?? string.Empty).Trim(); Touch(); }
    public void SetTestScript(string script) { TestScript = script ?? string.Empty; Touch(); }

    public void SetFinalScript(string script) { FinalScript = script ?? string.Empty; Touch(); }

    public void MarkGenerating() { Status = GenerationStatus.Generating; FailureReason = null; Touch(); }
    public void MarkReady(int additionalTokens = 0) { Status = GenerationStatus.Ready; TokensUsed += additionalTokens; Touch(); }
    public void MarkFailed(string reason) { Status = GenerationStatus.Failed; FailureReason = reason; Touch(); }

    public void MarkExecutionStarted()
    {
        ExecutionStatus = ExecutionStatus.Running;
        ExecutionLog = string.Empty;
        ExitCode = null;
        ExecutionStartedAt = DateTime.UtcNow;
        ExecutionEndedAt = null;
        Touch();
    }

    public void MarkExecutionFinished(ExecutionStatus terminalStatus, int? exitCode, string log)
    {
        if (terminalStatus == ExecutionStatus.Running || terminalStatus == ExecutionStatus.Idle)
            throw new ArgumentException("Terminal status required.", nameof(terminalStatus));
        ExecutionStatus = terminalStatus;
        ExitCode = exitCode;
        ExecutionLog = log;
        ExecutionEndedAt = DateTime.UtcNow;
        Touch();
    }
}
