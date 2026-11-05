using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostRestoreEvidence
{
    // These legacy rows are deliberately outside DomainDigest; their authority is the exact cut plus typed descendants.
    private async Task ValidateCurrentProjectionsAsync(OutpostTransition root, RestoreMainPlan plan,
        IReadOnlyDictionary<string, OutpostTransition> transitions, RimlinkDbContext source, CancellationToken ct)
    {
        var groups = transitions.Values.Where(t => t.PredecessorId is not null).GroupBy(t => t.PredecessorId!).ToArray();
        if (groups.Any(g => g.Count() != 1)) throw Invalid("restore-projection-chain-ambiguous");
        var next = groups.ToDictionary(g => g.Key, g => g.Single());
        var seen = new HashSet<string> { root.Id };
        var later = new List<OutpostTransition>();
        string cursor = root.Id;
        while (next.TryGetValue(cursor, out var child))
        {
            if (!seen.Add(child.Id)) throw Invalid("restore-projection-chain-cycle");
            later.Add(child); cursor = child.Id;
        }
        var operations = await db.OutpostLifecycleOperations.AsNoTracking().Where(o => o.Kind != "BulkRestore").ToDictionaryAsync(o => o.Id, ct);
        var replaced = new HashSet<long>();
        var scopedByLifecycle = new HashSet<long>();
        var canonicalEvents = new Dictionary<long, OutpostTransition?>();
        bool worldReplaced = false;
        foreach (var step in later)
        {
            if (step.Kind == "BulkRestore")
            {
                var following = Plan(step);
                foreach (var owner in following.Players) replaced.Add(owner.PlayerId);
                worldReplaced = true;
            }
            else if (step.Kind is "Lifecycle" or "WorldBind" or "Rollback")
            {
                var evidence = OutpostLifecycleEvidence.Decode<LifecycleTransition>(step.Intent);
                if (!operations.TryGetValue(evidence.OperationId, out var operation)) throw Invalid("restore-projection-lifecycle-missing");
                var intent = OutpostLifecycleEvidence.Intent(operation);
                foreach (long owner in intent.Owners) scopedByLifecycle.Add(owner);
                if (evidence.Phase is "Destructive" or "Completed")
                {
                    foreach (long owner in intent.Owners) canonicalEvents[owner] = null;
                    if (intent.Kind is "Recreate" or "WorldBind") worldReplaced = true;
                }
            }
            else if (step.Kind == "SnapshotAccepted") canonicalEvents[step.PlayerId] = step;
        }
        if (!worldReplaced)
        {
            var worlds = await source.World.AsNoTracking().ToListAsync(ct);
            if (worlds.Count > 1) throw Invalid("restore-source-world-ambiguous");
            if (worlds.Count == 0)
            {
                if (await db.World.AnyAsync(ct)) throw Invalid("restore-current-world-mismatch");
            }
            else
            {
                var world = worlds[0];
                await new OutpostLifecycleEvidence(db, files).ValidateCurrentWorldAsync(
                    new(world.Id, world.InfluenceInstanceId, world.Seed, world.Params, world.NpcTiles, world.CreatedAt), ct);
            }
        }
        foreach (var owner in plan.Players)
        {
            if (replaced.Contains(owner.PlayerId)) continue;
            var player = await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.Id == owner.PlayerId, ct);
            if (player is null || !scopedByLifecycle.Contains(owner.PlayerId)
                && (player.SaveGeneration != owner.Generation || player.SaveWriteEpoch != owner.WriteEpoch))
                throw Invalid("restore-current-owner-mismatch");
            var currents = await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == owner.PlayerId && v.IsCurrent).Take(2).ToListAsync(ct);
            if (currents.Count > 1) throw Invalid("restore-current-canonical-ambiguous");
            var current = currents.SingleOrDefault();
            if (canonicalEvents.TryGetValue(owner.PlayerId, out var accepted))
            {
                // A validated lifecycle owns its own current/archive/reset projection. A later exact acceptance replaces it.
                if (accepted is null) continue;
                var link = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(l => l.TransitionId == accepted.Id, ct);
                if (current is null || link is null || link.PlayerId != owner.PlayerId || current.SaveGeneration != link.SaveGeneration
                    || current.SaveWriteEpoch != link.SaveWriteEpoch || current.SnapshotId != link.SnapshotId || current.RawSha256 is null
                    || !current.RawSha256.AsSpan().SequenceEqual(link.RawSha256)) throw Invalid("restore-current-canonical-mismatch");
            }
            else if (!Same(current, owner.Canonical)) throw Invalid("restore-current-canonical-mismatch");
        }
    }
}
