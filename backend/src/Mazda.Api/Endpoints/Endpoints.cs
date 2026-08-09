using System.Text;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mazda.Application.Common.Abstractions;
using Mazda.Application.Modules.Generations;
using Mazda.Application.Modules.Identity;
using Mazda.Application.Modules.Mcp;
using Mazda.Application.Modules.Signatures;
using Mazda.Infrastructure.Llm;

namespace Mazda.Api.Endpoints;

public static class Endpoints
{
    public static void MapMazdaEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Meta (public) ─────────────────────────────────────────
        app.MapGet("/api/meta", (IOptions<LlmOptions> opts) => Results.Ok(new
        {
            analyzerModel = opts.Value.AnalyzerModel,
            generatorModel = opts.Value.GeneratorModel,
        })).WithTags("Meta");

        // ── Identity (public) ─────────────────────────────────────
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        auth.MapPost("/register", async (RegisterCommand cmd, ISender s) =>
            (await s.Send(cmd)).ToHttp());

        auth.MapPost("/login", async (LoginCommand cmd, ISender s) =>
            (await s.Send(cmd)).ToHttp());

        // ── Signatures (auth) ─────────────────────────────────────
        var sigs = app.MapGroup("/api/signatures").RequireAuthorization().WithTags("Signatures");

        sigs.MapGet("/", async (ISender s) =>
            (await s.Send(new ListSignaturesQuery())).ToHttp());

        sigs.MapGet("/{id:guid}", async (Guid id, ISender s) =>
            (await s.Send(new GetSignatureQuery(id))).ToHttp());

        sigs.MapPost("/", async (SaveSignatureCommand cmd, ISender s) =>
            (await s.Send(cmd with { Id = null })).ToHttp());

        sigs.MapPut("/{id:guid}", async (Guid id, SaveSignatureCommand cmd, ISender s) =>
            (await s.Send(cmd with { Id = id })).ToHttp());

        sigs.MapDelete("/{id:guid}", async (Guid id, ISender s) =>
            (await s.Send(new DeleteSignatureCommand(id))).ToHttp());

        sigs.MapPost("/analyze", async ([FromBody] AnalyzeComponentCommand cmd, ISender s) =>
            (await s.Send(cmd)).ToHttp());

        // Wizard step 2 — re-run the LLM extraction on the persisted source code
        // and replace the interactive slice (informational is preserved).
        sigs.MapPost("/{id:guid}/reanalyze-interactive", async (Guid id, ISender s) =>
            (await s.Send(new ReanalyzeInteractiveCommand(id))).ToHttp());

        // Wizard step 3 — deep informational re-analysis.
        sigs.MapPost("/{id:guid}/analyze-informational", async (Guid id, ISender s) =>
            (await s.Send(new AnalyzeInformationalCommand(id))).ToHttp());

        // Wizard step 4 — element-relationship graph (semantic edges between
        // selectors: labels, grouped-under, controls, refers-to, etc.).
        sigs.MapPost("/{id:guid}/analyze-relationships", async (Guid id, ISender s) =>
            (await s.Send(new AnalyzeRelationshipsCommand(id))).ToHttp());

        sigs.MapPut("/{id:guid}/relationships", async (Guid id, [FromBody] RelationshipsBody body, ISender s) =>
            (await s.Send(new UpdateRelationshipsCommand(id, body.GraphJson))).ToHttp());

        // Wizard step 5 — user flows.
        sigs.MapPost("/{id:guid}/flows/generate", async (Guid id, [FromBody] GenerateFlowsBody body, ISender s) =>
            (await s.Send(new GenerateUserFlowsCommand(id, body.CypressSource))).ToHttp());

        sigs.MapPut("/{id:guid}/flows", async (Guid id, [FromBody] FlowsBody body, ISender s) =>
            (await s.Send(new UpdateUserFlowsCommand(id, body.FlowsJson))).ToHttp());

        // ── Dependencies — signature → signature(s) it embeds / uses ──
        // The generation pipeline merges every transitive dep's elements into
        // the LLM context, so a test author can reference behaviour of an
        // embedded component without re-declaring its selectors. List returns
        // direct deps only; the resolver expands the transitive closure at
        // generation time.
        sigs.MapGet("/{id:guid}/dependencies", async (Guid id, ISender s) =>
            (await s.Send(new ListSignatureDependenciesQuery(id))).ToHttp());

        sigs.MapPost("/{id:guid}/dependencies", async (Guid id, [FromBody] AddDependencyBody body, ISender s) =>
            (await s.Send(new AddSignatureDependencyCommand(id, body.DependsOnId))).ToHttp());

        sigs.MapDelete("/{id:guid}/dependencies/{depId:guid}", async (Guid id, Guid depId, ISender s) =>
            (await s.Send(new RemoveSignatureDependencyCommand(id, depId))).ToHttp());

        // ── Generations (auth) ────────────────────────────────────
        var gens = app.MapGroup("/api/generations").RequireAuthorization().WithTags("Generations");

        gens.MapGet("/recent", async ([FromQuery] int? take, ISender s) =>
            (await s.Send(new ListRecentGenerationsQuery(take ?? 20))).ToHttp());

        gens.MapGet("/{id:guid}", async (Guid id, ISender s) =>
            (await s.Send(new GetGenerationQuery(id))).ToHttp());

        // Create empty generation tied to a signature.
        gens.MapPost("/", async (CreateGenerationCommand cmd, ISender s) =>
            (await s.Send(cmd)).ToHttp());

        // Wizard step 3 — setup.
        gens.MapPost("/{id:guid}/setup", async (Guid id, [FromBody] GenerateSetupBody body, ISender s) =>
            (await s.Send(new GenerateSetupCommand(id, body.SetupPrompt))).ToHttp());

        gens.MapPut("/{id:guid}/setup-script", async (Guid id, [FromBody] ScriptBody body, ISender s) =>
            (await s.Send(new UpdateScriptCommand(id, ScriptSlot.Setup, body.Script))).ToHttp());

        // Wizard step 6 — analyze state.
        gens.MapPost("/{id:guid}/analyze-state/generate", async (Guid id, ISender s) =>
            (await s.Send(new GenerateAnalyzeScriptCommand(id))).ToHttp());

        gens.MapPut("/{id:guid}/analyze-state-script", async (Guid id, [FromBody] ScriptBody body, ISender s) =>
            (await s.Send(new UpdateAnalyzeScriptCommand(id, body.Script))).ToHttp());

        // Update the scope selector that the analyze script roots its
        // lookups in. Body shape: { "scope": "<selector or null>" }.
        gens.MapPut("/{id:guid}/analyze-state-scope", async (Guid id, [FromBody] AnalyzeScopeBody body, ISender s) =>
            (await s.Send(new UpdateAnalyzeScopeCommand(id, body.Scope))).ToHttp());

        gens.MapPost("/{id:guid}/analyze-state/run", async (Guid id, ISender s) =>
            (await s.Send(new RunAnalyzeCommand(id))).ToHttp());

        // Wizard step 7 — test.
        gens.MapPost("/{id:guid}/test", async (Guid id, [FromBody] GenerateTestBody body, ISender s) =>
            (await s.Send(new GenerateTestCommand(id, body.TestPrompt))).ToHttp());

        gens.MapPut("/{id:guid}/test-script", async (Guid id, [FromBody] ScriptBody body, ISender s) =>
            (await s.Send(new UpdateScriptCommand(id, ScriptSlot.Test, body.Script))).ToHttp());

        // Wizard step 5 — merge & finalize.
        gens.MapPost("/{id:guid}/merge", async (Guid id, ISender s) =>
            (await s.Send(new GenerateMergedCommand(id))).ToHttp());

        gens.MapPut("/{id:guid}/final-script", async (Guid id, [FromBody] ScriptBody body, ISender s) =>
            (await s.Send(new UpdateScriptCommand(id, ScriptSlot.Final, body.Script))).ToHttp());

        // Wizard step 6 — execution.
        gens.MapPost("/{id:guid}/execute", async (Guid id, ISender s) =>
            (await s.Send(new StartExecutionCommand(id))).ToHttp());

        // ── MCP tools (auth) — REST CRUD ──────────────────────────
        var mcp = app.MapGroup("/api/mcp-tools").RequireAuthorization().WithTags("McpTools");

        mcp.MapGet("/", async (ISender s) =>
            (await s.Send(new ListMcpToolsQuery())).ToHttp());

        mcp.MapGet("/{id:guid}", async (Guid id, ISender s) =>
            (await s.Send(new GetMcpToolQuery(id))).ToHttp());

        mcp.MapPost("/", async (CreateMcpToolCommand cmd, ISender s) =>
            (await s.Send(cmd)).ToHttp());

        mcp.MapPut("/{id:guid}", async (Guid id, UpdateMcpToolCommand cmd, ISender s) =>
            (await s.Send(cmd with { Id = id })).ToHttp());

        mcp.MapDelete("/{id:guid}", async (Guid id, ISender s) =>
            (await s.Send(new DeleteMcpToolCommand(id))).ToHttp());

        // SSE log stream. Anonymous — the generation id is an unguessable GUID and the
        // stream is read-only. EventSource cannot send Authorization headers anyway.
        app.MapGet("/api/generations/{id:guid}/execution/stream",
            async (Guid id, IExecutionRegistry registry, HttpContext ctx, CancellationToken ct) =>
            {
                ctx.Response.Headers.ContentType = "text/event-stream";
                ctx.Response.Headers.CacheControl = "no-cache";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";
                await ctx.Response.Body.FlushAsync(ct);

                await foreach (var line in registry.Subscribe(id, ct))
                {
                    // SSE: each event is "data: <payload>\n\n". Encode JSON so newlines
                    // and ANSI bytes inside the line don't break the framing.
                    var payload = System.Text.Json.JsonSerializer.Serialize(line);
                    var bytes = Encoding.UTF8.GetBytes($"data: {payload}\n\n");
                    await ctx.Response.Body.WriteAsync(bytes, ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }

                // Final event lets the client close cleanly.
                var endBytes = Encoding.UTF8.GetBytes("event: end\ndata: \"\"\n\n");
                await ctx.Response.Body.WriteAsync(endBytes, ct);
                await ctx.Response.Body.FlushAsync(ct);
            }).WithTags("Generations");
    }

    public record GenerateSetupBody(string SetupPrompt);
    public record GenerateTestBody(string TestPrompt);
    public record ScriptBody(string Script);
    public record GenerateFlowsBody(string? CypressSource);
    public record AnalyzeScopeBody(string? Scope);
    public record FlowsBody(string FlowsJson);
    public record RelationshipsBody(string GraphJson);
    public record AddDependencyBody(Guid DependsOnId);
}
