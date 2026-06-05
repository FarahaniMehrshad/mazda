using FluentValidation;
using MediatR;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Common;

namespace Mazda.Application.Modules.Signatures;

/// <summary>
/// Light DTO for the dependencies endpoint. We surface the dep signature's
/// id, name, type, and element count — enough for the frontend to render a
/// row in the Dependencies panel without a second fetch per row.
/// </summary>
public record SignatureDependencyDto(
    Guid Id,
    string Name,
    string Type,
    int ElementCount);

// ── List ──────────────────────────────────────────────────────────
// Returns the DIRECT dependencies of a signature (not transitive). The
// frontend renders these in the Dependencies panel; the resolver is what
// expands them to the full set at generation time.

public record ListSignatureDependenciesQuery(Guid SignatureId) : IRequest<Result<IReadOnlyList<SignatureDependencyDto>>>;

public class ListSignatureDependenciesHandler(
    ISignatureRepository repo,
    ICurrentUser me) : IRequestHandler<ListSignatureDependenciesQuery, Result<IReadOnlyList<SignatureDependencyDto>>>
{
    public async Task<Result<IReadOnlyList<SignatureDependencyDto>>> Handle(ListSignatureDependenciesQuery q, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<IReadOnlyList<SignatureDependencyDto>>(Error.Validation("Not authenticated."));
        var sig = await repo.FindByIdAsync(q.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<IReadOnlyList<SignatureDependencyDto>>(Error.NotFound("Signature"));

        if (sig.DependencyIds.Count == 0)
            return Result.Success<IReadOnlyList<SignatureDependencyDto>>(Array.Empty<SignatureDependencyDto>());

        var depSigs = await repo.FindManyByIdsAsync(sig.DependencyIds, me.UserId.Value, ct);
        // Preserve the order the user added them in (mirrors _dependencies
        // navigation). Dangling refs (signature deleted by other means) are
        // dropped from the response — the FK ON DELETE CASCADE should keep
        // this from ever happening in practice.
        var byId = depSigs.ToDictionary(d => d.Id);
        var dtos = sig.DependencyIds
            .Where(id => byId.ContainsKey(id))
            .Select(id =>
            {
                var d = byId[id];
                return new SignatureDependencyDto(d.Id, d.Name, d.Type, d.Elements.Count);
            })
            .ToList();
        return Result.Success<IReadOnlyList<SignatureDependencyDto>>(dtos);
    }
}

// ── Add ───────────────────────────────────────────────────────────

public record AddSignatureDependencyCommand(Guid SignatureId, Guid DependsOnSignatureId)
    : IRequest<Result<IReadOnlyList<SignatureDependencyDto>>>;

public class AddSignatureDependencyValidator : AbstractValidator<AddSignatureDependencyCommand>
{
    public AddSignatureDependencyValidator()
    {
        RuleFor(x => x.SignatureId).NotEmpty();
        RuleFor(x => x.DependsOnSignatureId).NotEmpty();
        RuleFor(x => x).Must(x => x.SignatureId != x.DependsOnSignatureId)
            .WithMessage("A signature cannot depend on itself.");
    }
}

public class AddSignatureDependencyHandler(
    ISignatureRepository repo,
    IEffectiveSignatureResolver effective,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<AddSignatureDependencyCommand, Result<IReadOnlyList<SignatureDependencyDto>>>
{
    public async Task<Result<IReadOnlyList<SignatureDependencyDto>>> Handle(AddSignatureDependencyCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<IReadOnlyList<SignatureDependencyDto>>(Error.Validation("Not authenticated."));

        var sig = await repo.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<IReadOnlyList<SignatureDependencyDto>>(Error.NotFound("Signature"));

        var target = await repo.FindByIdAsync(cmd.DependsOnSignatureId, me.UserId.Value, ct);
        if (target is null) return Result.Failure<IReadOnlyList<SignatureDependencyDto>>(Error.NotFound("Target signature"));

        // Cycle guard. The resolver knows how to walk the candidate's existing
        // dep graph; if THIS signature is already reachable from the candidate,
        // adding the edge would close a loop. We reject at the use-case layer
        // so the DB check constraint stays a backstop, not the primary line of
        // defense.
        if (await effective.WouldCreateCycleAsync(cmd.SignatureId, cmd.DependsOnSignatureId, me.UserId.Value, ct))
            return Result.Failure<IReadOnlyList<SignatureDependencyDto>>(
                Error.Validation($"Adding '{target.Name}' as a dependency would create a cycle " +
                                 $"(it already depends on '{sig.Name}', directly or transitively)."));

        sig.AddDependency(cmd.DependsOnSignatureId);
        await uow.SaveChangesAsync(ct);

        // Return the updated list — saves the frontend a round-trip.
        return await new ListSignatureDependenciesHandler(repo, me).Handle(
            new ListSignatureDependenciesQuery(cmd.SignatureId), ct);
    }
}

// ── Remove ────────────────────────────────────────────────────────

public record RemoveSignatureDependencyCommand(Guid SignatureId, Guid DependsOnSignatureId)
    : IRequest<Result<IReadOnlyList<SignatureDependencyDto>>>;

public class RemoveSignatureDependencyHandler(
    ISignatureRepository repo,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<RemoveSignatureDependencyCommand, Result<IReadOnlyList<SignatureDependencyDto>>>
{
    public async Task<Result<IReadOnlyList<SignatureDependencyDto>>> Handle(RemoveSignatureDependencyCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<IReadOnlyList<SignatureDependencyDto>>(Error.Validation("Not authenticated."));

        var sig = await repo.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<IReadOnlyList<SignatureDependencyDto>>(Error.NotFound("Signature"));

        sig.RemoveDependency(cmd.DependsOnSignatureId);
        await uow.SaveChangesAsync(ct);

        return await new ListSignatureDependenciesHandler(repo, me).Handle(
            new ListSignatureDependenciesQuery(cmd.SignatureId), ct);
    }
}
