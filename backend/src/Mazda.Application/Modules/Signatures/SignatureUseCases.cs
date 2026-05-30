using FluentValidation;
using MediatR;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Common;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Application.Modules.Signatures;

public record SignatureElementDto(string Selector, string ElementType, string Description);

public record SignatureDto(
    Guid Id,
    string Name,
    string Version,
    string Source,
    string Type,
    string HumanDescription,
    string UserFlowsJson,
    int NodeCount,
    DateTime UpdatedAt,
    IReadOnlyList<SignatureElementDto> Elements);

internal static class SignatureMapper
{
    public static SignatureDto ToDto(this Signature s) => new(
        s.Id, s.Name, s.Version, s.Source, s.Type, s.HumanDescription, s.UserFlowsJson,
        s.Elements.Count, s.UpdatedAt,
        s.Elements.OrderBy(e => e.Order).Select(e => new SignatureElementDto(
            e.ElementSelector, e.ElementType, e.ElementDescription)).ToList());
}

// ── List ──────────────────────────────────────────────────────────
public record ListSignaturesQuery() : IRequest<Result<IReadOnlyList<SignatureDto>>>;

public class ListSignaturesHandler(ISignatureRepository repo, ICurrentUser me)
    : IRequestHandler<ListSignaturesQuery, Result<IReadOnlyList<SignatureDto>>>
{
    public async Task<Result<IReadOnlyList<SignatureDto>>> Handle(ListSignaturesQuery _, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<IReadOnlyList<SignatureDto>>(Error.Validation("Not authenticated."));
        var sigs = await repo.ListAsync(me.UserId.Value, ct);
        return Result.Success<IReadOnlyList<SignatureDto>>(sigs.Select(s => s.ToDto()).ToList());
    }
}

// ── Get ───────────────────────────────────────────────────────────
public record GetSignatureQuery(Guid Id) : IRequest<Result<SignatureDto>>;

public class GetSignatureHandler(ISignatureRepository repo, ICurrentUser me)
    : IRequestHandler<GetSignatureQuery, Result<SignatureDto>>
{
    public async Task<Result<SignatureDto>> Handle(GetSignatureQuery q, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<SignatureDto>(Error.Validation("Not authenticated."));
        var sig = await repo.FindByIdAsync(q.Id, me.UserId.Value, ct);
        return sig is null
            ? Result.Failure<SignatureDto>(Error.NotFound("Signature"))
            : Result.Success(sig.ToDto());
    }
}

// ── Create / Update ───────────────────────────────────────────────
public record SaveSignatureCommand(
    Guid? Id,
    string Name,
    string? Source,
    string? Type,
    IReadOnlyList<SignatureElementDto> Elements) : IRequest<Result<SignatureDto>>;

public class SaveSignatureValidator : AbstractValidator<SaveSignatureCommand>
{
    public SaveSignatureValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
        RuleForEach(x => x.Elements).ChildRules(e =>
        {
            e.RuleFor(x => x.Selector).NotEmpty();
            e.RuleFor(x => x.ElementType).NotEmpty();
        });
    }
}

public class SaveSignatureHandler(ISignatureRepository repo, ICurrentUser me, IUnitOfWork uow)
    : IRequestHandler<SaveSignatureCommand, Result<SignatureDto>>
{
    public async Task<Result<SignatureDto>> Handle(SaveSignatureCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<SignatureDto>(Error.Validation("Not authenticated."));

        Signature sig;
        if (cmd.Id is { } id)
        {
            var found = await repo.FindByIdAsync(id, me.UserId.Value, ct);
            if (found is null) return Result.Failure<SignatureDto>(Error.NotFound("Signature"));
            sig = found;
            sig.Rename(cmd.Name);
            if (!string.IsNullOrWhiteSpace(cmd.Type)) sig.SetType(cmd.Type);
        }
        else
        {
            sig = Signature.Create(me.UserId.Value, cmd.Name, cmd.Source, cmd.Type);
            await repo.AddAsync(sig, ct);
        }

        var elements = cmd.Elements
            .Select((e, i) => SignatureElement.Create(e.Selector, e.ElementType, e.Description, i))
            .ToList();
        sig.ReplaceElements(elements);

        await uow.SaveChangesAsync(ct);
        return Result.Success(sig.ToDto());
    }
}

// ── Delete ────────────────────────────────────────────────────────
public record DeleteSignatureCommand(Guid Id) : IRequest<Result>;

public class DeleteSignatureHandler(ISignatureRepository repo, ICurrentUser me, IUnitOfWork uow)
    : IRequestHandler<DeleteSignatureCommand, Result>
{
    public async Task<Result> Handle(DeleteSignatureCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure(Error.Validation("Not authenticated."));
        var sig = await repo.FindByIdAsync(cmd.Id, me.UserId.Value, ct);
        if (sig is null) return Result.Failure(Error.NotFound("Signature"));
        await repo.RemoveAsync(sig, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// ── Analyze a component file (LLM extraction) ─────────────────────
public record AnalyzeComponentCommand(string FileName, string Source, string? Description = null) : IRequest<Result<SignatureDto>>;

public class AnalyzeComponentValidator : AbstractValidator<AnalyzeComponentCommand>
{
    public AnalyzeComponentValidator()
    {
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Source).NotEmpty().MaximumLength(300_000);
        RuleFor(x => x.Description).MaximumLength(50_000).When(x => x.Description is not null);
    }
}

public class AnalyzeComponentHandler(
    IComponentAnalyzer analyzer,
    ISignatureRepository repo,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<AnalyzeComponentCommand, Result<SignatureDto>>
{
    public async Task<Result<SignatureDto>> Handle(AnalyzeComponentCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<SignatureDto>(Error.Validation("Not authenticated."));

        ComponentAnalysisResult analysis;
        try { analysis = await analyzer.AnalyzeAsync(cmd.Source, cmd.FileName, cmd.Description, ct); }
        catch (Exception ex) { return Result.Failure<SignatureDto>(Error.External($"Analyzer failed: {ex.Message}")); }

        var sig = Signature.Create(me.UserId.Value, analysis.SuggestedName, cmd.FileName, analysis.SuggestedType, cmd.Description);
        var elements = analysis.Elements
            .Select((e, i) => SignatureElement.Create(e.Selector, e.ElementType, e.Description, i))
            .ToList();
        sig.ReplaceElements(elements);

        await repo.AddAsync(sig, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(sig.ToDto());
    }
}

// === User flows (LLM-inferred graph of UI states + element-anchored actions) ===

public record GenerateUserFlowsCommand(Guid SignatureId, string? CypressSource) : IRequest<Result<SignatureDto>>;

public class GenerateUserFlowsValidator : AbstractValidator<GenerateUserFlowsCommand>
{
    public GenerateUserFlowsValidator()
    {
        RuleFor(x => x.SignatureId).NotEmpty();
        RuleFor(x => x.CypressSource).MaximumLength(200_000).When(x => x.CypressSource is not null);
    }
}

public class GenerateUserFlowsHandler(
    ISignatureRepository repo,
    IFlowAnalyzer analyzer,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<GenerateUserFlowsCommand, Result<SignatureDto>>
{
    public async Task<Result<SignatureDto>> Handle(GenerateUserFlowsCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<SignatureDto>(Error.Validation("Not authenticated."));
        var sig = await repo.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<SignatureDto>(Error.NotFound("Signature"));

        FlowAnalysisResult result;
        try { result = await analyzer.InferFlowsAsync(sig, cmd.CypressSource, ct); }
        catch (Exception ex) { return Result.Failure<SignatureDto>(Error.External($"Flow analyzer failed: {ex.Message}")); }

        sig.SetUserFlows(result.FlowsJson);
        await uow.SaveChangesAsync(ct);
        return Result.Success(sig.ToDto());
    }
}

public record UpdateUserFlowsCommand(Guid SignatureId, string FlowsJson) : IRequest<Result<SignatureDto>>;

public class UpdateUserFlowsValidator : AbstractValidator<UpdateUserFlowsCommand>
{
    public UpdateUserFlowsValidator()
    {
        RuleFor(x => x.SignatureId).NotEmpty();
        RuleFor(x => x.FlowsJson).NotNull().MaximumLength(200_000);
    }
}

public class UpdateUserFlowsHandler(
    ISignatureRepository repo,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<UpdateUserFlowsCommand, Result<SignatureDto>>
{
    public async Task<Result<SignatureDto>> Handle(UpdateUserFlowsCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<SignatureDto>(Error.Validation("Not authenticated."));
        var sig = await repo.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<SignatureDto>(Error.NotFound("Signature"));

        // Validate JSON shape lightly here so bad edits do not get persisted.
        try { using var _ = System.Text.Json.JsonDocument.Parse(cmd.FlowsJson); }
        catch (System.Text.Json.JsonException ex) { return Result.Failure<SignatureDto>(Error.Validation($"Flows JSON invalid: {ex.Message}")); }

        sig.SetUserFlows(cmd.FlowsJson);
        await uow.SaveChangesAsync(ct);
        return Result.Success(sig.ToDto());
    }
}
