using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Application.Saves;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostLifecycleEvidence
{
    internal async Task<OutpostSaveMetadata?> ValidateRollbackProvenanceAsync(OutpostSaveMetadata metadata, CancellationToken ct)
    {
        var stored = await db.OutpostLifecycleOperations.AsNoTracking().Where(r => r.Kind == "Rollback").ToListAsync(ct);
        var rows = stored.Where(r => Intent(r).PlayerId == metadata.Scope.OwnerPlayerId && Intent(r).Rollback!.Kind == "ProtectedSnapshot").ToArray();
        if (metadata.RollbackFromViewBridge is not null) return null; // The separate typed source validator owns this root.
        if (metadata.RollbackBridge is not { } bridge)
        {
            if (rows.Any(r => OutpostScopeMessage.Parser.ParseFrom(Intent(r).ResultScopes![0].Scope).Equals(metadata.Scope)))
                throw Invalid("rollback-fresh-snapshot-bridge-missing");
            return null;
        }
        var row = rows.SingleOrDefault(r => r.Id == bridge.LifecycleOperationId);
        if (row is null) throw Invalid("rollback-snapshot-authorization-missing");
        var intent = Intent(row);
        if (intent.Rollback!.TargetSource is not null || bridge.SourceProvenance is not null || bridge.SourcePathDigest.Length != 0)
            return await new OutpostRestoreEvidence(db, files).ValidateRollbackGraphProvenanceAsync(metadata, ct);
        var transitions = await db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        await ValidateRollbackStoredAsync(row, intent, transitions, [], ct);
        var proof = OutpostSnapshotProof.Parser.ParseFrom(intent.Rollback!.Target);
        var current = OutpostScopeMessage.Parser.ParseFrom(intent.ResultScopes![0].Scope);
        var link = await RollbackLinkAsync(proof, ct); var resolved = await ResolveSnapshotAsync(link, ct);
        var recovery = SaveRecoveryData.ReadXml(new MemoryStream(files.ReadEvidence(resolved.Version, resolved.Archive), false), out byte[] hash);
        var original = recovery.Outposts.Metadata;
        if (!hash.AsSpan().SequenceEqual(proof.RawSha256.Span) || !original.ToByteArray().AsSpan().SequenceEqual(link.Metadata)
            || !link.FrontierDigest.AsSpan().SequenceEqual(intent.Rollback.FrontierDigest)) throw Invalid("rollback-snapshot-original-unavailable");
        var transition = transitions[Decode<LifecycleProgress>(row.Progress).TransitionId];
        var expected = new OutpostRollbackBridge { LifecycleOperationId = row.Id, TransitionId = transition.Id,
            TransitionDigest = ByteString.CopyFrom(transition.Digest), CanonicalSnapshot = proof, SourceScope = proof.Scope.Clone(),
            CurrentScope = current, LocalFrontierDigest = ByteString.CopyFrom(link.FrontierDigest),
            MutableRegistry = { original.Registry.Where(r => !r.NonAuthoritativeMirror && !r.Tombstone && r.Scope.Equals(proof.Scope)).Select(r => r.Clone()) } };
        if (!bridge.Equals(expected) || !current.Equals(metadata.Scope)) throw Invalid("rollback-snapshot-bridge-mismatch");
        if (original.OrdinaryOutgoing.Any(c => !metadata.OrdinaryOutgoing.Any(n => n.Equals(c)))
            || metadata.OrdinaryOutgoing.Any(c => !c.Scope.Equals(current) && !original.OrdinaryOutgoing.Any(o => o.Equals(c)))
            || metadata.WorkerCaptures.Any(c => !c.Pawn.Registry.Scope.Equals(current) && !original.WorkerCaptures.Any(o => o.Equals(c))))
            throw Invalid("rollback-snapshot-historical-capture-changed");
        return original;
    }

    public async Task<OutpostRollbackBridge?> GetRollbackBridgeHeldAsync(OutpostLease lease, long playerId, SaveVersion target, CancellationToken ct)
    {
        if (!lease.CoversOwner(playerId)) throw new InvalidOperationException("outpost-lease-owner-mismatch");
        var actual = await db.SaveVersions.AsNoTracking().SingleOrDefaultAsync(v => v.PlayerId == playerId && v.IsCurrent, ct);
        if (actual is null || target.PlayerId != playerId || actual.Id != target.Id || actual.SnapshotId != target.SnapshotId
            || actual.SaveGeneration != target.SaveGeneration || actual.SaveWriteEpoch != target.SaveWriteEpoch
            || actual.RawSha256 is null || target.RawSha256 is null || !actual.RawSha256.AsSpan().SequenceEqual(target.RawSha256))
            throw Invalid("rollback-download-cut-mismatch");
        var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == playerId, ct);
        if (target.SaveWriteEpoch == player.SaveWriteEpoch) return null;
        var rows = await db.OutpostLifecycleOperations.AsNoTracking().ToListAsync(ct);
        var own = rows.Where(r => r.Kind == "Rollback" && Intent(r).PlayerId == playerId).ToArray();
        if (own.Length == 0) throw Invalid("rollback-download-authorization-missing");
        var transitions = await db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        var row = LatestForOwner(playerId, own, transitions); var intent = Intent(row);
        if (intent.Rollback!.Kind == "LegacySnapshot")
        {
            await ValidateRollbackStoredAsync(row, intent, transitions, [], ct);
            var authorized = OutpostScopeMessage.Parser.ParseFrom(intent.ResultScopes![0].Scope);
            var authority = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
            var original = intent.Rollback.Legacy!.Target; var identity = original.Locator.Version;
            var observed = ReadLegacySnapshot(target, intent.WorldId);
            if (authorized.WorldInstanceId != authority.WorldId || authorized.RestoreBoundary != authority.RestoreBoundary
                || authorized.SaveGeneration != player.SaveGeneration || authorized.SaveWriteEpoch != player.SaveWriteEpoch
                || target.Number != identity.Number || target.SnapshotId != identity.SnapshotId || target.SaveGeneration != identity.SaveGeneration
                || target.SaveWriteEpoch != identity.SaveWriteEpoch || !observed.RawSha256.AsSpan().SequenceEqual(original.RawSha256))
                throw Invalid("rollback-legacy-download-authorization-mismatch");
            return null;
        }
        var proof = OutpostSnapshotProof.Parser.ParseFrom(intent.Rollback.Target);
        var current = OutpostScopeMessage.Parser.ParseFrom(intent.ResultScopes![0].Scope);
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        if (row.Phase != "Completed" || current.WorldInstanceId != head.WorldId || current.RestoreBoundary != head.RestoreBoundary
            || current.SaveGeneration != player.SaveGeneration || current.SaveWriteEpoch != player.SaveWriteEpoch
            || proof.SnapshotId != target.SnapshotId || proof.Scope.SaveGeneration != target.SaveGeneration
            || proof.Scope.SaveWriteEpoch != target.SaveWriteEpoch || !proof.RawSha256.Span.SequenceEqual(target.RawSha256))
            throw Invalid("rollback-download-authorization-mismatch");
        if (intent.Rollback.TargetSource is not null)
            return (await new OutpostRestoreEvidence(db, files).CreateRollbackSourceBridgeAsync(row.Id, target, current, true, ct)).Actual;
        var link = await RollbackLinkAsync(proof, ct);
        var resolved = await ResolveSnapshotAsync(link, ct);
        var recovery = SaveRecoveryData.ReadXml(new MemoryStream(files.ReadEvidence(resolved.Version, resolved.Archive), false), out byte[] hash);
        if (!hash.AsSpan().SequenceEqual(proof.RawSha256.Span) || !recovery.Outposts.Metadata.Scope.Equals(proof.Scope)
            || !recovery.Outposts.Metadata.ToByteArray().AsSpan().SequenceEqual(link.Metadata)) throw Invalid("rollback-download-bytes-mismatch");
        var progress = Decode<LifecycleProgress>(row.Progress); var transition = transitions[progress.TransitionId];
        var bridge = new OutpostRollbackBridge { LifecycleOperationId = row.Id, TransitionId = transition.Id,
            TransitionDigest = ByteString.CopyFrom(transition.Digest), CanonicalSnapshot = proof, SourceScope = proof.Scope.Clone(),
            CurrentScope = current, LocalFrontierDigest = ByteString.CopyFrom(link.FrontierDigest) };
        var custody = await db.PawnCustody.AsNoTracking().Where(p => p.PlayerId == playerId).ToDictionaryAsync(p => p.Id, ct);
        foreach (var entry in recovery.Outposts.Metadata.Registry.Where(r => !r.NonAuthoritativeMirror && !r.Tombstone && r.Scope.Equals(proof.Scope)))
        {
            if (!custody.TryGetValue(entry.PawnCustodyId, out var pawn) || pawn.Tombstone || pawn.LocationKind != "Home"
                || pawn.WorldId != proof.Scope.WorldInstanceId || pawn.SaveGeneration != proof.Scope.SaveGeneration
                || pawn.RestoreBoundary != proof.Scope.RestoreBoundary || pawn.AssignmentId != entry.AssignmentId
                || pawn.LocationId != entry.PawnReferenceToken) throw Invalid("rollback-download-live-registry-mismatch");
            bridge.MutableRegistry.Add(entry.Clone());
        }
        return bridge;
    }
}
