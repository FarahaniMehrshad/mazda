using System.Text;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mazda.Application.Common.Abstractions;
using Mazda.Application.Modules.Generations;
using Mazda.Application.Modules.Identity;
using Mazda.Application.Modules.Mcp;
using Mazda.Application.Modules.Cdp;
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
        sigs.MapPost("/{id:guid}/flows/generate", async (Guid id, ISender s) =>
            (await s.Send(new GenerateUserFlowsCommand(id))).ToHttp());

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

        // ── High-level function catalogue (design-time) ───────────
        // The runtime planner LLM composes plans out of these — see
        // SignatureFunctionUseCases. GET returns the current catalogue for
        // the Functions wizard page; POST /generate hands the fully-analyzed
        // signature to the catalogue-generator LLM and wholesale-replaces the
        // stored catalogue with the result.
        sigs.MapGet("/{id:guid}/functions", async (Guid id, ISender s) =>
            (await s.Send(new ListSignatureFunctionsQuery(id))).ToHttp());

        sigs.MapPost("/{id:guid}/functions/generate", async (Guid id, ISender s) =>
            (await s.Send(new GenerateFunctionCatalogueCommand(id))).ToHttp());

        // Hand-authored / hand-tuned function CRUD. Sits alongside the
        // generator so authors can add a helper the LLM missed, tweak a
        // body, or delete a spurious function without regenerating the
        // whole catalogue and losing every other manual edit.
        //
        // POST rebinds the SignatureId from the route (the client sends
        // the rest of the payload in the body).
        sigs.MapPost("/{id:guid}/functions",
            async (Guid id, [FromBody] CreateSignatureFunctionCommand cmd, ISender s) =>
                (await s.Send(cmd with { SignatureId = id })).ToHttp());

        // PUT rebinds BOTH the SignatureId and FunctionId from the route so
        // the client cannot accidentally cross-write onto a different
        // signature or function by sending the wrong id in the body.
        sigs.MapPut("/{id:guid}/functions/{fid:guid}",
            async (Guid id, Guid fid, [FromBody] UpdateSignatureFunctionCommand cmd, ISender s) =>
                (await s.Send(cmd with { SignatureId = id, FunctionId = fid })).ToHttp());

        sigs.MapDelete("/{id:guid}/functions/{fid:guid}",
            async (Guid id, Guid fid, ISender s) =>
                (await s.Send(new DeleteSignatureFunctionCommand(id, fid))).ToHttp());

        // Run ONE function against the CDP-attached browser with caller-supplied
        // args — the Functions page's "Test" affordance. Synchronous and slow by
        // nature (Playwright spawn + real interaction), which the 30m
        // proxy_read_timeout on /api/ in frontend.nginx.conf accommodates.
        //
        // A body that throws still returns 200 with ok:false — the error message
        // IS the answer the author came for. Only an unreachable browser or a
        // spec that won't run produces a 502.
        // Ask the LLM to repair a function that just failed. The client posts
        // back the failure it already holds rather than making us re-run the
        // test to rediscover it. Returns a PROPOSAL — nothing is persisted, so
        // applying an AI edit goes through the same PUT as a hand edit.
        sigs.MapPost("/{id:guid}/functions/{fid:guid}/fix",
            async (Guid id, Guid fid, [FromBody] FixFunctionBody body, ISender s) =>
                (await s.Send(new FixSignatureFunctionCommand(
                    id, fid, body.UserHint, body.ErrorKind, body.ErrorMessage,
                    body.ErrorStack, body.ArgsJson, body.PageUrl, body.PageTitle,
                    body.LogTail))).ToHttp());

        sigs.MapPost("/{id:guid}/functions/{fid:guid}/test",
            async (Guid id, Guid fid, [FromBody] TestFunctionBody body, ISender s) =>
                (await s.Send(new TestSignatureFunctionCommand(id, fid, body.ArgsJson ?? "{}"))).ToHttp());

        // ── Runs / Generations (auth) ─────────────────────────────
        // Under the function-catalogue architecture a "run" is one pass
        // through gather → plan → execute. Two endpoints do the work:
        //
        //   POST /plan          — synchronous: create the Generation, run
        //                          gather (Playwright) + plan (LLM), return
        //                          a Ready DTO with GatheredStateJson +
        //                          PlanJson populated. This is where the
        //                          user gets to review the plan before it
        //                          runs.
        //
        //   POST /{id}/execute  — fire-and-forget: kicks off the
        //                          interpreter on a background task. The
        //                          SSE stream at .../execution/stream
        //                          carries __MZ_EVENT__ lines per step.
        //                          Returns the running DTO immediately.
        var gens = app.MapGroup("/api/generations").RequireAuthorization().WithTags("Generations");

        gens.MapGet("/recent", async ([FromQuery] int? take, ISender s) =>
            (await s.Send(new ListRecentGenerationsQuery(take ?? 20))).ToHttp());

        gens.MapGet("/{id:guid}", async (Guid id, ISender s) =>
            (await s.Send(new GetGenerationQuery(id))).ToHttp());

        gens.MapPost("/plan", async (PlanRunCommand cmd, ISender s) =>
            (await s.Send(cmd)).ToHttp());

        gens.MapPost("/{id:guid}/execute", async (Guid id, ISender s) =>
            (await s.Send(new ExecuteRunCommand(id))).ToHttp());

        // ── MCP tools (auth) — REST CRUD ──────────────────────────
        // ── CDP attachment ────────────────────────────────────────
        // Which Chrome, and which tab, MAZDA drives. GET probes the browser
        // and lists its tabs in one call so the panel renders from a single
        // round-trip; an unreachable browser is reported in the payload
        // (available:false + reason) rather than as a failed request, because
        // "Chrome isn't running" is a normal state the UI must render.
        var cdp = app.MapGroup("/api/cdp").RequireAuthorization().WithTags("Cdp");

        cdp.MapGet("/status", async (ISender s) =>
            (await s.Send(new GetCdpStatusQuery())).ToHttp());

        cdp.MapPost("/attach", async ([FromBody] AttachTabBody body, ISender s) =>
            (await s.Send(new AttachCdpTabCommand(body.TargetId))).ToHttp());

        cdp.MapPost("/detach", async (ISender s) =>
            (await s.Send(new DetachCdpTabCommand())).ToHttp());

        cdp.MapPut("/port", async ([FromBody] CdpPortBody body, ISender s) =>
            (await s.Send(new SetCdpPortCommand(body.Port))).ToHttp());

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

    // Body records for endpoints whose model-binder needs a shaped payload.
    // Command records defined in Application handle the rest.
    public record FlowsBody(string FlowsJson);
    public record RelationshipsBody(string GraphJson);
    public record AddDependencyBody(Guid DependsOnId);
    // Args travel as a JSON *string* rather than a bound object: the shape is
    // per-function (it comes from that function's parameter schema), so there
    // is nothing static to bind to.
    public record TestFunctionBody(string? ArgsJson);
    public record AttachTabBody(string TargetId);
    public record CdpPortBody(int Port);
    public record FixFunctionBody(
        string? UserHint, string? ErrorKind, string? ErrorMessage, string? ErrorStack,
        string? ArgsJson, string? PageUrl, string? PageTitle, string? LogTail);
}
