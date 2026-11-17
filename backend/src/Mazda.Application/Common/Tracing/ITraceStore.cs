using Mazda.Domain.Modules.Tracing;

namespace Mazda.Application.Common.Tracing;

public sealed record TraceListItem(Guid Id, string Kind, string Label, long TotalMs, DateTime CreatedAt);

/// <summary>
/// Stores traces. Saves in its own unit of work, so saving a trace never
/// commits a use case's half-done changes, and never throws: a trace that
/// cannot be stored must not fail the operation it describes.
/// </summary>
public interface ITraceStore
{
    /// <summary>Saves the trace (again: updates it) for <paramref name="subjectId"/>; keeps the latest 30 per subject.</summary>
    Task SaveAsync(TraceRecorder trace, Guid ownerId, Guid subjectId, CancellationToken ct = default);
    Task<RunTrace?> FindAsync(Guid id, Guid ownerId, CancellationToken ct = default);
    Task<IReadOnlyList<TraceListItem>> ListAsync(Guid subjectId, Guid ownerId, CancellationToken ct = default);
}
