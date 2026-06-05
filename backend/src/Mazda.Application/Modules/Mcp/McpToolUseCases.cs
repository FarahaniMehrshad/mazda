using System.Text;
using System.Text.Json;
using FluentValidation;
using MediatR;
using Mazda.Application.Common.Abstractions;
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
// Runs the full pipeline (setup gen → analyze script → analyze run → test gen
// → merge → execute) synchronously on behalf of an MCP tool call. Persists a
// fresh Generation row attributed to MCP so the user can audit the run from
// the Runs page.
//
// We deliberately bypass `StartExecutionCommand` (which is fire-and-forget)
// and call IScriptRunner.RunOnceAsync directly — MCP tool calls are
// request/response, so the caller is blocked until the script terminates.

public record McpInvocationResult(
    Guid GenerationId,
    bool IsError,
    string Status,         // "Passed" / "FailedTest" / "Errored" / "Cancelled" / "Failed"
    int? ExitCode,
    string Summary,        // 1-2 line human summary
    string FullLog);

public record InvokeMcpToolCommand(
    Guid ToolId,
    string? SetupPrompt,
    string? TestPrompt) : IRequest<Result<McpInvocationResult>>;

public class InvokeMcpToolHandler(
    IMcpToolRepository tools,
    ISignatureRepository signatures,
    IGenerationRepository generations,
    IScriptGenerator generator,
    IStateBuilder stateBuilder,
    IScriptRunner runner,
    IEffectiveSignatureResolver effective,
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

        // MCP invocations run the same effective-signature view as wizard
        // generations — every transitive dep flows into setup / analyze /
        // test prompts. Resolved once here and threaded through the pipeline.
        var effectiveSigs = await effective.ResolveAsync(sig.Id, me.UserId.Value, ct);
        var deps = effectiveSigs?.Where(s => s.Id != sig.Id).ToList();

        // Resolve the prompts: caller-provided wins, fall back to tool defaults.
        var setupPrompt = !string.IsNullOrWhiteSpace(cmd.SetupPrompt) ? cmd.SetupPrompt!.Trim() : tool.DefaultSetupPrompt;
        var testPrompt  = !string.IsNullOrWhiteSpace(cmd.TestPrompt)  ? cmd.TestPrompt!.Trim()  : tool.DefaultTestPrompt;
        if (string.IsNullOrWhiteSpace(setupPrompt))
            return Result.Failure<McpInvocationResult>(Error.Validation("setup_prompt is required (no default configured for this tool)."));
        if (string.IsNullOrWhiteSpace(testPrompt))
            return Result.Failure<McpInvocationResult>(Error.Validation("test_prompt is required (no default configured for this tool)."));

        // Materialize a Generation row up front. Source = Mcp, McpToolId = tool.Id.
        // Pre-fill scripts from the signature's "Last used" buffers so we can
        // reuse cached setup / analyze / test outputs when the user hasn't
        // changed anything — same behavior as CreateGenerationHandler.
        var gen = Generation.Create(me.UserId.Value, sig.Id);
        gen.AttributeToMcp(tool.Id);
        if (!string.IsNullOrWhiteSpace(sig.LastSetupScript)) gen.SetSetupScript(sig.LastSetupScript);
        if (!string.IsNullOrWhiteSpace(sig.LastTestScript))  gen.SetTestScript(sig.LastTestScript);
        if (!string.IsNullOrWhiteSpace(sig.LastFinalScript)) gen.SetFinalScript(sig.LastFinalScript);
        if (!string.IsNullOrWhiteSpace(sig.LastAnalyzeStateScript)) gen.SetAnalyzeStateScript(sig.LastAnalyzeStateScript);
        if (!string.IsNullOrWhiteSpace(sig.LastAnalyzeStateJson) || !string.IsNullOrWhiteSpace(sig.LastAnalyzeStateRawOutput))
            gen.SetAnalyzeStateOutput(sig.LastAnalyzeStateRawOutput, sig.LastAnalyzeStateJson);
        await generations.AddAsync(gen, ct);
        await uow.SaveChangesAsync(ct);

        try
        {
            // 1 — Generate setup script.
            gen.SetSetupPrompt(setupPrompt);
            sig.SetLastSetupPrompt(setupPrompt);
            var setup = await generator.GenerateSetupAsync(sig, setupPrompt, deps, ct);
            gen.SetSetupScript(setup.Script);
            sig.SetLastSetupScript(setup.Script);
            await uow.SaveChangesAsync(ct);

            // 2 — Build deterministic analyze script.
            var analyzeScript = AnalyzeScriptBuilder.Build(sig, gen, deps);
            gen.SetAnalyzeStateScript(analyzeScript);
            sig.SetLastAnalyzeStateScript(analyzeScript);
            await uow.SaveChangesAsync(ct);

            // 3 — Run the analyze script and parse the captured state.
            var analyzeRun = await runner.RunOnceAsync(analyzeScript, ct);
            var rawJson = AnalyzeScriptBuilder.ExtractStateJson(analyzeRun.FullLog) ?? "{}";
            string stateJson;
            try
            {
                var built = await stateBuilder.BuildStateAsync(sig, rawJson, deps, ct);
                stateJson = built.StateJson;
            }
            catch (Exception ex)
            {
                stateJson = $"{{\"error\":\"state builder failed: {ex.Message.Replace("\"","\\\"")}\",\"rawExtraction\":{rawJson}}}";
            }
            gen.SetAnalyzeStateOutput(analyzeRun.FullLog, stateJson);
            sig.SetLastAnalyzeStateOutput(analyzeRun.FullLog, stateJson);
            await uow.SaveChangesAsync(ct);

            // 4 — Generate the test body.
            gen.SetTestPrompt(testPrompt);
            sig.SetLastTestPrompt(testPrompt);
            var test = await generator.GenerateTestAsync(sig, testPrompt, gen.SetupScript, stateJson, deps, ct);
            gen.SetTestScript(test.Script);
            sig.SetLastTestScript(test.Script);
            await uow.SaveChangesAsync(ct);

            // 5 — Merge setup + test into a single spec.
            var merged = await generator.MergeAsync(setup.Script, test.Script, ct);
            gen.SetFinalScript(merged.Script);
            sig.SetLastFinalScript(merged.Script);
            gen.MarkReady(setup.TokensUsed + test.TokensUsed + merged.TokensUsed);
            await uow.SaveChangesAsync(ct);

            // 6 — Execute the merged script and persist the terminal status.
            gen.MarkExecutionStarted();
            await uow.SaveChangesAsync(ct);
            var exec = await runner.RunAsync(gen.Id, merged.Script, ct);
            var terminal = exec.Kind switch
            {
                RunnerExitKind.Passed     => ExecutionStatus.Passed,
                RunnerExitKind.FailedTest => ExecutionStatus.FailedTest,
                RunnerExitKind.Cancelled  => ExecutionStatus.Cancelled,
                _                         => ExecutionStatus.Errored,
            };
            gen.MarkExecutionFinished(terminal, exec.ExitCode, exec.FullLog);
            await uow.SaveChangesAsync(ct);

            var isError = terminal != ExecutionStatus.Passed;
            var summary = terminal switch
            {
                ExecutionStatus.Passed     => "All assertions passed.",
                ExecutionStatus.FailedTest => "Test ran but at least one assertion failed.",
                ExecutionStatus.Errored    => $"Runner errored (exit code {exec.ExitCode}).",
                ExecutionStatus.Cancelled  => "Run was cancelled.",
                _                          => $"Terminated with status {terminal}.",
            };
            return Result.Success(new McpInvocationResult(gen.Id, isError, terminal.ToString(), exec.ExitCode, summary, exec.FullLog));
        }
        catch (Exception ex)
        {
            gen.MarkFailed($"MCP invocation failed: {ex.Message}");
            await uow.SaveChangesAsync(ct);
            return Result.Success(new McpInvocationResult(
                gen.Id, true, "Failed", null,
                $"Pipeline failed before execution: {ex.Message}",
                ex.ToString()));
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
