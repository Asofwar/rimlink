using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostLifecycle
{
    internal async Task<bool> TryBindWorldAsync(Storage.Entities.World world, string actor, CancellationToken ct)
    {
        if (!await authority.HasHistoryAsync(ct)) return false;
        long[] owners = await db.Players.AsNoTracking().Select(p => p.Id).OrderBy(id => id).ToArrayAsync(ct);
        await using var lease = await authority.AcquireAsync(owners, ct);
        if (await db.World.AnyAsync(ct)) throw RpcErrors.Failed(ErrorCode.Unspecified, RefusalKeys.WorldAlreadyExists,
            "the world already exists; recreate it through the admin tools");
        if ((await authority.CheckHeldAsync(lease, ct)).State != OutpostStartupState.NoActiveWorld)
            throw RpcErrors.Failed(ErrorCode.RecoveryRequired, RefusalKeys.OutpostsOperation, "lifecycle-successor-binding-not-ready");
        var codec = new OutpostLifecycleEvidence(db, files);
        var retirement = await codec.LatestWorldOperationAsync(ct);
        if (retirement is not { Kind: "Recreate", Phase: "Completed" })
            throw new OutpostEvidenceUnavailableException("lifecycle-completed-retirement-required");
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var retired = OutpostLifecycleEvidence.Decode<LifecycleProgress>(retirement.Progress);
        var source = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == retired.TransitionId, ct);
        var scopes = new List<LifecycleOwner>(); var result = new List<LifecycleOwner>();
        foreach (long owner in owners)
        {
            var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == owner, ct);
            var scope = new OutpostScopeMessage { WorldInstanceId = head.WorldId, OwnerPlayerId = owner,
                SaveGeneration = player.SaveGeneration, SaveWriteEpoch = player.SaveWriteEpoch, RestoreBoundary = head.RestoreBoundary };
            scopes.Add(new(scope.ToByteArray())); scope.WorldInstanceId = world.InfluenceInstanceId;
            result.Add(new(scope.ToByteArray()));
        }
        // World is a single-row planet; the non-reused instance id, not this local row key, owns its namespace.
        world.Id = 1;
        string id = Guid.NewGuid().ToString("D"), transitionId = Guid.NewGuid().ToString("D"); var cut = clock.Sample();
        var binding = new LifecycleBinding(head.InstallationId, retirement.Id, retirement.IntentDigest, source.Digest,
            new(world.Id, world.InfluenceInstanceId, world.Seed, world.Params, world.NpcTiles, world.CreatedAt));
        var intent = new LifecycleIntent(1, id, "WorldBind", head.WorldId, head.RestoreBoundary, actor, head.HeadId, head.HeadDigest,
            owners, scopes.ToArray(), Path.Combine("archive", "outpost-evidence", id), cut.Utc, cut, [], [], [], 0,
            ResultScopes: result.ToArray(), Binding: binding);
        byte[] encoded = OutpostLifecycleEvidence.Encode(intent);
        var row = new OutpostLifecycleOperation { Id = id, WorldId = head.WorldId, Actor = actor, Kind = "WorldBind", RequestId = id,
            RestoreBoundary = head.RestoreBoundary, Phase = "Completed", Intent = encoded, IntentDigest = SHA256.HashData(encoded), CreatedAt = cut.Utc,
            Progress = OutpostLifecycleEvidence.Encode(new LifecycleProgress(1, "Completed", transitionId, null, [], 0, true)),
            Result = OutpostLifecycleEvidence.Encode(new LifecycleResult(1, "WorldBind", 0)) };
        _ = OutpostLifecycleEvidence.Intent(row);
        lease.LifecycleOperationId = id;
        await using (var transaction = await authority.BeginLifecycleHeldAsync(lease,
            new(transitionId, head.WorldId, 0, 0, head.RestoreBoundary, "WorldBind", actor,
                new(head.HeadId, ByteString.CopyFrom(head.HeadDigest)), ByteString.Empty), new(id, null, row.IntentDigest, "Completed"), ct))
        {
            db.World.Add(world); db.OutpostLifecycleOperations.Add(row);
            await transaction.CompleteAsync(ByteString.CopyFrom(OutpostLifecycleEvidence.Encode(
                new LifecycleTransition(1, id, row.IntentDigest, "Completed", null, [], 0, true))), ct);
        }
        if ((await authority.CheckHeldAsync(lease, CancellationToken.None)).State != OutpostStartupState.Ready)
            throw new OutpostEvidenceUnavailableException("lifecycle-successor-binding-not-ready");
        return true;
    }
}
