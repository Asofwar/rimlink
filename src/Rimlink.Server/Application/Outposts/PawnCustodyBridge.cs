using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using TransferKind = Rimlink.Protocol.V1.CustodyTransferKind;

namespace Rimlink.Server.Application.Outposts;

/// <summary>Validates real captured lineage and stages only inside the caller's already-held authority transaction.</summary>
public sealed partial class PawnCustodyBridge(RimlinkDbContext db, OutpostAuthority authority, SessionRegistry sessions,
    SaveFiles files, Application.World.ColonyLocks owners)
{
    public Task ValidateOutgoingHeldAsync(OutpostLease lease, long actorId, PawnList pawns,
        CustodyTransferEvidence evidence, CancellationToken ct) =>
        ValidateCapturedAsync(lease, actorId, pawns, evidence, checkReadiness: true, ct);

    private async Task ValidateCapturedAsync(OutpostLease lease, long actorId, PawnList pawns,
        CustodyTransferEvidence evidence, bool checkReadiness, CancellationToken ct)
    {
        lease.Require(authority);
        if (!lease.CoversOwner(actorId)) throw new InvalidOperationException("custody-owner-lease-required");
        if (pawns.Pawns.Count == 0) return;
        bool retained = await db.PawnCustody.AsNoTracking().AnyAsync(p => p.PlayerId == actorId, ct);
        if (!retained && pawns.Pawns.All(p => p.Registry is null)) return; // Untagged legacy path.
        if (pawns.CalculateSize() > Limits.EconomyMaxParcelBytes || evidence.ActorPlayerId != actorId
            || !evidence.Pawns.Equals(pawns) || !Enum.IsDefined(evidence.Kind) || (int)evidence.Kind == 0)
            throw Invalid("custody-transfer-shape");
        if (checkReadiness && (await authority.CheckHeldAsync(lease, ct)).State != OutpostStartupState.Ready)
            throw Invalid("custody-authority-not-ready");
        var proof = evidence.RemovalSnapshot;
        var scope = proof?.Scope;
        var player = await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.Id == actorId, ct);
        var anchor = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        if (scope is null || player is null || scope.WorldInstanceId != anchor.WorldId || scope.OwnerPlayerId != actorId
            || scope.SaveGeneration != player.SaveGeneration || scope.SaveWriteEpoch != player.SaveWriteEpoch
            || scope.RestoreBoundary != anchor.RestoreBoundary || proof!.RawSha256.Length != 32)
            throw Invalid("custody-removal-scope");
        var latest = await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == actorId).OrderByDescending(v => v.Number).FirstOrDefaultAsync(ct);
        var recovery = SaveStore.RecoveryOf(latest);
        var link = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(l => l.WorldId == scope.WorldInstanceId
            && l.PlayerId == actorId && l.SaveGeneration == scope.SaveGeneration && l.SnapshotId == proof.SnapshotId, ct);
        if (latest?.SnapshotId != proof.SnapshotId || link is null || !link.RawSha256.AsSpan().SequenceEqual(proof.RawSha256.Span)
            || link.SaveWriteEpoch != scope.SaveWriteEpoch || link.RestoreBoundary != scope.RestoreBoundary || !recovery.Outposts.Present)
            throw Invalid("custody-removal-snapshot");
        bool mirror = evidence.NonAuthoritativeMirror;
        if (mirror && evidence.Kind is not (TransferKind.CustodyTransferRaidLaunch or TransferKind.CustodyTransferLiveLaunch))
            throw Invalid("custody-mirror-route");
        OrdinaryOutgoingCapture? ordinary = null;
        if (!mirror && pawns.Pawns.Any(p => p.Registry is not null))
        {
            if (!OrdinaryRouteMatches(evidence.Kind, evidence.OrdinaryRequestKind)) throw Invalid("custody-ordinary-route-mismatch");
            ordinary = RequireOrdinaryCapture(recovery, evidence.OrdinaryRequestKind, evidence.ClientRequestId, evidence.SourceRequestIntent);
            if (evidence.OrdinaryCapture is null || !ordinary.Equals(evidence.OrdinaryCapture)
                || !ordinary.Scope.Equals(scope) || !ordinary.Pawns.Select(p => p.Pawn).SequenceEqual(pawns.Pawns.Where(p => p.Registry is not null)))
                throw Invalid("custody-ordinary-capture-mismatch");
        }
        var captures = recovery.Outposts.Metadata.WorkerCaptures;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pawn in pawns.Pawns)
        {
            if (pawn.Registry is not { } registry)
            {
                byte[] identity = BlobIdentity(pawn);
                if (captures.Any(c => BlobIdentity(c.Pawn).AsSpan().SequenceEqual(identity))
                    || recovery.Outposts.Metadata.OrdinaryOutgoing.SelectMany(c => c.Pawns).Any(c => BlobIdentity(c.Pawn).AsSpan().SequenceEqual(identity)))
                    throw Invalid("custody-known-envelope-missing");
                continue;
            }
            if (registry.SchemaVersion != 1 || !Guid.TryParseExact(registry.PawnCustodyId, "D", out _) || !ids.Add(registry.PawnCustodyId)
                || registry.NonAuthoritativeMirror != mirror || registry.Tombstone)
                throw Invalid("custody-envelope-shape");
            var custody = await db.PawnCustody.AsNoTracking().SingleOrDefaultAsync(p => p.Id == registry.PawnCustodyId, ct);
            var recorded = recovery.Outposts.Metadata.Registry.SingleOrDefault(e => e.PawnCustodyId == registry.PawnCustodyId && !e.NonAuthoritativeMirror);
            var capture = captures.SingleOrDefault(c => c.PawnCustodyId == registry.PawnCustodyId);
            var expected = mirror ? capture?.Pawn.Clone() : ordinary?.Pawns.SingleOrDefault(c => c.Pawn.Registry.PawnCustodyId == registry.PawnCustodyId)?.Pawn;
            if (mirror && expected?.Registry is not null) expected.Registry.NonAuthoritativeMirror = true;
            if (custody is null || recorded is null || expected is null || !expected.Equals(pawn)
                || custody.PlayerId != actorId || custody.WorldId != scope.WorldInstanceId || custody.SaveGeneration != scope.SaveGeneration
                || custody.RestoreBoundary != scope.RestoreBoundary || !registry.Scope.Equals(recorded.Scope)
                || registry.AssignmentId != (custody.AssignmentId ?? "") || custody.LocationKind != "Home"
                || recorded.Tombstone == mirror || custody.Tombstone == mirror
                || registry.PawnReferenceToken != custody.LocationId
                || recorded.PawnReferenceToken != registry.PawnReferenceToken || recorded.AssignmentId != registry.AssignmentId)
                throw Invalid("custody-capture-or-location-mismatch");
        }
        if (ids.Count > 0 && evidence.RecipientPlayerId > 0 && evidence.RecipientPlayerId != actorId)
        {
            if (!lease.CoversOwner(evidence.RecipientPlayerId)) throw new InvalidOperationException("custody-recipient-lease-required");
            var session = sessions.ByPlayer(evidence.RecipientPlayerId);
            bool compatible = session is not null ? session.OutpostVersion == 1
                : SaveStore.RecoveryOf(await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == evidence.RecipientPlayerId)
                    .OrderByDescending(v => v.Number).FirstOrDefaultAsync(ct)).Outposts.Present;
            if (!compatible) throw RpcErrors.Failed(ErrorCode.OutpostClientRequired, RefusalKeys.OutpostsClient, "custody-recipient-client-required");
        }
    }

    public async Task StageTransferHeldAsync(OutpostLease lease, CustodyTransferEvidence evidence, CancellationToken ct)
    {
        lease.Require(authority);
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("custody-caller-transaction-required");
        // Outer preflight reconciles readiness before BEGIN. Staging must not set SQLite synchronous inside its caller transaction.
        await ValidateRouteHeldAsync(lease, evidence, ct);
        if (!Guid.TryParseExact(evidence.TransitionId, "D", out _) || !await SourceMatchesAsync(db, evidence, ct))
            throw Invalid("custody-existing-source-required");
        if (evidence.Kind is TransferKind.CustodyTransferRaidReport or TransferKind.CustodyTransferLiveReturn && evidence.RecipientPlayerId == 0)
        {
            foreach (var pawn in evidence.Pawns.Pawns.Where(p => p.Registry is not null))
            {
                var dead = await db.PawnCustody.SingleAsync(p => p.Id == pawn.Registry.PawnCustodyId, ct); await db.Entry(dead).ReloadAsync(ct);
                dead.LocationKind = "Dead"; dead.LocationId = (evidence.Kind == TransferKind.CustodyTransferLiveReturn ? "live:" : "raid:") + evidence.SourceId; dead.Tombstone = true;
                dead.FrontierTransitionId = evidence.TransitionId; dead.Revision++;
            }
            return;
        }
        if (evidence.NonAuthoritativeMirror || evidence.Kind == TransferKind.CustodyTransferTradeFinish && evidence.ParcelIds.Count == 0) return;
        if (evidence.Kind is TransferKind.CustodyTransferSend or TransferKind.CustodyTransferMarketBuy or TransferKind.CustodyTransferMarketReturn or TransferKind.CustodyTransferGiftReturn
            or TransferKind.CustodyTransferTradeReturn or TransferKind.CustodyTransferTradeFinish or TransferKind.CustodyTransferAdminRefund or TransferKind.CustodyTransferRaidReturn or TransferKind.CustodyTransferRaidReport or TransferKind.CustodyTransferLiveReturn)
        { await StageHandoffAsync(lease, evidence, ct); return; }
        if (evidence.Kind is not (TransferKind.CustodyTransferMarketPost or TransferKind.CustodyTransferTradeSide or TransferKind.CustodyTransferRaidLaunch or TransferKind.CustodyTransferLiveLaunch)) throw Invalid("custody-route-not-ready");
        foreach (var pawn in evidence.Pawns.Pawns.Where(p => p.Registry is not null))
        {
            var row = await db.PawnCustody.SingleAsync(p => p.Id == pawn.Registry.PawnCustodyId, ct);
            await db.Entry(row).ReloadAsync(ct);
            row.LocationKind = "Escrow"; row.LocationId = (evidence.Kind == TransferKind.CustodyTransferTradeSide ? "trade:" : evidence.Kind == TransferKind.CustodyTransferRaidLaunch ? "raid:" : evidence.Kind == TransferKind.CustodyTransferLiveLaunch ? "live:" : "market:") + evidence.SourceId;
            row.Tombstone = true; row.FrontierTransitionId = evidence.TransitionId; row.Revision++;
        }
    }

    internal static async Task ValidateStoredEvidenceAsync(RimlinkDbContext db, IEnumerable<OutpostTransition> transitions, CancellationToken ct)
    {
        foreach (var transition in transitions.Where(t => t.Kind == "CustodyTransfer"))
        {
            try
            {
                if (transition.Intent.Length > Limits.MaxMessageBytes) throw new InvalidDataException();
                using var input = CodedInputStream.CreateWithLimits(new MemoryStream(transition.Intent, writable: false), Limits.MaxMessageBytes, 32);
                var evidence = CustodyTransferEvidence.Parser.ParseFrom(input);
                if (evidence.TransitionId != transition.Id || evidence.ActorPlayerId != transition.PlayerId
                    || evidence.RemovalSnapshot?.Scope?.SaveGeneration != transition.SaveGeneration) throw new InvalidDataException();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var part in Parts(evidence))
                {
                    var proof = part.RemovalSnapshot;
                    var link = proof?.Scope is null ? null : await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(l =>
                        l.WorldId == proof.Scope.WorldInstanceId && l.PlayerId == part.ActorPlayerId
                        && l.SaveGeneration == proof.Scope.SaveGeneration && l.SnapshotId == proof.SnapshotId, ct);
                    if (link is null || transition.WorldId != link.WorldId || transition.RestoreBoundary != link.RestoreBoundary
                        || !link.RawSha256.AsSpan().SequenceEqual(proof!.RawSha256.Span) || !await SourceMatchesAsync(db, part, ct)) throw new InvalidDataException();
                    foreach (var pawn in part.Pawns.Pawns.Where(p => p.Registry is not null))
                        if (!ids.Add(pawn.Registry.PawnCustodyId)) throw new InvalidDataException();
                }
            }
            catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException)
            { throw new OutpostEvidenceUnavailableException("custody-transfer-evidence-mismatch"); }
        }
    }

    private static async Task<bool> SourceMatchesAsync(RimlinkDbContext db, CustodyTransferEvidence evidence, CancellationToken ct)
    {
        if (evidence.Kind == TransferKind.CustodyTransferMarketPost)
        {
            var lot = await db.Lots.AsNoTracking().SingleOrDefaultAsync(l => l.Id == evidence.SourceId, ct);
            return lot is not null && lot.SellerPlayerId == evidence.ActorPlayerId && evidence.Pawns.Pawns.Count == 1 && lot.Pawn is not null
                && lot.Pawn.AsSpan().SequenceEqual(evidence.Pawns.Pawns[0].ToByteArray()) && lot.ClientRequestId == evidence.ClientRequestId
                && lot.RequestIntent is not null && lot.RequestIntent.AsSpan().SequenceEqual(evidence.SourceRequestIntent.Span)
                && evidence.SourceRequestIntent.Length == 32 && evidence.RecipientPlayerId == 0 && !evidence.NonAuthoritativeMirror;
        }
        if (evidence.Kind is TransferKind.CustodyTransferSend or TransferKind.CustodyTransferMarketBuy)
        {
            if (evidence.ParcelIds.Count != 1 || evidence.RecipientPlayerId <= 0 || evidence.NonAuthoritativeMirror) return false;
            var parcel = await db.Parcels.AsNoTracking().SingleOrDefaultAsync(p => p.Id == evidence.ParcelIds[0], ct);
            if (parcel is null || parcel.FromPlayerId != evidence.ActorPlayerId || parcel.ToPlayerId != evidence.RecipientPlayerId
                || !parcel.Pawns.AsSpan().SequenceEqual(evidence.Pawns.ToByteArray())) return false;
            if (evidence.Kind == TransferKind.CustodyTransferSend)
                return parcel.Id == evidence.SourceId && parcel.Kind == ParcelKindDb.Gift && parcel.ClientRequestId == evidence.ClientRequestId
                    && parcel.RequestIntent is not null && parcel.RequestIntent.AsSpan().SequenceEqual(evidence.SourceRequestIntent.Span);
            var lot = await db.Lots.AsNoTracking().SingleOrDefaultAsync(l => l.Id == evidence.SourceId, ct);
            return lot is not null && lot.Status == LotStatusDb.Sold && lot.SellerPlayerId == evidence.ActorPlayerId && lot.BuyerPlayerId == evidence.RecipientPlayerId
                && lot.Pawn is not null && evidence.Pawns.Pawns.Count == 1 && lot.Pawn.AsSpan().SequenceEqual(evidence.Pawns.Pawns[0].ToByteArray())
                && parcel.Kind == ParcelKindDb.Purchase;
        }
        if (evidence.Kind is TransferKind.CustodyTransferMarketReturn or TransferKind.CustodyTransferGiftReturn)
        {
            if (evidence.ParcelIds.Count != 1 || evidence.RecipientPlayerId != evidence.ActorPlayerId || evidence.NonAuthoritativeMirror) return false;
            var back = await db.Parcels.AsNoTracking().SingleOrDefaultAsync(p => p.Id == evidence.ParcelIds[0], ct);
            if (back is null || back.ToPlayerId != evidence.ActorPlayerId || !back.Pawns.AsSpan().SequenceEqual(evidence.Pawns.ToByteArray())) return false;
            if (evidence.Kind == TransferKind.CustodyTransferMarketReturn)
            {
                var lot = await db.Lots.AsNoTracking().SingleOrDefaultAsync(l => l.Id == evidence.SourceId, ct);
                return lot is not null && lot.SellerPlayerId == evidence.ActorPlayerId && lot.Status is LotStatusDb.Withdrawn or LotStatusDb.Expired
                    && lot.Pawn is not null && evidence.Pawns.Pawns.Count == 1 && lot.Pawn.AsSpan().SequenceEqual(evidence.Pawns.Pawns[0].ToByteArray())
                    && back.Kind == ParcelKindDb.LotReturn && back.FromPlayerId is null;
            }
            var original = await db.Parcels.AsNoTracking().SingleOrDefaultAsync(p => p.Id == evidence.SourceId, ct);
            return original is not null && original.FromPlayerId == evidence.ActorPlayerId && original.Decision == ParcelDecisionDb.Refused
                && original.DeliveredAt is not null && original.DeliveredTick == 0 && original.Kind == ParcelKindDb.Gift
                && original.Pawns.AsSpan().SequenceEqual(evidence.Pawns.ToByteArray()) && back.Kind == ParcelKindDb.GiftReturn && back.FromPlayerId == original.ToPlayerId;
        }
        if (evidence.Kind == TransferKind.CustodyTransferAdminRefund)
        {
            if (evidence.ParcelIds.Count != 1 || evidence.ActorPlayerId != evidence.RecipientPlayerId || evidence.NonAuthoritativeMirror) return false;
            var original = await db.Parcels.AsNoTracking().SingleOrDefaultAsync(p => p.Id == evidence.SourceId, ct);
            var back = await db.Parcels.AsNoTracking().SingleOrDefaultAsync(p => p.Id == evidence.ParcelIds[0], ct);
            return original is not null && original.Kind == ParcelKindDb.Gift && original.FromPlayerId == evidence.ActorPlayerId
                && original.DeliveredAt is not null && original.DeliveredTick == 0 && original.Pawns.AsSpan().SequenceEqual(evidence.Pawns.ToByteArray())
                && back is not null && back.Kind == ParcelKindDb.Admin && back.FromPlayerId is null && back.ToPlayerId == evidence.ActorPlayerId
                && back.Pawns.AsSpan().SequenceEqual(evidence.Pawns.ToByteArray())
                && await db.Transfers.AsNoTracking().AnyAsync(t => t.Kind == "refund" && t.FromPlayerId == original.ToPlayerId
                    && t.ToPlayerId == evidence.ActorPlayerId && t.RequestTargetId == original.Id, ct);
        }
        return await TradeSourceMatchesAsync(db, evidence, ct) || await RaidSourceMatchesAsync(db, evidence, ct) || await LiveSourceMatchesAsync(db, evidence, ct); // Each route requires its actual source codec.
    }

    private static byte[] BlobIdentity(PawnBlob pawn)
    {
        var copy = pawn.Clone(); copy.Registry = null;
        return SHA256.HashData(copy.ToByteArray());
    }
    private static global::Grpc.Core.RpcException Invalid(string reason) =>
        RpcErrors.Failed(ErrorCode.OutpostConflict, RefusalKeys.OutpostsOperation, reason);
}
