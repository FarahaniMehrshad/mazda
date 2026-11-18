using Mazda.Domain.Modules.Usage;

namespace Mazda.Application.Common.Usage;

/// <summary>
/// Writes ledger rows. Saves in its own unit of work and never throws: a call
/// that cannot be recorded must not fail the work that made it.
/// </summary>
public interface ILlmUsageRecorder
{
    Task RecordAsync(LlmCall call, CancellationToken ct = default);
}

/// <summary>Reads ledger rows the user may see: their own, plus unattributed ones.</summary>
public interface ILlmUsageQueries
{
    Task<IReadOnlyList<LlmCall>> ForSubjectsAsync(Guid ownerId, IReadOnlyCollection<Guid> subjectIds, CancellationToken ct = default);
    Task<IReadOnlyList<LlmCall>> BetweenAsync(Guid ownerId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct = default);
}
