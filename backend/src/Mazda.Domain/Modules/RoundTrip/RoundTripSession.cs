using Mazda.Domain.Common;

namespace Mazda.Domain.Modules.RoundTrip;

/// <summary>
/// One round trip: a recording replayed through the function catalogue.
///
///   Draft          plan rebuilt from the recording, waiting for the author
///   Running        replays / comparisons / fixes in progress
///   NeedsPlan      the fix loop proposes a plan change; waiting for the author
///   NeedsAnswers   finished, but some steps need the author's "same or not?"
///   Done           every step reproduces the demonstration (or was accepted)
///   Failed         could not continue (browser gone, plan invalid, …)
/// </summary>
public enum RoundTripStatus { Draft, Running, NeedsPlan, NeedsAnswers, Done, Failed }

public class RoundTripSession : Entity
{
    public Guid OwnerId { get; private set; }
    public Guid SignatureId { get; private set; }
    public Guid RecordingId { get; private set; }
    public RoundTripStatus Status { get; private set; } = RoundTripStatus.Draft;
    /// <summary><c>{ steps:[{ call, args, contrastArgs?, episodes:[i], why }] }</c>.</summary>
    public string PlanJson { get; private set; } = "{\"steps\":[]}";
    /// <summary>A plan change the fix loop proposes, waiting for the author; empty otherwise.</summary>
    public string PendingPlanChangeJson { get; private set; } = string.Empty;
    /// <summary>Latest per-step judgements, questions and run summary.</summary>
    public string ResultJson { get; private set; } = string.Empty;
    /// <summary>What happened, in order: replays, verdicts, fixes, reverts.</summary>
    public string LogJson { get; private set; } = "[]";
    public string Error { get; private set; } = string.Empty;

    private RoundTripSession() { }

    public RoundTripSession(Guid ownerId, Guid signatureId, Guid recordingId, string planJson)
    {
        OwnerId = ownerId;
        SignatureId = signatureId;
        RecordingId = recordingId;
        PlanJson = planJson;
    }

    public void SetPlan(string json) { PlanJson = json; Touch(); }
    public void SetStatus(RoundTripStatus s, string? error = null) { Status = s; Error = error ?? string.Empty; Touch(); }
    public void SetPendingPlanChange(string json) { PendingPlanChangeJson = json ?? string.Empty; Touch(); }
    public void SetResult(string json) { ResultJson = json ?? string.Empty; Touch(); }
    public void SetLog(string json) { LogJson = string.IsNullOrWhiteSpace(json) ? "[]" : json; Touch(); }
}

/// <summary>Page before / after one step of one replay (JPEG, base64).</summary>
public class RoundTripShot
{
    public Guid SessionId { get; private set; }
    public int Run { get; private set; }
    public int Step { get; private set; }
    /// <summary>"before" or "after".</summary>
    public string Phase { get; private set; } = null!;
    public string JpegB64 { get; private set; } = string.Empty;

    private RoundTripShot() { }

    public RoundTripShot(Guid sessionId, int run, int step, string phase, string jpegB64)
    {
        SessionId = sessionId; Run = run; Step = step; Phase = phase; JpegB64 = jpegB64;
    }
}

/// <summary>A function body as it was before an automatic fix replaced it: what "revert" restores.</summary>
public class FunctionBodyRevision : Entity
{
    public Guid FunctionId { get; private set; }
    public Guid SignatureId { get; private set; }
    public Guid? SessionId { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;

    private FunctionBodyRevision() { }

    public FunctionBodyRevision(Guid functionId, Guid signatureId, Guid? sessionId, string body, string reason)
    {
        FunctionId = functionId; SignatureId = signatureId; SessionId = sessionId; Body = body; Reason = reason;
    }
}

/// <summary>
/// What the author told Mazda about a function's differences: keys of
/// differences that are "the same result" (accepted) or "not the same"
/// (rejected). Consulted on every later comparison so a question is never
/// asked twice.
/// </summary>
public class FunctionComparisonProfile
{
    public Guid FunctionId { get; private set; }
    public Guid SignatureId { get; private set; }
    public string AcceptedJson { get; private set; } = "[]";
    public string RejectedJson { get; private set; } = "[]";
    public DateTime UpdatedAt { get; private set; } = DateTime.UtcNow;

    private FunctionComparisonProfile() { }

    public FunctionComparisonProfile(Guid functionId, Guid signatureId) { FunctionId = functionId; SignatureId = signatureId; }

    public void Set(string acceptedJson, string rejectedJson)
    {
        AcceptedJson = acceptedJson; RejectedJson = rejectedJson; UpdatedAt = DateTime.UtcNow;
    }
}
