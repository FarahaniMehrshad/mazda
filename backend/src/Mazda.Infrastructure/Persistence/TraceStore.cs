using System.Text.Json;
using Mazda.Application.Common.Tracing;
using Mazda.Domain.Modules.Tracing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mazda.Infrastructure.Persistence;

public sealed class TraceStore(IServiceScopeFactory scopes, ILogger<TraceStore> log) : ITraceStore
{
    public const int KeepPerSubject = 30;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task SaveAsync(TraceRecorder trace, Guid ownerId, Guid subjectId, CancellationToken ct = default)
    {
        try
        {
            var doc = trace.Snapshot();
            var json = JsonSerializer.Serialize(doc, Json);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MazdaDbContext>();
            var row = await db.RunTraces.FirstOrDefaultAsync(t => t.Id == trace.Id, CancellationToken.None);
            if (row is null)
            {
                row = new RunTrace(trace.Id, ownerId, trace.Kind, subjectId);
                await db.RunTraces.AddAsync(row, CancellationToken.None);
            }
            row.Update(trace.Label, doc.Ms, json);
            await db.SaveChangesAsync(CancellationToken.None);

            var old = await db.RunTraces.Where(t => t.SubjectId == subjectId && t.OwnerId == ownerId)
                .OrderByDescending(t => t.CreatedAt).Skip(KeepPerSubject).Select(t => t.Id).ToListAsync(CancellationToken.None);
            if (old.Count > 0)
                await db.RunTraces.Where(t => old.Contains(t.Id)).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not store trace {TraceId} ({Kind})", trace.Id, trace.Kind);
        }
    }

    public async Task<RunTrace?> FindAsync(Guid id, Guid ownerId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MazdaDbContext>();
        return await db.RunTraces.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.OwnerId == ownerId, ct);
    }

    public async Task<IReadOnlyList<TraceListItem>> ListAsync(Guid subjectId, Guid ownerId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MazdaDbContext>();
        return await db.RunTraces.AsNoTracking().Where(t => t.SubjectId == subjectId && t.OwnerId == ownerId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new TraceListItem(t.Id, t.Kind, t.Label, t.TotalMs, t.CreatedAt))
            .ToListAsync(ct);
    }
}
