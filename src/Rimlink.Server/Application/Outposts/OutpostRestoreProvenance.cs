using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Backup;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostRestoreEvidence
{
    public async Task<OutpostSaveMetadata?> ValidateProvenanceAsync(OutpostSaveMetadata metadata, CancellationToken ct)
    {
        if (metadata.RollbackFromViewBridge is not null || metadata.RollbackBridge?.SourceProvenance is not null
            || metadata.RollbackBridge?.SourcePathDigest.Length > 0)
            return await ValidateRollbackGraphProvenanceAsync(metadata, ct);
        var roots = await db.OutpostTransitions.AsNoTracking().Where(t => t.Kind == "BulkRestore").ToArrayAsync(ct);
        var applicable = roots.Select(t => (Root: t, Plan: Plan(t))).Where(r =>
            r.Plan.Journal.Selection.TargetBoundary == metadata.Scope.RestoreBoundary
            && r.Plan.Cut.Authority.WorldId == metadata.Scope.WorldInstanceId
            && r.Plan.Players.Any(p => p.PlayerId == metadata.Scope.OwnerPlayerId && p.Generation == metadata.Scope.SaveGeneration
                && p.WriteEpoch == metadata.Scope.SaveWriteEpoch)).ToArray();
        if (metadata.RestoreBridge is not { } bridge)
        {
            if (applicable.Length > 0 && metadata.RollbackBridge is null && metadata.RollbackFromViewBridge is null)
            {
                if (applicable.Length != 1) throw Invalid("restore-snapshot-authorization-missing-or-ambiguous");
                await RequireLegacyRestoreSourceAsync(applicable[0].Plan, metadata.Scope, ct);
            }
            return null;
        }
        _ = OutpostSourceGraph.ValidateRoot(bridge);
        if (applicable.Length != 1 || applicable[0].Root.Id != bridge.ActiveAuthorization.TransitionId)
            throw Invalid("restore-snapshot-authorization-missing");
        var (root, plan) = applicable[0];
        var owner = plan.Players.Single(p => p.PlayerId == metadata.Scope.OwnerPlayerId);
        var target = owner.Canonical ?? throw Invalid("restore-snapshot-canonical-missing");
        var original = ReadActualDownload(target).Outposts.Metadata;
        var expected = await CreateRestoreBridgeAsync(root, plan, target, metadata.Scope, original, false, ct);
        if (!bridge.Equals(expected)) throw Invalid("restore-snapshot-bridge-mismatch");
        // New current captures are allowed; historical captures cannot become new grants by editing an echo.
        if (original.OrdinaryOutgoing.Any(c => !metadata.OrdinaryOutgoing.Any(n => n.Equals(c)))
            || metadata.OrdinaryOutgoing.Any(c => !c.Scope.Equals(metadata.Scope) && !original.OrdinaryOutgoing.Any(o => o.Equals(c)))
            || metadata.WorkerCaptures.Any(c => !c.Pawn.Registry.Scope.Equals(metadata.Scope) && !original.WorkerCaptures.Any(o => o.Equals(c)))
            || metadata.Caravans.Any(c => original.Caravans.Any(o => o.ClientRequestId == c.ClientRequestId) && !original.Caravans.Any(o => o.Equals(c))))
            throw Invalid("restore-snapshot-historical-capture-changed");
        return original;
    }

    private async Task RequireLegacyRestoreSourceAsync(RestoreMainPlan plan, OutpostScopeMessage scope, CancellationToken ct)
    {
        var original = scope.Clone();
        original.RestoreBoundary = plan.Cut.Authority.RestoreBoundary;
        try
        {
            // Classify the retained cut. New protected snapshots in the restored scope do not change its origin.
            await OutpostRestoreProcedure.ValidateRetainedCutAsync(Path.Combine(files.DataRoot, plan.SourceDirectory), plan.Cut,
                async source =>
                {
                    if (await CutLifecycleEvidence(source).HasLocalProtectedHistoryAsync(original, ct))
                        throw Invalid("restore-fresh-snapshot-bridge-missing");
                }, ct, ManifestBytes(plan));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or SqliteException)
        { throw Invalid("restore-fresh-snapshot-legacy-source-unavailable"); }
    }

    internal async Task<bool> HistoricalTombstoneMatchesAsync(OutpostSaveMetadata metadata, OutpostSaveMetadata? original,
        PawnCustodyRecord custody, PawnRegistryEnvelope envelope, CancellationToken ct)
    {
        if (original is null || !envelope.Tombstone || !original.Registry.Any(r => r.Equals(envelope))) return false;
        var graph = metadata.RestoreBridge?.SourceProvenance ?? metadata.RollbackFromViewBridge?.SourceProvenance
            ?? metadata.RollbackBridge?.SourceProvenance;
        if (graph is null) return false;
        var cursor = custody; var visited = new HashSet<string>(StringComparer.Ordinal);
        // Tombstones are not mutable registry maps. Follow exact immutable custody maps separately, never relabel the envelope.
        while (visited.Add(cursor.RestoreBoundary) && visited.Count <= OutpostSourceGraph.MaxDepth)
        {
            var candidates = graph.AuthorizedViews.Where(v => v.Kind == OutpostSourceAuthorizationKind.Restore
                && v.EffectiveScope.RestoreBoundary == cursor.RestoreBoundary && v.EffectiveScope.OwnerPlayerId == cursor.PlayerId).ToArray();
            if (candidates.Length != 1) return false;
            var root = await SourceRootAsync(candidates[0].TransitionId, ct);
            var map = Plan(root).Custody.SingleOrDefault(m => m.After.Id == cursor.Id);
            if (map is null || !Same(map.After, cursor)) return false;
            cursor = map.Before;
            if (cursor.RestoreBoundary == envelope.Scope.RestoreBoundary)
                return cursor.WorldId == envelope.Scope.WorldInstanceId && cursor.PlayerId == envelope.Scope.OwnerPlayerId
                    && cursor.SaveGeneration == envelope.Scope.SaveGeneration && (cursor.AssignmentId ?? "") == envelope.AssignmentId;
        }
        return false;
    }
}
