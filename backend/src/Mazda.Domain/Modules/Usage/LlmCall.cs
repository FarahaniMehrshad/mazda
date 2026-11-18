using Mazda.Domain.Common;

namespace Mazda.Domain.Modules.Usage;

/// <summary>Where a call's cost comes from.</summary>
public static class LlmCostSources
{
    /// <summary>The provider said what the call cost (Claude Code, OpenRouter).</summary>
    public const string Reported = "reported";

    /// <summary>Tokens × the price list in Settings at the time of the call.</summary>
    public const string Estimated = "estimated";

    /// <summary>No reported cost and no price for the model.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// One LLM call, exactly as the provider reported it. Written for every call,
/// failed ones included, and never pruned: this is the usage ledger.
///
/// Token kinds follow Anthropic's split, which every provider maps onto:
/// <see cref="InputTokens"/> is uncached input only; cache reads and cache
/// writes are separate because they are priced differently.
/// <see cref="ReasoningTokens"/> is part of <see cref="OutputTokens"/>, not
/// added to it.
/// </summary>
public class LlmCall : Entity
{
    /// <summary>The user whose action made the call; null when nothing attributed it.</summary>
    public Guid? OwnerId { get; private set; }

    /// <summary>What kind of work: run, roundtrip, catalogue, function-fix, recording-analysis, …</summary>
    public string Kind { get; private set; } = string.Empty;

    /// <summary>The run, round trip, signature, function, … the call was for.</summary>
    public Guid? SubjectId { get; private set; }
    public string SubjectLabel { get; private set; } = string.Empty;

    /// <summary>What this one call did: plan, judge picture, generate catalogue, …</summary>
    public string Purpose { get; private set; } = string.Empty;

    /// <summary>The trace the call appears in, if the work was traced.</summary>
    public Guid? TraceId { get; private set; }

    public string Provider { get; private set; } = string.Empty;
    /// <summary>The model as configured (e.g. "opus", "gpt-4o").</summary>
    public string Model { get; private set; } = string.Empty;
    /// <summary>The model the provider says answered (e.g. "claude-opus-5-5", "gpt-4o-2024-08-06").</summary>
    public string ResolvedModel { get; private set; } = string.Empty;

    public long InputTokens { get; private set; }
    public long CacheReadTokens { get; private set; }
    public long CacheWriteTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long ReasoningTokens { get; private set; }

    /// <summary>US dollars; null when unknown.</summary>
    public decimal? CostUsd { get; private set; }
    /// <summary><see cref="LlmCostSources"/>.</summary>
    public string CostSource { get; private set; } = LlmCostSources.Unknown;
    /// <summary>The prices an estimate used (JSON), so editing prices later never rewrites history.</summary>
    public string PricesJson { get; private set; } = string.Empty;

    public long DurationMs { get; private set; }
    public bool Ok { get; private set; }
    public string Error { get; private set; } = string.Empty;

    /// <summary>Every token the call used: input, cache read, cache write and output.</summary>
    public long TotalTokens => InputTokens + CacheReadTokens + CacheWriteTokens + OutputTokens;

    private LlmCall() { } // EF

    public LlmCall(
        Guid? ownerId, string kind, Guid? subjectId, string subjectLabel, string purpose, Guid? traceId,
        string provider, string model, string resolvedModel,
        long inputTokens, long cacheReadTokens, long cacheWriteTokens, long outputTokens, long reasoningTokens,
        decimal? costUsd, string costSource, string pricesJson,
        long durationMs, bool ok, string? error)
    {
        OwnerId = ownerId;
        Kind = Clip(kind, 40);
        SubjectId = subjectId;
        SubjectLabel = Clip(subjectLabel, 300);
        Purpose = Clip(purpose, 100);
        TraceId = traceId;
        Provider = Clip(provider, 30);
        Model = Clip(model, 200);
        ResolvedModel = Clip(resolvedModel, 200);
        InputTokens = Math.Max(0, inputTokens);
        CacheReadTokens = Math.Max(0, cacheReadTokens);
        CacheWriteTokens = Math.Max(0, cacheWriteTokens);
        OutputTokens = Math.Max(0, outputTokens);
        ReasoningTokens = Math.Max(0, reasoningTokens);
        CostUsd = costUsd;
        CostSource = costSource;
        PricesJson = pricesJson ?? string.Empty;
        DurationMs = Math.Max(0, durationMs);
        Ok = ok;
        Error = Clip(error, 2000);
    }

    private static string Clip(string? s, int max)
    {
        var t = s?.Trim() ?? string.Empty;
        return t.Length > max ? t[..max] : t;
    }
}
