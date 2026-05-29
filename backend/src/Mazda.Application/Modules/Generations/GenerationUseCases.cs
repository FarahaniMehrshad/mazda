using FluentValidation;
using MediatR;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Common;
using Mazda.Domain.Modules.Generations;

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
        gen.MarkGenerating();
        await uow.SaveChangesAsync(ct);

        try
        {
            var result = await generator.GenerateSetupAsync(sig, cmd.SetupPrompt, ct);
            gen.SetSetupScript(result.Script);
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
        gen.MarkGenerating();
        await uow.SaveChangesAsync(ct);

        try
        {
            var result = await generator.GenerateTestAsync(sig, cmd.TestPrompt, gen.SetupScript, ct);
            gen.SetTestScript(result.Script);
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

        gen.MarkGenerating();
        await uow.SaveChangesAsync(ct);

        try
        {
            var result = await generator.MergeAsync(gen.SetupScript!, gen.TestScript!, ct);
            gen.SetFinalScript(result.Script);
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
    IGenerationRepository generations,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<UpdateScriptCommand, Result<GenerationDto>>
{
    public async Task<Result<GenerationDto>> Handle(UpdateScriptCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<GenerationDto>(Error.Validation("Not authenticated."));

        var gen = await generations.FindByIdAsync(cmd.GenerationId, me.UserId.Value, ct);
        if (gen is null) return Result.Failure<GenerationDto>(Error.NotFound("Generation"));

        switch (cmd.Slot)
        {
            case ScriptSlot.Setup: gen.SetSetupScript(cmd.Script); break;
            case ScriptSlot.Test:  gen.SetTestScript(cmd.Script); break;
            case ScriptSlot.Final: gen.SetFinalScript(cmd.Script); break;
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
