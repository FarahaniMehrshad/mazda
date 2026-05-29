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
}

public interface IGenerationRepository
{
    Task<Generation?> FindByIdAsync(Guid id, Guid ownerId, CancellationToken ct = default);
    Task<IReadOnlyList<Generation>> ListRecentAsync(Guid ownerId, int take, CancellationToken ct = default);
    Task AddAsync(Generation generation, CancellationToken ct = default);
}
