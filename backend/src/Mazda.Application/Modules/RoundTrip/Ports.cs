using System.Text.Json.Nodes;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Application.Modules.RoundTrip;

// ── Replay ───────────────────────────────────────────────────────────

public sealed record RoundTripCall(string Call, string ArgsJson);

/// <summary>What one plan step did when replayed under the recorder's instrumentation.</summary>
/// <param name="Mutations">Page-side "mutations" summaries (same shape the recorder stores).</param>
/// <param name="Network">Requests the step caused: { method, url, reqBody, status, type }.</param>
public sealed record ReplayedStep(
    string Call,
    bool Ran,
    bool Threw,
    string? Error,
    string? ErrorStack,
    int DurationMs,
    IReadOnlyList<JsonObject> Mutations,
    IReadOnlyList<JsonObject> Network,
    IReadOnlyList<string> Console,
    string? UrlAfter,
    string? Aria,
    string? BeforeJpeg,
    string? AfterJpeg);

public sealed record ReplayRun(IReadOnlyList<ReplayedStep> Steps, string LogTail);

public interface IRoundTripReplayer
{
    /// <summary>
    /// Opens <paramref name="startUrl"/> (when given) on the attached tab and runs the calls in order,
    /// observing each with the same page-side recorder used for demonstrations. Stops at the first throw.
    /// Lines prefixed <c>__MZ_RT__</c> are streamed to <paramref name="feedId"/> for live progress.
    /// </summary>
    Task<ReplayRun> ReplayAsync(
        Guid feedId,
        Signature signature,
        IReadOnlyList<Signature>? dependencies,
        IReadOnlyList<RoundTripCall> calls,
        CdpTarget target,
        string? startUrl,
        CancellationToken ct = default);
}

// ── LLM steps ────────────────────────────────────────────────────────

/// <param name="Episodes">Indices (<c>i</c>) of the demonstrated interactions this call reproduces.</param>
/// <param name="ContrastArgsJson">Another valid choice for the same call, used once to learn which values carry the choice.</param>
public sealed record PlanStep(string Call, string ArgsJson, IReadOnlyList<int> Episodes, string? ContrastArgsJson, string Why);

public sealed record ReversePlanResult(IReadOnlyList<PlanStep> Steps, string Notes, int TokensUsed);

/// <summary>Rebuilds, from a recording, the function calls that reproduce what the user did.</summary>
public interface IReversePlanner
{
    Task<ReversePlanResult> PlanAsync(
        Signature signature,
        IReadOnlyList<SignatureFunction> actions,
        string recordingTitle,
        string recordingIntent,
        string episodesJson,
        CancellationToken ct = default);
}

/// <param name="Verdict">same | different | unsure</param>
public sealed record JudgeVerdict(string Verdict, string Reason, int TokensUsed);

/// <summary>Looks at before/after pictures of the demonstration and the replay, plus the textual differences.</summary>
public interface IEffectJudge
{
    Task<JudgeVerdict> JudgeAsync(
        string stepDescription,
        string differences,
        string? demoBefore, string? demoAfter,
        string? replayBefore, string? replayAfter,
        CancellationToken ct = default);
}

/// <param name="Fault">function | plan | earlier-step | nondeterminism</param>
/// <param name="Body">Replacement body when <paramref name="Fault"/> is "function".</param>
/// <param name="PlanCall">Replacement call when <paramref name="Fault"/> is "plan".</param>
public sealed record RoundTripFix(string Fault, string? Body, string? PlanCall, string? PlanArgsJson, string Explanation, int TokensUsed);

/// <summary>Evidence about one step that did not reproduce the demonstration.</summary>
public sealed record RoundTripEvidence(
    string StepDescription,
    string ArgsJson,
    string DemoSteps,
    string Differences,
    string DemoRequests,
    string ReplayRequests,
    string? Error,
    string? ErrorStack,
    IReadOnlyList<string> Console,
    string? Aria,
    string EarlierSteps,
    string? DemoAfter,
    string? ReplayAfter,
    string? PreviousAttempts);

/// <summary>Diagnoses where a divergence comes from and proposes a change (body or plan step).</summary>
public interface IRoundTripFixer
{
    Task<RoundTripFix> DiagnoseAsync(
        Signature signature,
        SignatureFunction function,
        IReadOnlyList<SignatureFunction> actions,
        RoundTripEvidence evidence,
        CancellationToken ct = default);
}
