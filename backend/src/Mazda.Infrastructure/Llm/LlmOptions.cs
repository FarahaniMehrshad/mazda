using System.ClientModel;
using OpenAI;
using OpenAI.Chat;

namespace Mazda.Infrastructure.Llm;

/// <summary>
/// Configuration for any OpenAI-compatible LLM provider. The on-the-wire
/// protocol is OpenAI's Chat Completions API; only the base URL changes
/// between providers (OpenAI, Azure OpenAI, Ollama, vLLM, LM Studio,
/// OpenRouter, DeepSeek, Together, Groq, Fireworks, Mistral, etc.).
/// </summary>
public class LlmOptions
{
    /// <summary>
    /// Default provider when Settings has none saved: "openai" (any
    /// OpenAI-compatible API, below) or "claude-code" (the Claude Code CLI on
    /// the user's computer through the host bridge, see <see cref="ClaudeBridgeOptions"/>).
    /// </summary>
    public string Provider { get; set; } = "openai";

    /// <summary>
    /// Base URL of the OpenAI-compatible API. Default points at the public
    /// OpenAI API. Examples:
    ///   - https://api.openai.com/v1
    ///   - http://localhost:11434/v1            (Ollama)
    ///   - http://localhost:8000/v1             (vLLM, LM Studio, llama.cpp)
    ///   - https://openrouter.ai/api/v1
    ///   - https://api.deepseek.com/v1
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>
    /// API key. Many local servers (Ollama, vLLM) ignore this but the SDK
    /// still requires a non-empty value — use "sk-local" or similar.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model name used for the component-analysis (signature extraction) call.</summary>
    public string AnalyzerModel { get; set; } = "gpt-4o-mini";

    /// <summary>Model name used for the Playwright script-generation call.</summary>
    public string GeneratorModel { get; set; } = "gpt-4o";

    /// <summary>
    /// How long to wait for a single completion before giving up. The SDK's own
    /// default is 100s, which is well short of what a large source bundle takes
    /// on a slower or self-hosted model.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 600;
}

/// <summary>
/// The Claude Code host bridge (<c>tools/claude-bridge/bridge.mjs</c>). The
/// token is shared with the bridge through the repository's .env and is
/// never stored in the database or shown in the UI.
/// </summary>
public class ClaudeBridgeOptions
{
    /// <summary>Where the API reaches the bridge; empty for the default (host.docker.internal:8787).</summary>
    public string Url { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    /// <summary>Default models for Claude Code: aliases (sonnet, opus, haiku) or full model ids.</summary>
    public string AnalyzerModel { get; set; } = string.Empty;
    public string GeneratorModel { get; set; } = string.Empty;
}

internal static class ChatClientFactory
{
    public static ChatClient Create(string baseUrl, string apiKey, string model, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new LlmException("No LLM API key is configured: save one in Settings or set LLM_API_KEY in .env.");
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new LlmException("No LLM base URL is configured.");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint))
            throw new LlmException($"The LLM base URL is not a valid absolute URL: '{baseUrl}'.");

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            NetworkTimeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)),
        };
        return new ChatClient(
            model: model,
            credential: new ApiKeyCredential(apiKey),
            options: clientOptions);
    }
}
