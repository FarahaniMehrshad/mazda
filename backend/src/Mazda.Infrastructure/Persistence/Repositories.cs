using Microsoft.EntityFrameworkCore;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Identity;
using Mazda.Domain.Modules.Signatures;
using Mazda.Domain.Modules.Generations;
using Mazda.Domain.Modules.Mcp;

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

    public async Task ReplaceElementsAsync(Signature signature, IEnumerable<SignatureElement> newElements, CancellationToken ct = default)
    {
        var sigId = signature.Id;

        // 1. Wipe the change tracker. Every previous attempt failed because the
        //    parent's navigation snapshot kept old element references around, and
        //    SaveChanges then computed a diff against rows we'd already deleted
        //    (or were about to delete) — producing DbUpdateConcurrencyException
        //    "expected 1 row, affected 0".
        //
        //    With the tracker cleared we lose ALL snapshots, so the SaveChanges
        //    diff cannot reach back to phantom rows. Safe in this handler — the
        //    only entities loaded here are this signature and its elements.
        db.ChangeTracker.Clear();

        // 2. Hard-delete the existing element rows at the DB level. ExecuteDelete
        //    emits a single SQL statement and bypasses the change tracker entirely.
        await db.SignatureElements
            .Where(e => e.SignatureId == sigId)
            .ExecuteDeleteAsync(ct);

        // 3. Reset the in-memory navigation collection on the (detached) signature
        //    to the new elements.
        signature.ReplaceElements(newElements);

        // 4. Re-attach with explicit states. Don't rely on EF inference: each
        //    SignatureElement has a non-default Guid Id (Entity base ctor sets
        //    Guid.NewGuid()), so Attach would default them to Unchanged and EF
        //    would skip inserting them. We force Added.
        db.Signatures.Attach(signature);
        db.Entry(signature).State = EntityState.Modified;
        foreach (var child in signature.Elements)
            db.Entry(child).State = EntityState.Added;
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

public class McpToolRepository(MazdaDbContext db) : IMcpToolRepository
{
    public Task<McpTool?> FindByIdAsync(Guid id, Guid ownerId, CancellationToken ct = default)
        => db.McpTools.FirstOrDefaultAsync(t => t.Id == id && t.OwnerId == ownerId, ct);

    public Task<McpTool?> FindByNameAsync(string name, Guid ownerId, CancellationToken ct = default)
    {
        var canonical = name.Trim().ToLowerInvariant();
        return db.McpTools.FirstOrDefaultAsync(t => t.Name == canonical && t.OwnerId == ownerId, ct);
    }

    public async Task<IReadOnlyList<McpTool>> ListAsync(Guid ownerId, CancellationToken ct = default)
        => await db.McpTools
            .Where(t => t.OwnerId == ownerId)
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<McpTool>> ListEnabledAsync(Guid ownerId, CancellationToken ct = default)
        => await db.McpTools
            .Where(t => t.OwnerId == ownerId && t.Enabled)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

    public async Task AddAsync(McpTool tool, CancellationToken ct = default)
        => await db.McpTools.AddAsync(tool, ct);

    public Task RemoveAsync(McpTool tool, CancellationToken ct = default)
    {
        db.McpTools.Remove(tool);
        return Task.CompletedTask;
    }
}
