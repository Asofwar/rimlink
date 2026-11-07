using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Backup;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostRestoreEvidence
{
    internal async Task<bool> HasCurrentRestoreOwnerAsync(long playerId, CancellationToken ct)
    {
        var roots = await (from transition in db.OutpostTransitions.AsNoTracking()
            join head in db.OutpostAuthority.AsNoTracking() on new { transition.WorldId, transition.RestoreBoundary }
                equals new { head.WorldId, head.RestoreBoundary }
            where transition.Kind == "BulkRestore"
            select transition).Take(2).ToArrayAsync(ct);
        if (roots.Length > 1) throw Invalid("restore-owner-projection-ambiguous");
        return roots.Length == 1 && Plan(roots[0]).Players.Any(owner => owner.PlayerId == playerId);
    }

    internal async Task ValidateLegacyCutArchiveAsync(OutpostScopeMessage scope, long archiveId, string? liveDirectory, CancellationToken ct)
    {
        var roots = await db.OutpostTransitions.AsNoTracking().Where(t => t.Kind == "BulkRestore"
            && t.WorldId == scope.WorldInstanceId && t.RestoreBoundary == scope.RestoreBoundary).Take(2).ToArrayAsync(ct);
        if (roots.Length != 1) throw Invalid("lifecycle-restore-source-missing");
        var plan = Plan(roots[0]);
        var owner = plan.Players.SingleOrDefault(p => p.PlayerId == scope.OwnerPlayerId);
        if (owner is null || owner.Canonical is not null || owner.Generation != scope.SaveGeneration || owner.WriteEpoch != scope.SaveWriteEpoch)
            throw Invalid("lifecycle-restore-source-mismatch");

        await OutpostRestoreProcedure.ValidateRetainedCutAsync(Path.Combine(files.DataRoot, plan.SourceDirectory), plan.Cut, async source =>
        {
            var archive = await source.ArchivedColonies.AsNoTracking().SingleOrDefaultAsync(a => a.Id == archiveId, ct);
            var original = scope.Clone(); original.RestoreBoundary = plan.Cut.Authority.RestoreBoundary;
            var evidence = CutLifecycleEvidence(source);
            if (archive is null || archive.State != ArchiveState.Archived || archive.PlayerId != scope.OwnerPlayerId
                || archive.SaveGeneration != scope.SaveGeneration || liveDirectory is not null && liveDirectory != archive.Dir
                || await evidence.HasLocalProtectedHistoryAsync(original, ct)) throw Invalid("lifecycle-restore-source-mismatch");

            string relative = archive.Dir.Replace('\\', '/');
            string prefix = relative + "/";
            var inventory = plan.Cut.Files.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            var manifestFile = inventory.SingleOrDefault(f => f.Path == prefix + SaveArchive.ManifestFile);
            if (manifestFile is null || manifestFile.Length > OutpostLifecycleEvidence.MaxBytes) throw Invalid("lifecycle-restore-source-mismatch");
            string sourceRoot = Path.GetDirectoryName(source.Database.GetDbConnection().DataSource)!;
            string directory = new SaveFiles(DataDirectory.Existing(sourceRoot)).EvidenceArchiveDirectory(relative);
            var manifest = OutpostLifecycleEvidence.Decode<SaveArchive.Manifest>(await File.ReadAllBytesAsync(Path.Combine(directory, SaveArchive.ManifestFile), ct));
            if (manifest.Versions.Count > OutpostSnapshotGuard.MaxSnapshots
                || manifest.PlayerId != (manifest.Versions.Count == 0 ? 0 : scope.OwnerPlayerId)
                || manifest.Versions.Count(v => v.IsCurrent) != (manifest.Versions.Count == 0 ? 0 : 1)
                || manifest.Versions.Select(v => v.Number).Distinct().Count() != manifest.Versions.Count
                || inventory.Length != manifest.Versions.Count + 1) throw Invalid("lifecycle-restore-source-mismatch");
            foreach (var version in manifest.Versions)
            {
                if (version.SaveGeneration != scope.SaveGeneration || version.SaveWriteEpoch < 0 || version.SaveWriteEpoch > scope.SaveWriteEpoch
                    || !inventory.Any(f => f.Path == prefix + $"{version.Number}.rws.zst")) throw Invalid("lifecycle-restore-source-mismatch");
                _ = evidence.ReadLegacySnapshot(OutpostLifecycleEvidence.Version(new(scope.WorldInstanceId, scope.OwnerPlayerId, version)), scope.WorldInstanceId, directory);
            }
            if (liveDirectory is not null)
            {
                string live = files.EvidenceArchiveDirectory(relative);
                foreach (var file in inventory)
                    if (!await OutpostRestoreProcedure.FileMatchesAsync(Path.Combine(live, Path.GetFileName(file.Path)), file, ct))
                        throw Invalid("lifecycle-restore-archive-bytes-mismatch");
            }
        }, ct, ManifestBytes(plan));
    }

    // These legacy rows are outside DomainDigest. Check the retained cut and the applicable successor evidence.
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
            else if (!Same(current, owner.Canonical))
            {
                // An unrelated colony still uploads through the ordinary save path after restore.
                if (current is null || current.SaveGeneration != owner.Generation || current.SaveWriteEpoch != owner.WriteEpoch
                    || current.RawSha256 is not { Length: 32 } || current.Number <= (owner.Canonical?.Number ?? 0)
                    || current.GameTick < (owner.Canonical?.GameTick ?? 0)
                    || await source.ArchivedColonies.AnyAsync(a => a.PlayerId == owner.PlayerId && a.SaveGeneration == owner.Generation
                        && a.State == ArchiveState.Archived, ct))
                    throw Invalid("restore-current-canonical-mismatch");

                var scope = new OutpostScopeMessage { WorldInstanceId = root.WorldId, OwnerPlayerId = owner.PlayerId,
                    SaveGeneration = owner.Generation, SaveWriteEpoch = owner.WriteEpoch, RestoreBoundary = plan.Cut.Authority.RestoreBoundary };
                if (await CutLifecycleEvidence(source).HasLocalProtectedHistoryAsync(scope, ct))
                    throw Invalid("restore-current-canonical-mismatch");
                scope.RestoreBoundary = root.RestoreBoundary;
                var live = new OutpostLifecycleEvidence(db, files);
                if (await live.HasLocalProtectedHistoryAsync(scope, ct, transitions)) throw Invalid("restore-current-canonical-mismatch");
                _ = live.ReadLegacySnapshot(current, root.WorldId);
            }
        }
    }

    private static OutpostLifecycleEvidence CutLifecycleEvidence(RimlinkDbContext source) =>
        new(source, new SaveFiles(DataDirectory.Existing(Path.GetDirectoryName(source.Database.GetDbConnection().DataSource)!)));
}
