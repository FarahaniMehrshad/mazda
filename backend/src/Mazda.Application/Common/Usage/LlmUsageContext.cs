namespace Mazda.Application.Common.Usage;

/// <summary>
/// Who and what the LLM calls on this async flow are for. The LLM services are
/// singletons with no user context, so attribution travels like
/// <see cref="Tracing.TraceContext"/>: an AsyncLocal that background work
/// started with <c>Task.Run</c> inherits.
///
/// <see cref="LlmUsageScopeBehavior{TRequest,TResponse}"/> opens a scope for
/// every MediatR request (owner + a kind named after the request); handlers that
/// reach the LLM name their subject with <see cref="SetSubject"/>.
/// </summary>
public static class LlmUsageContext
{
    private static readonly AsyncLocal<LlmUsageScope?> CurrentScope = new();

    public static LlmUsageScope? Current => CurrentScope.Value;

    /// <summary>Starts a scope for this flow and the flows it starts.</summary>
    public static LlmUsageScope Begin(Guid? ownerId, string kind, Guid? subjectId = null, string? subjectLabel = null)
    {
        var s = new LlmUsageScope { OwnerId = ownerId, Kind = kind, SubjectId = subjectId, SubjectLabel = subjectLabel ?? "" };
        CurrentScope.Value = s;
        return s;
    }

    /// <summary>
    /// Names what the calls are for. Changes the current scope in place, so a
    /// handler's call reaches the scope its request opened; with no scope it
    /// starts one.
    /// </summary>
    public static void SetSubject(string kind, Guid? subjectId, string? subjectLabel)
    {
        var s = CurrentScope.Value;
        if (s is null) { Begin(null, kind, subjectId, subjectLabel); return; }
        s.Kind = kind;
        s.SubjectId = subjectId;
        s.SubjectLabel = subjectLabel ?? "";
    }
}

public sealed class LlmUsageScope
{
    public Guid? OwnerId { get; set; }
    public string Kind { get; set; } = "";
    public Guid? SubjectId { get; set; }
    public string SubjectLabel { get; set; } = "";
}

/// <summary>Kinds of work that use the LLM, as stored in the ledger.</summary>
public static class LlmUsageKinds
{
    public const string Run = "run";
    public const string RoundTrip = "roundtrip";
    public const string Catalogue = "catalogue";
    public const string FunctionFix = "function-fix";
    public const string RecordingAnalysis = "recording-analysis";
    public const string SignatureAnalysis = "signature-analysis";
    public const string Mcp = "mcp";
    public const string ConnectionTest = "connection-test";
    public const string Unattributed = "unattributed";
}
