using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class PawnCustodyBridge
{
    private async Task ValidateRouteHeldAsync(OutpostLease lease, CustodyTransferEvidence evidence, CancellationToken ct)
    {
        if (evidence.Kind == CustodyTransferKind.CustodyTransferRaidReport && evidence.ActorPlayerId == evidence.SourceAuthorizerPlayerId)
        { await ValidateCapturedAsync(lease, evidence.ActorPlayerId, evidence.Pawns, evidence, false, ct); return; }
        if (evidence.Kind is not (CustodyTransferKind.CustodyTransferMarketBuy or CustodyTransferKind.CustodyTransferMarketReturn or CustodyTransferKind.CustodyTransferGiftReturn
            or CustodyTransferKind.CustodyTransferTradeReturn or CustodyTransferKind.CustodyTransferTradeFinish or CustodyTransferKind.CustodyTransferAdminRefund or CustodyTransferKind.CustodyTransferRaidReturn or CustodyTransferKind.CustodyTransferRaidReport or CustodyTransferKind.CustodyTransferLiveReturn))
        { await ValidateCapturedAsync(lease, evidence.ActorPlayerId, evidence.Pawns, evidence, false, ct); return; }
        if (!lease.CoversOwner(evidence.ActorPlayerId) || evidence.RecipientPlayerId > 0 && !lease.CoversOwner(evidence.RecipientPlayerId)) throw new InvalidOperationException("custody-owner-lease-required");
        if (evidence.Kind is CustodyTransferKind.CustodyTransferGiftReturn or CustodyTransferKind.CustodyTransferAdminRefund
            && await db.OutpostDeliveryAttempts.AnyAsync(a => a.ParcelId == evidence.SourceId && a.Active, ct))
            throw Invalid("custody-started-delivery-cannot-return");
        var proof = evidence.RemovalSnapshot;
        if (proof?.Scope is null || proof.Scope.OwnerPlayerId != evidence.ActorPlayerId || proof.RawSha256.Length != 32)
            throw Invalid("custody-escrow-proof");
        if (!await db.Players.AsNoTracking().AnyAsync(p => p.Id == evidence.ActorPlayerId && p.SaveGeneration == proof.Scope.SaveGeneration, ct))
            throw Invalid("custody-source-generation-changed");
        var link = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(l => l.PlayerId == evidence.ActorPlayerId && l.SnapshotId == proof.SnapshotId
            && l.WorldId == proof.Scope.WorldInstanceId && l.SaveGeneration == proof.Scope.SaveGeneration, ct);
        if (link is null || !link.RawSha256.AsSpan().SequenceEqual(proof.RawSha256.Span)) throw Invalid("custody-escrow-proof");
        foreach (var pawn in evidence.Pawns.Pawns.Where(p => p.Registry is not null))
        {
            var registry = pawn.Registry;
            var row = await db.PawnCustody.AsNoTracking().SingleOrDefaultAsync(p => p.Id == registry.PawnCustodyId, ct);
            bool giftReturn = evidence.Kind is CustodyTransferKind.CustodyTransferGiftReturn or CustodyTransferKind.CustodyTransferAdminRefund;
            bool trade = evidence.Kind is CustodyTransferKind.CustodyTransferTradeReturn or CustodyTransferKind.CustodyTransferTradeFinish;
            string location = giftReturn ? evidence.SourceId.ToString(System.Globalization.CultureInfo.InvariantCulture) :
                (trade ? "trade:" : evidence.Kind == CustodyTransferKind.CustodyTransferLiveReturn ? "live:" : evidence.Kind is CustodyTransferKind.CustodyTransferRaidReturn or CustodyTransferKind.CustodyTransferRaidReport ? "raid:" : "market:") + evidence.SourceId;
            if (registry.NonAuthoritativeMirror || row is null || row.PlayerId != evidence.ActorPlayerId || row.LocationKind != (giftReturn ? "TransferPending" : "Escrow")
                || row.LocationId != location || row.WorldId != proof.Scope.WorldInstanceId
                || row.SaveGeneration != proof.Scope.SaveGeneration || row.RestoreBoundary != proof.Scope.RestoreBoundary)
                throw Invalid("custody-escrow-location");
            var source = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == row.FrontierTransitionId, ct);
            var original = Parts(CustodyTransferEvidence.Parser.ParseFrom(source.Intent))
                .Single(p => p.Pawns.Pawns.Any(x => x.Registry?.PawnCustodyId == registry.PawnCustodyId));
            if (!original.Pawns.Pawns.Any(p => p.Equals(pawn)) || original.SourceId != evidence.SourceId || !original.RemovalSnapshot.Equals(proof))
                throw Invalid("custody-escrow-source");
        }
        if (evidence.RecipientPlayerId > 0 && evidence.RecipientPlayerId != evidence.ActorPlayerId) await RequireRecipientAsync(evidence.RecipientPlayerId, ct);
    }

    private async Task RequireRecipientAsync(long recipient, CancellationToken ct)
    {
        var session = sessions.ByPlayer(recipient);
        bool compatible = session is not null ? session.OutpostVersion == 1
            : SaveStore.RecoveryOf(await db.SaveVersions.AsNoTracking().SingleOrDefaultAsync(v => v.PlayerId == recipient && v.IsCurrent, ct)).Outposts.Present;
        if (!compatible) throw RpcErrors.Failed(ErrorCode.OutpostClientRequired, RefusalKeys.OutpostsClient, "custody-recipient-client-required");
    }

    private async Task StageHandoffAsync(OutpostLease lease, CustodyTransferEvidence evidence, CancellationToken ct)
    {
        if (!lease.CoversOwner(evidence.RecipientPlayerId)) throw new InvalidOperationException("custody-recipient-lease-required");
        var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == evidence.RecipientPlayerId, ct);
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var scope = new OutpostScopeMessage { WorldInstanceId = head.WorldId, OwnerPlayerId = player.Id, SaveGeneration = player.SaveGeneration,
            SaveWriteEpoch = player.SaveWriteEpoch, RestoreBoundary = head.RestoreBoundary };
        var payload = new OutpostParcelPayload { SchemaVersion = 1, Scope = scope, Kind = OutpostPayloadKind.Workers,
            BatchOperationId = evidence.TransitionId, ChildCount = 1 };
        var delivered = evidence.ResultPawns ?? evidence.Pawns;
        var tagged = delivered.Pawns.Where(p => p.Registry is not null).ToArray();
        payload.PawnCustodyIds.Add(tagged.Select(p => p.Registry.PawnCustodyId));
        payload.AssignmentIds.Add(tagged.Select(p => p.Registry.AssignmentId).Where(id => id.Length > 0));
        long parcelId = evidence.ParcelIds.Single();
        var parcel = await db.Parcels.AsNoTracking().SingleAsync(p => p.Id == parcelId, ct);
        OutpostParcelContents.Bind(payload, ItemList.Parser.ParseFrom(parcel.Items), PawnList.Parser.ParseFrom(parcel.Pawns), parcel.Silver);
        byte[] bytes = payload.ToByteArray();
        db.OutpostParcelLinks.Add(new OutpostParcelLink { ParcelId = parcelId, WorldId = scope.WorldInstanceId, PlayerId = player.Id,
            SaveGeneration = player.SaveGeneration, RestoreBoundary = scope.RestoreBoundary, BatchId = evidence.TransitionId,
            Payload = bytes, PayloadDigest = SHA256.HashData(bytes), TransitionId = evidence.TransitionId,
            ValueX100 = (long)Math.Clamp(delivered.Pawns.Sum(p => (decimal)p.MarketValueX100), 0, long.MaxValue) });
        foreach (var pawn in tagged)
        {
            var row = await db.PawnCustody.SingleAsync(p => p.Id == pawn.Registry.PawnCustodyId, ct); await db.Entry(row).ReloadAsync(ct);
            row.LocationKind = evidence.ActorPlayerId == evidence.RecipientPlayerId ? "ReturnPending" : "TransferPending"; row.LocationId = parcelId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            row.Tombstone = true; row.FrontierTransitionId = evidence.TransitionId; row.Revision++;
        }
    }

    internal static async Task<bool> IsHandoffSourceAsync(Storage.RimlinkDbContext db, OutpostTransition source, OutpostParcelLink parcel, CancellationToken ct)
    {
        if (source.Kind != "CustodyTransfer") return false;
        foreach (var evidence in Parts(CustodyTransferEvidence.Parser.ParseFrom(source.Intent)))
            if (evidence.RecipientPlayerId == parcel.PlayerId && evidence.ParcelIds.Contains(parcel.ParcelId)
                && evidence.RemovalSnapshot.Scope.WorldInstanceId == parcel.WorldId && await SourceMatchesAsync(db, evidence, ct)) return true;
        return false;
    }
}
