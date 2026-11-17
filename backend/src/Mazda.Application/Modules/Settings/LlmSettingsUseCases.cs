using FluentValidation;
using MediatR;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Common;
using Mazda.Domain.Modules.Settings;

namespace Mazda.Application.Modules.Settings;

// ── What .env says, and what is in effect ────────────────────────────

/// <summary>The LLM settings from the environment (.env), the fallback for every empty saved field.</summary>
public sealed record LlmEnvironment(
    string Provider,
    string OpenAiBaseUrl,
    string OpenAiApiKey,
    string OpenAiAnalyzerModel,
    string OpenAiGeneratorModel,
    string ClaudeBridgeUrl,
    string ClaudeBridgeToken,
    string ClaudeAnalyzerModel,
    string ClaudeGeneratorModel,
    int TimeoutSeconds);

/// <summary>The settings in effect: saved values where given, .env otherwise.</summary>
public sealed record ResolvedLlmSettings(
    string Provider,
    string OpenAiBaseUrl,
    string OpenAiApiKey,
    string OpenAiAnalyzerModel,
    string OpenAiGeneratorModel,
    string ClaudeBridgeUrl,
    string ClaudeBridgeToken,
    string ClaudeAnalyzerModel,
    string ClaudeGeneratorModel,
    int TimeoutSeconds)
{
    public string AnalyzerModel => Provider == LlmProviders.ClaudeCode ? ClaudeAnalyzerModel : OpenAiAnalyzerModel;
    public string GeneratorModel => Provider == LlmProviders.ClaudeCode ? ClaudeGeneratorModel : OpenAiGeneratorModel;
}

public static class LlmSettingsResolver
{
    public const string DefaultBridgeUrl = "http://host.docker.internal:8787";
    public const string DefaultClaudeAnalyzerModel = "sonnet";
    public const string DefaultClaudeGeneratorModel = "opus";

    public static ResolvedLlmSettings Resolve(LlmSettings? saved, LlmEnvironment env)
    {
        static string Pick(string? saved, string? env, string fallback = "")
            => !string.IsNullOrWhiteSpace(saved) ? saved.Trim()
             : !string.IsNullOrWhiteSpace(env) ? env.Trim()
             : fallback;

        var provider = Pick(saved?.Provider, env.Provider, LlmProviders.OpenAi);
        if (!LlmProviders.IsKnown(provider)) provider = LlmProviders.OpenAi;
        return new ResolvedLlmSettings(
            Provider: provider,
            OpenAiBaseUrl: Pick(saved?.OpenAiBaseUrl, env.OpenAiBaseUrl, "https://api.openai.com/v1"),
            OpenAiApiKey: Pick(saved?.OpenAiApiKey, env.OpenAiApiKey),
            OpenAiAnalyzerModel: Pick(saved?.OpenAiAnalyzerModel, env.OpenAiAnalyzerModel, "gpt-4o-mini"),
            OpenAiGeneratorModel: Pick(saved?.OpenAiGeneratorModel, env.OpenAiGeneratorModel, "gpt-4o"),
            ClaudeBridgeUrl: Pick(saved?.ClaudeBridgeUrl, env.ClaudeBridgeUrl, DefaultBridgeUrl),
            ClaudeBridgeToken: env.ClaudeBridgeToken?.Trim() ?? string.Empty,
            ClaudeAnalyzerModel: Pick(saved?.ClaudeAnalyzerModel, env.ClaudeAnalyzerModel, DefaultClaudeAnalyzerModel),
            ClaudeGeneratorModel: Pick(saved?.ClaudeGeneratorModel, env.ClaudeGeneratorModel, DefaultClaudeGeneratorModel),
            TimeoutSeconds: saved is { TimeoutSeconds: > 0 } ? saved.TimeoutSeconds : env.TimeoutSeconds > 0 ? env.TimeoutSeconds : 600);
    }
}

/// <summary>
/// The settings every LLM call uses. Cached; <see cref="Invalidate"/> after
/// saving so the next call picks the change up.
/// </summary>
public interface ILlmSettingsStore
{
    LlmEnvironment Environment { get; }
    Task<ResolvedLlmSettings> GetAsync(CancellationToken ct = default);
    void Invalidate();
}

public sealed record LlmTestResult(bool Ok, string Provider, string Model, string? Reply, long Ms, int Tokens, string? Error);

/// <summary>Sends one tiny prompt through the settings in effect.</summary>
public interface ILlmConnectionTester
{
    Task<LlmTestResult> TestAsync(CancellationToken ct = default);
}

// ── DTOs ─────────────────────────────────────────────────────────────

/// <summary>
/// Saved values (empty = not saved, .env applies) next to the .env values
/// the UI shows as placeholders. API keys and the bridge token never leave
/// the server: only whether one is set.
/// </summary>
public record LlmSettingsDto(
    string Provider,
    string OpenAiBaseUrl,
    bool OpenAiApiKeySaved,
    string OpenAiAnalyzerModel,
    string OpenAiGeneratorModel,
    string ClaudeBridgeUrl,
    string ClaudeAnalyzerModel,
    string ClaudeGeneratorModel,
    int TimeoutSeconds,
    LlmDefaultsDto Defaults,
    LlmEffectiveDto Effective);

public record LlmDefaultsDto(
    string Provider,
    string OpenAiBaseUrl,
    bool OpenAiApiKeySet,
    string OpenAiAnalyzerModel,
    string OpenAiGeneratorModel,
    string ClaudeBridgeUrl,
    bool ClaudeBridgeTokenSet,
    string ClaudeAnalyzerModel,
    string ClaudeGeneratorModel,
    int TimeoutSeconds);

public record LlmEffectiveDto(string Provider, string AnalyzerModel, string GeneratorModel, bool Ready, string? NotReadyReason);

internal static class LlmSettingsMapping
{
    public static LlmSettingsDto ToDto(LlmSettings? s, LlmEnvironment env)
    {
        var d = LlmSettingsResolver.Resolve(null, env);
        var r = LlmSettingsResolver.Resolve(s, env);
        string? notReady = r.Provider == LlmProviders.ClaudeCode
            ? (string.IsNullOrEmpty(r.ClaudeBridgeToken) ? "CLAUDE_BRIDGE_TOKEN is not set in .env for the API." : null)
            : (string.IsNullOrEmpty(r.OpenAiApiKey) ? "No API key: save one here or set LLM_API_KEY in .env." : null);
        return new LlmSettingsDto(
            Provider: s?.Provider ?? "",
            OpenAiBaseUrl: s?.OpenAiBaseUrl ?? "",
            OpenAiApiKeySaved: !string.IsNullOrEmpty(s?.OpenAiApiKey),
            OpenAiAnalyzerModel: s?.OpenAiAnalyzerModel ?? "",
            OpenAiGeneratorModel: s?.OpenAiGeneratorModel ?? "",
            ClaudeBridgeUrl: s?.ClaudeBridgeUrl ?? "",
            ClaudeAnalyzerModel: s?.ClaudeAnalyzerModel ?? "",
            ClaudeGeneratorModel: s?.ClaudeGeneratorModel ?? "",
            TimeoutSeconds: s?.TimeoutSeconds ?? 0,
            Defaults: new LlmDefaultsDto(
                d.Provider, d.OpenAiBaseUrl, !string.IsNullOrEmpty(d.OpenAiApiKey), d.OpenAiAnalyzerModel, d.OpenAiGeneratorModel,
                d.ClaudeBridgeUrl, !string.IsNullOrEmpty(d.ClaudeBridgeToken), d.ClaudeAnalyzerModel, d.ClaudeGeneratorModel,
                d.TimeoutSeconds),
            Effective: new LlmEffectiveDto(r.Provider, r.AnalyzerModel, r.GeneratorModel, notReady is null, notReady));
    }
}

// ── Get ──────────────────────────────────────────────────────────────

public record GetLlmSettingsQuery : IRequest<Result<LlmSettingsDto>>;

public class GetLlmSettingsHandler(ILlmSettingsRepository repo, ILlmSettingsStore store)
    : IRequestHandler<GetLlmSettingsQuery, Result<LlmSettingsDto>>
{
    public async Task<Result<LlmSettingsDto>> Handle(GetLlmSettingsQuery q, CancellationToken ct)
        => Result.Success(LlmSettingsMapping.ToDto(await repo.GetAsync(ct), store.Environment));
}

// ── Update ───────────────────────────────────────────────────────────

/// <summary>Empty strings fall back to .env. <c>OpenAiApiKey</c> null/empty keeps the saved key.</summary>
public record UpdateLlmSettingsCommand(
    string Provider,
    string? OpenAiBaseUrl,
    string? OpenAiApiKey,
    bool ClearOpenAiApiKey,
    string? OpenAiAnalyzerModel,
    string? OpenAiGeneratorModel,
    string? ClaudeBridgeUrl,
    string? ClaudeAnalyzerModel,
    string? ClaudeGeneratorModel,
    int TimeoutSeconds) : IRequest<Result<LlmSettingsDto>>;

public class UpdateLlmSettingsValidator : AbstractValidator<UpdateLlmSettingsCommand>
{
    public UpdateLlmSettingsValidator()
    {
        RuleFor(x => x.Provider).Must(p => string.IsNullOrEmpty(p) || LlmProviders.IsKnown(p))
            .WithMessage($"Provider must be '{LlmProviders.OpenAi}' or '{LlmProviders.ClaudeCode}'.");
        RuleFor(x => x.OpenAiBaseUrl).Must(BeEmptyOrHttpUrl).WithMessage("The API base URL must be an absolute http(s) URL.");
        RuleFor(x => x.ClaudeBridgeUrl).Must(BeEmptyOrHttpUrl).WithMessage("The bridge URL must be an absolute http(s) URL.");
        RuleFor(x => x.TimeoutSeconds).InclusiveBetween(0, 3600);
        RuleFor(x => x.OpenAiAnalyzerModel).MaximumLength(LlmSettings.MaxModel);
        RuleFor(x => x.OpenAiGeneratorModel).MaximumLength(LlmSettings.MaxModel);
        RuleFor(x => x.ClaudeAnalyzerModel).MaximumLength(LlmSettings.MaxModel);
        RuleFor(x => x.ClaudeGeneratorModel).MaximumLength(LlmSettings.MaxModel);
        RuleFor(x => x.OpenAiApiKey).MaximumLength(LlmSettings.MaxApiKey);
    }

    private static bool BeEmptyOrHttpUrl(string? s)
        => string.IsNullOrWhiteSpace(s)
           || (s.Trim().Length <= LlmSettings.MaxUrl
               && Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u)
               && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps));
}

public class UpdateLlmSettingsHandler(ILlmSettingsRepository repo, ILlmSettingsStore store, IUnitOfWork uow)
    : IRequestHandler<UpdateLlmSettingsCommand, Result<LlmSettingsDto>>
{
    public async Task<Result<LlmSettingsDto>> Handle(UpdateLlmSettingsCommand c, CancellationToken ct)
    {
        var s = await repo.GetOrCreateAsync(ct);
        s.Update(c.Provider ?? "", c.OpenAiBaseUrl, c.OpenAiApiKey, c.ClearOpenAiApiKey,
            c.OpenAiAnalyzerModel, c.OpenAiGeneratorModel,
            c.ClaudeBridgeUrl, c.ClaudeAnalyzerModel, c.ClaudeGeneratorModel, c.TimeoutSeconds);
        await uow.SaveChangesAsync(ct);
        store.Invalidate();
        return Result.Success(LlmSettingsMapping.ToDto(s, store.Environment));
    }
}

// ── Test ─────────────────────────────────────────────────────────────

public record TestLlmConnectionCommand : IRequest<Result<LlmTestResult>>;

public class TestLlmConnectionHandler(ILlmConnectionTester tester)
    : IRequestHandler<TestLlmConnectionCommand, Result<LlmTestResult>>
{
    // A failed test is an answer (shown in the UI), not a failed request.
    public async Task<Result<LlmTestResult>> Handle(TestLlmConnectionCommand c, CancellationToken ct)
        => Result.Success(await tester.TestAsync(ct));
}
