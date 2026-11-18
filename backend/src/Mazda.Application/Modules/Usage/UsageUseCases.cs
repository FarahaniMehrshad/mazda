using MediatR;
using Mazda.Application.Common.Abstractions;
using Mazda.Application.Common.Usage;
using Mazda.Domain.Common;
using Mazda.Domain.Modules.Usage;

namespace Mazda.Application.Modules.Usage;

// ── DTOs ─────────────────────────────────────────────────────────────

/// <summary>
/// Sums over calls. <c>Input</c> is uncached input; <c>Reasoning</c> is part of
/// <c>Output</c>. <c>CostUsd</c> adds up the calls whose cost is known;
/// <c>UnknownCostCalls</c> says how many are missing from it.
/// </summary>
public sealed record LlmUsageTotals(
    int Calls,
    int FailedCalls,
    long Input,
    long CacheRead,
    long CacheWrite,
    long Output,
    long Reasoning,
    long Total,
    decimal CostUsd,
    int ReportedCostCalls,
    int EstimatedCostCalls,
    int UnknownCostCalls,
    long DurationMs)
{
    public static readonly LlmUsageTotals Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0m, 0, 0, 0, 0);
}

public sealed record LlmCallDto(
    Guid Id,
    DateTime CreatedAt,
    string Kind,
    Guid? SubjectId,
    string SubjectLabel,
    string Purpose,
    Guid? TraceId,
    string Provider,
    string Model,
    string ResolvedModel,
    long Input,
    long CacheRead,
    long CacheWrite,
    long Output,
    long Reasoning,
    long Total,
    decimal? CostUsd,
    string CostSource,
    long DurationMs,
    bool Ok,
    string Error);

public sealed record SubjectUsageDto(LlmUsageTotals Totals, IReadOnlyList<LlmCallDto> Calls);

public sealed record UsageGroupDto(string Key, LlmUsageTotals Totals);

public sealed record UsageSummaryDto(
    LlmUsageTotals Totals,
    IReadOnlyList<UsageGroupDto> ByDay,
    IReadOnlyList<UsageGroupDto> ByModel,
    IReadOnlyList<UsageGroupDto> ByKind,
    IReadOnlyList<LlmCallDto> Recent);

// ── Aggregation (pure) ───────────────────────────────────────────────

public static class LlmUsageMath
{
    public static LlmUsageTotals Sum(IEnumerable<LlmCall> calls)
    {
        var list = calls as IReadOnlyCollection<LlmCall> ?? calls.ToList();
        if (list.Count == 0) return LlmUsageTotals.Empty;
        return new LlmUsageTotals(
            Calls: list.Count,
            FailedCalls: list.Count(c => !c.Ok),
            Input: list.Sum(c => c.InputTokens),
            CacheRead: list.Sum(c => c.CacheReadTokens),
            CacheWrite: list.Sum(c => c.CacheWriteTokens),
            Output: list.Sum(c => c.OutputTokens),
            Reasoning: list.Sum(c => c.ReasoningTokens),
            Total: list.Sum(c => c.TotalTokens),
            CostUsd: list.Sum(c => c.CostUsd ?? 0m),
            ReportedCostCalls: list.Count(c => c.CostSource == LlmCostSources.Reported),
            EstimatedCostCalls: list.Count(c => c.CostSource == LlmCostSources.Estimated),
            // A failed call that used no tokens costs nothing: not "unknown".
            UnknownCostCalls: list.Count(c => c.CostSource == LlmCostSources.Unknown && c.TotalTokens > 0),
            DurationMs: list.Sum(c => c.DurationMs));
    }

    public static LlmCallDto ToDto(LlmCall c) => new(
        c.Id, c.CreatedAt, c.Kind, c.SubjectId, c.SubjectLabel, c.Purpose, c.TraceId,
        c.Provider, c.Model, c.ResolvedModel,
        c.InputTokens, c.CacheReadTokens, c.CacheWriteTokens, c.OutputTokens, c.ReasoningTokens, c.TotalTokens,
        c.CostUsd, c.CostSource, c.DurationMs, c.Ok, c.Error);

    /// <param name="utcOffsetMinutes">The viewer's offset from UTC, so days start at their midnight.</param>
    public static UsageSummaryDto Summarize(IReadOnlyList<LlmCall> calls, int utcOffsetMinutes, int recent = 100)
    {
        var offset = TimeSpan.FromMinutes(Math.Clamp(utcOffsetMinutes, -14 * 60, 14 * 60));
        static List<UsageGroupDto> Group(IEnumerable<IGrouping<string, LlmCall>> g)
            => g.Select(x => new UsageGroupDto(x.Key, Sum(x))).ToList();

        return new UsageSummaryDto(
            Sum(calls),
            Group(calls.GroupBy(c => (c.CreatedAt + offset).ToString("yyyy-MM-dd"))).OrderBy(x => x.Key).ToList(),
            Group(calls.GroupBy(c => string.IsNullOrEmpty(c.ResolvedModel) ? c.Model : c.ResolvedModel))
                .OrderByDescending(x => x.Totals.CostUsd).ThenByDescending(x => x.Totals.Total).ToList(),
            Group(calls.GroupBy(c => c.Kind)).OrderByDescending(x => x.Totals.CostUsd).ThenByDescending(x => x.Totals.Total).ToList(),
            calls.OrderByDescending(c => c.CreatedAt).Take(recent).Select(ToDto).ToList());
    }
}

// ── Queries ──────────────────────────────────────────────────────────

/// <summary>Every LLM call for a run, a round trip, a signature, … oldest first.</summary>
public record GetSubjectUsageQuery(Guid SubjectId) : IRequest<Result<SubjectUsageDto>>;

public class GetSubjectUsageHandler(ILlmUsageQueries usage, ICurrentUser me)
    : IRequestHandler<GetSubjectUsageQuery, Result<SubjectUsageDto>>
{
    public async Task<Result<SubjectUsageDto>> Handle(GetSubjectUsageQuery q, CancellationToken ct)
    {
        if (me.UserId is not { } owner) return Result.Failure<SubjectUsageDto>(Error.Validation("Not authenticated."));
        var calls = (await usage.ForSubjectsAsync(owner, new[] { q.SubjectId }, ct)).OrderBy(c => c.CreatedAt).ToList();
        return Result.Success(new SubjectUsageDto(LlmUsageMath.Sum(calls), calls.Select(LlmUsageMath.ToDto).ToList()));
    }
}

/// <summary>Totals per subject, for lists (the runs page).</summary>
public record GetSubjectsUsageQuery(IReadOnlyList<Guid> SubjectIds) : IRequest<Result<Dictionary<Guid, LlmUsageTotals>>>;

public class GetSubjectsUsageHandler(ILlmUsageQueries usage, ICurrentUser me)
    : IRequestHandler<GetSubjectsUsageQuery, Result<Dictionary<Guid, LlmUsageTotals>>>
{
    public async Task<Result<Dictionary<Guid, LlmUsageTotals>>> Handle(GetSubjectsUsageQuery q, CancellationToken ct)
    {
        if (me.UserId is not { } owner) return Result.Failure<Dictionary<Guid, LlmUsageTotals>>(Error.Validation("Not authenticated."));
        var ids = q.SubjectIds.Distinct().Take(500).ToList();
        var calls = await usage.ForSubjectsAsync(owner, ids, ct);
        var bySubject = calls.Where(c => c.SubjectId is not null).GroupBy(c => c.SubjectId!.Value).ToDictionary(g => g.Key, g => LlmUsageMath.Sum(g));
        return Result.Success(ids.ToDictionary(id => id, id => bySubject.GetValueOrDefault(id) ?? LlmUsageTotals.Empty));
    }
}

/// <summary>Usage over a period: totals, per day, per model, per kind of work, and the latest calls.</summary>
public record GetUsageSummaryQuery(DateTime? FromUtc, DateTime? ToUtc, int UtcOffsetMinutes) : IRequest<Result<UsageSummaryDto>>;

public class GetUsageSummaryHandler(ILlmUsageQueries usage, ICurrentUser me)
    : IRequestHandler<GetUsageSummaryQuery, Result<UsageSummaryDto>>
{
    public async Task<Result<UsageSummaryDto>> Handle(GetUsageSummaryQuery q, CancellationToken ct)
    {
        if (me.UserId is not { } owner) return Result.Failure<UsageSummaryDto>(Error.Validation("Not authenticated."));
        var calls = await usage.BetweenAsync(owner, q.FromUtc, q.ToUtc, ct);
        return Result.Success(LlmUsageMath.Summarize(calls, q.UtcOffsetMinutes));
    }
}
