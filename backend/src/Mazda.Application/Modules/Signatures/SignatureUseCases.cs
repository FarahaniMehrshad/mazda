using FluentValidation;
using MediatR;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Common;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Application.Modules.Signatures;

public record SignatureElementDto(string Selector, string Category, string ElementType, string Description, string Logic);

public record SignatureDto(
    Guid Id,
    string Name,
    string Version,
    string Source,
    string Type,
    string HumanDescription,
    string UserFlowsJson,
    // Element-relationship graph JSON produced by the relationship-analysis
    // step (step 4 of the signature pipeline). Empty string when the step
    // hasn't run yet. Frontend's RelationshipsPage round-trips it through a
    // JSON editor + graph viewer.
    string RelationshipsJson,
    int NodeCount,
    DateTime UpdatedAt,
    IReadOnlyList<SignatureElementDto> Elements,
    // IDs of signatures this one depends on. Frontend uses the count for the
    // sidebar badge; the full Dependencies panel pulls the labeled rows via
    // ListSignatureDependenciesQuery (which adds names + element counts).
    IReadOnlyList<Guid> DependencyIds);

internal static class SignatureMapper
{
    public static SignatureDto ToDto(this Signature s) => new(
        s.Id, s.Name, s.Version, s.Source, s.Type, s.HumanDescription, s.UserFlowsJson,
        s.RelationshipsJson,
        s.Elements.Count, s.UpdatedAt,
        s.Elements.OrderBy(e => e.Order).Select(e => new SignatureElementDto(
            e.ElementSelector, e.Category.ToString(), e.ElementType, e.ElementDescription, e.ElementLogic)).ToList(),
        s.DependencyIds);

    internal static ElementCategory ParseCategory(string? raw)
    {
        // Accept any case + a few common synonyms. Default to Interactive (matches
        // pre-feature behaviour) when the value is missing or unrecognised.
        if (string.IsNullOrWhiteSpace(raw)) return ElementCategory.Interactive;
        var n = raw.Trim().ToLowerInvariant();
        return n switch
        {
            "informational" or "info" or "readonly" or "static" or "data" => ElementCategory.Informational,
            _ => ElementCategory.Interactive,
        };
    }
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
        var isUpdate = cmd.Id is { } id;
        if (isUpdate)
        {
            var found = await repo.FindByIdAsync(cmd.Id!.Value, me.UserId.Value, ct);
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
            .Select((e, i) => SignatureElement.Create(e.Selector, SignatureMapper.ParseCategory(e.Category), e.ElementType, e.Description, i, e.Logic))
            .ToList();

        if (isUpdate)
        {
            // Explicit hard-replace via the repo: bulk-deletes old element rows in SQL,
            // then attaches the new collection. Avoids EF orphan-tracking pitfalls.
            await repo.ReplaceElementsAsync(sig, elements, ct);
        }
        else
        {
            // Brand-new signature — no rows in DB yet, the simple aggregate method
            // is sufficient (everything goes in as INSERTs).
            sig.ReplaceElements(elements);
        }

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
// `Name` and `Description` are user overrides — when present they take
// precedence over the LLM's SuggestedName / over an empty description.
public record AnalyzeComponentCommand(string FileName, string Source, string? Description = null, string? Name = null) : IRequest<Result<SignatureDto>>;

public class AnalyzeComponentValidator : AbstractValidator<AnalyzeComponentCommand>
{
    public AnalyzeComponentValidator()
    {
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Source).NotEmpty().MaximumLength(1_000_000);
        RuleFor(x => x.Description).MaximumLength(50_000).When(x => x.Description is not null);
        // Mirrors the SaveSignatureCommand cap so a user-typed name and an
        // LLM-suggested one round-trip through the same validation surface.
        RuleFor(x => x.Name).MaximumLength(80).When(x => !string.IsNullOrWhiteSpace(x.Name));
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
        catch (Exception ex) { return Result.Failure<SignatureDto>(Error.External($"Analyzer failed: {ExceptionMessage.Describe(ex)}")); }

        // User-supplied name wins. Falls back to the LLM's SuggestedName, which
        // itself falls back to "Component" inside the analyzer when the model
        // returned nothing usable.
        var effectiveName = string.IsNullOrWhiteSpace(cmd.Name) ? analysis.SuggestedName : cmd.Name!.Trim();
        var sig = Signature.Create(me.UserId.Value, effectiveName, cmd.FileName, analysis.SuggestedType, cmd.Description);
        // Persist the actual bundled source code so deep re-analysis (interactive
        // OR informational) can re-call the LLM later without re-upload.
        sig.SetSourceCode(cmd.Source);
        var elements = analysis.Elements
            .Select((e, i) => SignatureElement.Create(e.Selector, SignatureMapper.ParseCategory(e.Category), e.ElementType, e.Description, i, e.Logic))
            .ToList();
        sig.ReplaceElements(elements);

        await repo.AddAsync(sig, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(sig.ToDto());
    }
}

// === User flows (LLM-inferred graph of UI states + element-anchored actions) ===

public record GenerateUserFlowsCommand(Guid SignatureId) : IRequest<Result<SignatureDto>>;

public class GenerateUserFlowsValidator : AbstractValidator<GenerateUserFlowsCommand>
{
    public GenerateUserFlowsValidator()
    {
        RuleFor(x => x.SignatureId).NotEmpty();
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
        try { result = await analyzer.InferFlowsAsync(sig, ct); }
        catch (Exception ex) { return Result.Failure<SignatureDto>(Error.External($"Flow analyzer failed: {ExceptionMessage.Describe(ex)}")); }

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

// === Re-analyze INTERACTIVE elements (signature step 2) ===
// Reruns the same LLM extraction the original upload performed, but on the
// already-persisted source code. Replaces the interactive slice; keeps the
// existing informational slice untouched so the user doesn't lose their step-3
// work.

public record ReanalyzeInteractiveCommand(Guid SignatureId) : IRequest<Result<SignatureDto>>;

public class ReanalyzeInteractiveValidator : AbstractValidator<ReanalyzeInteractiveCommand>
{
    public ReanalyzeInteractiveValidator()
    {
        RuleFor(x => x.SignatureId).NotEmpty();
    }
}

public class ReanalyzeInteractiveHandler(
    ISignatureRepository repo,
    IComponentAnalyzer analyzer,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<ReanalyzeInteractiveCommand, Result<SignatureDto>>
{
    public async Task<Result<SignatureDto>> Handle(ReanalyzeInteractiveCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<SignatureDto>(Error.Validation("Not authenticated."));
        var sig = await repo.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<SignatureDto>(Error.NotFound("Signature"));

        if (string.IsNullOrWhiteSpace(sig.SourceCode))
            return Result.Failure<SignatureDto>(Error.Validation("Original component source not available; re-upload the component."));

        ComponentAnalysisResult analysis;
        try { analysis = await analyzer.AnalyzeAsync(sig.SourceCode, sig.Source, sig.HumanDescription, ct); }
        catch (Exception ex) { return Result.Failure<SignatureDto>(Error.External($"Analyzer failed: {ExceptionMessage.Describe(ex)}")); }

        // Replace ONLY the interactive elements; preserve everything informational.
        var informational = sig.Elements
            .Where(e => e.Category == ElementCategory.Informational)
            .OrderBy(e => e.Order)
            .ToList();

        // New interactive elements come from the re-analysis. Force the category
        // to Interactive — even if the analyzer mislabels one, this step is
        // semantically the interactive slice.
        var nextOrder = 0;
        var newInteractive = analysis.Elements
            .Where(e => SignatureMapper.ParseCategory(e.Category) == ElementCategory.Interactive)
            .Select(e => SignatureElement.Create(
                e.Selector,
                ElementCategory.Interactive,
                e.ElementType,
                e.Description,
                nextOrder++,
                e.Logic))
            .ToList();

        var preservedInformational = informational
            .Select(e => SignatureElement.Create(
                e.ElementSelector,
                ElementCategory.Informational,
                e.ElementType,
                e.ElementDescription,
                nextOrder++,
                e.ElementLogic))
            .ToList();

        var merged = newInteractive.Concat(preservedInformational).ToList();

        await repo.ReplaceElementsAsync(sig, merged, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(sig.ToDto());
    }
}

// === Deep informational re-analysis (signature step 3) ===

public record AnalyzeInformationalCommand(Guid SignatureId) : IRequest<Result<SignatureDto>>;

public class AnalyzeInformationalValidator : AbstractValidator<AnalyzeInformationalCommand>
{
    public AnalyzeInformationalValidator()
    {
        RuleFor(x => x.SignatureId).NotEmpty();
    }
}

public class AnalyzeInformationalHandler(
    ISignatureRepository repo,
    IInformationalAnalyzer analyzer,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<AnalyzeInformationalCommand, Result<SignatureDto>>
{
    public async Task<Result<SignatureDto>> Handle(AnalyzeInformationalCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<SignatureDto>(Error.Validation("Not authenticated."));
        var sig = await repo.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<SignatureDto>(Error.NotFound("Signature"));

        // SourceCode is the actual bundled component text the original analyze pass
        // ran against (Source is just the short display label). We need the real
        // code again so the dedicated informational pass can do its deep scan.
        if (string.IsNullOrWhiteSpace(sig.SourceCode))
            return Result.Failure<SignatureDto>(Error.Validation("Original component source not available; re-upload the component."));

        InformationalAnalysisResult result;
        try { result = await analyzer.AnalyzeInformationalAsync(sig, sig.SourceCode, ct); }
        catch (Exception ex) { return Result.Failure<SignatureDto>(Error.External($"Informational analyzer failed: {ExceptionMessage.Describe(ex)}")); }

        // Preserve all existing INTERACTIVE elements. Replace the INFORMATIONAL
        // subset entirely with the deeper analysis. Reorder so interactive
        // comes first, then informational; preserves stable navigation in the
        // editor pages.
        var interactive = sig.Elements
            .Where(e => e.Category == ElementCategory.Interactive)
            .OrderBy(e => e.Order)
            .ToList();
        var newInformational = result.Elements
            .Select((e, i) => SignatureElement.Create(
                e.Selector,
                ElementCategory.Informational,
                e.ElementType,
                e.Description,
                interactive.Count + i,
                e.Logic))
            .ToList();

        var nextOrder = 0;
        var merged = interactive
            .Select(e => SignatureElement.Create(
                e.ElementSelector,
                ElementCategory.Interactive,
                e.ElementType,
                e.ElementDescription,
                nextOrder++,
                e.ElementLogic))
            .Concat(newInformational.Select(e => SignatureElement.Create(
                e.ElementSelector,
                ElementCategory.Informational,
                e.ElementType,
                e.ElementDescription,
                nextOrder++,
                e.ElementLogic)))
            .ToList();

        await repo.ReplaceElementsAsync(sig, merged, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(sig.ToDto());
    }
}

// === Element-relationship graph (signature step 4) ===
// Runs after Interactive + Informational passes have populated the signature
// with element selectors and descriptions. Asks the LLM to infer the
// semantic graph: which container labels which children, which control
// affects which field, which read target identifies which row, etc.
// Persists the resulting JSON onto the signature; downstream test-gen
// merges it into the LLM context so relational prompts ("delete next to
// row X") get the right selector.

public record AnalyzeRelationshipsCommand(Guid SignatureId) : IRequest<Result<SignatureDto>>;

public class AnalyzeRelationshipsValidator : AbstractValidator<AnalyzeRelationshipsCommand>
{
    public AnalyzeRelationshipsValidator()
    {
        RuleFor(x => x.SignatureId).NotEmpty();
    }
}

public class AnalyzeRelationshipsHandler(
    ISignatureRepository repo,
    IRelationshipAnalyzer analyzer,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<AnalyzeRelationshipsCommand, Result<SignatureDto>>
{
    public async Task<Result<SignatureDto>> Handle(AnalyzeRelationshipsCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<SignatureDto>(Error.Validation("Not authenticated."));
        var sig = await repo.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<SignatureDto>(Error.NotFound("Signature"));

        if (string.IsNullOrWhiteSpace(sig.SourceCode))
            return Result.Failure<SignatureDto>(Error.Validation("Original component source not available; re-upload the component."));

        RelationshipAnalysisResult result;
        try { result = await analyzer.AnalyzeRelationshipsAsync(sig, sig.SourceCode, ct); }
        catch (Exception ex) { return Result.Failure<SignatureDto>(Error.External($"Relationship analyzer failed: {ExceptionMessage.Describe(ex)}")); }

        sig.SetRelationships(result.GraphJson);
        await uow.SaveChangesAsync(ct);
        return Result.Success(sig.ToDto());
    }
}

// Hand-edit pass on the relationship graph — lets the user fix up the
// LLM's output without rerunning the analyzer.
public record UpdateRelationshipsCommand(Guid SignatureId, string GraphJson) : IRequest<Result<SignatureDto>>;

public class UpdateRelationshipsValidator : AbstractValidator<UpdateRelationshipsCommand>
{
    public UpdateRelationshipsValidator()
    {
        RuleFor(x => x.SignatureId).NotEmpty();
        RuleFor(x => x.GraphJson).NotNull().MaximumLength(500_000);
    }
}

public class UpdateRelationshipsHandler(
    ISignatureRepository repo,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<UpdateRelationshipsCommand, Result<SignatureDto>>
{
    public async Task<Result<SignatureDto>> Handle(UpdateRelationshipsCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<SignatureDto>(Error.Validation("Not authenticated."));
        var sig = await repo.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<SignatureDto>(Error.NotFound("Signature"));

        try { using var _ = System.Text.Json.JsonDocument.Parse(cmd.GraphJson); }
        catch (System.Text.Json.JsonException ex) { return Result.Failure<SignatureDto>(Error.Validation($"Relationships JSON invalid: {ex.Message}")); }

        sig.SetRelationships(cmd.GraphJson);
        await uow.SaveChangesAsync(ct);
        return Result.Success(sig.ToDto());
    }
}
