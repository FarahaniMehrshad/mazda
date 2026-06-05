using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Application.Common.Services;

/// <summary>
/// BFS-based resolver. Loads the root, then iteratively pulls in dependency
/// signatures by ID until the frontier is empty. Detects cycles by tracking
/// the ancestor set; signals via exception (callers treat it as a 5xx — the
/// add-dependency use case prevents cycles at write time, so seeing one here
/// means data corruption).
///
/// Performance: signatures per user are small (10s, maybe low hundreds);
/// dep graphs are shallow. A single bulk-load per frontier level keeps
/// round-trips O(depth), not O(nodes).
/// </summary>
public class EffectiveSignatureResolver(ISignatureRepository signatures) : IEffectiveSignatureResolver
{
    public async Task<IReadOnlyList<Signature>?> ResolveAsync(Guid rootId, Guid ownerId, CancellationToken ct = default)
    {
        var root = await signatures.FindByIdAsync(rootId, ownerId, ct);
        if (root is null) return null;

        // Materialize every reachable signature, keyed by id.
        var all = new Dictionary<Guid, Signature> { [root.Id] = root };
        var frontier = root.DependencyIds.ToList();
        while (frontier.Count > 0)
        {
            // Skip ids we've already loaded (cycle short-circuit + diamond
            // dedup — e.g. both A and B depend on C, C only loaded once).
            var toLoad = frontier.Where(id => !all.ContainsKey(id)).Distinct().ToList();
            if (toLoad.Count == 0) break;
            var loaded = await signatures.FindManyByIdsAsync(toLoad, ownerId, ct);
            // Anything in toLoad that wasn't returned by the DB is either a
            // dangling FK (signature was deleted via raw SQL) or a cross-user
            // reference (shouldn't happen because the repo scopes to owner).
            // Silently drop — the dependency just doesn't contribute elements.
            foreach (var s in loaded) all[s.Id] = s;
            // Build the next frontier from THIS level's loaded deps.
            frontier = loaded.SelectMany(s => s.DependencyIds).ToList();
        }

        // Topological sort: deepest first, root last. Iterative Kahn-style on
        // the loaded subgraph. If a cycle slipped past the add-time guard
        // we'd detect it here as nodes that never become "free" — throw.
        var ordered = TopoSort(all);
        return ordered;
    }

    public async Task<bool> WouldCreateCycleAsync(Guid signatureId, Guid candidateDependsOnId, Guid ownerId, CancellationToken ct = default)
    {
        // Self-loop is the trivial cycle. The domain method also rejects this
        // but we surface a clean false→reject here so callers can return a
        // validation error before mutating the aggregate.
        if (signatureId == candidateDependsOnId) return true;

        // A cycle is created iff signatureId is already reachable FROM
        // candidateDependsOnId. Resolve the candidate's effective set and
        // see if signatureId appears in it.
        var candidate = await signatures.FindByIdAsync(candidateDependsOnId, ownerId, ct);
        if (candidate is null) return false; // candidate doesn't exist — nothing to cycle through
        var visited = new HashSet<Guid> { candidate.Id };
        var frontier = candidate.DependencyIds.ToList();
        while (frontier.Count > 0)
        {
            if (frontier.Contains(signatureId)) return true;
            var toLoad = frontier.Where(id => !visited.Contains(id)).Distinct().ToList();
            if (toLoad.Count == 0) return false;
            foreach (var id in toLoad) visited.Add(id);
            var loaded = await signatures.FindManyByIdsAsync(toLoad, ownerId, ct);
            frontier = loaded.SelectMany(s => s.DependencyIds).ToList();
        }
        return false;
    }

    /// <summary>
    /// Topo sort (Kahn): deepest deps first, root last. Throws if a cycle is
    /// present (defensive — the add-time check should make this impossible).
    /// </summary>
    private static IReadOnlyList<Signature> TopoSort(Dictionary<Guid, Signature> all)
    {
        // out-edges = dep IDs THIS node depends on; in-degree counts the
        // number of those out-edges that point at nodes still in the set.
        // We process nodes whose out-edges are all resolved (in-degree-of-
        // out-set == 0), which yields leaves-first order.
        var indeg = new Dictionary<Guid, int>(all.Count);
        foreach (var (id, sig) in all)
            indeg[id] = sig.DependencyIds.Count(d => all.ContainsKey(d));

        var queue = new Queue<Guid>(indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        // Reverse map: who depends on me? Needed to decrement in-degree when
        // I'm emitted.
        var dependents = new Dictionary<Guid, List<Guid>>(all.Count);
        foreach (var (id, sig) in all)
            foreach (var dep in sig.DependencyIds)
                if (all.ContainsKey(dep))
                {
                    if (!dependents.TryGetValue(dep, out var list))
                        dependents[dep] = list = new List<Guid>();
                    list.Add(id);
                }

        var ordered = new List<Signature>(all.Count);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            ordered.Add(all[id]);
            if (!dependents.TryGetValue(id, out var parents)) continue;
            foreach (var p in parents)
            {
                if (--indeg[p] == 0) queue.Enqueue(p);
            }
        }

        if (ordered.Count != all.Count)
            throw new InvalidOperationException(
                $"Cycle detected in signature dependencies (got {ordered.Count}/{all.Count} nodes). " +
                "The add-dependency use case should have rejected this — DB likely tampered with.");

        return ordered;
    }
}
