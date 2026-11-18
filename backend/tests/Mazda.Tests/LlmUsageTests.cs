using System.Text.Json;
using Mazda.Application.Common.Tracing;
using Mazda.Application.Common.Usage;
using Mazda.Application.Modules.Usage;
using Mazda.Domain.Modules.Usage;
using Mazda.Infrastructure.Llm;
using OpenAI.Chat;
using Xunit;

namespace Mazda.Tests;

/// <summary>Exact LLM usage: what providers report, what it costs, who it is for.</summary>
public class LlmUsageTests
{
    // ── OpenAI-compatible responses ──

    [Fact]
    public void OpenAi_cached_tokens_are_split_out_of_prompt_tokens()
    {
        var u = OpenAiUsage.Parse("""
            { "model": "gpt-4o-2024-08-06",
              "usage": { "prompt_tokens": 5000, "completion_tokens": 700, "total_tokens": 5700,
                         "prompt_tokens_details": { "cached_tokens": 3072 },
                         "completion_tokens_details": { "reasoning_tokens": 200 } } }
            """)!;
        Assert.Equal(1928, u.InputTokens);
        Assert.Equal(3072, u.CacheReadTokens);
        Assert.Equal(0, u.CacheWriteTokens);
        Assert.Equal(700, u.OutputTokens);
        Assert.Equal(200, u.ReasoningTokens);
        Assert.Equal(5700, u.TotalTokens);
        Assert.Equal("gpt-4o-2024-08-06", u.ResolvedModel);
        Assert.Null(u.ReportedCostUsd);
    }

    [Fact]
    public void OpenRouter_reported_cost_and_cache_writes_are_kept()
    {
        var u = OpenAiUsage.Parse("""
            { "model": "anthropic/claude-sonnet-5",
              "usage": { "prompt_tokens": 1200, "completion_tokens": 50, "cost": 0.0123,
                         "prompt_tokens_details": { "cached_tokens": 200, "cache_write_tokens": 900 } } }
            """)!;
        Assert.Equal(100, u.InputTokens);
        Assert.Equal(200, u.CacheReadTokens);
        Assert.Equal(900, u.CacheWriteTokens);
        Assert.Equal(0.0123m, u.ReportedCostUsd);
    }

    [Fact]
    public void A_response_without_usage_gives_nothing()
        => Assert.Null(OpenAiUsage.Parse("""{ "model": "x", "choices": [] }"""));

    // ── Claude Code bridge ──

    [Fact]
    public void Bridge_usage_is_taken_as_reported()
    {
        var r = JsonSerializer.Deserialize<ClaudeBridgeChat.BridgeResponse>("""
            { "text": "ok", "inputTokens": 3694, "outputTokens": 4, "costUsd": 0.00061729,
              "usage": { "inputTokens": 2, "cacheReadTokens": 649, "cacheWriteTokens": 3043, "outputTokens": 4,
                         "thinkingTokens": 0, "costUsd": 0.00061729,
                         "models": [ { "id": "claude-haiku-5-5", "inputTokens": 2, "cacheReadTokens": 649,
                                       "cacheWriteTokens": 3043, "outputTokens": 4, "thinkingTokens": 0, "costUsd": 0.00061729 } ] } }
            """, ClaudeBridgeRequest.Json)!;
        var u = ClaudeBridgeChat.UsageOf(r, "haiku");
        Assert.Equal(new LlmUsage(2, 649, 3043, 4, 0, "claude-haiku-5-5", 0.00061729m), u);
    }

    [Fact]
    public void An_older_bridge_still_counts_every_token()
    {
        var r = JsonSerializer.Deserialize<ClaudeBridgeChat.BridgeResponse>(
            """{ "text": "ok", "inputTokens": 3694, "outputTokens": 4, "costUsd": 0.0006 }""", ClaudeBridgeRequest.Json)!;
        var u = ClaudeBridgeChat.UsageOf(r, "opus");
        Assert.Equal(3698, u.TotalTokens);
        Assert.Equal("opus", u.ResolvedModel);
        Assert.Equal(0.0006m, u.ReportedCostUsd);
    }

    // ── Prices ──

    [Theory]
    [InlineData("gpt-4o", "gpt-4o")]
    [InlineData("GPT-4o", "gpt-4o")]
    [InlineData("gpt-4o-2024-08-06", "gpt-4o")]
    [InlineData("gpt-4o-mini-2024-07-18", "gpt-4o-mini")]
    [InlineData("openai/gpt-4.1-mini", "gpt-4.1-mini")]
    public void Prices_match_the_model_or_a_dated_snapshot(string name, string expected)
        => Assert.Equal(expected, LlmPricing.Find(LlmPricing.Defaults, name)?.Model);

    [Theory]
    [InlineData("gpt-5.3")]          // unlisted: must not be priced as gpt-5
    [InlineData("gpt-4o-audio")]     // a different model, not a snapshot
    [InlineData("llama3")]
    public void Unlisted_models_have_no_price(string name)
        => Assert.Null(LlmPricing.Find(LlmPricing.Defaults, name));

    [Fact]
    public void The_resolved_model_is_tried_before_the_configured_one()
    {
        var prices = new List<LlmPrice> { new("a", 1, null, null, 1), new("b", 2, null, null, 2) };
        Assert.Equal("b", LlmPricing.Find(prices, "b", "a")!.Model);
        Assert.Equal("a", LlmPricing.Find(prices, "unknown", "a")!.Model);
    }

    [Fact]
    public void Estimate_prices_each_token_kind()
    {
        var gpt4o = LlmPricing.Find(LlmPricing.Defaults, "gpt-4o")!; // 2.50 in, 1.25 cached, 10 out
        var usage = new LlmUsage(1000, 2000, 0, 500, 0, "gpt-4o-2024-08-06", null);
        var (cost, source, prices) = LlmCosting.Cost(usage, "gpt-4o", LlmPricing.Defaults);
        Assert.Equal(0.01m, cost);  // (1000×2.5 + 2000×1.25 + 500×10) / 1M
        Assert.Equal(LlmCostSources.Estimated, source);
        Assert.Contains("\"model\":\"gpt-4o\"", prices);
        Assert.Equal(gpt4o, JsonSerializer.Deserialize<LlmPrice>(prices, LlmPricing.Json));
    }

    [Fact]
    public void Missing_cache_prices_fall_back_to_the_input_price()
        => Assert.Equal(0.006m, LlmPricing.Cost(new LlmTokenCounts(1000, 1000, 1000, 0, 0), new LlmPrice("m", 2, null, null, 9)));

    [Fact]
    public void A_reported_cost_wins_and_an_unpriced_model_is_unknown()
    {
        Assert.Equal((0.5m, LlmCostSources.Reported), Pick(LlmCosting.Cost(new LlmUsage(10, 0, 0, 10, 0, "gpt-4o", 0.5m), "gpt-4o", LlmPricing.Defaults)));
        Assert.Equal(((decimal?)null, LlmCostSources.Unknown), Pick(LlmCosting.Cost(new LlmUsage(10, 0, 0, 10, 0, "", null), "my-local-model", LlmPricing.Defaults)));
        static (decimal?, string) Pick((decimal? Cost, string Source, string _) x) => (x.Cost, x.Source);
    }

    [Fact]
    public void Saved_price_lists_round_trip_and_empty_means_defaults()
    {
        var list = new List<LlmPrice> { new("m", 1.5m, 0.15m, null, 6m) };
        Assert.Equal(list, LlmPricing.Parse(LlmPricing.Serialize(list)));
        Assert.Null(LlmPricing.Parse(""));
    }

    // ── Attribution ──

    [Fact]
    public async Task A_subject_set_in_a_handler_reaches_background_work()
    {
        var outer = Task.Run(async () =>
        {
            LlmUsageContext.Begin(Guid.Empty, "plan-run");
            var subject = Guid.NewGuid();
            LlmUsageContext.SetSubject(LlmUsageKinds.Run, subject, "a run");
            var seen = await Task.Run(() => LlmUsageContext.Current);
            Assert.Equal(LlmUsageKinds.Run, seen!.Kind);
            Assert.Equal(subject, seen.SubjectId);
            Assert.Equal(Guid.Empty, seen.OwnerId);
        });
        await outer;
    }

    [Theory]
    [InlineData(typeof(Mazda.Application.Modules.Settings.TestLlmConnectionCommand), "test-llm-connection")]
    [InlineData(typeof(GetUsageSummaryQuery), "get-usage-summary")]
    public void Request_types_name_the_default_kind(Type t, string kind)
        => Assert.Equal(kind, LlmUsageScopeBehavior<object, object>.KindOf(t));

    // ── Metering ──

    [Fact]
    public async Task Every_call_is_recorded_with_its_scope_and_trace()
    {
        await Task.Run(async () =>
        {
            var owner = Guid.NewGuid();
            var subject = Guid.NewGuid();
            LlmUsageContext.Begin(owner, LlmUsageKinds.RoundTrip, subject, "round trip: demo");
            var trace = TraceContext.Begin("roundtrip", "demo");
            var recorder = new ListRecorder();
            var usage = new LlmUsage(100, 2000, 300, 40, 10, "claude-opus-5-5", 0.0421m);
            var chat = new MeteredChat(new FakeChat(usage), "judge picture", LlmPricing.Defaults, recorder);

            var answer = await chat.CompleteChatSafeAsync(new List<ChatMessage> { new UserChatMessage("hi") }, new ChatCompletionOptions());

            Assert.Equal(2440, answer.Tokens);
            var row = Assert.Single(recorder.Rows);
            Assert.Equal(owner, row.OwnerId);
            Assert.Equal(LlmUsageKinds.RoundTrip, row.Kind);
            Assert.Equal(subject, row.SubjectId);
            Assert.Equal("judge picture", row.Purpose);
            Assert.Equal(trace.Id, row.TraceId);
            Assert.Equal((100L, 2000L, 300L, 40L, 10L), (row.InputTokens, row.CacheReadTokens, row.CacheWriteTokens, row.OutputTokens, row.ReasoningTokens));
            Assert.Equal(0.0421m, row.CostUsd);
            Assert.Equal(LlmCostSources.Reported, row.CostSource);
            Assert.Equal("claude-opus-5-5", row.ResolvedModel);
            Assert.True(row.Ok);

            var node = Assert.Single(trace.Snapshot().Nodes, n => n.Llm is not null);
            Assert.Equal(TraceCategory.Llm, node.Category);
            Assert.Equal(2000, node.Llm!.CacheRead);
            Assert.Equal(0.0421m, TraceAnalyzer.Summarize(trace.Snapshot()).Llm!.CostUsd);
        });
    }

    [Fact]
    public async Task A_failed_call_is_recorded_with_what_it_used()
    {
        await Task.Run(async () =>
        {
            LlmUsageContext.Begin(null, "x");
            var recorder = new ListRecorder();
            var spent = new LlmUsage(500, 0, 0, 20, 0, "claude-opus-5-5", 0.01m);
            var chat = new MeteredChat(new FakeChat(null, new LlmException("Claude Code failed: limit") { Usage = spent }),
                "plan", LlmPricing.Defaults, recorder);

            await Assert.ThrowsAsync<LlmException>(() =>
                chat.CompleteChatSafeAsync(new List<ChatMessage> { new UserChatMessage("hi") }, new ChatCompletionOptions()));

            var row = Assert.Single(recorder.Rows);
            Assert.False(row.Ok);
            Assert.Equal(520, row.TotalTokens);
            Assert.Equal(0.01m, row.CostUsd);
            Assert.Contains("limit", row.Error);
        });
    }

    [Fact]
    public void Totals_do_not_call_free_failures_unknown()
    {
        var calls = new[]
        {
            Call(100, 10, 0.002m, LlmCostSources.Estimated, ok: true),
            Call(0, 0, null, LlmCostSources.Unknown, ok: false),       // refused before any token: costs nothing
            Call(50, 5, null, LlmCostSources.Unknown, ok: true),       // a model with no price
        };
        var t = LlmUsageMath.Sum(calls);
        Assert.Equal((3, 1, 165L, 0.002m, 1, 1), (t.Calls, t.FailedCalls, t.Total, t.CostUsd, t.EstimatedCostCalls, t.UnknownCostCalls));
    }

    private static LlmCall Call(long input, long output, decimal? cost, string source, bool ok)
        => new(null, "run", null, "", "plan", null, "openai", "m", "m", input, 0, 0, output, 0, cost, source, "", 10, ok, ok ? null : "failed");

    private sealed class ListRecorder : ILlmUsageRecorder
    {
        public List<LlmCall> Rows { get; } = new();
        public Task RecordAsync(LlmCall call, CancellationToken ct = default) { Rows.Add(call); return Task.CompletedTask; }
    }

    private sealed class FakeChat(LlmUsage? usage, Exception? fail = null) : ILlmChat
    {
        public string Provider => "claude-code";
        public string Model => "opus";
        public Task<LlmCompletion> CompleteChatSafeAsync(IEnumerable<ChatMessage> messages, ChatCompletionOptions options, CancellationToken ct = default)
            => fail is not null ? Task.FromException<LlmCompletion>(fail) : Task.FromResult(new LlmCompletion("{}", usage!));
    }
}
