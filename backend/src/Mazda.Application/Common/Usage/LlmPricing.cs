using System.Text.Json;

namespace Mazda.Application.Common.Usage;

/// <summary>
/// One model's price in US dollars per 1M tokens. Cached input and cache
/// writes fall back to the input price when not given.
/// </summary>
public sealed record LlmPrice(
    string Model,
    decimal InputPerM,
    decimal? CachedInputPerM,
    decimal? CacheWritePerM,
    decimal OutputPerM);

/// <summary>Exact token counts of one call (see <c>LlmCall</c> for what each kind means).</summary>
public sealed record LlmTokenCounts(long Input, long CacheRead, long CacheWrite, long Output, long Reasoning)
{
    public long Total => Input + CacheRead + CacheWrite + Output;
}

/// <summary>
/// Price list matching and cost estimates for providers that report tokens but
/// no cost (OpenAI-compatible APIs). Pure.
/// </summary>
public static class LlmPricing
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Standard-tier text prices from OpenAI's price page (checked 2026-11-18),
    /// short-context rates. Used until the price list is edited in Settings.
    /// </summary>
    public static readonly IReadOnlyList<LlmPrice> Defaults = new List<LlmPrice>
    {
        new("gpt-6.2-astra", 8.00m, 0.40m, null, 40.00m),
        new("gpt-6.2-sol", 2.00m, 0.10m, null, 10.00m),
        new("gpt-6.1-sol", 2.00m, 0.10m, null, 10.00m),
        new("gpt-6.1-terra", 1.00m, 0.05m, null, 5.00m),
        new("gpt-6-astra", 10.00m, 1.00m, null, 50.00m),
        new("gpt-6-sol", 2.00m, 0.20m, null, 10.00m),
        new("gpt-6-luna", 0.10m, 0.01m, null, 0.50m),
        new("gpt-5.6-sol", 4.00m, 0.40m, null, 20.00m),
        new("gpt-5.6-terra", 2.00m, 0.20m, null, 12.00m),
        new("gpt-5.6-luna", 0.20m, 0.02m, null, 1.20m),
        new("gpt-5.5", 5.00m, 0.50m, null, 30.00m),
        new("gpt-5.4", 2.50m, 0.25m, null, 15.00m),
        new("gpt-5.4-mini", 0.75m, 0.075m, null, 4.50m),
        new("gpt-5.4-nano", 0.20m, 0.02m, null, 1.25m),
        new("gpt-5.2", 1.75m, 0.175m, null, 14.00m),
        new("gpt-5.1", 1.25m, 0.125m, null, 10.00m),
        new("gpt-5", 1.25m, 0.125m, null, 10.00m),
        new("gpt-5-mini", 0.25m, 0.025m, null, 2.00m),
        new("gpt-5-nano", 0.05m, 0.005m, null, 0.40m),
        new("gpt-4.1", 2.00m, 0.50m, null, 8.00m),
        new("gpt-4.1-mini", 0.40m, 0.10m, null, 1.60m),
        new("gpt-4o", 2.50m, 1.25m, null, 10.00m),
        new("gpt-4o-mini", 0.15m, 0.075m, null, 0.60m),
        new("o3", 2.00m, 0.50m, null, 8.00m),
    };

    /// <summary>
    /// The price for a model. Tries each name in order (the model the provider
    /// says answered, then the configured one). A name matches a price exactly
    /// (case-insensitive), or as a dated snapshot of it ("gpt-4o-2024-08-06" →
    /// "gpt-4o"). A router prefix ("openai/gpt-4o") is ignored. No other prefix
    /// matching: an unlisted "gpt-5.3" must not be priced as "gpt-5".
    /// </summary>
    public static LlmPrice? Find(IReadOnlyList<LlmPrice> prices, params string?[] names)
    {
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var name = raw.Trim();
            var slash = name.LastIndexOf('/');
            if (slash >= 0) name = name[(slash + 1)..];

            var exact = prices.FirstOrDefault(p => string.Equals(p.Model.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            var snapshot = prices
                .Where(p => p.Model.Trim().Length > 0 && IsSnapshotOf(name, p.Model.Trim()))
                .OrderByDescending(p => p.Model.Trim().Length)
                .FirstOrDefault();
            if (snapshot is not null) return snapshot;
        }
        return null;
    }

    /// <summary>"gpt-4o-2024-08-06" or "gpt-4o-0613" is a snapshot of "gpt-4o"; "gpt-4o-mini" is not.</summary>
    private static bool IsSnapshotOf(string name, string model)
    {
        if (!name.StartsWith(model + "-", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = name[(model.Length + 1)..];
        return rest.Length >= 4 && rest.All(c => char.IsDigit(c) || c == '-');
    }

    /// <summary>Cost in US dollars of <paramref name="t"/> at <paramref name="p"/>.</summary>
    public static decimal Cost(LlmTokenCounts t, LlmPrice p)
    {
        var cached = p.CachedInputPerM ?? p.InputPerM;
        var write = p.CacheWritePerM ?? p.InputPerM;
        var micro = t.Input * p.InputPerM + t.CacheRead * cached + t.CacheWrite * write + t.Output * p.OutputPerM;
        return micro / 1_000_000m;
    }

    /// <summary>Reads a saved price list; empty or unreadable means "use the defaults" (null).</summary>
    public static IReadOnlyList<LlmPrice>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<LlmPrice>>(json, Json); }
        catch (JsonException) { return null; }
    }

    public static string Serialize(IReadOnlyList<LlmPrice> prices) => JsonSerializer.Serialize(prices, Json);
}
