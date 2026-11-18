using Mazda.Application.Common.Usage;
using Mazda.Domain.Modules.Usage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mazda.Infrastructure.Persistence;

/// <summary>
/// The LLM usage ledger. Writes in its own scope (never inside a use case's
/// unit of work) and never throws.
/// </summary>
public sealed class LlmUsageStore(IServiceScopeFactory scopes, ILogger<LlmUsageStore> log) : ILlmUsageRecorder, ILlmUsageQueries
{
    public async Task RecordAsync(LlmCall call, CancellationToken ct = default)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MazdaDbContext>();
            await db.LlmCalls.AddAsync(call, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not record LLM call ({Purpose}, {Model}, {Tokens} tokens)", call.Purpose, call.Model, call.TotalTokens);
        }
    }

    public async Task<IReadOnlyList<LlmCall>> ForSubjectsAsync(Guid ownerId, IReadOnlyCollection<Guid> subjectIds, CancellationToken ct = default)
    {
        if (subjectIds.Count == 0) return Array.Empty<LlmCall>();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MazdaDbContext>();
        var ids = subjectIds.ToList();
        return await db.LlmCalls.AsNoTracking()
            .Where(c => c.SubjectId != null && ids.Contains(c.SubjectId.Value) && (c.OwnerId == ownerId || c.OwnerId == null))
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LlmCall>> BetweenAsync(Guid ownerId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MazdaDbContext>();
        var q = db.LlmCalls.AsNoTracking().Where(c => c.OwnerId == ownerId || c.OwnerId == null);
        if (fromUtc is { } from) q = q.Where(c => c.CreatedAt >= from);
        if (toUtc is { } to) q = q.Where(c => c.CreatedAt < to);
        return await q.OrderBy(c => c.CreatedAt).ToListAsync(ct);
    }
}
