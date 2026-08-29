using System.ClientModel;
using System.Text.Json;
using Mazda.Domain.Common;
using OpenAI.Chat;

namespace Mazda.Infrastructure.Llm;

/// <summary>
/// Thrown when the LLM provider rejects or fails a request. The message is
/// already human-readable ("LLM quota/budget exceeded (429): You exceeded your
/// current quota") and is safe to surface directly in the UI — every use case
/// funnels it into <c>Error.External</c>, which the API returns as the
/// ProblemDetails <c>detail</c>.
/// </summary>
public class LlmException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal static class LlmChatExtensions
{
    /// <summary>
    /// <see cref="ChatClient.CompleteChatAsync(IEnumerable{ChatMessage}, ChatCompletionOptions, CancellationToken)"/>
    /// with provider failures translated into <see cref="LlmException"/>. The raw
    /// SDK exception only says "Service request failed. Status: 429" — the part a
    /// human needs (quota exceeded, unknown model, bad key) lives in the response
    /// body, so we dig it out here rather than at every call site.
    /// </summary>
    public static async Task<ClientResult<ChatCompletion>> CompleteChatSafeAsync(
        this ChatClient chat,
        IEnumerable<ChatMessage> messages,
        ChatCompletionOptions options,
        CancellationToken ct = default)
    {
        try
        {
            return await chat.CompleteChatAsync(messages, options, ct);
        }
        catch (ClientResultException ex)
        {
            throw new LlmException(Describe(ex), ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller went away (browser closed, request aborted) — not a
            // provider failure, so let it propagate as a plain cancellation.
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Our own clock ran out, not the caller's: the SDK's NetworkTimeout
            // fired. Without this the user sees a bare "A task was canceled."
            throw new LlmException(
                "LLM request timed out waiting for the provider to respond. "
                + "Raise Llm:TimeoutSeconds, use a faster model, or analyze a smaller source bundle.",
                ex);
        }
        catch (Exception ex)
        {
            // Connection refused / DNS / TLS: the provider was never reached.
            throw new LlmException($"Could not reach the LLM provider: {ExceptionMessage.Describe(ex)}", ex);
        }
    }

    private static string Describe(ClientResultException ex)
    {
        var provider = ProviderMessage(ex);
        var prefix = ex.Status switch
        {
            400 => "LLM provider rejected the request (400)",
            401 => "LLM API key was rejected (401) — check Llm:ApiKey",
            402 => "LLM billing/budget exhausted (402)",
            403 => "LLM provider denied access (403) — the key may lack access to this model",
            404 => "LLM model or endpoint not found (404) — check Llm:BaseUrl and the configured model name",
            408 => "LLM request timed out (408)",
            413 => "LLM request was too large (413) — the prompt exceeds the model's limit",
            429 => "LLM rate limit or quota/budget exceeded (429)",
            >= 500 => $"LLM provider is unavailable ({ex.Status})",
            _ => $"LLM request failed ({ex.Status})"
        };

        return string.IsNullOrWhiteSpace(provider) ? prefix : $"{prefix}: {provider}";
    }

    /// <summary>
    /// Pulls the human-readable message out of the provider's error body.
    /// Covers OpenAI's <c>{"error":{"message":...}}</c> plus the shapes used by
    /// OpenRouter, Ollama, vLLM and friends.
    /// </summary>
    private static string ProviderMessage(ClientResultException ex)
    {
        var body = ex.GetRawResponse()?.Content?.ToString();
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return Trim(err.GetString());
                if (err.ValueKind == JsonValueKind.Object)
                {
                    if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                        return Trim(m.GetString());
                    if (err.TryGetProperty("metadata", out var meta)
                        && meta.TryGetProperty("raw", out var raw)
                        && raw.ValueKind == JsonValueKind.String)
                        return Trim(raw.GetString());
                }
            }

            foreach (var key in new[] { "message", "detail", "description" })
                if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    return Trim(v.GetString());
        }
        catch (JsonException)
        {
            // Non-JSON body (an HTML error page from a proxy, say) — show a snippet.
        }

        return Trim(body);
    }

    private static string Trim(string? s)
    {
        s = s?.Trim() ?? string.Empty;
        return s.Length > 500 ? s[..500] + "…" : s;
    }
}
