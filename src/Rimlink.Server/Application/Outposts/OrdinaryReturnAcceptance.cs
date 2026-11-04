using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostSnapshotGuard
{
    private static OrdinaryCancelRequest ReadOrdinaryReturnIntent(OutpostOrdinaryReturn row)
    {
        if (row.Intent.Length is < 1 or > Limits.MaxMessageBytes) throw Unavailable("ordinary-return-stored-size");
        using var input = CodedInputStream.CreateWithLimits(new MemoryStream(row.Intent, false), Limits.MaxMessageBytes, 32);
        try { return OrdinaryCancelRequest.Parser.ParseFrom(input); }
        catch (InvalidProtocolBufferException) { throw Unavailable("ordinary-return-stored-intent"); }
    }

    private async Task ValidateOrdinaryReturnReceiptAsync(OutpostSaveMetadata metadata, OutpostTransitionReceipt receipt,
        OutpostTransition transition, CancellationToken ct)
    {
        var row = await db.OutpostOrdinaryReturns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == receipt.OrdinaryReturnAttemptId, ct);
        if (row is null || transition.Kind != "OrdinaryReturnStarted" || row.Id != transition.Id)
            throw Unavailable("ordinary-return-receipt-source");
        var intent = ReadOrdinaryReturnIntent(row); var capture = intent.Capture;
        if (!metadata.Scope.Equals(intent.Scope) || !receipt.Snapshot.Equals(intent.CanonicalSnapshot)
            || receipt.ClientRequestId != capture.ClientRequestId || receipt.OrdinaryLocalOperationId != capture.LocalOperationId
            || !SHA256.HashData(row.Intent).AsSpan().SequenceEqual(receipt.IntentDigest.Span)
            || receipt.DeliveryAttemptId.Length != 0 || receipt.NoRemovalBranchId.Length != 0
            || receipt.CancellationKind != OutpostCancelKind.Unspecified || receipt.ParcelIds.Count != 0
            || !receipt.PawnCustodyIds.Order().SequenceEqual(capture.Pawns.Select(p => p.Pawn.Registry.PawnCustodyId).Order())
            || !receipt.AssignmentIds.Order().SequenceEqual(capture.Pawns.Select(p => p.Pawn.Registry.AssignmentId).Where(id => id.Length > 0).Distinct().Order()))
            throw Unavailable("ordinary-return-receipt-mismatch");
        if (row.State == "Started" && row.AcceptedSnapshotId is null)
        {
            foreach (var pawn in capture.Pawns)
                RequireReturnedEnvelope(metadata, pawn.Pawn.Registry);
        }
        else if (row.State == "Accepted" && row.AcceptedSnapshotId is not null)
        {
            var accepted = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(l => l.PlayerId == row.PlayerId
                && l.WorldId == row.WorldId && l.SaveGeneration == row.SaveGeneration && l.SaveWriteEpoch == row.SaveWriteEpoch
                && l.RestoreBoundary == row.RestoreBoundary && l.SnapshotId == row.AcceptedSnapshotId, ct);
            if (accepted is null || !ParseMetadata(accepted.Metadata).Receipts.Any(r => r.Equals(receipt)))
                throw Unavailable("ordinary-return-acceptance-missing");
        }
        else throw Unavailable("ordinary-return-receipt-state");
    }

    private static void RequireReturnedEnvelope(OutpostSaveMetadata metadata, PawnRegistryEnvelope original)
    {
        var restored = metadata.Registry.SingleOrDefault(r => r.PawnCustodyId == original.PawnCustodyId && !r.NonAuthoritativeMirror);
        if (restored is null || restored.Tombstone || restored.PawnReferenceToken.Length == 0
            || restored.AssignmentId != original.AssignmentId || !restored.Scope.Equals(original.Scope))
            throw Unavailable("ordinary-return-registry-mismatch");
    }

    private static void RequireReturnedSnapshot(SaveRecoveryData recovery, OrdinaryCancelRequest intent)
    {
        if (!recovery.Outposts.Present || !recovery.Outposts.Metadata.Scope.Equals(intent.Scope)
            || !recovery.Removed.Contains(intent.Capture.LocalOperationId) || !recovery.Returned.Contains(intent.Capture.LocalOperationId)
            || !recovery.Outposts.Metadata.OrdinaryOutgoing.Any(c => c.Equals(intent.Capture)))
            throw Unavailable("ordinary-return-returned-proof-missing");
        foreach (var pawn in intent.Capture.Pawns) RequireReturnedEnvelope(recovery.Outposts.Metadata, pawn.Pawn.Registry);
    }

    private async Task StageOrdinaryReturnsAsync(OutpostLease lease, SaveVersion snapshot, OutpostSaveMetadata metadata,
        string transitionId, CancellationToken ct)
    {
        foreach (var receipt in metadata.Receipts.Where(r => r.OrdinaryReturnAttemptId.Length > 0))
        {
            var row = await db.OutpostOrdinaryReturns.SingleAsync(r => r.Id == receipt.OrdinaryReturnAttemptId, ct);
            if (row.State == "Accepted") continue; // Retained receipts authorize ACK-only, never a second import.
            RequireReturnedSnapshot(SaveStore.RecoveryOf(snapshot), ReadOrdinaryReturnIntent(row));
            row.State = "Accepted"; row.AcceptedSnapshotId = snapshot.SnapshotId;
            foreach (string id in receipt.PawnCustodyIds)
            {
                var custody = await db.PawnCustody.SingleAsync(p => p.Id == id, ct);
                await db.Entry(custody).ReloadAsync(ct); RequireOwner(lease, custody.PlayerId);
                if (custody.LocationKind != "LocalReturnPending" || custody.LocationId != row.Id || !custody.Tombstone)
                    throw Unavailable("ordinary-return-custody-conflict");
                var envelope = metadata.Registry.Single(r => r.PawnCustodyId == id && !r.NonAuthoritativeMirror);
                custody.LocationKind = "Home"; custody.LocationId = envelope.PawnReferenceToken; custody.Tombstone = false;
                custody.FrontierTransitionId = transitionId; custody.Revision++;
            }
        }
    }
}
