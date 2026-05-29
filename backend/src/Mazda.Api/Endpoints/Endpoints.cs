using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mazda.Application.Modules.Generations;
using Mazda.Application.Modules.Identity;
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

        // ── Generations (auth) ────────────────────────────────────
        var gens = app.MapGroup("/api/generations").RequireAuthorization().WithTags("Generations");

        gens.MapGet("/recent", async ([FromQuery] int? take, ISender s) =>
            (await s.Send(new ListRecentGenerationsQuery(take ?? 20))).ToHttp());

        gens.MapGet("/{id:guid}", async (Guid id, ISender s) =>
            (await s.Send(new GetGenerationQuery(id))).ToHttp());

        gens.MapPost("/", async (GenerateScriptCommand cmd, ISender s) =>
            (await s.Send(cmd)).ToHttp());
    }
}
