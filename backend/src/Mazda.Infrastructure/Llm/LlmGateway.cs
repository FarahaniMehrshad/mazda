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
using Mazda.Application.Common.Tracing;
using Mazda.Application.Common.Usage;
using Mazda.Application.Modules.Settings;
using Mazda.Domain.Common;
using Mazda.Domain.Modules.Settings;
using Mazda.Domain.Modules.Usage;
using Mazda.Infrastructure.Persistence;
using OpenAI.Chat;

namespace Mazda.Infrastructure.Llm;

/// <summary>Which model a call wants: the cheaper analyzer or the stronger generator.</summary>
public enum LlmTier { Analyzer, Generator }

/// <summary>
/// Exactly what one call used, as the provider reported it. Anthropic's split:
/// <see cref="InputTokens"/> is uncached input only, cache reads and writes are
/// separate, and <see cref="ReasoningTokens"/> is part of <see cref="OutputTokens"/>.
/// </summary>
/// <param name="ResolvedModel">The model the provider says answered; empty when it did not say.</param>
/// <param name="ReportedCostUsd">What the provider says the call cost; null when it reports no cost.</param>
public sealed record LlmUsage(
    long InputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long OutputTokens,
    long ReasoningTokens,
    string ResolvedModel,
    decimal? ReportedCostUsd)
{
    public static readonly LlmUsage None = new(0, 0, 0, 0, 0, "", null);

    public long TotalTokens => InputTokens + CacheReadTokens + CacheWriteTokens + OutputTokens;

    public LlmTokenCounts Counts => new(InputTokens, CacheReadTokens, CacheWriteTokens, OutputTokens, ReasoningTokens);
}

/// <summary>An LLM answer: its text and exactly what it used.</summary>
public sealed record LlmCompletion(string Text, LlmUsage Usage)
{
    /// <summary>Every token the call used (input, cache read, cache write, output).</summary>
    public int Tokens => (int)Math.Min(int.MaxValue, Usage.TotalTokens);

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
    /// <param name="purpose">What the calls do ("plan", "judge picture", …), as the usage ledger shows it.</param>
    Task<ILlmChat> ChatAsync(LlmTier tier, string purpose, CancellationToken ct = default);
}

public sealed class LlmGateway(ILlmSettingsStore store, IHttpClientFactory http, ILlmUsageRecorder usage) : ILlmGateway
{
    public async Task<ILlmChat> ChatAsync(LlmTier tier, string purpose, CancellationToken ct = default)
    {
        var s = await store.GetAsync(ct);
        var model = tier == LlmTier.Analyzer ? s.AnalyzerModel : s.GeneratorModel;
        ILlmChat chat = s.Provider == LlmProviders.ClaudeCode
            ? new ClaudeBridgeChat(http.CreateClient(ClaudeBridgeChat.HttpClientName), s.ClaudeBridgeUrl, s.ClaudeBridgeToken, model, s.TimeoutSeconds)
            : new OpenAiCompatibleChat(s.OpenAiBaseUrl, s.OpenAiApiKey, model, s.TimeoutSeconds);
        return new MeteredChat(chat, purpose, s.Prices, usage);
    }
}

/// <summary>
/// Records every call: one ledger row (attributed through
/// <see cref="LlmUsageContext"/>) and one node on the current trace. Failed
/// and cancelled calls are recorded too, with whatever usage is known.
/// </summary>
internal sealed class MeteredChat(ILlmChat inner, string purpose, IReadOnlyList<LlmPrice> prices, ILlmUsageRecorder recorder) : ILlmChat
{
    public string Provider => inner.Provider;
    public string Model => inner.Model;

    public async Task<LlmCompletion> CompleteChatSafeAsync(IEnumerable<ChatMessage> messages, ChatCompletionOptions options, CancellationToken ct = default)
    {
        var start = TraceRecorder.Now();
        var sw = Stopwatch.StartNew();
        try
        {
            var completion = await inner.CompleteChatSafeAsync(messages, options, ct);
            await RecordAsync(completion.Usage, start, sw.ElapsedMilliseconds, null);
            return completion;
        }
        catch (LlmException ex)
        {
            await RecordAsync(ex.Usage ?? LlmUsage.None, start, sw.ElapsedMilliseconds, ex.Message);
            throw;
        }
        catch (OperationCanceledException)
        {
            await RecordAsync(LlmUsage.None, start, sw.ElapsedMilliseconds, "Cancelled before the answer arrived.");
            throw;
        }
    }

    private async Task RecordAsync(LlmUsage usage, long start, long ms, string? error)
    {
        var (cost, source, pricesJson) = LlmCosting.Cost(usage, Model, prices);
        var scope = LlmUsageContext.Current;
        var trace = TraceContext.Current;
        trace?.AddLlmCall($"LLM: {purpose} ({(usage.ResolvedModel.Length > 0 ? usage.ResolvedModel : Model)})", start, ms,
            new TraceLlmUsage(purpose, Provider, usage.ResolvedModel.Length > 0 ? usage.ResolvedModel : Model,
                usage.InputTokens, usage.CacheReadTokens, usage.CacheWriteTokens, usage.OutputTokens, usage.ReasoningTokens,
                cost, source, error is null),
            error);
        await recorder.RecordAsync(new LlmCall(
            scope?.OwnerId, string.IsNullOrEmpty(scope?.Kind) ? LlmUsageKinds.Unattributed : scope.Kind,
            scope?.SubjectId, scope?.SubjectLabel ?? "", purpose, trace?.Id,
            Provider, Model, usage.ResolvedModel,
            usage.InputTokens, usage.CacheReadTokens, usage.CacheWriteTokens, usage.OutputTokens, usage.ReasoningTokens,
            cost, source, pricesJson, ms, error is null, error), CancellationToken.None);
    }
}

/// <summary>What a call cost: what the provider reported, else an estimate from the price list.</summary>
public static class LlmCosting
{
    public static (decimal? Cost, string Source, string PricesJson) Cost(LlmUsage usage, string configuredModel, IReadOnlyList<LlmPrice> prices)
    {
        if (usage.ReportedCostUsd is { } reported) return (reported, LlmCostSources.Reported, "");
        if (usage.TotalTokens == 0) return (null, LlmCostSources.Unknown, "");
        var price = LlmPricing.Find(prices, usage.ResolvedModel, configuredModel);
        if (price is null) return (null, LlmCostSources.Unknown, "");
        return (Math.Round(LlmPricing.Cost(usage.Counts, price), 8), LlmCostSources.Estimated,
            JsonSerializer.Serialize(price, LlmPricing.Json));
    }
}

/// <summary>
/// Usage from an OpenAI-compatible Chat Completions response body. Read from the
/// raw JSON because the SDK (2.0.0) drops cached-token details and providers'
/// extras: OpenRouter's <c>usage.cost</c>, proxies' cache-write counts.
/// OpenAI's <c>prompt_tokens</c> includes cached tokens; they are split out so
/// every provider means the same by "input".
/// </summary>
public static class OpenAiUsage
{
    public static LlmUsage? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return null;

            var prompt = Num(u, "prompt_tokens") ?? Num(u, "input_tokens") ?? 0;
            var output = Num(u, "completion_tokens") ?? Num(u, "output_tokens") ?? 0;
            var pd = Obj(u, "prompt_tokens_details") ?? Obj(u, "input_tokens_details");
            var cd = Obj(u, "completion_tokens_details") ?? Obj(u, "output_tokens_details");
            var cacheRead = (pd is { } p1 ? Num(p1, "cached_tokens") : null) ?? Num(u, "cache_read_input_tokens") ?? 0;
            var cacheWrite = (pd is { } p2 ? Num(p2, "cache_write_tokens") : null) ?? Num(u, "cache_creation_input_tokens") ?? 0;
            var reasoning = cd is { } c ? Num(c, "reasoning_tokens") ?? 0 : 0;
            decimal? cost = u.TryGetProperty("cost", out var k) && k.ValueKind == JsonValueKind.Number ? k.GetDecimal() : null;

            return new LlmUsage(
                InputTokens: Math.Max(0, prompt - cacheRead - cacheWrite),
                CacheReadTokens: cacheRead,
                CacheWriteTokens: cacheWrite,
                OutputTokens: output,
                ReasoningTokens: reasoning,
                ResolvedModel: model,
                ReportedCostUsd: cost);
        }
        catch (JsonException) { return null; }
        catch (FormatException) { return null; }
    }

    private static JsonElement? Obj(JsonElement o, string p)
        => o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static long? Num(JsonElement o, string p)
        => o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number
            ? (v.TryGetInt64(out var x) ? x : (long)v.GetDouble())
            : null;
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
        LlmUsage? usage = null;
        try { usage = OpenAiUsage.Parse(completion.GetRawResponse().Content.ToString()); } catch (Exception) { }
        usage ??= completion.Value.Usage is { } u
            ? new LlmUsage(u.InputTokenCount, 0, 0, u.OutputTokenCount, 0, completion.Value.Model ?? "", null)
            : LlmUsage.None;
        return new LlmCompletion(text, usage);
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
                    : $"Claude Code ({(int)res.StatusCode}): {why}")
                {
                    Usage = r?.Usage is null ? null : UsageOf(r, model),
                };
            }
            return new LlmCompletion(r.Text ?? "", UsageOf(r, model));
        }
    }

    private static string Combine(string baseUrl, string path) => baseUrl.TrimEnd('/') + path;

    /// <summary>
    /// The bridge's exact usage. An older bridge sends only flat totals (input
    /// with cache reads and writes folded in): those are kept as input, since
    /// the split is unknown.
    /// </summary>
    internal static LlmUsage UsageOf(BridgeResponse r, string configuredModel)
    {
        if (r.Usage is { } u)
        {
            var models = u.Models ?? new List<BridgeModelUsage>();
            var resolved = models.Count switch
            {
                0 => configuredModel,
                1 => models[0].Id ?? configuredModel,
                // Several models in one call: the main one (most output) first.
                _ => string.Join(" + ", models.OrderByDescending(m => m.OutputTokens).Select(m => m.Id)),
            };
            return new LlmUsage(u.InputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.OutputTokens, u.ThinkingTokens,
                resolved, u.CostUsd);
        }
        return new LlmUsage(r.InputTokens, 0, 0, r.OutputTokens, 0, configuredModel, r.CostUsd);
    }

    internal sealed record BridgeResponse(
        string? Text, long InputTokens, long OutputTokens, decimal? CostUsd, long DurationMs, string? Error, BridgeUsage? Usage);

    internal sealed record BridgeUsage(
        long InputTokens, long CacheReadTokens, long CacheWriteTokens, long OutputTokens, long ThinkingTokens,
        decimal? CostUsd, List<BridgeModelUsage>? Models);

    internal sealed record BridgeModelUsage(
        string? Id, long InputTokens, long CacheReadTokens, long CacheWriteTokens, long OutputTokens, long ThinkingTokens, decimal CostUsd);
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
            LlmUsageContext.SetSubject(LlmUsageKinds.ConnectionTest, null, "Settings: test connection");
            chat = await gateway.ChatAsync(LlmTier.Analyzer, "connection test", ct);
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
