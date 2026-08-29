using Mazda.Domain.Common;

namespace Mazda.Domain.Modules.Generations;

/// <summary>
/// Lifecycle of the design-time-into-plan side of a run:
///   • Pending    — freshly created, nothing done yet.
///   • Generating — mid-gather or mid-plan.
///   • Ready      — gather + plan complete; execution can start.
///   • Failed     — gather or plan blew up (see <c>FailureReason</c>).
/// The runtime side is tracked separately in <see cref="ExecutionStatus"/>.
/// </summary>
public enum GenerationStatus { Pending, Generating, Ready, Failed }

/// <summary>
/// Lifecycle of the actual browser execution phase. <c>Passed</c> maps to
/// <c>ExecutionEnvelope.Outcome == Ok</c>; <c>FailedTest</c> to
/// <c>Outcome == Failed</c> (a step body threw); <c>Errored</c> to a
/// harness-level failure (couldn't spawn Playwright, CDP unreachable, ...);
/// <c>Cancelled</c> to a user-abort during streaming.
/// </summary>
public enum ExecutionStatus { Idle, Running, Passed, FailedTest, Errored, Cancelled }

/// <summary>
/// How the run was initiated. Wizard = the user pressed Run in the app;
/// Mcp = an external agent invoked the signature's MCP tool. Both flow
/// through the same gather → plan → execute pipeline.
/// </summary>
public enum GenerationSource { Wizard, Mcp }

/// <summary>
/// A single "the user asked, Mazda did" record. Under the function-catalogue
/// runtime, one Generation represents a natural-language prompt turned into
/// a gathered UI state, then into a plan, then into an execution envelope.
///
/// Populated in order:
///   1. <see cref="Prompt"/> — supplied at creation.
///   2. <see cref="GatheredStateJson"/> — filled by IStateGatherer during
///      the plan phase.
///   3. <see cref="PlanJson"/> — filled by IPlanGenerator (only LLM call
///      on the request path).
///   4. <see cref="ExecutionEnvelopeJson"/> — filled by IPlanInterpreter
///      after execution.
///
/// Every serialised blob is opaque JSON; the domain doesn't parse them.
/// The application layer serialises records into these slots and the API
/// hands the strings straight back to the frontend, which renders them.
/// </summary>
public class Generation : Entity
{
    public Guid OwnerId { get; private set; }
    public Guid SignatureId { get; private set; }

    /// <summary>How this generation was created. Wizard by default.</summary>
    public GenerationSource Source { get; private set; } = GenerationSource.Wizard;

    /// <summary>When Source == Mcp, the tool that triggered the run.</summary>
    public Guid? McpToolId { get; private set; }

    /// <summary>
    /// The user's plain-English prompt. This is what the planner LLM saw.
    /// Persisted verbatim (trimmed) so the Runs page can display it and so
    /// re-planning is auditable ("we ran <em>this</em> plan for <em>this</em>
    /// prompt").
    /// </summary>
    public string Prompt { get; private set; } = string.Empty;

    /// <summary>
    /// Snapshot of the UI at plan time — the merged state functions' returns,
    /// serialised as
    /// <c>{ manifest, capturedAt, state: { GetTasks: [...], ... } }</c>.
    /// Null before the gather phase completes; set once, never mutated.
    /// </summary>
    public string? GatheredStateJson { get; private set; }

    /// <summary>
    /// The plan the planner LLM produced — serialised as
    /// <c>{ reasoning, steps: [{ id, call, args }, ...] }</c>. Same
    /// write-once semantics as <see cref="GatheredStateJson"/>.
    /// </summary>
    public string? PlanJson { get; private set; }

    /// <summary>
    /// The <see cref="ExecutionEnvelope"/> the interpreter produced after
    /// running the plan — serialised as
    /// <c>{ outcome, stepsTotal, stepsExecuted, steps: [...], capturedState, plan }</c>.
    /// Null while execution is running / hasn't started; set once at the
    /// end of the execute phase.
    /// </summary>
    public string? ExecutionEnvelopeJson { get; private set; }

    public GenerationStatus Status { get; private set; } = GenerationStatus.Pending;
    public string? FailureReason { get; private set; }
    public int TokensUsed { get; private set; }

    public ExecutionStatus ExecutionStatus { get; private set; } = ExecutionStatus.Idle;
    public string? ExecutionLog { get; private set; }
    public int? ExitCode { get; private set; }
    public DateTime? ExecutionStartedAt { get; private set; }
    public DateTime? ExecutionEndedAt { get; private set; }

    private Generation() { }

    public static Generation Create(Guid ownerId, Guid signatureId, string prompt)
    {
        if (signatureId == Guid.Empty) throw new ArgumentException("Signature required.", nameof(signatureId));
        return new Generation
        {
            OwnerId = ownerId,
            SignatureId = signatureId,
            Prompt = (prompt ?? string.Empty).Trim(),
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

    public void SetPrompt(string prompt)
    {
        Prompt = (prompt ?? string.Empty).Trim();
        Touch();
    }

    public void RecordGatheredState(string json)
    {
        GatheredStateJson = json ?? string.Empty;
        Touch();
    }

    public void RecordPlan(string json)
    {
        PlanJson = json ?? string.Empty;
        Touch();
    }

    public void RecordExecutionEnvelope(string json)
    {
        ExecutionEnvelopeJson = json ?? string.Empty;
        Touch();
    }

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
