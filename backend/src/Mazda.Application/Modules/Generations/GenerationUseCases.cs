using FluentValidation;
using MediatR;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Common;
using Mazda.Domain.Modules.Generations;

namespace Mazda.Application.Modules.Generations;

public record GenerationDto(
    Guid Id,
    Guid SignatureId,
    string TargetUrl,
    string Prompt,
    GenerationStatus Status,
    string? Script,
    string? FailureReason,
    int TokensUsed,
    DateTime CreatedAt);

// ── Generate ──────────────────────────────────────────────────────
public record GenerateScriptCommand(Guid SignatureId, string TargetUrl, string Prompt) : IRequest<Result<GenerationDto>>;

public class GenerateScriptValidator : AbstractValidator<GenerateScriptCommand>
{
    public GenerateScriptValidator()
    {
        RuleFor(x => x.SignatureId).NotEmpty();
        RuleFor(x => x.TargetUrl)
            .NotEmpty()
            .Must(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            .WithMessage("Target URL must be an absolute http(s) URL.");
        RuleFor(x => x.Prompt).NotEmpty().MaximumLength(2000);
    }
}

public class GenerateScriptHandler(
    ISignatureRepository signatures,
    IGenerationRepository generations,
    IScriptGenerator generator,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<GenerateScriptCommand, Result<GenerationDto>>
{
    public async Task<Result<GenerationDto>> Handle(GenerateScriptCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<GenerationDto>(Error.Validation("Not authenticated."));

        var signature = await signatures.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (signature is null) return Result.Failure<GenerationDto>(Error.NotFound("Signature"));

        var generation = Generation.Create(me.UserId.Value, signature.Id, cmd.TargetUrl, cmd.Prompt);
        await generations.AddAsync(generation, ct);
        generation.MarkGenerating();
        await uow.SaveChangesAsync(ct);

        try
        {
            var result = await generator.GenerateAsync(signature, cmd.TargetUrl, cmd.Prompt, ct);
            generation.MarkReady(result.Script, result.TokensUsed);
        }
        catch (Exception ex)
        {
            generation.MarkFailed(ex.Message);
        }

        await uow.SaveChangesAsync(ct);

        return generation.Status == GenerationStatus.Ready
            ? Result.Success(ToDto(generation))
            : Result.Failure<GenerationDto>(Error.External(generation.FailureReason ?? "Generation failed."));
    }

    private static GenerationDto ToDto(Generation g) => new(
        g.Id, g.SignatureId, g.TargetUrl, g.Prompt, g.Status,
        g.GeneratedScript, g.FailureReason, g.TokensUsed, g.CreatedAt);
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
        if (g is null) return Result.Failure<GenerationDto>(Error.NotFound("Generation"));
        return Result.Success(new GenerationDto(
            g.Id, g.SignatureId, g.TargetUrl, g.Prompt, g.Status,
            g.GeneratedScript, g.FailureReason, g.TokensUsed, g.CreatedAt));
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
        return Result.Success<IReadOnlyList<GenerationDto>>(list.Select(g => new GenerationDto(
            g.Id, g.SignatureId, g.TargetUrl, g.Prompt, g.Status,
            g.GeneratedScript, g.FailureReason, g.TokensUsed, g.CreatedAt)).ToList());
    }
}
