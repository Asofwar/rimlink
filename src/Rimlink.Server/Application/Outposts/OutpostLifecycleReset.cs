using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Influence;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostLifecycle
{
    // The cleanup callback borrows this exact barrier, never acquires another owner/storage/authority lock.
    internal Task<bool> TryResetAsync(long playerId, string actor, Func<OutpostLease, Task> cleanup,
        Func<CancellationToken, Task> stage, CancellationToken ct) =>
        TryResetCoreAsync(playerId, actor, cleanup, stage, ct);

    internal Task<bool> TryRecreateAsync(string actor, Func<OutpostLease, Task> cleanup,
        Func<CancellationToken, Task> stage, CancellationToken ct) =>
        TryResetCoreAsync(0, actor, cleanup, stage, ct);

    private async Task<bool> TryResetCoreAsync(long playerId, string actor, Func<OutpostLease, Task> cleanup,
        Func<CancellationToken, Task> stage, CancellationToken ct)
    {
        if (!await authority.HasHistoryAsync(ct)) return false;
        bool recreate = playerId == 0;
        long[] owners = await db.Players.AsNoTracking().Select(p => p.Id).OrderBy(id => id).ToArrayAsync(ct);
        long[] targets = recreate ? owners : [playerId];
        await using var lease = await authority.AcquireAsync(owners, ct);
        var decision = await CheckResetHeldAsync(lease, targets, ct);
        if (!decision.Allowed) throw RpcErrors.Failed(ErrorCode.RecoveryRequired, RefusalKeys.OutpostsOperation, decision.Reason);
        var row = await PrepareResetHeldAsync(lease, targets, playerId, actor, ct);
        await RetainArchiveEvidenceHeldAsync(lease, row, ct);
        await cleanup(lease);
        await DestructiveHeldAsync(lease, row, async token =>
        {
            await InfluenceLifecycle.RevokePlayers(db, targets, token);
            await InfluenceLifecycle.Advance(db, token);
            await db.SaveVersions.Where(v => targets.Contains(v.PlayerId)).ExecuteDeleteAsync(token);
            await db.Players.Where(p => targets.Contains(p.Id)).ExecuteUpdateAsync(s => s
                .SetProperty(p => p.SaveGeneration, p => p.SaveGeneration + 1)
                .SetProperty(p => p.LastOutgoingTick, 0L).SetProperty(p => p.FirstSaveAt, (DateTime?)null)
                .SetProperty(p => p.WealthTotal, 0L).SetProperty(p => p.WealthItems, 0L), token);
            // Ordinary legacy mail keeps the 0.8.1 tick-reset behavior. Protected accepted receipts never become deliverable again.
            await db.Parcels.Where(p => targets.Contains(p.ToPlayerId) && p.DeliveredAt != null
                && !db.OutpostParcelLinks.Any(l => l.ParcelId == p.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.DeliveredTick, 0L), token);
            // RevokePlayers tracks these homes; mark those same entities deleted rather than bulk-deleting behind the tracker.
            db.WorldObjects.RemoveRange(await db.WorldObjects.Where(o => recreate || targets.Contains(o.OwnerPlayerId)).ToListAsync(token));
            await db.Set<ColonyMember>().Where(m => targets.Contains(m.OwnerPlayerId)).ExecuteDeleteAsync(token);
            if (recreate) db.World.RemoveRange(await db.World.ToListAsync(token));
            await stage(token);
        }, () => 0, ct);
        var intent = OutpostLifecycleEvidence.Intent(row);
        foreach (var file in intent.Files)
        {
            string source = Path.Combine(Path.GetDirectoryName(files.FullPath(file.PlayerId, 1))!, file.Name);
            // Only the verified source named by the immutable plan can be removed; never enumerate unrelated files.
            using (var stream = File.OpenRead(source))
                if (stream.Length != file.Length || !SHA256.HashData(stream).AsSpan().SequenceEqual(file.Sha256))
                    throw new OutpostEvidenceUnavailableException("lifecycle-source-cleanup-mismatch");
            File.Delete(source);
        }
        SourceCleanupCheckpoint();
        await CompleteArchiveHeldAsync(lease, row, 0, _ => Task.CompletedTask, ct);
        return true;
    }

    private async Task<OutpostLifecycleOperation> PrepareResetHeldAsync(OutpostLease lease, long[] targets, long playerId, string actor, CancellationToken ct)
    {
        lease.Require(authority);
        if (targets.Any(id => !lease.CoversOwner(id)) || lease.LifecycleOperationId is not null) throw new InvalidOperationException("lifecycle-owned-barrier-required");
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var scopes = new List<LifecycleOwner>(); var result = new List<LifecycleOwner>();
        foreach (long owner in lease.OwnerIds)
        {
            var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == owner, ct);
            var scope = new OutpostScopeMessage { WorldInstanceId = head.WorldId, OwnerPlayerId = owner,
                SaveGeneration = player.SaveGeneration, SaveWriteEpoch = player.SaveWriteEpoch, RestoreBoundary = head.RestoreBoundary };
            scopes.Add(new(scope.ToByteArray()));
            if (targets.Contains(owner))
            {
                if (scope.SaveGeneration == long.MaxValue) throw new OutpostEvidenceUnavailableException("lifecycle-generation-exhausted");
                scope.SaveGeneration++;
            }
            result.Add(new(scope.ToByteArray()));
        }
        var versions = await db.SaveVersions.AsNoTracking().Where(v => targets.Contains(v.PlayerId)).OrderBy(v => v.PlayerId).ThenBy(v => v.Number).ToArrayAsync(ct);
        var evidence = new Dictionary<(long PlayerId, string Name), LifecycleFile>();
        foreach (var version in versions)
        {
            _ = files.ReadEvidence(version);
            IEnumerable<string> paths = files.Locate(version.PlayerId, version.Number) switch
            {
                SaveForm.Full full => [full.Path], SaveForm.Delta delta => [delta.Path, delta.BasePath],
                _ => throw new OutpostEvidenceUnavailableException("lifecycle-source-bytes-missing")
            };
            foreach (string path in paths.Concat(files.Deltas(version.PlayerId, version.Number).SelectMany(d => new[] { d.Path, d.BasePath })).Distinct())
            {
                using var stream = File.OpenRead(path);
                if (stream.Length <= 0 || stream.Length > Rimlink.Protocol.Limits.MaxSaveBytes) throw new OutpostEvidenceUnavailableException("lifecycle-source-size-limit");
                evidence[(version.PlayerId, Path.GetFileName(path))] = new(version.PlayerId, Path.GetFileName(path), stream.Length, SHA256.HashData(stream));
            }
        }
        string id = Guid.NewGuid().ToString("D"); var cut = clock.Sample();
        var intent = new LifecycleIntent(1, id, playerId == 0 ? "Recreate" : "Reset", head.WorldId, head.RestoreBoundary, actor, head.HeadId, head.HeadDigest,
            lease.OwnerIds.ToArray(), scopes.ToArray(), Path.Combine("archive", "outpost-evidence", id), cut.Utc, cut, [],
            versions.Select(v => new LifecycleVersion(head.WorldId, v.PlayerId, new(v.Number, v.GameTick, v.Size, Convert.ToHexString(v.Sha256),
                v.CreatedAt, v.IsCurrent, v.DevMode, v.WealthTotal, v.WealthItems, v.SnapshotId, v.SaveGeneration, v.SaveWriteEpoch,
                v.RecoveryVersion, v.RawSha256, v.RecoveryJson))).ToArray(), evidence.Values.OrderBy(f => f.PlayerId).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray(),
            playerId, ResultScopes: result.ToArray());
        byte[] encoded = OutpostLifecycleEvidence.Encode(intent);
        var row = new OutpostLifecycleOperation { Id = id, WorldId = head.WorldId, Actor = actor, Kind = intent.Kind, RequestId = id,
            RestoreBoundary = head.RestoreBoundary, Phase = "Prepared", Intent = encoded, IntentDigest = SHA256.HashData(encoded), CreatedAt = cut.Utc };
        _ = OutpostLifecycleEvidence.Intent(row); // All size/shape/exhaustion checks precede the first protected journal write.
        lease.LifecycleOperationId = id;
        await PhaseHeldAsync(lease, row, null, "Prepared", 0, false, _ => { db.OutpostLifecycleOperations.Add(row); return Task.CompletedTask; }, ct);
        return row;
    }
}
