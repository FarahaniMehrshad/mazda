using Mazda.Domain.Modules.Identity;
using Mazda.Domain.Modules.Signatures;
using Mazda.Domain.Modules.Generations;

namespace Mazda.Application.Common.Abstractions;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public interface IUserRepository
{
    Task<User?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task<User?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(User user, CancellationToken ct = default);
}

public interface ISignatureRepository
{
    Task<Signature?> FindByIdAsync(Guid id, Guid ownerId, CancellationToken ct = default);
    Task<IReadOnlyList<Signature>> ListAsync(Guid ownerId, CancellationToken ct = default);
    Task AddAsync(Signature signature, CancellationToken ct = default);
    Task RemoveAsync(Signature signature, CancellationToken ct = default);

    /// <summary>
    /// Hard-replace the element collection of an existing signature. Hard-deletes any
    /// current rows at the SQL level (so we don't depend on EF Core's orphan-tracking,
    /// which is fragile with field-backed collections under <c>WithOne()</c> mappings)
    /// then attaches the new collection through the aggregate.
    /// </summary>
    Task ReplaceElementsAsync(Signature signature, IEnumerable<SignatureElement> newElements, CancellationToken ct = default);
}

public interface IGenerationRepository
{
    Task<Generation?> FindByIdAsync(Guid id, Guid ownerId, CancellationToken ct = default);
    Task<IReadOnlyList<Generation>> ListRecentAsync(Guid ownerId, int take, CancellationToken ct = default);
    Task AddAsync(Generation generation, CancellationToken ct = default);
}
