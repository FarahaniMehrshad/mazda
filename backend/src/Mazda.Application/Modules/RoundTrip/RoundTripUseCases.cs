using System.Text.Json.Nodes;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Common;
using Mazda.Domain.Modules.Recordings;
using Mazda.Domain.Modules.RoundTrip;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Application.Modules.RoundTrip;

// Round trip: replay a recording through the function catalogue, compare
// every step with what the user did, and fix what differs.
//
//   start   -> the reverse planner rebuilds the calls from the recording (Draft)
//   plan    -> the author edits it
//   confirm -> nothing runs before this; then replays, comparisons, fixes (background)
//   answer  -> the author's "same or not?" for steps Mazda could not decide
//   plan-change / revert -> the author decides on the fix loop's proposals

public record RoundTripStepDto(int Index, string Call, string ArgsJson, List<int> Episodes, string? ContrastArgsJson, string Why, List<string> DemoSteps);

public record RoundTripDto(
    Guid Id,
    Guid SignatureId,
    Guid RecordingId,
    string RecordingLabel,
    string Status,
    List<RoundTripStepDto> Plan,
    string Notes,
    string? PlanProblem,
    PlanChangeModel? PendingPlanChange,
    ResultModel? Result,
    List<LogEntry> Log,
    string Error,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record RoundTripSummaryDto(Guid Id, Guid RecordingId, string RecordingLabel, string Status, string Summary, DateTime CreatedAt);

internal static class RoundTripMapping
{
    public static JsonObject? Episodes(RecordingSession? rec)
    {
        if (rec is null || string.IsNullOrWhiteSpace(rec.EpisodesJson)) return null;
        try { return JsonNode.Parse(rec.EpisodesJson) as JsonObject; } catch { return null; }
    }

    public static RoundTripDto ToDto(RoundTripSession s, RecordingSession? rec, IReadOnlyList<SignatureFunction> actions)
    {
        var plan = RoundTripJson.Read<PlanModel>(s.PlanJson) ?? new PlanModel(new(), "");
        var doc = Episodes(rec);
        var steps = plan.Steps.Select((p, i) => new RoundTripStepDto(i, p.Call, p.ArgsJson, p.Episodes, p.ContrastArgsJson, p.Why,
            p.Episodes.Select(e => doc is not null && RoundTripDemo.FindEpisode(doc, e) is { } ep ? $"{e}. {RoundTripDemo.Describe(ep)}" : $"{e}. (not in the recording)").ToList())).ToList();
        return new RoundTripDto(s.Id, s.SignatureId, s.RecordingId, rec?.Label ?? "(deleted recording)", s.Status.ToString(),
            steps, plan.Notes, PlanRules.Problem(plan, actions, doc),
            RoundTripJson.Read<PlanChangeModel>(s.PendingPlanChangeJson),
            RoundTripJson.Read<ResultModel>(s.ResultJson),
            RoundTripJson.Read<List<LogEntry>>(s.LogJson) ?? new List<LogEntry>(),
            s.Error, s.CreatedAt, s.UpdatedAt);
    }

    public static List<SignatureFunction> Actions(Signature sig) => sig.Functions.Where(f => f.Kind == FunctionKind.Action).ToList();
}

// ── Start: rebuild the plan from the recording ──────────────────────

public record StartRoundTripCommand(Guid SignatureId, Guid RecordingId) : IRequest<Result<RoundTripDto>>;

public class StartRoundTripHandler(
    ISignatureRepository signatures, IRecordingRepository recordings, IRoundTripRepository roundTrips,
    IReversePlanner planner, ICurrentUser me, IUnitOfWork uow)
    : IRequestHandler<StartRoundTripCommand, Result<RoundTripDto>>
{
    public async Task<Result<RoundTripDto>> Handle(StartRoundTripCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<RoundTripDto>(Error.Validation("Not authenticated."));
        var sig = await signatures.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<RoundTripDto>(Error.NotFound("Signature"));
        var rec = await recordings.FindByIdAsync(cmd.RecordingId, me.UserId.Value, ct);
        if (rec is null || rec.SignatureId != sig.Id) return Result.Failure<RoundTripDto>(Error.NotFound("Recording"));
        if (rec.Status != RecordingStatus.Completed || string.IsNullOrWhiteSpace(rec.EpisodesJson))
            return Result.Failure<RoundTripDto>(Error.Validation("That recording is not finished, or captured no interactions."));
        var actions = RoundTripMapping.Actions(sig);
        if (actions.Count == 0) return Result.Failure<RoundTripDto>(Error.Validation("Generate the function catalogue first."));

        ReversePlanResult planned;
        try { planned = await planner.PlanAsync(sig, actions, rec.Label, rec.Intent, rec.EpisodesJson, ct); }
        catch (Exception ex) { return Result.Failure<RoundTripDto>(Error.External($"Could not rebuild the plan: {ExceptionMessage.Describe(ex)}")); }

        var plan = new PlanModel(planned.Steps.Select(s => new PlanStepModel(s.Call, s.ArgsJson, s.Episodes.ToList(), s.ContrastArgsJson, s.Why)).ToList(), planned.Notes);
        var session = new RoundTripSession(me.UserId.Value, sig.Id, rec.Id, RoundTripJson.Write(plan));
        session.SetLog(RoundTripJson.Write(new List<LogEntry> { new(DateTime.UtcNow, "plan", $"Plan rebuilt from “{rec.Label}”: {plan.Steps.Count} step(s). Waiting for your confirmation.") }));
        await roundTrips.AddAsync(session, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(RoundTripMapping.ToDto(session, rec, actions));
    }
}

// ── Read ─────────────────────────────────────────────────────────────

public record ListRoundTripsQuery(Guid SignatureId) : IRequest<Result<IReadOnlyList<RoundTripSummaryDto>>>;

public class ListRoundTripsHandler(IRoundTripRepository roundTrips, IRecordingRepository recordings, ICurrentUser me)
    : IRequestHandler<ListRoundTripsQuery, Result<IReadOnlyList<RoundTripSummaryDto>>>
{
    public async Task<Result<IReadOnlyList<RoundTripSummaryDto>>> Handle(ListRoundTripsQuery q, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<IReadOnlyList<RoundTripSummaryDto>>(Error.Validation("Not authenticated."));
        var list = await roundTrips.ListBySignatureAsync(q.SignatureId, me.UserId.Value, ct);
        var labels = (await recordings.ListBySignatureAsync(q.SignatureId, me.UserId.Value, ct)).ToDictionary(r => r.Id, r => r.Label);
        return Result.Success<IReadOnlyList<RoundTripSummaryDto>>(list.Select(s => new RoundTripSummaryDto(
            s.Id, s.RecordingId, labels.GetValueOrDefault(s.RecordingId, "(deleted recording)"), s.Status.ToString(),
            RoundTripJson.Read<ResultModel>(s.ResultJson)?.Summary ?? "", s.CreatedAt)).ToList());
    }
}

public record GetRoundTripQuery(Guid Id) : IRequest<Result<RoundTripDto>>;

public class GetRoundTripHandler(IRoundTripRepository roundTrips, ISignatureRepository signatures, IRecordingRepository recordings, ICurrentUser me)
    : IRequestHandler<GetRoundTripQuery, Result<RoundTripDto>>
{
    public async Task<Result<RoundTripDto>> Handle(GetRoundTripQuery q, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<RoundTripDto>(Error.Validation("Not authenticated."));
        var s = await roundTrips.FindAsync(q.Id, me.UserId.Value, ct);
        if (s is null) return Result.Failure<RoundTripDto>(Error.NotFound("Round trip"));
        var sig = await signatures.FindByIdAsync(s.SignatureId, me.UserId.Value, ct);
        var rec = await recordings.FindByIdAsync(s.RecordingId, me.UserId.Value, ct);
        return Result.Success(RoundTripMapping.ToDto(s, rec, sig is null ? new List<SignatureFunction>() : RoundTripMapping.Actions(sig)));
    }
}

/// <param name="Side">"demo" (the recording) or "replay".</param>
public record GetRoundTripShotQuery(Guid Id, string Side, int Step, string Phase, int? Run) : IRequest<Result<string>>;

public class GetRoundTripShotHandler(IRoundTripRepository roundTrips, IRecordingRepository recordings, ICurrentUser me)
    : IRequestHandler<GetRoundTripShotQuery, Result<string>>
{
    public async Task<Result<string>> Handle(GetRoundTripShotQuery q, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<string>(Error.Validation("Not authenticated."));
        var s = await roundTrips.FindAsync(q.Id, me.UserId.Value, ct);
        if (s is null) return Result.Failure<string>(Error.NotFound("Round trip"));
        string? b64;
        if (q.Side == "demo")
        {
            var plan = RoundTripJson.Read<PlanModel>(s.PlanJson);
            if (plan is null || q.Step < 0 || q.Step >= plan.Steps.Count) return Result.Failure<string>(Error.NotFound("Picture"));
            var demo = RoundTripDemo.Build(plan, await recordings.ListAllEventsAsync(s.RecordingId, ct));
            var key = q.Phase == "before" ? demo.ShotKeys[q.Step].Before : demo.ShotKeys[q.Step].After;
            b64 = key is null ? null : await recordings.FindScreenshotAsync(s.RecordingId, key, ct);
        }
        else
        {
            var run = q.Run ?? RoundTripJson.Read<ResultModel>(s.ResultJson)?.Steps.ElementAtOrDefault(q.Step)?.Run ?? 0;
            b64 = await roundTrips.FindShotAsync(s.Id, run, q.Step, q.Phase, ct);
        }
        return b64 is null ? Result.Failure<string>(Error.NotFound("Picture")) : Result.Success(b64);
    }
}

// ── Edit the plan ────────────────────────────────────────────────────

public record UpdateRoundTripPlanCommand(Guid Id, List<PlanStepModel> Steps) : IRequest<Result<RoundTripDto>>;

public class UpdateRoundTripPlanHandler(IRoundTripRepository roundTrips, ISignatureRepository signatures, IRecordingRepository recordings, ICurrentUser me, IUnitOfWork uow)
    : IRequestHandler<UpdateRoundTripPlanCommand, Result<RoundTripDto>>
{
    public async Task<Result<RoundTripDto>> Handle(UpdateRoundTripPlanCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<RoundTripDto>(Error.Validation("Not authenticated."));
        var s = await roundTrips.FindAsync(cmd.Id, me.UserId.Value, ct);
        if (s is null) return Result.Failure<RoundTripDto>(Error.NotFound("Round trip"));
        if (s.Status == RoundTripStatus.Running) return Result.Failure<RoundTripDto>(Error.Conflict("The round trip is running; wait for it to finish."));
        var sig = await signatures.FindByIdAsync(s.SignatureId, me.UserId.Value, ct);
        var rec = await recordings.FindByIdAsync(s.RecordingId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<RoundTripDto>(Error.NotFound("Signature"));
        var old = RoundTripJson.Read<PlanModel>(s.PlanJson);
        var plan = new PlanModel(cmd.Steps.Select(x => x with
        {
            ArgsJson = string.IsNullOrWhiteSpace(x.ArgsJson) ? "{}" : x.ArgsJson.Trim(),
            ContrastArgsJson = string.IsNullOrWhiteSpace(x.ContrastArgsJson) ? null : x.ContrastArgsJson.Trim(),
            Episodes = x.Episodes ?? new List<int>(),
            Why = x.Why ?? "",
        }).ToList(), old?.Notes ?? "");
        if (PlanRules.Problem(plan, RoundTripMapping.Actions(sig), RoundTripMapping.Episodes(rec)) is { } problem)
            return Result.Failure<RoundTripDto>(Error.Validation(problem));
        s.SetPlan(RoundTripJson.Write(plan));
        s.SetPendingPlanChange(string.Empty);
        s.SetStatus(RoundTripStatus.Draft);
        await uow.SaveChangesAsync(ct);
        return Result.Success(RoundTripMapping.ToDto(s, rec, RoundTripMapping.Actions(sig)));
    }
}

// ── Confirm and run ──────────────────────────────────────────────────

/// <param name="Confirmed">The author reviewed the plan and acknowledges the replays perform the actions for real.</param>
public record ConfirmRoundTripCommand(Guid Id, bool Confirmed) : IRequest<Result<RoundTripDto>>;

public class ConfirmRoundTripHandler(
    IRoundTripRepository roundTrips, ISignatureRepository signatures, IRecordingRepository recordings,
    IExecutionRegistry feed, IServiceScopeFactory scopes, ICurrentUser me, IUnitOfWork uow)
    : IRequestHandler<ConfirmRoundTripCommand, Result<RoundTripDto>>
{
    public async Task<Result<RoundTripDto>> Handle(ConfirmRoundTripCommand cmd, CancellationToken _ct)
    {
        if (me.UserId is null) return Result.Failure<RoundTripDto>(Error.Validation("Not authenticated."));
        if (!cmd.Confirmed)
            return Result.Failure<RoundTripDto>(Error.Validation("Review the plan and confirm: the replays perform these actions for real in the app."));
        var s = await roundTrips.FindAsync(cmd.Id, me.UserId.Value, _ct);
        if (s is null) return Result.Failure<RoundTripDto>(Error.NotFound("Round trip"));
        if (s.Status == RoundTripStatus.Running) return Result.Failure<RoundTripDto>(Error.Conflict("Already running."));
        var sig = await signatures.FindByIdAsync(s.SignatureId, me.UserId.Value, _ct);
        var rec = await recordings.FindByIdAsync(s.RecordingId, me.UserId.Value, _ct);
        if (sig is null || rec is null) return Result.Failure<RoundTripDto>(Error.NotFound("Signature or recording"));
        var plan = RoundTripJson.Read<PlanModel>(s.PlanJson);
        if (plan is null || PlanRules.Problem(plan, RoundTripMapping.Actions(sig), RoundTripMapping.Episodes(rec)) is { } problem)
            return Result.Failure<RoundTripDto>(Error.Validation(plan is null ? "The plan could not be read." : PlanRules.Problem(plan, RoundTripMapping.Actions(sig), RoundTripMapping.Episodes(rec))!));

        s.SetPendingPlanChange(string.Empty);
        s.SetStatus(RoundTripStatus.Running);
        await uow.SaveChangesAsync(_ct);
        RoundTripLauncher.Launch(s.Id, me.UserId.Value, feed, scopes);
        return Result.Success(RoundTripMapping.ToDto(s, rec, RoundTripMapping.Actions(sig)));
    }
}

internal static class RoundTripLauncher
{
    /// <summary>Runs the engine in its own scope; the request returns at once and the feed carries progress.</summary>
    public static void Launch(Guid sessionId, Guid ownerId, IExecutionRegistry feed, IServiceScopeFactory scopes)
    {
        feed.Reset(sessionId);
        feed.Append(sessionId, "Starting…");
        _ = Task.Run(async () =>
        {
            using var scope = scopes.CreateScope();
            var engine = ActivatorUtilities.CreateInstance<RoundTripEngine>(scope.ServiceProvider);
            await engine.RunAsync(sessionId, ownerId, CancellationToken.None);
        });
    }
}

// ── The author decides ───────────────────────────────────────────────

public record DecidePlanChangeCommand(Guid Id, bool Accept) : IRequest<Result<RoundTripDto>>;

public class DecidePlanChangeHandler(
    IRoundTripRepository roundTrips, ISignatureRepository signatures, IRecordingRepository recordings,
    IExecutionRegistry feed, IServiceScopeFactory scopes, ICurrentUser me, IUnitOfWork uow)
    : IRequestHandler<DecidePlanChangeCommand, Result<RoundTripDto>>
{
    public async Task<Result<RoundTripDto>> Handle(DecidePlanChangeCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<RoundTripDto>(Error.Validation("Not authenticated."));
        var s = await roundTrips.FindAsync(cmd.Id, me.UserId.Value, ct);
        if (s is null) return Result.Failure<RoundTripDto>(Error.NotFound("Round trip"));
        var change = RoundTripJson.Read<PlanChangeModel>(s.PendingPlanChangeJson);
        if (s.Status != RoundTripStatus.NeedsPlan || change is null) return Result.Failure<RoundTripDto>(Error.Validation("There is no plan change waiting."));
        var sig = await signatures.FindByIdAsync(s.SignatureId, me.UserId.Value, ct);
        var rec = await recordings.FindByIdAsync(s.RecordingId, me.UserId.Value, ct);
        var log = RoundTripJson.Read<List<LogEntry>>(s.LogJson) ?? new();
        s.SetPendingPlanChange(string.Empty);
        if (cmd.Accept)
        {
            var plan = RoundTripJson.Read<PlanModel>(s.PlanJson)!;
            plan.Steps[change.Step] = plan.Steps[change.Step] with { Call = change.Call, ArgsJson = change.ArgsJson };
            s.SetPlan(RoundTripJson.Write(plan));
            log.Add(new LogEntry(DateTime.UtcNow, "plan", $"You accepted the change to step {change.Step + 1}: {change.Call} {change.ArgsJson}. Replaying again."));
            s.SetLog(RoundTripJson.Write(log));
            s.SetStatus(RoundTripStatus.Running);
            await uow.SaveChangesAsync(ct);
            RoundTripLauncher.Launch(s.Id, me.UserId.Value, feed, scopes);
        }
        else
        {
            log.Add(new LogEntry(DateTime.UtcNow, "plan", $"You kept step {change.Step + 1} as it was."));
            s.SetLog(RoundTripJson.Write(log));
            s.SetStatus(RoundTripStatus.Done);
            await uow.SaveChangesAsync(ct);
        }
        return Result.Success(RoundTripMapping.ToDto(s, rec, sig is null ? new() : RoundTripMapping.Actions(sig)));
    }
}

/// <param name="Same">The author's answer: did the function have the same result as the demonstration?</param>
public record AnswerRoundTripQuestionCommand(Guid Id, int Step, bool Same) : IRequest<Result<RoundTripDto>>;

public class AnswerRoundTripQuestionHandler(
    IRoundTripRepository roundTrips, ISignatureRepository signatures, IRecordingRepository recordings, ICurrentUser me, IUnitOfWork uow)
    : IRequestHandler<AnswerRoundTripQuestionCommand, Result<RoundTripDto>>
{
    public async Task<Result<RoundTripDto>> Handle(AnswerRoundTripQuestionCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<RoundTripDto>(Error.Validation("Not authenticated."));
        var s = await roundTrips.FindAsync(cmd.Id, me.UserId.Value, ct);
        if (s is null) return Result.Failure<RoundTripDto>(Error.NotFound("Round trip"));
        var sig = await signatures.FindByIdAsync(s.SignatureId, me.UserId.Value, ct);
        var rec = await recordings.FindByIdAsync(s.RecordingId, me.UserId.Value, ct);
        var result = RoundTripJson.Read<ResultModel>(s.ResultJson);
        var step = result?.Steps.ElementAtOrDefault(cmd.Step);
        if (sig is null || result is null || step is null) return Result.Failure<RoundTripDto>(Error.NotFound("Step"));
        var fn = sig.Functions.FirstOrDefault(f => f.Name == step.Call);
        if (fn is null) return Result.Failure<RoundTripDto>(Error.NotFound("Function"));

        // Remember the answer per difference, so the same question is never asked again for this function.
        var profile = await roundTrips.GetOrCreateProfileAsync(fn.Id, sig.Id, ct);
        var accepted = RoundTripJson.Read<List<string>>(profile.AcceptedJson) ?? new();
        var rejected = RoundTripJson.Read<List<string>>(profile.RejectedJson) ?? new();
        foreach (var key in step.Findings.Where(f => f.Severity != "note").Select(f => f.Key).Distinct())
        {
            accepted.Remove(key); rejected.Remove(key);
            (cmd.Same ? accepted : rejected).Add(key);
        }
        profile.Set(RoundTripJson.Write(accepted), RoundTripJson.Write(rejected));

        result.Steps[cmd.Step] = step with { Verdict = cmd.Same ? "equivalent" : "different", Question = null };
        var log = RoundTripJson.Read<List<LogEntry>>(s.LogJson) ?? new();
        log.Add(new LogEntry(DateTime.UtcNow, "question",
            $"You said step {cmd.Step + 1} ({step.Call}) {(cmd.Same ? "has the same result" : "does NOT have the same result")}." +
            (cmd.Same ? "" : " Run the round trip again to let Mazda fix it."), step.Call));
        s.SetResult(RoundTripJson.Write(result with { Summary = result.Summary }));
        s.SetLog(RoundTripJson.Write(log));
        if (s.Status == RoundTripStatus.NeedsAnswers && result.Steps.All(x => x.Verdict != "question"))
            s.SetStatus(RoundTripStatus.Done);
        await uow.SaveChangesAsync(ct);
        return Result.Success(RoundTripMapping.ToDto(s, rec, RoundTripMapping.Actions(sig)));
    }
}

public record RevertRoundTripFixCommand(Guid Id, string Function) : IRequest<Result<RoundTripDto>>;

public class RevertRoundTripFixHandler(
    IRoundTripRepository roundTrips, ISignatureRepository signatures, IRecordingRepository recordings, ICurrentUser me, IUnitOfWork uow)
    : IRequestHandler<RevertRoundTripFixCommand, Result<RoundTripDto>>
{
    public async Task<Result<RoundTripDto>> Handle(RevertRoundTripFixCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<RoundTripDto>(Error.Validation("Not authenticated."));
        var s = await roundTrips.FindAsync(cmd.Id, me.UserId.Value, ct);
        if (s is null) return Result.Failure<RoundTripDto>(Error.NotFound("Round trip"));
        if (s.Status == RoundTripStatus.Running) return Result.Failure<RoundTripDto>(Error.Conflict("Wait for the round trip to finish."));
        var sig = await signatures.FindByIdAsync(s.SignatureId, me.UserId.Value, ct);
        var fn = sig?.Functions.FirstOrDefault(f => f.Name == cmd.Function);
        if (sig is null || fn is null) return Result.Failure<RoundTripDto>(Error.NotFound("Function"));
        var revision = await roundTrips.LatestRevisionAsync(fn.Id, s.Id, ct);
        if (revision is null) return Result.Failure<RoundTripDto>(Error.Validation($"This round trip did not change {fn.Name}."));
        fn.UpdateBody(revision.Body);
        await roundTrips.RemoveRevisionAsync(revision, ct);
        var log = RoundTripJson.Read<List<LogEntry>>(s.LogJson) ?? new();
        log.Add(new LogEntry(DateTime.UtcNow, "revert", $"You undid Mazda's fix to {fn.Name}.", fn.Name));
        s.SetLog(RoundTripJson.Write(log));
        await uow.SaveChangesAsync(ct);
        var rec = await recordings.FindByIdAsync(s.RecordingId, me.UserId.Value, ct);
        return Result.Success(RoundTripMapping.ToDto(s, rec, RoundTripMapping.Actions(sig)));
    }
}
