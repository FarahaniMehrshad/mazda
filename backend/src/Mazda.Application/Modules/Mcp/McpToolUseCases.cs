using System.Text;
using System.Text.Json;
using FluentValidation;
using MediatR;
using Mazda.Application.Common.Abstractions;
using Mazda.Application.Modules.Cdp;
using Mazda.Application.Modules.Generations;
using Mazda.Domain.Common;
using Mazda.Domain.Modules.Generations;
using Mazda.Domain.Modules.Mcp;

namespace Mazda.Application.Modules.Mcp;

// ── DTOs ──────────────────────────────────────────────────────────

public record McpToolDto(
    Guid Id,
    Guid SignatureId,
    string SignatureName,
    string Name,
    string DisplayName,
    string Description,
    string DefaultSetupPrompt,
    string DefaultTestPrompt,
    bool Enabled,
    DateTime CreatedAt,
    DateTime UpdatedAt);

internal static class McpToolMapper
{
    public static McpToolDto ToDto(this McpTool t, string signatureName) => new(
        t.Id, t.SignatureId, signatureName,
        t.Name, t.DisplayName, t.Description,
        t.DefaultSetupPrompt, t.DefaultTestPrompt,
        t.Enabled, t.CreatedAt, t.UpdatedAt);

    // Tool names must be MCP-shape: lowercase, alphanumerics + underscores.
    // We sanitize on the way in so the user can type freely in the UI and the
    // server still produces a spec-compliant name.
    public static string SanitizeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is ' ' or '-' or '_' or '.') sb.Append('_');
            // drop everything else
        }
        var result = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(result) ? "tool" : result;
    }
}

// ── List ──────────────────────────────────────────────────────────

public record ListMcpToolsQuery() : IRequest<Result<IReadOnlyList<McpToolDto>>>;

public class ListMcpToolsHandler(
    IMcpToolRepository tools,
    ISignatureRepository signatures,
    ICurrentUser me) : IRequestHandler<ListMcpToolsQuery, Result<IReadOnlyList<McpToolDto>>>
{
    public async Task<Result<IReadOnlyList<McpToolDto>>> Handle(ListMcpToolsQuery _, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<IReadOnlyList<McpToolDto>>(Error.Validation("Not authenticated."));
        var list = await tools.ListAsync(me.UserId.Value, ct);
        // Resolve each tool's signature name so the UI can display it without
        // a per-tool round-trip. Repositories are small enough that listing
        // once and indexing is fine here.
        var sigs = await signatures.ListAsync(me.UserId.Value, ct);
        var byId = sigs.ToDictionary(s => s.Id, s => s.Name);
        var dtos = list.Select(t => t.ToDto(byId.GetValueOrDefault(t.SignatureId, "(deleted)"))).ToList();
        return Result.Success<IReadOnlyList<McpToolDto>>(dtos);
    }
}

// ── Get ───────────────────────────────────────────────────────────

public record GetMcpToolQuery(Guid Id) : IRequest<Result<McpToolDto>>;

public class GetMcpToolHandler(
    IMcpToolRepository tools,
    ISignatureRepository signatures,
    ICurrentUser me) : IRequestHandler<GetMcpToolQuery, Result<McpToolDto>>
{
    public async Task<Result<McpToolDto>> Handle(GetMcpToolQuery q, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<McpToolDto>(Error.Validation("Not authenticated."));
        var t = await tools.FindByIdAsync(q.Id, me.UserId.Value, ct);
        if (t is null) return Result.Failure<McpToolDto>(Error.NotFound("Tool"));
        var sig = await signatures.FindByIdAsync(t.SignatureId, me.UserId.Value, ct);
        return Result.Success(t.ToDto(sig?.Name ?? "(deleted)"));
    }
}

// ── Create ────────────────────────────────────────────────────────

public record CreateMcpToolCommand(
    Guid SignatureId,
    string Name,
    string DisplayName,
    string? Description,
    string? DefaultSetupPrompt,
    string? DefaultTestPrompt,
    bool Enabled) : IRequest<Result<McpToolDto>>;

public class CreateMcpToolValidator : AbstractValidator<CreateMcpToolCommand>
{
    public CreateMcpToolValidator()
    {
        RuleFor(x => x.SignatureId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
        RuleFor(x => x.DisplayName).MaximumLength(120);
        RuleFor(x => x.Description).MaximumLength(10_000).When(x => x.Description is not null);
        RuleFor(x => x.DefaultSetupPrompt).MaximumLength(50_000).When(x => x.DefaultSetupPrompt is not null);
        RuleFor(x => x.DefaultTestPrompt).MaximumLength(50_000).When(x => x.DefaultTestPrompt is not null);
    }
}

public class CreateMcpToolHandler(
    IMcpToolRepository tools,
    ISignatureRepository signatures,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<CreateMcpToolCommand, Result<McpToolDto>>
{
    public async Task<Result<McpToolDto>> Handle(CreateMcpToolCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<McpToolDto>(Error.Validation("Not authenticated."));
        var sig = await signatures.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<McpToolDto>(Error.NotFound("Signature"));

        var canonical = McpToolMapper.SanitizeName(cmd.Name);
        var existing = await tools.FindByNameAsync(canonical, me.UserId.Value, ct);
        if (existing is not null) return Result.Failure<McpToolDto>(Error.Validation($"Tool name '{canonical}' is already taken."));

        var tool = McpTool.Create(
            me.UserId.Value, sig.Id, canonical,
            string.IsNullOrWhiteSpace(cmd.DisplayName) ? canonical : cmd.DisplayName,
            cmd.Description, cmd.DefaultSetupPrompt, cmd.DefaultTestPrompt, cmd.Enabled);
        await tools.AddAsync(tool, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(tool.ToDto(sig.Name));
    }
}

// ── Update ────────────────────────────────────────────────────────

public record UpdateMcpToolCommand(
    Guid Id,
    Guid SignatureId,
    string Name,
    string DisplayName,
    string? Description,
    string? DefaultSetupPrompt,
    string? DefaultTestPrompt,
    bool Enabled) : IRequest<Result<McpToolDto>>;

public class UpdateMcpToolValidator : AbstractValidator<UpdateMcpToolCommand>
{
    public UpdateMcpToolValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.SignatureId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
        RuleFor(x => x.DisplayName).MaximumLength(120);
        RuleFor(x => x.Description).MaximumLength(10_000).When(x => x.Description is not null);
        RuleFor(x => x.DefaultSetupPrompt).MaximumLength(50_000).When(x => x.DefaultSetupPrompt is not null);
        RuleFor(x => x.DefaultTestPrompt).MaximumLength(50_000).When(x => x.DefaultTestPrompt is not null);
    }
}

public class UpdateMcpToolHandler(
    IMcpToolRepository tools,
    ISignatureRepository signatures,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<UpdateMcpToolCommand, Result<McpToolDto>>
{
    public async Task<Result<McpToolDto>> Handle(UpdateMcpToolCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<McpToolDto>(Error.Validation("Not authenticated."));
        var tool = await tools.FindByIdAsync(cmd.Id, me.UserId.Value, ct);
        if (tool is null) return Result.Failure<McpToolDto>(Error.NotFound("Tool"));
        var sig = await signatures.FindByIdAsync(cmd.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<McpToolDto>(Error.NotFound("Signature"));

        var canonical = McpToolMapper.SanitizeName(cmd.Name);
        if (canonical != tool.Name)
        {
            var clash = await tools.FindByNameAsync(canonical, me.UserId.Value, ct);
            if (clash is not null && clash.Id != tool.Id)
                return Result.Failure<McpToolDto>(Error.Validation($"Tool name '{canonical}' is already taken."));
        }

        tool.Rename(canonical, string.IsNullOrWhiteSpace(cmd.DisplayName) ? canonical : cmd.DisplayName);
        if (tool.SignatureId != cmd.SignatureId) tool.Retarget(cmd.SignatureId);
        tool.SetDescription(cmd.Description);
        tool.SetDefaults(cmd.DefaultSetupPrompt, cmd.DefaultTestPrompt);
        tool.SetEnabled(cmd.Enabled);
        await uow.SaveChangesAsync(ct);
        return Result.Success(tool.ToDto(sig.Name));
    }
}

// ── Delete ────────────────────────────────────────────────────────

public record DeleteMcpToolCommand(Guid Id) : IRequest<Result>;

public class DeleteMcpToolHandler(
    IMcpToolRepository tools,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<DeleteMcpToolCommand, Result>
{
    public async Task<Result> Handle(DeleteMcpToolCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure(Error.Validation("Not authenticated."));
        var tool = await tools.FindByIdAsync(cmd.Id, me.UserId.Value, ct);
        if (tool is null) return Result.Failure(Error.NotFound("Tool"));
        await tools.RemoveAsync(tool, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// ── Invoke (the big one) ──────────────────────────────────────────
// Under the function-catalogue architecture this runs the whole pipeline
// (gather → plan → execute) synchronously on behalf of an MCP tool call.
// MCP is a request/response protocol — the caller stays blocked until the
// plan finishes executing, so we bypass the fire-and-forget ExecuteRun
// path and call the runtime services directly.
//
// The Generation row is attributed to the MCP tool so the Runs page can
// surface which agent invoked which plan.

public record McpInvocationResult(
    Guid GenerationId,
    bool IsError,
    string Status,         // "Passed" / "FailedTest" / "Errored" / "Failed"
    int? ExitCode,
    string Summary,        // 1-2 line human summary
    string FullLog);       // Full envelope + optional error stack — the transport surfaces this to the agent.

// Kept for wire compatibility with existing MCP clients: SetupPrompt and
// TestPrompt are joined into a single planner prompt (setup first, then
// blank line, then test). Most tools set only DefaultTestPrompt; the
// concatenation degrades gracefully when either side is empty.
public record InvokeMcpToolCommand(
    Guid ToolId,
    string? SetupPrompt,
    string? TestPrompt) : IRequest<Result<McpInvocationResult>>;

public class InvokeMcpToolHandler(
    IMcpToolRepository tools,
    ISignatureRepository signatures,
    IGenerationRepository generations,
    IEffectiveSignatureResolver effective,
    IStateGatherer gatherer,
    IPlanGenerator planner,
    IPlanInterpreter interpreter,
    IUserSettingsRepository userSettings,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<InvokeMcpToolCommand, Result<McpInvocationResult>>
{
    public async Task<Result<McpInvocationResult>> Handle(InvokeMcpToolCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<McpInvocationResult>(Error.Validation("Not authenticated."));

        var tool = await tools.FindByIdAsync(cmd.ToolId, me.UserId.Value, ct);
        if (tool is null) return Result.Failure<McpInvocationResult>(Error.NotFound("Tool"));
        if (!tool.Enabled) return Result.Failure<McpInvocationResult>(Error.Validation("Tool is disabled."));

        var sig = await signatures.FindByIdAsync(tool.SignatureId, me.UserId.Value, ct);
        if (sig is null) return Result.Failure<McpInvocationResult>(Error.NotFound("Signature"));
        if (sig.Functions.Count == 0)
            return Result.Failure<McpInvocationResult>(Error.Validation(
                "Signature has no function catalogue. Generate one first."));

        var effectiveSigs = await effective.ResolveAsync(sig.Id, me.UserId.Value, ct);
        var deps = effectiveSigs?.Where(s => s.Id != sig.Id).ToList();

        // Resolve the effective prompt. The wire still carries setup +
        // test for compatibility — join them (with fallbacks to the tool's
        // defaults) into the single natural-language prompt the planner
        // sees. Both empty is a validation error.
        var setup = !string.IsNullOrWhiteSpace(cmd.SetupPrompt) ? cmd.SetupPrompt!.Trim() : tool.DefaultSetupPrompt;
        var testPrompt = !string.IsNullOrWhiteSpace(cmd.TestPrompt) ? cmd.TestPrompt!.Trim() : tool.DefaultTestPrompt;
        var prompt = string.Join(
            "\n\n",
            new[] { setup, testPrompt }.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (string.IsNullOrWhiteSpace(prompt))
            return Result.Failure<McpInvocationResult>(Error.Validation(
                "Provide setup_prompt and/or test_prompt (no default configured for this tool)."));

        // Materialise the Generation row up front so the run is visible on
        // the Runs page even if the pipeline fails mid-way.
        var gen = Generation.Create(me.UserId.Value, sig.Id, prompt);
        gen.AttributeToMcp(tool.Id);
        gen.MarkGenerating();
        await generations.AddAsync(gen, ct);
        await uow.SaveChangesAsync(ct);

        try
        {
            // 1 — Gather state.
            var target = await CdpTargetResolver.ResolveAsync(userSettings, me.UserId.Value, ct);
            var state = await gatherer.GatherAsync(sig, deps, target, ct);
            gen.RecordGatheredState(GenerationMapper.SerializeGatheredState(state));
            await uow.SaveChangesAsync(ct);

            // 2 — Ask the planner.
            var planResult = await planner.GeneratePlanAsync(sig, deps, state, prompt, ct);
            gen.RecordPlan(GenerationMapper.SerializePlan(planResult.Plan));
            gen.MarkReady(planResult.TokensUsed);
            await uow.SaveChangesAsync(ct);

            // 3 — Execute. Synchronous by design (MCP is request/response).
            gen.MarkExecutionStarted();
            await uow.SaveChangesAsync(ct);
            var envelope = await interpreter.ExecuteAsync(gen.Id, sig, deps, planResult.Plan, state, target, ct);

            var envelopeJson = GenerationMapper.SerializeEnvelope(envelope);
            gen.RecordExecutionEnvelope(envelopeJson);
            var terminal = envelope.Outcome switch
            {
                RunOutcome.Ok => ExecutionStatus.Passed,
                _ => ExecutionStatus.FailedTest,
            };
            gen.MarkExecutionFinished(terminal, terminal == ExecutionStatus.Passed ? 0 : 1, string.Empty);
            await uow.SaveChangesAsync(ct);

            var isError = terminal != ExecutionStatus.Passed;
            var summary = terminal == ExecutionStatus.Passed
                ? $"Plan succeeded: {envelope.StepsExecuted}/{envelope.StepsTotal} steps."
                : $"Plan failed after {envelope.StepsExecuted}/{envelope.StepsTotal} steps.";
            // FullLog carries the envelope JSON so the MCP client surface
            // (which just prints it back to the agent) has the structured
            // per-step data available. Not human-friendly, but it IS the
            // audit record.
            return Result.Success(new McpInvocationResult(
                gen.Id, isError, terminal.ToString(),
                terminal == ExecutionStatus.Passed ? 0 : 1,
                summary, envelopeJson));
        }
        catch (HarnessException ex)
        {
            gen.MarkFailed($"Runner harness failed: {ex.Message}");
            await uow.SaveChangesAsync(ct);
            return Result.Success(new McpInvocationResult(
                gen.Id, true, "Errored", -1,
                $"Runner harness failed: {ex.Message}", ex.ToString()));
        }
        catch (Exception ex)
        {
            gen.MarkFailed($"MCP invocation failed: {ex.Message}");
            await uow.SaveChangesAsync(ct);
            return Result.Success(new McpInvocationResult(
                gen.Id, true, "Failed", null,
                $"Pipeline failed: {ex.Message}", ex.ToString()));
        }
    }
}

// ── Helper for the MCP transport layer ────────────────────────────
// The transport (HTTP endpoint) needs both the enabled tools AND their
// backing signatures so it can build rich tool descriptions (including the
// list of element selectors the LLM can reference in its prompts). One
// dedicated query keeps the transport thin.

public record ListMcpToolDescriptorsQuery() : IRequest<Result<IReadOnlyList<McpToolDescriptor>>>;

public record McpToolDescriptor(
    Guid Id,
    string Name,
    string DisplayName,
    string Description,
    string DefaultSetupPrompt,
    string DefaultTestPrompt,
    string SignatureName,
    string SignatureContextJson);

public class ListMcpToolDescriptorsHandler(
    IMcpToolRepository tools,
    ISignatureRepository signatures,
    ICurrentUser me) : IRequestHandler<ListMcpToolDescriptorsQuery, Result<IReadOnlyList<McpToolDescriptor>>>
{
    public async Task<Result<IReadOnlyList<McpToolDescriptor>>> Handle(ListMcpToolDescriptorsQuery _, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<IReadOnlyList<McpToolDescriptor>>(Error.Validation("Not authenticated."));
        var enabled = await tools.ListEnabledAsync(me.UserId.Value, ct);
        var sigs = await signatures.ListAsync(me.UserId.Value, ct);
        var byId = sigs.ToDictionary(s => s.Id);

        var descriptors = new List<McpToolDescriptor>(enabled.Count);
        foreach (var t in enabled)
        {
            if (!byId.TryGetValue(t.SignatureId, out var sig)) continue;
            var ctx = JsonSerializer.Serialize(new
            {
                signatureName = sig.Name,
                signatureType = sig.Type,
                humanDescription = sig.HumanDescription,
                userFlowsJson = string.IsNullOrWhiteSpace(sig.UserFlowsJson) ? null : sig.UserFlowsJson,
                elements = sig.Elements.OrderBy(e => e.Order).Select(e => new
                {
                    selector = e.ElementSelector,
                    category = e.Category.ToString().ToLowerInvariant(),
                    elementType = e.ElementType,
                    description = e.ElementDescription,
                    logic = string.IsNullOrWhiteSpace(e.ElementLogic) ? null : e.ElementLogic,
                }),
            }, new JsonSerializerOptions { WriteIndented = false });

            descriptors.Add(new McpToolDescriptor(
                t.Id, t.Name, t.DisplayName, t.Description,
                t.DefaultSetupPrompt, t.DefaultTestPrompt, sig.Name, ctx));
        }
        return Result.Success<IReadOnlyList<McpToolDescriptor>>(descriptors);
    }
}
