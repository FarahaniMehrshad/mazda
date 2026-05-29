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
}

internal static class ChatClientFactory
{
    public static ChatClient ForModel(LlmOptions opts, string model)
    {
        if (string.IsNullOrWhiteSpace(opts.ApiKey))
            throw new InvalidOperationException("Llm:ApiKey is not configured.");
        if (string.IsNullOrWhiteSpace(opts.BaseUrl))
            throw new InvalidOperationException("Llm:BaseUrl is not configured.");
        if (!Uri.TryCreate(opts.BaseUrl, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException($"Llm:BaseUrl is not a valid absolute URL: '{opts.BaseUrl}'.");

        var clientOptions = new OpenAIClientOptions { Endpoint = endpoint };
        return new ChatClient(
            model: model,
            credential: new ApiKeyCredential(opts.ApiKey),
            options: clientOptions);
    }
}
