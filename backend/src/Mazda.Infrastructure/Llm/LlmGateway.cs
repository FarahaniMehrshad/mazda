using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mazda.Application.Modules.Settings;
using Mazda.Domain.Common;
using Mazda.Domain.Modules.Settings;
using Mazda.Infrastructure.Persistence;
using OpenAI.Chat;

namespace Mazda.Infrastructure.Llm;

/// <summary>Which model a call wants: the cheaper analyzer or the stronger generator.</summary>
public enum LlmTier { Analyzer, Generator }

/// <summary>An LLM answer: its text and the tokens it cost.</summary>
public sealed record LlmCompletion(string Text, int Tokens)
{
    /// <summary>The text, or <paramref name="fallback"/> when the model returned nothing.</summary>
    public string TextOr(string fallback) => string.IsNullOrWhiteSpace(Text) ? fallback : Text;
}

/// <summary>One model of the provider in effect.</summary>
public interface ILlmChat
{
    string Provider { get; }
    string Model { get; }

    /// <summary>
    /// Sends the conversation and returns the answer. Provider failures come
    /// back as <see cref="LlmException"/> with a message fit for the UI.
    /// </summary>
    Task<LlmCompletion> CompleteChatSafeAsync(IEnumerable<ChatMessage> messages, ChatCompletionOptions options, CancellationToken ct = default);
}

/// <summary>
/// Where every LLM call goes: the provider chosen in Settings (or .env).
/// Messages use the OpenAI SDK's message types as the common format; the
/// Claude Code provider translates them.
/// </summary>
public interface ILlmGateway
{
    Task<ILlmChat> ChatAsync(LlmTier tier, CancellationToken ct = default);
}

public sealed class LlmGateway(ILlmSettingsStore store, IHttpClientFactory http) : ILlmGateway
{
    public async Task<ILlmChat> ChatAsync(LlmTier tier, CancellationToken ct = default)
    {
        var s = await store.GetAsync(ct);
        var model = tier == LlmTier.Analyzer ? s.AnalyzerModel : s.GeneratorModel;
        return s.Provider == LlmProviders.ClaudeCode
            ? new ClaudeBridgeChat(http.CreateClient(ClaudeBridgeChat.HttpClientName), s.ClaudeBridgeUrl, s.ClaudeBridgeToken, model, s.TimeoutSeconds)
            : new OpenAiCompatibleChat(s.OpenAiBaseUrl, s.OpenAiApiKey, model, s.TimeoutSeconds);
    }
}

/// <summary>Any OpenAI-compatible Chat Completions API (OpenAI, OpenRouter, Ollama, vLLM, …).</summary>
internal sealed class OpenAiCompatibleChat(string baseUrl, string apiKey, string model, int timeoutSeconds) : ILlmChat
{
    public string Provider => LlmProviders.OpenAi;
    public string Model => model;

    public async Task<LlmCompletion> CompleteChatSafeAsync(IEnumerable<ChatMessage> messages, ChatCompletionOptions options, CancellationToken ct = default)
    {
        var chat = ChatClientFactory.Create(baseUrl, apiKey, model, timeoutSeconds);
        var completion = await chat.CompleteChatSafeAsync(messages, options, ct);
        var text = completion.Value.Content.Count > 0 ? completion.Value.Content[0].Text ?? "" : "";
        return new LlmCompletion(text, completion.Value.Usage?.TotalTokenCount ?? 0);
    }
}

/// <summary>
/// The Claude Code CLI on the user's computer, through the host bridge
/// (<c>tools/claude-bridge/bridge.mjs</c>). The bridge runs one isolated
/// <c>claude -p</c> per call with the user's own login.
/// </summary>
internal sealed class ClaudeBridgeChat(HttpClient client, string bridgeUrl, string token, string model, int timeoutSeconds) : ILlmChat
{
    public const string HttpClientName = "claude-bridge";
    public string Provider => LlmProviders.ClaudeCode;
    public string Model => model;

    public async Task<LlmCompletion> CompleteChatSafeAsync(IEnumerable<ChatMessage> messages, ChatCompletionOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new LlmException(
                "CLAUDE_BRIDGE_TOKEN is not set for the API. Add it to .env (the bridge reads the same file) and restart the API: docker compose up -d api.");

        var request = ClaudeBridgeRequest.Build(messages, model, timeoutSeconds);
        using var msg = new HttpRequestMessage(HttpMethod.Post, Combine(bridgeUrl, "/v1/complete"))
        {
            Content = JsonContent.Create(request, options: ClaudeBridgeRequest.Json),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // The bridge enforces the call's own timeout; give the HTTP call a little more.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds) + 30));

        HttpResponseMessage res;
        try
        {
            res = await client.SendAsync(msg, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new LlmException($"Claude Code did not answer within {timeoutSeconds} s.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException(
                $"The Claude Code bridge is not reachable at {bridgeUrl}. Start it on your computer: node tools/claude-bridge/bridge.mjs ({ExceptionMessage.Describe(ex)})", ex);
        }

        using (res)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
                throw new LlmException(
                    "The Claude Code bridge rejected Mazda's token. CLAUDE_BRIDGE_TOKEN in .env must be the same for the bridge and the API (restart both after changing it).");
            BridgeResponse? r = null;
            try { r = JsonSerializer.Deserialize<BridgeResponse>(body, ClaudeBridgeRequest.Json); } catch (JsonException) { }
            if (!res.IsSuccessStatusCode || r is null)
            {
                var why = r?.Error ?? (body.Length > 500 ? body[..500] + "…" : body);
                throw new LlmException(res.StatusCode == HttpStatusCode.GatewayTimeout
                    ? why
                    : $"Claude Code ({(int)res.StatusCode}): {why}");
            }
            return new LlmCompletion(r.Text ?? "", r.InputTokens + r.OutputTokens);
        }
    }

    private static string Combine(string baseUrl, string path) => baseUrl.TrimEnd('/') + path;

    private sealed record BridgeResponse(string? Text, int InputTokens, int OutputTokens, double CostUsd, long DurationMs, string? Error);
}

/// <summary>
/// The bridge's request: one system prompt and one user message made of text
/// and image parts. Claude Code's print mode takes a single user turn, so a
/// longer conversation (a repair round: request, answer, follow-up) is
/// flattened into one message that quotes the earlier answer.
/// </summary>
public static class ClaudeBridgeRequest
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record Part(string Type, string? Text = null, string? MediaType = null, string? Data = null);

    public sealed record Request(string Model, string System, IReadOnlyList<Part> Content, int TimeoutSec);

    public static Request Build(IEnumerable<ChatMessage> messages, string model, int timeoutSeconds)
    {
        var list = messages.ToList();
        var system = string.Join("\n\n", list.OfType<SystemChatMessage>().Select(TextOf).Where(t => t.Length > 0));
        var turns = list.Where(m => m is not SystemChatMessage).ToList();
        var multi = turns.Count > 1;

        var parts = new List<Part>();
        void AddText(string text)
        {
            if (text.Length == 0) return;
            if (parts.Count > 0 && parts[^1].Type == "text")
                parts[^1] = parts[^1] with { Text = parts[^1].Text + text };
            else parts.Add(new Part("text", text));
        }

        var userTurns = 0;
        foreach (var m in turns)
        {
            if (m is AssistantChatMessage a)
            {
                AddText("\n\n<your_previous_answer>\n" + TextOf(a) + "\n</your_previous_answer>\n\n");
                continue;
            }
            if (multi && userTurns++ > 0) AddText("\n\n<follow_up>\n");
            foreach (var p in m.Content)
            {
                if (p.Kind == ChatMessageContentPartKind.Image && p.ImageBytes is not null)
                    parts.Add(new Part("image", MediaType: p.ImageBytesMediaType ?? "image/jpeg", Data: Convert.ToBase64String(p.ImageBytes.ToArray())));
                else if (p.Kind == ChatMessageContentPartKind.Image && p.ImageUri is not null)
                    AddText($"(picture at {p.ImageUri})");
                else
                    AddText(p.Text ?? "");
            }
            if (multi && userTurns > 1) AddText("\n</follow_up>");
        }
        if (parts.Count == 0) parts.Add(new Part("text", "(empty request)"));
        return new Request(model, system, parts, timeoutSeconds);
    }

    private static string TextOf(ChatMessage m)
        => string.Concat(m.Content.Where(p => p.Kind == ChatMessageContentPartKind.Text).Select(p => p.Text));
}

/// <summary>
/// Saved settings (database) over .env. Cached for a short while and dropped
/// on save, so a change applies to the next LLM call.
/// </summary>
public sealed class LlmSettingsStore(
    IServiceScopeFactory scopes,
    IOptions<LlmOptions> llm,
    IOptions<ClaudeBridgeOptions> bridge) : ILlmSettingsStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private (ResolvedLlmSettings Value, DateTime At)? _cache;

    public LlmEnvironment Environment => new(
        Provider: llm.Value.Provider,
        OpenAiBaseUrl: llm.Value.BaseUrl,
        OpenAiApiKey: llm.Value.ApiKey,
        OpenAiAnalyzerModel: llm.Value.AnalyzerModel,
        OpenAiGeneratorModel: llm.Value.GeneratorModel,
        ClaudeBridgeUrl: bridge.Value.Url,
        ClaudeBridgeToken: bridge.Value.Token,
        ClaudeAnalyzerModel: bridge.Value.AnalyzerModel,
        ClaudeGeneratorModel: bridge.Value.GeneratorModel,
        TimeoutSeconds: llm.Value.TimeoutSeconds);

    public async Task<ResolvedLlmSettings> GetAsync(CancellationToken ct = default)
    {
        if (_cache is { } c && DateTime.UtcNow - c.At < Ttl) return c.Value;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MazdaDbContext>();
        var saved = await db.LlmSettings.AsNoTracking().OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        var resolved = LlmSettingsResolver.Resolve(saved, Environment);
        _cache = (resolved, DateTime.UtcNow);
        return resolved;
    }

    public void Invalidate() => _cache = null;
}

public sealed class LlmConnectionTester(ILlmGateway gateway) : ILlmConnectionTester
{
    public async Task<LlmTestResult> TestAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        ILlmChat? chat = null;
        try
        {
            chat = await gateway.ChatAsync(LlmTier.Analyzer, ct);
            var r = await chat.CompleteChatSafeAsync(
                new List<ChatMessage>
                {
                    new SystemChatMessage("You are a connection check. Answer with exactly: OK"),
                    new UserChatMessage("Reply with OK."),
                },
                new ChatCompletionOptions(), ct);
            return new LlmTestResult(true, chat.Provider, chat.Model, r.Text.Trim(), sw.ElapsedMilliseconds, r.Tokens, null);
        }
        catch (LlmException ex)
        {
            return new LlmTestResult(false, chat?.Provider ?? "", chat?.Model ?? "", null, sw.ElapsedMilliseconds, 0, ex.Message);
        }
    }
}
