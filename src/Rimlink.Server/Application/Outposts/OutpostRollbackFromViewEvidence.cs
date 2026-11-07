using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostRestoreEvidence
{
    internal async Task<OutpostSaveMetadata> ValidateRollbackGraphProvenanceAsync(OutpostSaveMetadata metadata, CancellationToken ct)
    {
        string operationId;
        if (metadata.RollbackFromViewBridge is { } fromView)
            operationId = OutpostSourceGraph.ValidateRoot(fromView).LifecycleOperationId;
        else if (metadata.RollbackBridge is { } actual)
            operationId = actual.LifecycleOperationId;
        else throw Invalid("rollback-source-active-root-missing");
        var (_, intent, _) = await SourceRollbackAsync(operationId, ct);
        var proof = OutpostSnapshotProof.Parser.ParseFrom(intent.Rollback!.Target);
        var current = OutpostScopeMessage.Parser.ParseFrom(intent.ResultScopes![0].Scope);
        if (!current.Equals(metadata.Scope)) throw Invalid("rollback-snapshot-current-scope-mismatch");
        var target = await SourceVersionAsync(proof, ct); var original = ReadActualDownload(target.Version, target.Archive).Outposts.Metadata;
        var expected = await CreateRollbackSourceBridgeAsync(operationId, target.Version, current, false, ct, target.Archive);
        if (metadata.RollbackBridge is { } bridge && !bridge.Equals(expected.Actual)
            || metadata.RollbackFromViewBridge is { } view && !view.Equals(expected.View))
            throw Invalid("rollback-source-bridge-mismatch");
        if (original.OrdinaryOutgoing.Any(c => !metadata.OrdinaryOutgoing.Any(n => n.Equals(c)))
            || metadata.OrdinaryOutgoing.Any(c => !c.Scope.Equals(current) && !original.OrdinaryOutgoing.Any(o => o.Equals(c)))
            || metadata.WorkerCaptures.Any(c => !c.Pawn.Registry.Scope.Equals(current) && !original.WorkerCaptures.Any(o => o.Equals(c)))
            || metadata.Caravans.Any(c => original.Caravans.Any(o => o.ClientRequestId == c.ClientRequestId) && !original.Caravans.Any(o => o.Equals(c))))
            throw Invalid("rollback-source-historical-capture-changed");
        return original;
    }

    public async Task<OutpostRollbackFromViewBridge?> GetRollbackFromViewBridgeHeldAsync(OutpostLease lease, long playerId, SaveVersion target, CancellationToken ct)
    {
        if (!lease.CoversOwner(playerId)) throw new InvalidOperationException("outpost-lease-owner-mismatch");
        var candidates = await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == playerId && v.IsCurrent).Take(2).ToArrayAsync(ct);
        if (candidates.Length != 1 || !Same(candidates[0], target)) throw Invalid("rollback-download-cut-mismatch");
        var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == playerId, ct);
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var current = new OutpostScopeMessage { WorldInstanceId = head.WorldId, RestoreBoundary = head.RestoreBoundary,
            OwnerPlayerId = playerId, SaveGeneration = player.SaveGeneration, SaveWriteEpoch = player.SaveWriteEpoch };
        var original = ReadActualDownload(target).Outposts;
        if (!original.Present || original.Metadata.Scope.Equals(current)) return null;
        var rows = await db.OutpostLifecycleOperations.AsNoTracking().Where(r => r.Kind == "Rollback").ToArrayAsync(ct);
        var applicable = rows.Where(r => OutpostLifecycleEvidence.Intent(r).Rollback!.Kind == "ProtectedSnapshot"
            && OutpostScopeMessage.Parser.ParseFrom(OutpostLifecycleEvidence.Intent(r).ResultScopes![0].Scope).Equals(current)).ToArray();
        if (applicable.Length == 0) return null;
        if (applicable.Length != 1) throw Invalid("rollback-download-authorization-ambiguous");
        return (await CreateRollbackSourceBridgeAsync(applicable[0].Id, target, current, true, ct)).View;
    }
}
