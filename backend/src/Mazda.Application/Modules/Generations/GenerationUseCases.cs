using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Common;
using Mazda.Domain.Modules.Generations;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Application.Modules.Generations;

public record GenerationDto(
    Guid Id,
    Guid SignatureId,
    string SetupPrompt,
    string? SetupScript,
    string TestPrompt,
    string? TestScript,
    string? FinalScript,
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
    public static GenerationDto ToDto(this Generation g) => new(
        g.Id, g.SignatureId,
        g.SetupPrompt, g.SetupScript,
        g.TestPrompt, g.TestScript,
        g.FinalScript,
        g.Status, g.FailureReason, g.TokensUsed,
        g.ExecutionStatus, g.ExitCode, g.ExecutionLog,
        g.ExecutionStartedAt, g.ExecutionEndedAt,
        g.CreatedAt, g.UpdatedAt);
}

// ── Create empty generation ───────────────────────────────────────
public record CreateGenerationCommand(Guid SignatureId) : IRequest<Result<GenerationDto>>;

public class CreateGenerationValidator : AbstractValidator<CreateGenerationCommand>
{
    public CreateGenerationValidator()
    {
        RuleFor(x => x.SignatureId).NotEmpty();
    }
}

public class CreateGenerationHandler(
    ISignatureRepository signatures,
    IGenerationRepository generations,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<CreateGenerationCommand, Result<GenerationDto>>
{
    public async Task<Result<GenerationDto>> Handle(CreateGenerationCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<GenerationDto>(Error.Validation("Not authenticated."));

        var sig = await signatures.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<GenerationDto>(Error.NotFound("Signature"));

        var gen = Generation.Create(me.UserId.Value, cmd.SignatureId);
        // Pre-fill the wizard prompts AND the generated scripts from whatever
        // the user last used against this signature, so re-runs do not start
        // from scratch.
        if (!string.IsNullOrWhiteSpace(sig.LastSetupPrompt)) gen.SetSetupPrompt(sig.LastSetupPrompt);
        if (!string.IsNullOrWhiteSpace(sig.LastTestPrompt))  gen.SetTestPrompt(sig.LastTestPrompt);
        if (!string.IsNullOrWhiteSpace(sig.LastSetupScript)) gen.SetSetupScript(sig.LastSetupScript);
        if (!string.IsNullOrWhiteSpace(sig.LastTestScript))  gen.SetTestScript(sig.LastTestScript);
        if (!string.IsNullOrWhiteSpace(sig.LastFinalScript)) gen.SetFinalScript(sig.LastFinalScript);
        await generations.AddAsync(gen, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(gen.ToDto());
    }
}

// ── Generate setup script ─────────────────────────────────────────
public record GenerateSetupCommand(Guid GenerationId, string SetupPrompt) : IRequest<Result<GenerationDto>>;

public class GenerateSetupValidator : AbstractValidator<GenerateSetupCommand>
{
    public GenerateSetupValidator()
    {
        RuleFor(x => x.GenerationId).NotEmpty();
        RuleFor(x => x.SetupPrompt).NotEmpty().MaximumLength(4000);
    }
}

public class GenerateSetupHandler(
    ISignatureRepository signatures,
    IGenerationRepository generations,
    IScriptGenerator generator,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<GenerateSetupCommand, Result<GenerationDto>>
{
    public async Task<Result<GenerationDto>> Handle(GenerateSetupCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<GenerationDto>(Error.Validation("Not authenticated."));

        var gen = await generations.FindByIdAsync(cmd.GenerationId, me.UserId.Value, ct);
        if (gen is null) return Result.Failure<GenerationDto>(Error.NotFound("Generation"));

        var sig = await signatures.FindByIdAsync(gen.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<GenerationDto>(Error.NotFound("Signature"));

        gen.SetSetupPrompt(cmd.SetupPrompt);
        sig.SetLastSetupPrompt(cmd.SetupPrompt);
        gen.MarkGenerating();
        await uow.SaveChangesAsync(ct);

        try
        {
            var result = await generator.GenerateSetupAsync(sig, cmd.SetupPrompt, ct);
            gen.SetSetupScript(result.Script);
            sig.SetLastSetupScript(result.Script);
            gen.MarkReady(result.TokensUsed);
        }
        catch (Exception ex)
        {
            gen.MarkFailed($"Setup generation failed: {ex.Message}");
        }
        await uow.SaveChangesAsync(ct);

        return gen.Status == GenerationStatus.Ready
            ? Result.Success(gen.ToDto())
            : Result.Failure<GenerationDto>(Error.External(gen.FailureReason ?? "Setup generation failed."));
    }
}

// ── Generate test script ──────────────────────────────────────────
public record GenerateTestCommand(Guid GenerationId, string TestPrompt) : IRequest<Result<GenerationDto>>;

public class GenerateTestValidator : AbstractValidator<GenerateTestCommand>
{
    public GenerateTestValidator()
    {
        RuleFor(x => x.GenerationId).NotEmpty();
        RuleFor(x => x.TestPrompt).NotEmpty().MaximumLength(4000);
    }
}

public class GenerateTestHandler(
    ISignatureRepository signatures,
    IGenerationRepository generations,
    IScriptGenerator generator,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<GenerateTestCommand, Result<GenerationDto>>
{
    public async Task<Result<GenerationDto>> Handle(GenerateTestCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<GenerationDto>(Error.Validation("Not authenticated."));

        var gen = await generations.FindByIdAsync(cmd.GenerationId, me.UserId.Value, ct);
        if (gen is null) return Result.Failure<GenerationDto>(Error.NotFound("Generation"));

        var sig = await signatures.FindByIdAsync(gen.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<GenerationDto>(Error.NotFound("Signature"));

        gen.SetTestPrompt(cmd.TestPrompt);
        sig.SetLastTestPrompt(cmd.TestPrompt);
        gen.MarkGenerating();
        await uow.SaveChangesAsync(ct);

        try
        {
            var result = await generator.GenerateTestAsync(sig, cmd.TestPrompt, gen.SetupScript, ct);
            gen.SetTestScript(result.Script);
            sig.SetLastTestScript(result.Script);
            gen.MarkReady(result.TokensUsed);
        }
        catch (Exception ex)
        {
            gen.MarkFailed($"Test generation failed: {ex.Message}");
        }
        await uow.SaveChangesAsync(ct);

        return gen.Status == GenerationStatus.Ready
            ? Result.Success(gen.ToDto())
            : Result.Failure<GenerationDto>(Error.External(gen.FailureReason ?? "Test generation failed."));
    }
}

// ── Generate merged final script ──────────────────────────────────
public record GenerateMergedCommand(Guid GenerationId) : IRequest<Result<GenerationDto>>;

public class GenerateMergedHandler(
    ISignatureRepository signatures,
    IGenerationRepository generations,
    IScriptGenerator generator,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<GenerateMergedCommand, Result<GenerationDto>>
{
    public async Task<Result<GenerationDto>> Handle(GenerateMergedCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<GenerationDto>(Error.Validation("Not authenticated."));

        var gen = await generations.FindByIdAsync(cmd.GenerationId, me.UserId.Value, ct);
        if (gen is null) return Result.Failure<GenerationDto>(Error.NotFound("Generation"));
        if (string.IsNullOrWhiteSpace(gen.SetupScript))
            return Result.Failure<GenerationDto>(Error.Validation("Setup script missing — generate it first."));
        if (string.IsNullOrWhiteSpace(gen.TestScript))
            return Result.Failure<GenerationDto>(Error.Validation("Test script missing — generate it first."));

        var sig = await signatures.FindByIdAsync(gen.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<GenerationDto>(Error.NotFound("Signature"));

        gen.MarkGenerating();
        await uow.SaveChangesAsync(ct);

        try
        {
            var result = await generator.MergeAsync(gen.SetupScript!, gen.TestScript!, ct);
            gen.SetFinalScript(result.Script);
            sig.SetLastFinalScript(result.Script);
            gen.MarkReady(result.TokensUsed);
        }
        catch (Exception ex)
        {
            gen.MarkFailed($"Merge failed: {ex.Message}");
        }
        await uow.SaveChangesAsync(ct);

        return gen.Status == GenerationStatus.Ready
            ? Result.Success(gen.ToDto())
            : Result.Failure<GenerationDto>(Error.External(gen.FailureReason ?? "Merge failed."));
    }
}

// ── Save a user-edited script (setup / test / final) ──────────────
public enum ScriptSlot { Setup, Test, Final }

public record UpdateScriptCommand(Guid GenerationId, ScriptSlot Slot, string Script) : IRequest<Result<GenerationDto>>;

public class UpdateScriptValidator : AbstractValidator<UpdateScriptCommand>
{
    public UpdateScriptValidator()
    {
        RuleFor(x => x.GenerationId).NotEmpty();
        RuleFor(x => x.Script).NotNull().MaximumLength(200_000);
    }
}

public class UpdateScriptHandler(
    ISignatureRepository signatures,
    IGenerationRepository generations,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<UpdateScriptCommand, Result<GenerationDto>>
{
    public async Task<Result<GenerationDto>> Handle(UpdateScriptCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<GenerationDto>(Error.Validation("Not authenticated."));

        var gen = await generations.FindByIdAsync(cmd.GenerationId, me.UserId.Value, ct);
        if (gen is null) return Result.Failure<GenerationDto>(Error.NotFound("Generation"));

        // Mirror user edits of every slot (setup/test/final) onto the signature
        // so a fresh run starts where the last one left off.
        var sig = await signatures.FindByIdAsync(gen.SignatureId, me.UserId.Value, ct);

        switch (cmd.Slot)
        {
            case ScriptSlot.Setup:
                gen.SetSetupScript(cmd.Script);
                sig?.SetLastSetupScript(cmd.Script);
                break;
            case ScriptSlot.Test:
                gen.SetTestScript(cmd.Script);
                sig?.SetLastTestScript(cmd.Script);
                break;
            case ScriptSlot.Final:
                gen.SetFinalScript(cmd.Script);
                sig?.SetLastFinalScript(cmd.Script);
                break;
        }
        await uow.SaveChangesAsync(ct);
        return Result.Success(gen.ToDto());
    }
}

// ── Get one ───────────────────────────────────────────────────────
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

// ── Execute the final script ──────────────────────────────────────
public record StartExecutionCommand(Guid GenerationId) : IRequest<Result<GenerationDto>>;

public class StartExecutionHandler(
    IGenerationRepository generations,
    IScriptRunner runner,
    IExecutionRegistry registry,
    ICurrentUser me,
    IUnitOfWork uow,
    IServiceScopeFactory scopeFactory) : IRequestHandler<StartExecutionCommand, Result<GenerationDto>>
{
    public async Task<Result<GenerationDto>> Handle(StartExecutionCommand cmd, CancellationToken requestCt)
    {
        if (me.UserId is null) return Result.Failure<GenerationDto>(Error.Validation("Not authenticated."));

        var gen = await generations.FindByIdAsync(cmd.GenerationId, me.UserId.Value, CancellationToken.None);
        if (gen is null) return Result.Failure<GenerationDto>(Error.NotFound("Generation"));
        if (string.IsNullOrWhiteSpace(gen.FinalScript))
            return Result.Failure<GenerationDto>(Error.Validation("Final script missing — merge first."));
        if (registry.IsRunning(gen.Id))
            return Result.Failure<GenerationDto>(Error.Validation("This run is already executing."));

        gen.MarkExecutionStarted();
        await uow.SaveChangesAsync(CancellationToken.None);
        // Wipe any state from a previous run so old log lines do not leak into
        // the new SSE stream and `Append` is re-enabled for this generation.
        registry.Reset(gen.Id);
        var dto = gen.ToDto();
        var script = gen.FinalScript!;
        var genId = gen.Id;
        var ownerId = me.UserId.Value;

        // Fire-and-forget — the runner streams into IExecutionRegistry. The SSE
        // endpoint reads from there; we persist the terminal state in a new scope
        // because the MediatR request scope dies as soon as we return.
        _ = Task.Run(async () =>
        {
            RunnerCompletion? result = null;
            try
            {
                result = await runner.RunAsync(genId, script, CancellationToken.None);
                using var scope = scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IGenerationRepository>();
                var sUow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var fresh = await repo.FindByIdAsync(genId, ownerId, CancellationToken.None);
                if (fresh is not null)
                {
                    var terminal = result.Kind switch
                    {
                        RunnerExitKind.Passed     => Domain.Modules.Generations.ExecutionStatus.Passed,
                        RunnerExitKind.FailedTest => Domain.Modules.Generations.ExecutionStatus.FailedTest,
                        RunnerExitKind.Cancelled  => Domain.Modules.Generations.ExecutionStatus.Cancelled,
                        _                         => Domain.Modules.Generations.ExecutionStatus.Errored,
                    };
                    fresh.MarkExecutionFinished(terminal, result.ExitCode, result.FullLog);
                    await sUow.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch
            {
                // Swallow — the registry already captured the log. A health monitor
                // could surface this if we add one.
            }
            finally
            {
                // Signal end-of-stream to SSE subscribers ONLY AFTER the terminal
                // status has been persisted. Otherwise the client refetches on
                // 'end' and races the DB write — frontend gets stuck on Running.
                if (result is not null)
                    registry.Complete(genId, result);
                else
                    registry.Complete(genId, new RunnerCompletion(RunnerExitKind.Errored, -1, string.Empty));
            }
        });

        return Result.Success(dto);
    }
}

// ── Recent runs (for the studio dashboard) ────────────────────────
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
