using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostRestoreEvidence
{
    internal async Task<(OutpostRollbackBridge? Rollback, OutpostRestoreBridge? Restore, OutpostRollbackFromViewBridge? FromView)>
        GetActiveDownloadBridgesHeldAsync(OutpostLease lease, long playerId, SaveVersion target, CancellationToken ct)
    {
        if (!lease.CoversOwner(playerId)) throw new InvalidOperationException("outpost-lease-owner-mismatch");
        var canonical = await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == playerId && v.IsCurrent).Take(2).ToArrayAsync(ct);
        if (canonical.Length != 1 || !Same(canonical[0], target)) throw Invalid("source-download-cut-mismatch");
        var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == playerId, ct);
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var current = new OutpostScopeMessage { WorldInstanceId = head.WorldId, RestoreBoundary = head.RestoreBoundary,
            OwnerPlayerId = playerId, SaveGeneration = player.SaveGeneration, SaveWriteEpoch = player.SaveWriteEpoch };
        var original = ReadActualDownload(target).Outposts;
        if (!original.Present) throw Invalid("source-download-protected-metadata-missing");
        if (original.Metadata.Scope.Equals(current)) return (null, null, null);
        // A seam is applicable by its complete resulting scope, never by populated-field priority or save number.
        var restores = await SourceRestoreRootsAsync(current, ct);
        var rollbacks = (await db.OutpostLifecycleOperations.AsNoTracking().Where(r => r.Kind == "Rollback").ToArrayAsync(ct))
            .Where(r => OutpostLifecycleEvidence.Intent(r).Rollback!.Kind == "ProtectedSnapshot"
                && OutpostScopeMessage.Parser.ParseFrom(OutpostLifecycleEvidence.Intent(r).ResultScopes![0].Scope).Equals(current)).ToArray();
        if (restores.Length + rollbacks.Length != 1) throw Invalid("source-download-authorization-missing-or-ambiguous");
        if (restores.Length == 1)
        {
            var root = restores[0];
            return (null, await CreateRestoreBridgeAsync(root, Plan(root), target, current, original.Metadata, true, ct), null);
        }
        var operation = rollbacks[0];
        if (OutpostLifecycleEvidence.Intent(operation).Rollback!.TargetSource is null)
            return (await new OutpostLifecycleEvidence(db, files).GetRollbackBridgeHeldAsync(lease, playerId, target, ct), null, null);
        var result = await CreateRollbackSourceBridgeAsync(operation.Id, target, current, true, ct);
        return (result.Actual, null, result.View);
    }

    public async Task<OutpostRestoreBridge?> GetDownloadBridgeHeldAsync(OutpostLease lease, long playerId, SaveVersion target, CancellationToken ct)
    {
        if (!lease.CoversOwner(playerId)) throw new InvalidOperationException("outpost-lease-owner-mismatch");
        var candidates = await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == playerId && v.IsCurrent).Take(2).ToArrayAsync(ct);
        if (candidates.Length != 1 || !Same(candidates[0], target)) throw Invalid("restore-download-cut-mismatch");
        var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == playerId, ct);
        var authority = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var current = new OutpostScopeMessage { WorldInstanceId = authority.WorldId, OwnerPlayerId = playerId,
            SaveGeneration = player.SaveGeneration, SaveWriteEpoch = player.SaveWriteEpoch, RestoreBoundary = authority.RestoreBoundary };
        await ValidateStoredAsync(ct);
        var original = ReadActualDownload(target);
        if (!original.Outposts.Present) throw Invalid("restore-legacy-download-proof-required");
        if (original.Outposts.Metadata.Scope.Equals(current)) return null;
        var roots = await db.OutpostTransitions.AsNoTracking().Where(t => t.Kind == "BulkRestore").ToArrayAsync(ct);
        var applicable = roots.Select(t => (Root: t, Plan: Plan(t))).Where(p => p.Plan.Journal.Selection.TargetBoundary == current.RestoreBoundary
            && p.Plan.Cut.Authority.WorldId == current.WorldInstanceId).ToArray();
        if (applicable.Length != 1) throw Invalid("restore-download-authorization-missing-or-ambiguous");
        var (root, plan) = applicable[0];
        return await CreateRestoreBridgeAsync(root, plan, target, current, original.Outposts.Metadata, true, ct);
    }

    private async Task<OutpostRestoreBridge> CreateRestoreBridgeAsync(OutpostTransition root, RestoreMainPlan plan,
        SaveVersion target, OutpostScopeMessage current, OutpostSaveMetadata source, bool requireCurrentCustody, CancellationToken ct)
    {
        var resolver = new SourceResolver(this, ct);
        var actual = await resolver.ActualAsync(target);
        if (!actual.Snapshot.Scope.Equals(source.Scope)) throw Invalid("restore-download-source-scope-mismatch");
        var view = await resolver.RestoreAsync(root, plan, actual.Snapshot, current);
        if (requireCurrentCustody)
            foreach (var map in view.RegistryMap)
            {
                var expected = plan.Custody.Single(m => m.After.Id == map.After.PawnCustodyId).After;
                var observed = await db.PawnCustody.AsNoTracking().SingleOrDefaultAsync(p => p.Id == expected.Id, ct);
                if (observed is null || !Same(expected, observed)) throw Invalid("restore-download-live-registry-mismatch");
            }
        var bridge = SourceResolver.Bridge(actual.Snapshot, view, resolver.Graph());
        _ = OutpostSourceGraph.ValidateRoot(bridge); return bridge;
    }

    private SaveRecoveryData ReadActualDownload(SaveVersion target, string? archive = null)
    {
        SaveRecoveryData original;
        try
        {
            original = SaveRecoveryData.ReadXml(new MemoryStream(files.ReadEvidence(target, archive), false), out byte[] rawHash);
            if (target.RawSha256 is null || !rawHash.AsSpan().SequenceEqual(target.RawSha256) || target.SnapshotId != original.SnapshotId
                || target.RecoveryVersion != original.Version || target.SaveGeneration != original.Generation || target.SaveWriteEpoch != original.WriteEpoch
                || !Same(SaveStore.RecoveryOf(target), original)) throw Invalid("restore-download-actual-bytes-mismatch");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ZstdSharp.ZstdException)
        { throw Invalid("restore-download-actual-bytes-unavailable"); }
        return original;
    }
}
