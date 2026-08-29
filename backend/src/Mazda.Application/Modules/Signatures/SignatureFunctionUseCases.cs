using FluentValidation;
using MediatR;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Common;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Application.Modules.Signatures;

/// <summary>
/// DTO for a single <see cref="SignatureFunction"/>. The runtime interpreter
/// consumes function bodies + schemas directly, but the frontend Functions
/// page only ever sees this shape — the mapping is one-to-one and lossless
/// so the DTO can be round-tripped through Monaco editors without translation.
/// </summary>
public record SignatureFunctionDto(
    Guid Id,
    string Name,
    // Serialized as lowercase ("state" / "action") so the wire form matches
    // the LLM's own vocabulary and the frontend can string-compare without
    // enum plumbing.
    string Kind,
    string Description,
    string ParametersJsonSchema,
    string ReturnsJsonSchema,
    string Preconditions,
    string Body,
    int Order,
    DateTime UpdatedAt);

internal static class SignatureFunctionMapper
{
    public static SignatureFunctionDto ToDto(this SignatureFunction f) => new(
        f.Id,
        f.Name,
        f.Kind.ToString().ToLowerInvariant(),
        f.Description,
        f.ParametersJsonSchema,
        f.ReturnsJsonSchema,
        f.Preconditions,
        f.Body,
        f.Order,
        f.UpdatedAt);

    /// <summary>
    /// Convert the LLM's <see cref="GeneratedFunction"/> record (wire strings)
    /// into a persisted <see cref="SignatureFunction"/>. Kind is validated at
    /// the analyzer boundary, but we defend once more here — a bad kind value
    /// past this point would silently miscategorise a function and confuse the
    /// runtime interpreter.
    /// </summary>
    public static SignatureFunction ToEntity(this GeneratedFunction g, int order)
    {
        var kind = g.Kind?.Trim().ToLowerInvariant() switch
        {
            "state"  => FunctionKind.State,
            "action" => FunctionKind.Action,
            _ => throw new InvalidOperationException(
                $"Function '{g.Name}' has invalid kind '{g.Kind}' (expected 'state' or 'action')."),
        };

        return SignatureFunction.Create(
            name: g.Name,
            kind: kind,
            description: g.Description,
            parametersJsonSchema: g.ParametersJsonSchema,
            returnsJsonSchema: g.ReturnsJsonSchema,
            body: g.Body,
            order: order,
            preconditions: g.Preconditions);
    }
}

// ── List (read-side) ─────────────────────────────────────────────────
// A dedicated query rather than pulling the whole SignatureDto every time
// the Functions page renders. The frontend only needs the catalogue on this
// page, and function bodies are large enough that hydrating them into every
// SignatureDto (list view, editor page, etc.) is wasteful.

public record ListSignatureFunctionsQuery(Guid SignatureId)
    : IRequest<Result<IReadOnlyList<SignatureFunctionDto>>>;

public class ListSignatureFunctionsHandler(ISignatureRepository repo, ICurrentUser me)
    : IRequestHandler<ListSignatureFunctionsQuery, Result<IReadOnlyList<SignatureFunctionDto>>>
{
    public async Task<Result<IReadOnlyList<SignatureFunctionDto>>> Handle(
        ListSignatureFunctionsQuery q, CancellationToken ct)
    {
        if (me.UserId is null)
            return Result.Failure<IReadOnlyList<SignatureFunctionDto>>(
                Error.Validation("Not authenticated."));

        var sig = await repo.FindByIdAsync(q.SignatureId, me.UserId.Value, ct);
        if (sig is null)
            return Result.Failure<IReadOnlyList<SignatureFunctionDto>>(Error.NotFound("Signature"));

        var dtos = sig.Functions
            .OrderBy(f => f.Order)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => f.ToDto())
            .ToList();

        return Result.Success<IReadOnlyList<SignatureFunctionDto>>(dtos);
    }
}

// ── Generate (LLM regenerate) ────────────────────────────────────────
// Wholesale-replaces the catalogue with a fresh LLM run. Any hand-tuning the
// author had done on individual bodies is lost — matches how every other
// analyzer step in the pipeline works (relationships, flows), and matches the
// user's expectation when they click "Regenerate". A dedicated per-function
// edit endpoint will land in the next slice for surgical fixes.

public record GenerateFunctionCatalogueCommand(Guid SignatureId)
    : IRequest<Result<IReadOnlyList<SignatureFunctionDto>>>;

public class GenerateFunctionCatalogueValidator
    : AbstractValidator<GenerateFunctionCatalogueCommand>
{
    public GenerateFunctionCatalogueValidator()
    {
        RuleFor(x => x.SignatureId).NotEmpty();
    }
}

public class GenerateFunctionCatalogueHandler(
    ISignatureRepository repo,
    IEffectiveSignatureResolver resolver,
    IFunctionCatalogueGenerator generator,
    ICurrentUser me,
    IUnitOfWork uow)
    : IRequestHandler<GenerateFunctionCatalogueCommand, Result<IReadOnlyList<SignatureFunctionDto>>>
{
    public async Task<Result<IReadOnlyList<SignatureFunctionDto>>> Handle(
        GenerateFunctionCatalogueCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null)
            return Result.Failure<IReadOnlyList<SignatureFunctionDto>>(
                Error.Validation("Not authenticated."));

        var sig = await repo.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (sig is null)
            return Result.Failure<IReadOnlyList<SignatureFunctionDto>>(Error.NotFound("Signature"));

        // A catalogue with zero elements would produce a catalogue with zero
        // useful functions (the LLM has nothing to reference). Fail early
        // with a clean validation error rather than burning tokens on a
        // guaranteed-empty output.
        if (sig.Elements.Count == 0)
            return Result.Failure<IReadOnlyList<SignatureFunctionDto>>(
                Error.Validation("Cannot generate a function catalogue for a signature with no elements. " +
                                 "Complete the Upload / Interactable / Informational steps first."));

        // Resolve transitive dependencies so bodies that need to reach into an
        // embedded component's selectors get accurate context. The resolver
        // returns [deepest deps, ..., root]; the root is `sig` itself so we
        // filter it out of the "dependencies" list we pass to the generator
        // (the generator takes `signature` and `dependencies` separately —
        // handing the root as a dep would confuse the prompt).
        IReadOnlyList<Signature>? deps = null;
        if (sig.DependencyIds.Count > 0)
        {
            var effective = await resolver.ResolveAsync(sig.Id, me.UserId.Value, ct);
            deps = effective?
                .Where(s => s.Id != sig.Id)
                .ToList();
        }

        FunctionCatalogueResult result;
        try
        {
            result = await generator.GenerateAsync(sig, deps, ct);
        }
        catch (InvalidOperationException ex)
        {
            // Validation-shaped failures from the generator (unknown kind,
            // duplicate name, missing body, unparseable JSON) — surface as a
            // 400 rather than a 502 because the LLM's output was structurally
            // wrong, not the LLM endpoint itself. The user can regenerate.
            return Result.Failure<IReadOnlyList<SignatureFunctionDto>>(
                Error.Validation($"Function catalogue generator returned an invalid catalogue: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<SignatureFunctionDto>>(
                Error.External($"Function catalogue generator failed: {ExceptionMessage.Describe(ex)}"));
        }

        // Map to entities in the order the generator returned them — that
        // order is meaningful (state functions first, then actions, roughly
        // in flow order) and preserving it saves the frontend from re-sorting.
        var entities = result.Functions
            .Select((g, i) => g.ToEntity(i))
            .ToList();

        await repo.ReplaceFunctionsAsync(sig, entities, ct);
        await uow.SaveChangesAsync(ct);

        var dtos = sig.Functions
            .OrderBy(f => f.Order)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => f.ToDto())
            .ToList();

        return Result.Success<IReadOnlyList<SignatureFunctionDto>>(dtos);
    }
}

// ── Shared name-parse helper ─────────────────────────────────────────
// Central so Create and Update both surface the same error text when a
// caller sends an unknown kind — the alternative is the enum-parse throwing
// a raw ArgumentException, which the validation pipeline can't turn into a
// clean 400 with a useful message.
internal static class SignatureFunctionKindParser
{
    public static Result<FunctionKind> Parse(string? raw)
    {
        return raw?.Trim().ToLowerInvariant() switch
        {
            "state"  => Result.Success(FunctionKind.State),
            "action" => Result.Success(FunctionKind.Action),
            _ => Result.Failure<FunctionKind>(
                Error.Validation($"Invalid function kind '{raw}' (expected 'state' or 'action').")),
        };
    }
}

// ── Small JSON well-formedness helper ────────────────────────────────
// Deliberately does NOT validate JSON-Schema semantics — that's the
// interpreter's job, and JSON-Schema libraries are heavyweight for a v1
// authoring flow. This just makes sure the string is parseable so the
// author gets an immediate red squiggle on typos instead of a mysterious
// failed run later.
internal static class SignatureFunctionSchemaValidator
{
    public static bool TryValidateJson(string raw, out string error)
    {
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(raw);
            error = string.Empty;
            return true;
        }
        catch (System.Text.Json.JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

// ── Create (hand-authored) ───────────────────────────────────────────
// The generator produces most functions; this endpoint exists so authors
// can add a targeted helper the LLM missed without regenerating the whole
// catalogue (which would blow away all their other manual tweaks).

public record CreateSignatureFunctionCommand(
    Guid SignatureId,
    string Name,
    string Kind,
    string Description,
    string ParametersJsonSchema,
    string ReturnsJsonSchema,
    string Preconditions,
    string Body) : IRequest<Result<SignatureFunctionDto>>;

public class CreateSignatureFunctionValidator : AbstractValidator<CreateSignatureFunctionCommand>
{
    public CreateSignatureFunctionValidator()
    {
        RuleFor(x => x.SignatureId).NotEmpty();
        // PascalCase-ish check: at minimum non-empty, start with a letter,
        // no whitespace. Full identifier validation would be stricter but
        // the planner LLM addresses functions by name string (not by JS
        // symbol), so a lenient rule is safer than one that rejects a
        // working name.
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120)
            .Matches("^[A-Za-z][A-Za-z0-9_]*$")
            .WithMessage("Name must start with a letter and contain only letters, digits, and underscores.");
        RuleFor(x => x.Kind).NotEmpty();
        RuleFor(x => x.Body).NotEmpty().MaximumLength(200_000);
        RuleFor(x => x.Description).MaximumLength(5_000);
        RuleFor(x => x.Preconditions).MaximumLength(2_000);
        RuleFor(x => x.ParametersJsonSchema).NotEmpty().MaximumLength(50_000);
        RuleFor(x => x.ReturnsJsonSchema).NotEmpty().MaximumLength(50_000);
    }
}

public class CreateSignatureFunctionHandler(
    ISignatureRepository repo,
    ICurrentUser me,
    IUnitOfWork uow)
    : IRequestHandler<CreateSignatureFunctionCommand, Result<SignatureFunctionDto>>
{
    public async Task<Result<SignatureFunctionDto>> Handle(
        CreateSignatureFunctionCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null)
            return Result.Failure<SignatureFunctionDto>(Error.Validation("Not authenticated."));

        var kindResult = SignatureFunctionKindParser.Parse(cmd.Kind);
        if (!kindResult.IsSuccess)
            return Result.Failure<SignatureFunctionDto>(kindResult.Error);

        if (!SignatureFunctionSchemaValidator.TryValidateJson(cmd.ParametersJsonSchema, out var parametersErr))
            return Result.Failure<SignatureFunctionDto>(
                Error.Validation($"parametersJsonSchema is not valid JSON: {parametersErr}"));
        if (!SignatureFunctionSchemaValidator.TryValidateJson(cmd.ReturnsJsonSchema, out var returnsErr))
            return Result.Failure<SignatureFunctionDto>(
                Error.Validation($"returnsJsonSchema is not valid JSON: {returnsErr}"));

        var sig = await repo.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (sig is null)
            return Result.Failure<SignatureFunctionDto>(Error.NotFound("Signature"));

        // Pre-check the unique-name constraint at the app layer so we return
        // a clean Conflict instead of leaking a Postgres unique-index violation
        // through the default exception handler as a 500. The DB index still
        // enforces it — this is a friendliness layer, not the authority.
        var canonicalName = cmd.Name.Trim();
        if (sig.Functions.Any(f => string.Equals(f.Name, canonicalName, StringComparison.Ordinal)))
            return Result.Failure<SignatureFunctionDto>(
                Error.Conflict($"A function named '{canonicalName}' already exists on this signature."));

        // Append at end-of-list. Explicit reordering is a separate concern
        // the frontend can wire up later; forcing the author to pick an
        // Order for every new function would be busywork.
        var nextOrder = sig.Functions.Count == 0 ? 0 : sig.Functions.Max(f => f.Order) + 1;

        var fn = SignatureFunction.Create(
            name: canonicalName,
            kind: kindResult.Value,
            description: cmd.Description,
            parametersJsonSchema: cmd.ParametersJsonSchema,
            returnsJsonSchema: cmd.ReturnsJsonSchema,
            body: cmd.Body,
            order: nextOrder,
            preconditions: cmd.Preconditions);

        sig.AddFunction(fn);
        await uow.SaveChangesAsync(ct);
        return Result.Success(fn.ToDto());
    }
}

// ── Update (hand-tune) ───────────────────────────────────────────────
// Round-trips every editable field. Mutations go through the aggregate on
// an EF-tracked signature loaded via ISignatureRepository.FindByIdAsync
// (which includes Functions), so SaveChanges picks them up automatically.

public record UpdateSignatureFunctionCommand(
    Guid SignatureId,
    Guid FunctionId,
    string Name,
    string Kind,
    string Description,
    string ParametersJsonSchema,
    string ReturnsJsonSchema,
    string Preconditions,
    string Body,
    int Order) : IRequest<Result<SignatureFunctionDto>>;

public class UpdateSignatureFunctionValidator : AbstractValidator<UpdateSignatureFunctionCommand>
{
    public UpdateSignatureFunctionValidator()
    {
        RuleFor(x => x.SignatureId).NotEmpty();
        RuleFor(x => x.FunctionId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120)
            .Matches("^[A-Za-z][A-Za-z0-9_]*$")
            .WithMessage("Name must start with a letter and contain only letters, digits, and underscores.");
        RuleFor(x => x.Kind).NotEmpty();
        RuleFor(x => x.Body).NotEmpty().MaximumLength(200_000);
        RuleFor(x => x.Description).MaximumLength(5_000);
        RuleFor(x => x.Preconditions).MaximumLength(2_000);
        RuleFor(x => x.ParametersJsonSchema).NotEmpty().MaximumLength(50_000);
        RuleFor(x => x.ReturnsJsonSchema).NotEmpty().MaximumLength(50_000);
    }
}

public class UpdateSignatureFunctionHandler(
    ISignatureRepository repo,
    ICurrentUser me,
    IUnitOfWork uow)
    : IRequestHandler<UpdateSignatureFunctionCommand, Result<SignatureFunctionDto>>
{
    public async Task<Result<SignatureFunctionDto>> Handle(
        UpdateSignatureFunctionCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null)
            return Result.Failure<SignatureFunctionDto>(Error.Validation("Not authenticated."));

        var kindResult = SignatureFunctionKindParser.Parse(cmd.Kind);
        if (!kindResult.IsSuccess)
            return Result.Failure<SignatureFunctionDto>(kindResult.Error);

        if (!SignatureFunctionSchemaValidator.TryValidateJson(cmd.ParametersJsonSchema, out var parametersErr))
            return Result.Failure<SignatureFunctionDto>(
                Error.Validation($"parametersJsonSchema is not valid JSON: {parametersErr}"));
        if (!SignatureFunctionSchemaValidator.TryValidateJson(cmd.ReturnsJsonSchema, out var returnsErr))
            return Result.Failure<SignatureFunctionDto>(
                Error.Validation($"returnsJsonSchema is not valid JSON: {returnsErr}"));

        var sig = await repo.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (sig is null)
            return Result.Failure<SignatureFunctionDto>(Error.NotFound("Signature"));

        var fn = sig.Functions.FirstOrDefault(f => f.Id == cmd.FunctionId);
        if (fn is null)
            return Result.Failure<SignatureFunctionDto>(Error.NotFound("Function"));

        var canonicalName = cmd.Name.Trim();
        // Name-collision check ONLY when the name actually changed. Otherwise
        // a no-op save against a fn's own name would spuriously 409.
        if (!string.Equals(canonicalName, fn.Name, StringComparison.Ordinal)
            && sig.Functions.Any(f => f.Id != fn.Id
                                   && string.Equals(f.Name, canonicalName, StringComparison.Ordinal)))
        {
            return Result.Failure<SignatureFunctionDto>(
                Error.Conflict($"A function named '{canonicalName}' already exists on this signature."));
        }

        fn.Rename(canonicalName);
        fn.ChangeKind(kindResult.Value);
        fn.UpdateDescription(cmd.Description);
        fn.UpdateParametersSchema(cmd.ParametersJsonSchema);
        fn.UpdateReturnsSchema(cmd.ReturnsJsonSchema);
        fn.UpdatePreconditions(cmd.Preconditions);
        fn.UpdateBody(cmd.Body);
        fn.UpdateOrder(cmd.Order);

        await uow.SaveChangesAsync(ct);
        return Result.Success(fn.ToDto());
    }
}

// ── Delete ───────────────────────────────────────────────────────────

public record DeleteSignatureFunctionCommand(Guid SignatureId, Guid FunctionId) : IRequest<Result>;

public class DeleteSignatureFunctionValidator : AbstractValidator<DeleteSignatureFunctionCommand>
{
    public DeleteSignatureFunctionValidator()
    {
        RuleFor(x => x.SignatureId).NotEmpty();
        RuleFor(x => x.FunctionId).NotEmpty();
    }
}

public class DeleteSignatureFunctionHandler(
    ISignatureRepository repo,
    ICurrentUser me,
    IUnitOfWork uow)
    : IRequestHandler<DeleteSignatureFunctionCommand, Result>
{
    public async Task<Result> Handle(DeleteSignatureFunctionCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure(Error.Validation("Not authenticated."));

        var sig = await repo.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure(Error.NotFound("Signature"));

        // Explicit 404 (not silent success) so the frontend can distinguish
        // "the function was already gone" from "your delete went through".
        // The repo's ExecuteDeleteAsync is a silent no-op on a non-existent
        // row, so without this pre-check the endpoint would 204 in both
        // cases and callers couldn't tell.
        if (!sig.Functions.Any(f => f.Id == cmd.FunctionId))
            return Result.Failure(Error.NotFound("Function"));

        await repo.RemoveFunctionAsync(sig, cmd.FunctionId, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
