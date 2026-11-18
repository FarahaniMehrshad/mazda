using Mazda.Domain.Common;

namespace Mazda.Domain.Modules.Settings;

/// <summary>Which kind of LLM connection Mazda uses.</summary>
public static class LlmProviders
{
    /// <summary>An OpenAI-compatible HTTP API with an API key (OpenAI, OpenRouter, Ollama, vLLM, …).</summary>
    public const string OpenAi = "openai";

    /// <summary>The Claude Code CLI on the user's computer, reached through the host bridge.</summary>
    public const string ClaudeCode = "claude-code";

    public static bool IsKnown(string? provider) => provider is OpenAi or ClaudeCode;
}

/// <summary>
/// The language-model connection, chosen in the UI. One row for the whole
/// installation: the LLM services are shared and Mazda runs as a local tool.
///
/// Every field is optional. An empty value means "use what .env says"
/// (<c>LLM_*</c>, <c>CLAUDE_BRIDGE_*</c>), so an installation that never
/// opens Settings keeps working exactly as before.
/// </summary>
public class LlmSettings : Entity
{
    public const int MaxUrl = 2000;
    public const int MaxModel = 200;
    public const int MaxApiKey = 2000;

    /// <summary><see cref="LlmProviders"/> value, or empty for the .env default.</summary>
    public string Provider { get; private set; } = string.Empty;

    public string OpenAiBaseUrl { get; private set; } = string.Empty;
    public string OpenAiApiKey { get; private set; } = string.Empty;
    public string OpenAiAnalyzerModel { get; private set; } = string.Empty;
    public string OpenAiGeneratorModel { get; private set; } = string.Empty;

    public string ClaudeBridgeUrl { get; private set; } = string.Empty;
    public string ClaudeAnalyzerModel { get; private set; } = string.Empty;
    public string ClaudeGeneratorModel { get; private set; } = string.Empty;

    /// <summary>Seconds one LLM call may take; 0 for the .env default.</summary>
    public int TimeoutSeconds { get; private set; }

    /// <summary>
    /// The price list (JSON) used to estimate what API-key calls cost; empty
    /// for the built-in defaults.
    /// </summary>
    public string PricesJson { get; private set; } = string.Empty;

    private LlmSettings() { } // EF

    public static LlmSettings CreateDefault() => new();

    /// <summary>
    /// Replaces every setting. <paramref name="openAiApiKey"/> null keeps the
    /// saved key (the UI never sees it, so it cannot send it back); use
    /// <paramref name="clearApiKey"/> to fall back to the .env key.
    /// </summary>
    public void Update(
        string provider,
        string? openAiBaseUrl, string? openAiApiKey, bool clearApiKey,
        string? openAiAnalyzerModel, string? openAiGeneratorModel,
        string? claudeBridgeUrl, string? claudeAnalyzerModel, string? claudeGeneratorModel,
        int timeoutSeconds)
    {
        if (!string.IsNullOrEmpty(provider) && !LlmProviders.IsKnown(provider))
            throw new ArgumentException($"Unknown provider '{provider}'.", nameof(provider));
        if (timeoutSeconds is < 0 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Timeout must be between 0 and 3600 seconds.");

        Provider = provider ?? string.Empty;
        OpenAiBaseUrl = Clean(openAiBaseUrl, MaxUrl);
        if (clearApiKey) OpenAiApiKey = string.Empty;
        else if (!string.IsNullOrWhiteSpace(openAiApiKey)) OpenAiApiKey = Clean(openAiApiKey, MaxApiKey);
        OpenAiAnalyzerModel = Clean(openAiAnalyzerModel, MaxModel);
        OpenAiGeneratorModel = Clean(openAiGeneratorModel, MaxModel);
        ClaudeBridgeUrl = Clean(claudeBridgeUrl, MaxUrl);
        ClaudeAnalyzerModel = Clean(claudeAnalyzerModel, MaxModel);
        ClaudeGeneratorModel = Clean(claudeGeneratorModel, MaxModel);
        TimeoutSeconds = timeoutSeconds;
        Touch();
    }

    /// <summary>Replaces the price list; empty goes back to the built-in defaults.</summary>
    public void SetPrices(string? pricesJson)
    {
        PricesJson = pricesJson?.Trim() ?? string.Empty;
        Touch();
    }

    private static string Clean(string? s, int max)
    {
        var t = s?.Trim() ?? string.Empty;
        return t.Length > max ? t[..max] : t;
    }
}
