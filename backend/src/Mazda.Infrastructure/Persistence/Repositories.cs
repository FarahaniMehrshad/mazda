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
            .Include(s => s.Dependencies)
            .Include(s => s.Functions)
            .FirstOrDefaultAsync(s => s.Id == id && s.OwnerId == ownerId, ct);

    public async Task<IReadOnlyList<Signature>> ListAsync(Guid ownerId, CancellationToken ct = default)
        => await db.Signatures
            .Include(s => s.Elements)
            .Include(s => s.Dependencies)
            .Include(s => s.Functions)
            .Where(s => s.OwnerId == ownerId)
            .OrderByDescending(s => s.UpdatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Signature>> FindManyByIdsAsync(IReadOnlyCollection<Guid> ids, Guid ownerId, CancellationToken ct = default)
    {
        if (ids.Count == 0) return Array.Empty<Signature>();
        return await db.Signatures
            .Include(s => s.Elements)
            .Include(s => s.Dependencies)
            .Include(s => s.Functions)
            .Where(s => s.OwnerId == ownerId && ids.Contains(s.Id))
            .ToListAsync(ct);
    }

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

        // Compute a content hash of the element collection BEFORE and AFTER so
        // we know whether anything meaningful actually changed. We don't want
        // to wipe derived caches when the user just renames the signature and
        // re-saves the same element list — but we DO want to wipe them when a
        // selector / description / category actually shifts.
        var newElementsList = newElements.ToList();
        var beforeHash = HashElements(signature.Elements);
        var afterHash = HashElements(newElementsList);
        var elementsChanged = beforeHash != afterHash;

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
        signature.ReplaceElements(newElementsList);

        // 4. When the element set actually shifted, every signature-level cache
        //    derived from those elements (LastTestScript, LastFinalScript,
        //    LastAnalyzeState*, UserFlowsJson) is now stale. Wipe them BEFORE
        //    re-attaching so the parent's Modified state captures the cleared
        //    columns in the same SaveChanges round-trip. Without this, fresh
        //    generations pre-fill from stale Last-* buffers and the user sees /
        //    runs scripts written against selectors that no longer exist.
        if (elementsChanged) signature.InvalidateDerivedCaches();

        // 5. Re-attach with explicit states. Don't rely on EF inference: each
        //    SignatureElement has a non-default Guid Id (Entity base ctor sets
        //    Guid.NewGuid()), so Attach would default them to Unchanged and EF
        //    would skip inserting them. We force Added.
        db.Signatures.Attach(signature);
        db.Entry(signature).State = EntityState.Modified;
        foreach (var child in signature.Elements)
            db.Entry(child).State = EntityState.Added;
    }

    public async Task RemoveFunctionAsync(Signature signature, Guid functionId, CancellationToken ct = default)
    {
        // Hard-delete at the SQL level — bypasses EF orphan-tracking, which is
        // the same reason ReplaceFunctionsAsync doesn't rely on it either. We
        // don't need the tracker-clear dance here because we're not going to
        // re-attach anything.
        await db.SignatureFunctions
            .Where(f => f.SignatureId == signature.Id && f.Id == functionId)
            .ExecuteDeleteAsync(ct);

        // Sync the in-memory aggregate so the caller returning DTOs from the
        // aggregate sees the deletion immediately without a re-load.
        signature.RemoveFunction(functionId);
    }

    public async Task ReplaceFunctionsAsync(Signature signature, IEnumerable<SignatureFunction> newFunctions, CancellationToken ct = default)
    {
        var sigId = signature.Id;
        var newList = newFunctions.ToList();

        // Same reasoning as ReplaceElementsAsync: clear the tracker first so
        // the parent's navigation snapshot can't hold stale references to
        // deleted rows and trip the "expected 1 row, affected 0" trap on
        // SaveChanges.
        db.ChangeTracker.Clear();

        // Hard-delete existing function rows at the SQL level.
        await db.SignatureFunctions
            .Where(f => f.SignatureId == sigId)
            .ExecuteDeleteAsync(ct);

        // Reset the aggregate's in-memory collection.
        signature.ReplaceFunctions(newList);

        // Re-attach with explicit states so EF inserts every child instead of
        // treating them as Unchanged (the entity's non-default Guid Id would
        // otherwise cause Attach() to default to Unchanged).
        db.Signatures.Attach(signature);
        db.Entry(signature).State = EntityState.Modified;
        foreach (var fn in signature.Functions)
            db.Entry(fn).State = EntityState.Added;
    }

    // Stable content hash of an element collection. Sorting first means
    // equivalent collections with different in-memory ordering compare equal
    // (we don't want to invalidate caches just because the editor renumbered
    // a row). The Order field IS included so genuine reordering counts as a
    // change — display order affects test generation (the LLM treats earlier
    // entries as more salient).
    private static string HashElements(IEnumerable<SignatureElement> elements)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in elements.OrderBy(e => e.Order).ThenBy(e => e.ElementSelector, StringComparer.Ordinal))
        {
            sb.Append(e.Order).Append('|')
              .Append(e.ElementSelector).Append('|')
              .Append(e.Category).Append('|')
              .Append(e.ElementType).Append('|')
              .Append(e.ElementDescription).Append('|')
              .Append(e.ElementLogic).Append('\n');
        }
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
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

    public async Task<IReadOnlyList<Generation>> ListForSignatureAsync(Guid signatureId, Guid ownerId, CancellationToken ct = default)
        => await db.Generations
            .Where(g => g.SignatureId == signatureId && g.OwnerId == ownerId)
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

public class UserSettingsRepository(MazdaDbContext db) : IUserSettingsRepository
{
    public async Task<UserSettings> GetOrCreateAsync(Guid userId, CancellationToken ct = default)
    {
        var existing = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (existing is not null) return existing;

        // Created lazily on first read. The caller's SaveChanges persists it;
        // if it never saves, we simply create it again next time — the row
        // holds nothing but defaults at this point.
        var created = UserSettings.CreateDefault(userId);
        await db.UserSettings.AddAsync(created, ct);
        return created;
    }
}
