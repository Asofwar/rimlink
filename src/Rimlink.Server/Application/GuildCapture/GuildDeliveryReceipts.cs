using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal static partial class GuildSnapshotGuard
{
    internal static async Task ValidateReceiptAsync(RimlinkDbContext db, OutpostScopeMessage scope, GuildDeliveryReceipt receipt, CancellationToken ct,
        bool fromRestoreSource = false)
    {
        var attempt = await db.Set<GuildDeliveryAttempt>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == receipt.AttemptId, ct)
            ?? throw Invalid("guild-delivery-attempt-missing");
        if (attempt.RecipientId != scope.OwnerPlayerId || attempt.WorldId != scope.WorldInstanceId || attempt.SaveGeneration != scope.SaveGeneration
            || attempt.SaveWriteEpoch != scope.SaveWriteEpoch || attempt.Boundary != scope.RestoreBoundary && (!fromRestoreSource || attempt.AcceptedSnapshotId is null)
            || receipt.ParcelId != attempt.ParcelId
            || receipt.ClientRequestId != attempt.RequestId || receipt.AuthorizationTransitionId != attempt.Id
            || !receipt.AuthorizationIntentDigest.Span.SequenceEqual(attempt.IntentDigest)) throw Invalid("guild-delivery-receipt-scope-mismatch");
        var source = await db.OutpostTransitions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == attempt.Id, ct);
        var link = await db.Set<GuildParcelLink>().AsNoTracking().SingleOrDefaultAsync(p => p.ParcelId == receipt.ParcelId, ct);
        var before = receipt.BeforeSnapshot;
        if (source is null || source.Kind != "GuildBeginDelivery" || source.PlayerId != scope.OwnerPlayerId
            || !receipt.AuthorizationTransitionDigest.Span.SequenceEqual(source.Digest) || link is null || receipt.ClaimId != link.ClaimId
            || !receipt.PayloadDigest.Span.SequenceEqual(link.PayloadDigest) || before is null || before.SnapshotId != attempt.BeforeSnapshotId
            || !before.RawSha256.Span.SequenceEqual(attempt.BeforeSnapshotDigest) || before.Scope is null
            || before.Scope.PlayerId != attempt.RecipientId || before.Scope.WorldInstanceId != attempt.WorldId
            || before.Scope.SaveGeneration != attempt.SaveGeneration || before.Scope.SaveWriteEpoch != attempt.SaveWriteEpoch
            || before.Scope.RestoreBoundary != attempt.Boundary || link.AcceptedSnapshotId != attempt.AcceptedSnapshotId)
            throw Invalid("guild-delivery-receipt-mismatch");
        if (attempt.AcceptedSnapshotId is null)
        {
            if (!attempt.Active || attempt.Receipt is not null) throw Invalid("guild-delivery-attempt-not-active");
        }
        else
        {
            if (attempt.Active || attempt.Receipt is null || !attempt.Receipt.AsSpan().SequenceEqual(receipt.ToByteArray()))
                throw Invalid("guild-delivery-first-receipt-changed");
            var accepted = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(s => s.PlayerId == attempt.RecipientId
                && s.SnapshotId == attempt.AcceptedSnapshotId, ct) ?? throw Invalid("guild-delivery-accepted-snapshot-missing");
            var metadata = OutpostSaveMetadata.Parser.ParseFrom(accepted.Metadata);
            if (metadata.GuildMetadata is null || !metadata.GuildMetadata.Receipts.Any(r => r.Equals(receipt)))
                throw Invalid("guild-delivery-accepted-receipt-missing");
        }
    }

    internal static async Task<int> StageAcceptedAsync(RimlinkDbContext db, SaveVersion snapshot, OutpostSaveMetadata metadata, CancellationToken ct)
    {
        if (metadata.GuildMetadata is null) return 0;
        int accepted = 0;
        foreach (var receipt in metadata.GuildMetadata.Receipts)
        {
            var attempt = await db.Set<GuildDeliveryAttempt>().SingleAsync(a => a.Id == receipt.AttemptId, ct);
            if (attempt.AcceptedSnapshotId is not null) continue;
            var link = await db.Set<GuildParcelLink>().SingleAsync(p => p.ParcelId == receipt.ParcelId, ct);
            var claim = await db.Set<GuildResourceClaim>().SingleAsync(c => c.Id == link.ClaimId, ct);
            var parcel = await db.Parcels.SingleAsync(p => p.Id == link.ParcelId, ct);
            attempt.Active = false; attempt.AcceptedSnapshotId = snapshot.SnapshotId; attempt.Receipt = receipt.ToByteArray();
            link.AcceptedSnapshotId = snapshot.SnapshotId; claim.State = "Accepted";
            parcel.DeliveredAt = snapshot.CreatedAt; parcel.DeliveredTick = snapshot.GameTick;
            parcel.DeliveryRecoveryVersion = snapshot.RecoveryVersion; parcel.DeliveredGeneration = snapshot.SaveGeneration;
            accepted++;
        }
        await StageFamilyAsync(db, snapshot, metadata, ct);
        return accepted;
    }

    internal static async Task ValidateStoredMetadataAsync(RimlinkDbContext db, OutpostSaveMetadata metadata, CancellationToken ct,
        OutpostSaveMetadata? restoreSource = null)
    {
        if (metadata.GuildMetadata is null) return;
        foreach (var receipt in metadata.GuildMetadata.Receipts)
            await ValidateReceiptAsync(db, metadata.Scope, receipt, ct,
                restoreSource?.GuildMetadata?.Receipts.Any(old => old.Equals(receipt)) == true);
    }
}
