using Microsoft.EntityFrameworkCore;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Identity;
using Mazda.Domain.Modules.Signatures;
using Mazda.Domain.Modules.Generations;

namespace Mazda.Infrastructure.Persistence.Repositories;

public class UserRepository(MazdaDbContext db) : IUserRepository
{
    public Task<User?> FindByEmailAsync(string email, CancellationToken ct = default)
        => db.Users.FirstOrDefaultAsync(u => u.Email == email.ToLowerInvariant(), ct);

    public Task<User?> FindByIdAsync(Guid id, CancellationToken ct = default)
        => db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public async Task AddAsync(User user, CancellationToken ct = default)
        => await db.Users.AddAsync(user, ct);
}

public class SignatureRepository(MazdaDbContext db) : ISignatureRepository
{
    public Task<Signature?> FindByIdAsync(Guid id, Guid ownerId, CancellationToken ct = default)
        => db.Signatures
            .Include(s => s.Elements)
            .FirstOrDefaultAsync(s => s.Id == id && s.OwnerId == ownerId, ct);

    public async Task<IReadOnlyList<Signature>> ListAsync(Guid ownerId, CancellationToken ct = default)
        => await db.Signatures
            .Include(s => s.Elements)
            .Where(s => s.OwnerId == ownerId)
            .OrderByDescending(s => s.UpdatedAt)
            .ToListAsync(ct);

    public async Task AddAsync(Signature signature, CancellationToken ct = default)
        => await db.Signatures.AddAsync(signature, ct);

    public Task RemoveAsync(Signature signature, CancellationToken ct = default)
    {
        db.Signatures.Remove(signature);
        return Task.CompletedTask;
    }
}

public class GenerationRepository(MazdaDbContext db) : IGenerationRepository
{
    public Task<Generation?> FindByIdAsync(Guid id, Guid ownerId, CancellationToken ct = default)
        => db.Generations.FirstOrDefaultAsync(g => g.Id == id && g.OwnerId == ownerId, ct);

    public async Task<IReadOnlyList<Generation>> ListRecentAsync(Guid ownerId, int take, CancellationToken ct = default)
        => await db.Generations
            .Where(g => g.OwnerId == ownerId)
            .OrderByDescending(g => g.CreatedAt)
            .Take(take)
            .ToListAsync(ct);

    public async Task AddAsync(Generation generation, CancellationToken ct = default)
        => await db.Generations.AddAsync(generation, ct);
}
