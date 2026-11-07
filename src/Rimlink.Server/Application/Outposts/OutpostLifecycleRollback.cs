using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostLifecycle
{
    public async Task<LifecycleDecision> CheckRollbackHeldAsync(OutpostLease lease, long playerId, SaveVersion target, CancellationToken ct)
    {
        lease.Require(authority);
        if (!lease.CoversOwner(playerId) || target.PlayerId != playerId) throw new InvalidOperationException("lifecycle-owner-lease-required");
        if ((await authority.CheckHeldAsync(lease, ct)).State != OutpostStartupState.Ready) return new(false, "lifecycle-authority-not-ready", []);
        authority.RequireRuntimeCustody();
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == playerId, ct);
        var current = new OutpostScopeMessage { WorldInstanceId = head.WorldId, OwnerPlayerId = playerId,
            SaveGeneration = player.SaveGeneration, SaveWriteEpoch = player.SaveWriteEpoch, RestoreBoundary = head.RestoreBoundary };
        if (player.SaveWriteEpoch == long.MaxValue || target.SaveGeneration != player.SaveGeneration || target.RawSha256?.Length != 32
            || target.SnapshotId is null || target.SaveWriteEpoch > player.SaveWriteEpoch
            || await db.ArchivedColonies.AnyAsync(a => a.PlayerId == playerId && a.SaveGeneration == player.SaveGeneration && a.State == ArchiveState.Archived, ct))
            return new(false, "lifecycle-rollback-target-scope", []);
        if (await db.OutpostOperations.AnyAsync(o => o.WorldId == head.WorldId && o.PlayerId == playerId && o.SaveGeneration == player.SaveGeneration
                && (o.State == "Prepared" || o.State == "CancelFenced"), ct)
            || await db.OutpostDeliveryAttempts.AnyAsync(a => a.PlayerId == playerId && a.WorldId == head.WorldId && a.SaveGeneration == player.SaveGeneration && a.Active, ct)
            || await db.OutpostOrdinaryReturns.AnyAsync(r => r.PlayerId == playerId && r.WorldId == head.WorldId && r.SaveGeneration == player.SaveGeneration && r.AcceptedSnapshotId == null, ct))
            return new(false, "lifecycle-rollback-unresolved", []);
        try
        {
            var codec = new OutpostLifecycleEvidence(db, files);
            if (await codec.HasLocalProtectedHistoryAsync(current, ct))
                await new OutpostSnapshotGuard(db, files).ValidateRollbackTargetHeldAsync(lease, playerId, target, current, ct);
            else
            {
                _ = codec.ReadLegacySnapshot(target, current.WorldInstanceId);
                var previous = await db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == playerId && v.IsCurrent, ct);
                if (previous.SaveGeneration != current.SaveGeneration || previous.SaveWriteEpoch > current.SaveWriteEpoch)
                    throw new OutpostEvidenceUnavailableException("lifecycle-legacy-current-scope");
                _ = codec.ReadLegacySnapshot(previous, current.WorldInstanceId);
            }
        }
        catch (Exception error) when (error is OutpostEvidenceUnavailableException or IOException or InvalidDataException)
        { return new(false, "lifecycle-rollback-target-conflict", []); }
        return new(true, "", [current]);
    }

    internal async Task<bool?> TryRollbackAsync(long playerId, int number, string actor, CancellationToken ct)
    {
        if (!await authority.HasHistoryAsync(ct)) return null;
        await using var lease = await authority.AcquireAsync([playerId], ct);
        var target = await db.SaveVersions.AsNoTracking().SingleOrDefaultAsync(v => v.PlayerId == playerId && v.Number == number, ct);
        if (target is null) return false;
        var decision = await CheckRollbackHeldAsync(lease, playerId, target, ct);
        if (!decision.Allowed) throw RpcErrors.Failed(ErrorCode.RecoveryRequired, RefusalKeys.OutpostsOperation, decision.Reason);
        var codec = new OutpostLifecycleEvidence(db, files);
        var scope = decision.Scopes.Single();
        var previous = await db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == playerId && v.IsCurrent, ct);
        var completed = (await db.OutpostLifecycleOperations.AsNoTracking().Where(r => r.Kind == "Rollback").ToListAsync(ct))
            .Where(r => OutpostLifecycleEvidence.Intent(r).PlayerId == playerId).ToList();
        if (completed.Count != 0)
        {
            var transitions = await db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
            var last = OutpostLifecycleEvidence.Intent(OutpostLifecycleEvidence.LatestForOwner(playerId, completed, transitions));
            var result = OutpostScopeMessage.Parser.ParseFrom(last.ResultScopes![0].Scope);
            bool sameTarget = last.Rollback!.Kind == "LegacySnapshot"
                ? last.Rollback.Legacy!.Target.Locator.Version.Number == target.Number && last.Rollback.Legacy.Target.Locator.Version.SnapshotId == target.SnapshotId
                : OutpostSnapshotProof.Parser.ParseFrom(last.Rollback.Target).SnapshotId == target.SnapshotId;
            if (target.IsCurrent && result.Equals(scope) && sameTarget) return true;
        }
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        bool legacy = !await codec.HasLocalProtectedHistoryAsync(scope, ct);
        LifecycleLegacyRollback? legacyEvidence = legacy ? new(codec.ReadLegacySnapshot(target, head.WorldId), codec.ReadLegacySnapshot(previous, head.WorldId)) : null;
        var resolver = new OutpostRestoreEvidence(db, files);
        var targetSource = legacy ? null : await resolver.ResolveRollbackSourceAsync(target, scope, ct);
        var previousSource = legacy ? null : await resolver.ResolveRollbackSourceAsync(previous, scope, ct);
        var originalScope = scope.Clone(); originalScope.SaveWriteEpoch = target.SaveWriteEpoch;
        var proof = targetSource?.Actual ?? new OutpostSnapshotProof { Scope = originalScope, SnapshotId = target.SnapshotId!, RawSha256 = ByteString.CopyFrom(target.RawSha256!) };
        var link = legacy ? null : await codec.RollbackLinkAsync(proof, ct);
        var previousScope = scope.Clone(); previousScope.SaveWriteEpoch = previous.SaveWriteEpoch;
        var prior = previousSource?.Actual ?? new OutpostSnapshotProof { Scope = previousScope, SnapshotId = previous.SnapshotId!, RawSha256 = ByteString.CopyFrom(previous.RawSha256!) };
        var resultScope = scope.Clone(); resultScope.SaveWriteEpoch++;
        string id = Guid.NewGuid().ToString("D"), transitionId = Guid.NewGuid().ToString("D"); var cut = clock.Sample();
        LifecycleVersion[] versions = legacyEvidence is null ? [] : new[] { legacyEvidence.Target.Locator, legacyEvidence.PreviousCurrent.Locator }.DistinctBy(v => v.Version.Number).ToArray();
        var retained = new Dictionary<string, LifecycleFile>(StringComparer.Ordinal);
        foreach (var version in legacy ? new[] { target, previous }.DistinctBy(v => v.Number) : [])
        {
            IEnumerable<string> paths = files.Locate(playerId, version.Number) switch
            {
                SaveForm.Full full => [full.Path], SaveForm.Delta delta => [delta.Path, delta.BasePath],
                _ => throw new OutpostEvidenceUnavailableException("lifecycle-legacy-source-missing")
            };
            foreach (string path in paths)
            {
                using var stream = File.OpenRead(path);
                retained[Path.GetFileName(path)] = new(playerId, Path.GetFileName(path), stream.Length, SHA256.HashData(stream));
            }
        }
        var intent = new LifecycleIntent(1, id, "Rollback", head.WorldId, head.RestoreBoundary, actor, head.HeadId, head.HeadDigest,
            [playerId], [new(scope.ToByteArray())], Path.Combine("archive", "outpost-evidence", id), cut.Utc, cut, [], versions, retained.Values.ToArray(), playerId,
            ResultScopes: [new(resultScope.ToByteArray())], Rollback: legacyEvidence is null
                ? new(proof.ToByteArray(), prior.ToByteArray(), link!.FrontierDigest, TargetSource: targetSource!.Authorization, PreviousSource: previousSource!.Authorization)
                : new([], [], [], "LegacySnapshot", legacyEvidence));
        byte[] encoded = OutpostLifecycleEvidence.Encode(intent);
        var row = new OutpostLifecycleOperation { Id = id, WorldId = head.WorldId, Actor = actor, Kind = "Rollback", RequestId = id,
            RestoreBoundary = head.RestoreBoundary, Phase = "Completed", Intent = encoded, IntentDigest = SHA256.HashData(encoded), CreatedAt = cut.Utc,
            Progress = OutpostLifecycleEvidence.Encode(new LifecycleProgress(1, "Completed", transitionId, null, [], 0, true)),
            Result = OutpostLifecycleEvidence.Encode(new LifecycleResult(1, "Rollback", 0)) };
        _ = OutpostLifecycleEvidence.Intent(row); lease.LifecycleOperationId = id;
        await using (var transaction = await authority.BeginLifecycleHeldAsync(lease,
            new(transitionId, head.WorldId, playerId, scope.SaveGeneration, head.RestoreBoundary, "Rollback", actor,
                new(head.HeadId, ByteString.CopyFrom(head.HeadDigest)), ByteString.Empty), new(id, null, row.IntentDigest, "Completed"), ct))
        {
            if (legacyEvidence is not null)
            {
                // Copy-on-write evidence only: no original row/file is changed before the sole protected commit.
                // An old-branch crash may leave an unreferenced unique directory, never a changed colony or a guessed replay.
                string root = Path.Combine(files.EvidenceArchiveDirectory(intent.Directory), playerId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(root);
                foreach (var file in intent.Files)
                {
                    string destination = Path.Combine(root, file.Name);
                    string source = Path.Combine(Path.GetDirectoryName(files.FullPath(playerId, target.Number))!, file.Name);
                    using var input = File.OpenRead(source); using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    input.CopyTo(output); output.Flush(flushToDisk: true);
                }
                await codec.VerifyLocators(intent, ct);
            }
            await db.SaveVersions.Where(v => v.PlayerId == playerId && v.IsCurrent).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, false), ct);
            await db.SaveVersions.Where(v => v.Id == target.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, true), ct);
            await db.Set<ColonyMember>().Where(m => m.OwnerPlayerId == playerId).ExecuteUpdateAsync(s => s.SetProperty(m => m.PawnIds, ""), ct);
            await db.Players.Where(p => p.Id == playerId).ExecuteUpdateAsync(s => s.SetProperty(p => p.SaveWriteEpoch, resultScope.SaveWriteEpoch)
                .SetProperty(p => p.LastOutgoingTick, p => p.LastOutgoingTick > target.GameTick ? target.GameTick : p.LastOutgoingTick), ct);
            if (target.WealthTotal > 0) await db.Players.Where(p => p.Id == playerId).ExecuteUpdateAsync(s => s
                .SetProperty(p => p.WealthTotal, target.WealthTotal).SetProperty(p => p.WealthItems, target.WealthItems), ct);
            db.OutpostLifecycleOperations.Add(row);
            await transaction.CompleteAsync(ByteString.CopyFrom(OutpostLifecycleEvidence.Encode(
                new LifecycleTransition(1, id, row.IntentDigest, "Completed", null, [], 0, true))), ct);
        }
        return true;
    }
}
