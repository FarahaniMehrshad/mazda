using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Mazda.Application.Common.Abstractions;
using Mazda.Application.Modules.Cdp;
using Mazda.Domain.Common;
using Mazda.Domain.Modules.Generations;

namespace Mazda.Application.Modules.Generations;

/// <summary>
/// DTO the frontend renders on the Runs page. Every JSON blob is opaque —
/// the shape is documented in the Generation aggregate. The frontend
/// parses each blob into its own typed model.
/// </summary>
public record GenerationDto(
    Guid Id,
    Guid SignatureId,
    string Prompt,
    string? GatheredStateJson,
    string? PlanJson,
    string? ExecutionEnvelopeJson,
    GenerationStatus Status,
    string? FailureReason,
    int TokensUsed,
    ExecutionStatus ExecutionStatus,
    int? ExitCode,
    string? ExecutionLog,
    DateTime? ExecutionStartedAt,
    DateTime? ExecutionEndedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

internal static class GenerationMapper
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static GenerationDto ToDto(this Generation g) => new(
        g.Id, g.SignatureId, g.Prompt,
        g.GatheredStateJson, g.PlanJson, g.ExecutionEnvelopeJson,
        g.Status, g.FailureReason, g.TokensUsed,
        g.ExecutionStatus, g.ExitCode, g.ExecutionLog,
        g.ExecutionStartedAt, g.ExecutionEndedAt,
        g.CreatedAt, g.UpdatedAt);

    /// <summary>
    /// Wire form of a <see cref="GatheredState"/>. Uses <c>state</c> as the
    /// inline JSON element so the frontend sees the same nested shape as
    /// the harness produced (rather than a doubly-escaped JSON string).
    /// </summary>
    public static string SerializeGatheredState(GatheredState s) =>
        JsonSerializer.Serialize(new
        {
            signature = s.SignatureLabel,
            capturedAt = s.CapturedAt,
            state = JsonDocument.Parse(string.IsNullOrWhiteSpace(s.StateJson) ? "{}" : s.StateJson).RootElement,
        }, JsonOpts);

    public static string SerializePlan(Plan p) =>
        JsonSerializer.Serialize(new
        {
            reasoning = p.Reasoning,
            steps = p.Steps.Select(step => new
            {
                id = step.Id,
                call = step.Call,
                args = JsonDocument.Parse(string.IsNullOrWhiteSpace(step.ArgsJson) ? "{}" : step.ArgsJson).RootElement,
            }),
        }, JsonOpts);

    public static string SerializeEnvelope(ExecutionEnvelope e) =>
        JsonSerializer.Serialize(new
        {
            outcome = e.Outcome.ToString().ToLowerInvariant(),
            stepsTotal = e.StepsTotal,
            stepsExecuted = e.StepsExecuted,
            steps = e.Steps.Select(s => new
            {
                id = s.Id,
                call = s.Call,
                status = s.Status.ToString().ToLowerInvariant(),
                durationMs = s.DurationMs,
                error = s.Error is null ? null : new
                {
                    kind = s.Error.Kind.ToString(),
                    message = s.Error.Message,
                },
            }),
            // Inline the state + plan blobs so a saved envelope is
            // self-contained — the Runs page can render a historical run
            // without a second round-trip.
            capturedState = JsonDocument.Parse(SerializeGatheredState(e.CapturedState)).RootElement,
            plan = JsonDocument.Parse(SerializePlan(e.Plan)).RootElement,
        }, JsonOpts);

    /// <summary>
    /// Round-trip a persisted plan JSON back into a <see cref="Plan"/> so
    /// the interpreter can execute it. We only need the shape the
    /// interpreter operates on; the persisted form is a superset (also
    /// carries the reasoning), which we preserve.
    /// </summary>
    public static Plan DeserializePlan(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? reasoning = root.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() : null;
        var steps = new List<PlanStep>();
        if (root.TryGetProperty("steps", out var ss) && ss.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in ss.EnumerateArray())
            {
                var id = s.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
                var call = s.TryGetProperty("call", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                var args = s.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a.GetRawText() : "{}";
                steps.Add(new PlanStep(id, call, args));
            }
        }
        return new Plan(reasoning, steps);
    }

    public static GatheredState DeserializeState(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var signatureLabel = root.TryGetProperty("signature", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
        var capturedAt = root.TryGetProperty("capturedAt", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetDateTime() : DateTime.UtcNow;
        var stateJson = root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.Object
            ? s.GetRawText() : "{}";
        return new GatheredState(signatureLabel, capturedAt, stateJson);
    }
}

// ── Plan a run: create Generation + gather state + generate plan ─────
// Synchronous end-to-end (gather is one Playwright round-trip, plan is one
// LLM call). Returns a Ready generation whose GatheredStateJson + PlanJson
// are populated; execution is a separate ExecuteRunCommand so the user
// gets a chance to review the plan before it runs.

public record PlanRunCommand(Guid SignatureId, string Prompt) : IRequest<Result<GenerationDto>>;

public class PlanRunValidator : AbstractValidator<PlanRunCommand>
{
    public PlanRunValidator()
    {
        RuleFor(x => x.SignatureId).NotEmpty();
        RuleFor(x => x.Prompt).NotEmpty().MaximumLength(50_000);
    }
}

public class PlanRunHandler(
    ISignatureRepository signatures,
    IGenerationRepository generations,
    IEffectiveSignatureResolver effective,
    IStateGatherer gatherer,
    IPlanGenerator planner,
    IUserSettingsRepository userSettings,
    ICdpClient cdp,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<PlanRunCommand, Result<GenerationDto>>
{
    public async Task<Result<GenerationDto>> Handle(PlanRunCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<GenerationDto>(Error.Validation("Not authenticated."));

        var sig = await signatures.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<GenerationDto>(Error.NotFound("Signature"));

        // A signature without a catalogue can't be planned against — the
        // planner has nothing to compose. Fail early with a clear message
        // rather than making an LLM call that would return an empty plan.
        if (sig.Functions.Count == 0)
            return Result.Failure<GenerationDto>(Error.Validation(
                "Signature has no function catalogue. Generate one via " +
                "POST /api/signatures/{id}/functions/generate first."));

        var effectiveSigs = await effective.ResolveAsync(sig.Id, me.UserId.Value, ct);
        var deps = effectiveSigs?.Where(s => s.Id != sig.Id).ToList();

        var gen = Generation.Create(me.UserId.Value, sig.Id, cmd.Prompt);
        gen.MarkGenerating();
        await generations.AddAsync(gen, ct);
        await uow.SaveChangesAsync(ct);

        try
        {
            var (target, cdpError) = await CdpTargetResolver.ResolveValidatedAsync(
                userSettings, cdp, me.UserId.Value, ct);
            if (cdpError is not null)
            {
                gen.MarkFailed(cdpError);
                await uow.SaveChangesAsync(ct);
                return Result.Failure<GenerationDto>(Error.Validation(cdpError));
            }
            var state = await gatherer.GatherAsync(sig, deps, target, ct);
            gen.RecordGatheredState(GenerationMapper.SerializeGatheredState(state));
            // Persist between phases so the UI can render the state
            // snapshot even if the plan call hangs or the browser is slow.
            await uow.SaveChangesAsync(ct);

            var planResult = await planner.GeneratePlanAsync(sig, deps, state, cmd.Prompt, ct);
            gen.RecordPlan(GenerationMapper.SerializePlan(planResult.Plan));
            gen.MarkReady(planResult.TokensUsed);
            await uow.SaveChangesAsync(ct);

            return Result.Success(gen.ToDto());
        }
        catch (HarnessException ex)
        {
            gen.MarkFailed($"Gather failed: {ex.Message}");
            await uow.SaveChangesAsync(ct);
            return Result.Failure<GenerationDto>(Error.External($"Runner harness failed: {ExceptionMessage.Describe(ex)}"));
        }
        catch (InvalidOperationException ex)
        {
            // Structural failures from the planner (empty catalogue, malformed
            // LLM output, unknown call in the plan). These are actionable by
            // the user — regenerate the catalogue, tweak the prompt — so
            // surface as validation, not as a 5xx.
            gen.MarkFailed(ex.Message);
            await uow.SaveChangesAsync(ct);
            return Result.Failure<GenerationDto>(Error.Validation(ex.Message));
        }
        catch (Exception ex)
        {
            gen.MarkFailed(ExceptionMessage.Describe(ex));
            await uow.SaveChangesAsync(ct);
            return Result.Failure<GenerationDto>(Error.External(ExceptionMessage.Describe(ex)));
        }
    }
}

// ── Execute a planned run ────────────────────────────────────────────
// Fire-and-forget: kicks off the interpreter on a background task, returns
// the DTO immediately. The frontend subscribes to the SSE stream on
// /api/generations/{id}/execution/stream for per-step events and refetches
// the generation when the stream closes.

public record ExecuteRunCommand(Guid GenerationId) : IRequest<Result<GenerationDto>>;

public class ExecuteRunValidator : AbstractValidator<ExecuteRunCommand>
{
    public ExecuteRunValidator()
    {
        RuleFor(x => x.GenerationId).NotEmpty();
    }
}

public class ExecuteRunHandler(
    ISignatureRepository signatures,
    IGenerationRepository generations,
    IEffectiveSignatureResolver effective,
    IPlanInterpreter interpreter,
    IExecutionRegistry registry,
    IUserSettingsRepository userSettings,
    ICurrentUser me,
    IUnitOfWork uow,
    IServiceScopeFactory scopeFactory) : IRequestHandler<ExecuteRunCommand, Result<GenerationDto>>
{
    // The CancellationToken parameter is deliberately named `_ct` (not `_`)
    // because inside the method we `_ = Task.Run(...)` to discard the
    // fire-and-forget task; a `_`-named parameter would shadow that
    // discard target and cause a CS0029 "cannot convert Task to
    // CancellationToken" at compile time.
    public async Task<Result<GenerationDto>> Handle(ExecuteRunCommand cmd, CancellationToken _ct)
    {
        if (me.UserId is null) return Result.Failure<GenerationDto>(Error.Validation("Not authenticated."));

        var gen = await generations.FindByIdAsync(cmd.GenerationId, me.UserId.Value, CancellationToken.None);
        if (gen is null) return Result.Failure<GenerationDto>(Error.NotFound("Generation"));
        if (gen.Status != GenerationStatus.Ready)
            return Result.Failure<GenerationDto>(Error.Validation(
                $"Run must be Ready to execute (currently {gen.Status}). Call PlanRun first."));
        if (string.IsNullOrWhiteSpace(gen.PlanJson) || string.IsNullOrWhiteSpace(gen.GatheredStateJson))
            return Result.Failure<GenerationDto>(Error.Validation(
                "Run has no persisted plan / gathered state."));
        if (registry.IsRunning(gen.Id))
            return Result.Failure<GenerationDto>(Error.Validation("This run is already executing."));

        var sig = await signatures.FindByIdAsync(gen.SignatureId, me.UserId.Value, CancellationToken.None);
        if (sig is null) return Result.Failure<GenerationDto>(Error.NotFound("Signature"));

        var effectiveSigs = await effective.ResolveAsync(sig.Id, me.UserId.Value, CancellationToken.None);
        var deps = effectiveSigs?.Where(s => s.Id != sig.Id).ToList();

        // Deserialize the persisted artefacts once up front, so a corrupt
        // blob fails cleanly here (400) rather than crashing the background
        // task with an opaque error.
        Plan plan;
        GatheredState state;
        try
        {
            plan = GenerationMapper.DeserializePlan(gen.PlanJson!);
            state = GenerationMapper.DeserializeState(gen.GatheredStateJson!);
        }
        catch (Exception ex)
        {
            return Result.Failure<GenerationDto>(Error.Validation(
                $"Persisted plan / gathered state is unparseable: {ex.Message}"));
        }

        gen.MarkExecutionStarted();
        await uow.SaveChangesAsync(CancellationToken.None);
        // Wipe any previous-run state so old log lines do not leak into the
        // new SSE stream and Append is re-enabled for this generation.
        registry.Reset(gen.Id);

        var dto = gen.ToDto();
        var runId = gen.Id;
        var ownerId = me.UserId.Value;

        // Resolve the CDP attachment HERE, not inside the Task below: the
        // MediatR request scope (and userSettings with it) is disposed the
        // moment this handler returns.
        var target = await CdpTargetResolver.ResolveAsync(userSettings, ownerId, _ct);

        // Fire-and-forget — the interpreter streams per-step events into
        // IExecutionRegistry via IScriptRunner.RunAsync. The SSE endpoint
        // reads from there; we persist the terminal state in a fresh scope
        // because the MediatR request scope dies as soon as we return.
        _ = Task.Run(async () =>
        {
            RunnerCompletion completion = new(RunnerExitKind.Errored, -1, string.Empty);
            try
            {
                var envelope = await interpreter.ExecuteAsync(runId, sig, deps, plan, state, target, CancellationToken.None);

                // Terminal execution status derived from the envelope's outcome.
                // We fold Ok → Passed and Failed → FailedTest because the
                // existing UI + SSE machinery already knows those enum values.
                // A harness-level failure would have thrown HarnessException
                // and never reached this path.
                var terminal = envelope.Outcome switch
                {
                    RunOutcome.Ok => ExecutionStatus.Passed,
                    _ => ExecutionStatus.FailedTest,
                };
                var envelopeJson = GenerationMapper.SerializeEnvelope(envelope);

                using var scope = scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IGenerationRepository>();
                var sUow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var fresh = await repo.FindByIdAsync(runId, ownerId, CancellationToken.None);
                if (fresh is not null)
                {
                    fresh.RecordExecutionEnvelope(envelopeJson);
                    // Exit code is synthetic — the runner doesn't surface its
                    // real code through IPlanInterpreter. 0 for pass, 1 for
                    // any failure. Keeps the DTO shape unsurprising.
                    fresh.MarkExecutionFinished(terminal, terminal == ExecutionStatus.Passed ? 0 : 1, "");
                    await sUow.SaveChangesAsync(CancellationToken.None);
                }

                completion = new RunnerCompletion(
                    terminal == ExecutionStatus.Passed ? RunnerExitKind.Passed : RunnerExitKind.FailedTest,
                    terminal == ExecutionStatus.Passed ? 0 : 1,
                    string.Empty);
            }
            catch (HarnessException ex)
            {
                await PersistTerminal(runId, ownerId, ExecutionStatus.Errored, -1, ex.ToString());
                completion = new RunnerCompletion(RunnerExitKind.Errored, -1, ex.ToString());
            }
            catch (Exception ex)
            {
                await PersistTerminal(runId, ownerId, ExecutionStatus.Errored, -1, ex.ToString());
                completion = new RunnerCompletion(RunnerExitKind.Errored, -1, ex.ToString());
            }
            finally
            {
                // Signal end-of-stream to SSE subscribers ONLY AFTER the
                // terminal status has been persisted. Otherwise the client
                // refetches on 'end' and races the DB write — the frontend
                // gets stuck showing Running when the DB is already terminal.
                registry.Complete(runId, completion);
            }
        });

        return Result.Success(dto);

        async Task PersistTerminal(Guid id, Guid owner, ExecutionStatus status, int code, string log)
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IGenerationRepository>();
            var sUow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var fresh = await repo.FindByIdAsync(id, owner, CancellationToken.None);
            if (fresh is null) return;
            fresh.MarkExecutionFinished(status, code, log);
            await sUow.SaveChangesAsync(CancellationToken.None);
        }
    }
}

// ── Get ──────────────────────────────────────────────────────────────
public record GetGenerationQuery(Guid Id) : IRequest<Result<GenerationDto>>;

public class GetGenerationHandler(IGenerationRepository repo, ICurrentUser me)
    : IRequestHandler<GetGenerationQuery, Result<GenerationDto>>
{
    public async Task<Result<GenerationDto>> Handle(GetGenerationQuery q, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<GenerationDto>(Error.Validation("Not authenticated."));
        var g = await repo.FindByIdAsync(q.Id, me.UserId.Value, ct);
        return g is null
            ? Result.Failure<GenerationDto>(Error.NotFound("Generation"))
            : Result.Success(g.ToDto());
    }
}

// ── List recent (for the Runs page) ─────────────────────────────────
public record ListRecentGenerationsQuery(int Take = 20) : IRequest<Result<IReadOnlyList<GenerationDto>>>;

public class ListRecentGenerationsHandler(IGenerationRepository repo, ICurrentUser me)
    : IRequestHandler<ListRecentGenerationsQuery, Result<IReadOnlyList<GenerationDto>>>
{
    public async Task<Result<IReadOnlyList<GenerationDto>>> Handle(ListRecentGenerationsQuery q, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<IReadOnlyList<GenerationDto>>(Error.Validation("Not authenticated."));
        var list = await repo.ListRecentAsync(me.UserId.Value, q.Take, ct);
        return Result.Success<IReadOnlyList<GenerationDto>>(list.Select(g => g.ToDto()).ToList());
    }
}
