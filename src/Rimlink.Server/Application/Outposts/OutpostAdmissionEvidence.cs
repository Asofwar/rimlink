using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostSnapshotGuard
{
    private async Task ValidateAdmissionReceiptAsync(OutpostSaveMetadata metadata, OutpostTransitionReceipt receipt,
        OutpostTransition transition, CancellationToken ct, bool historicalRollback = false)
    {
        var operation = await db.OutpostOperations.AsNoTracking().SingleOrDefaultAsync(o => o.WorldId == transition.WorldId
            && o.PlayerId == transition.PlayerId && o.SaveGeneration == transition.SaveGeneration && o.RestoreBoundary == transition.RestoreBoundary
            && o.RequestId == receipt.ClientRequestId, ct);
        if (operation is null || !operation.IntentDigest.AsSpan().SequenceEqual(receipt.IntentDigest.Span)
            || receipt.DeliveryAttemptId.Length != 0 || receipt.ParcelIds.Count != 0 || !transition.Intent.AsSpan().SequenceEqual(operation.Intent))
            throw Unavailable("snapshot-admission-receipt-mismatch");
        var intent = OutpostAssignmentService.ParseIntent(operation);
        if (!receipt.Snapshot.Equals(intent.PreRemoval) || !intent.Scope.Equals(metadata.Scope) && !historicalRollback
            || !intent.Workers.Select(w => w.PawnCustodyId).Order().SequenceEqual(receipt.PawnCustodyIds.Order())
            || !intent.Workers.Select(w => w.AssignmentId).Order().SequenceEqual(receipt.AssignmentIds.Order()))
            throw Unavailable("snapshot-admission-receipt-mismatch");
        bool noRemoval = transition.Kind == "CancelFence";
        if (noRemoval ? receipt.CancellationKind != OutpostCancelKind.AbortNoRemoval || receipt.NoRemovalBranchId != intent.Caravan.GameBranchId
            : transition.Id != operation.Id || receipt.CancellationKind != OutpostCancelKind.Unspecified || receipt.NoRemovalBranchId.Length != 0)
            throw Unavailable("snapshot-admission-receipt-kind");
        var previous = await db.OutpostSnapshotLinks.AsNoTracking().Where(l => l.PlayerId == operation.PlayerId && l.WorldId == operation.WorldId
            && l.SaveGeneration == operation.SaveGeneration).Select(l => l.Metadata).ToListAsync(ct);
        // A receipt attests one historical acceptance, not every later life state of the pawn.
        // Its original immutable snapshot must still prove the admission; a bare self-reference is insufficient.
        if (previous.Select(ParseMetadata).Any(original => original.Receipts.Any(r => r.Equals(receipt))
            && AdmissionRegistryMatches(original, intent, noRemoval))) return;
        if (!noRemoval && operation.State == "CancelFenced") throw Unavailable("snapshot-removal-after-cancellation-fence");
        if (!AdmissionRegistryMatches(metadata, intent, noRemoval)) throw Unavailable("snapshot-admission-registry-mismatch");
    }

    private static bool AdmissionRegistryMatches(OutpostSaveMetadata metadata, OutpostPrepareRequest intent, bool noRemoval) =>
        intent.Workers.All(worker => metadata.Registry.SingleOrDefault(e => e.PawnCustodyId == worker.PawnCustodyId && !e.NonAuthoritativeMirror) is { } registry
            && registry.Scope.Equals(worker.Pawn.Registry.Scope) && (noRemoval ? registry.Equals(worker.Pawn.Registry)
                : registry.Tombstone && registry.AssignmentId == worker.AssignmentId && registry.PawnReferenceToken.Length == 0));

    private async Task<bool> AdmissionChangesAssignmentAsync(OutpostSaveMetadata metadata, PawnCustodyRecord custody,
        PawnRegistryEnvelope envelope, CancellationToken ct)
    {
        if (custody.LocationKind != "Prepared" || !envelope.Tombstone) return false;
        var operation = await db.OutpostOperations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == custody.LocationId, ct);
        if (operation is null || operation.State != "Prepared" || !OutpostAssignmentService.HasReceipt(metadata, operation.Id, operation, OutpostCancelKind.Unspecified)) return false;
        return OutpostAssignmentService.ParseIntent(operation).Workers.Any(w => w.PawnCustodyId == custody.Id && w.AssignmentId == envelope.AssignmentId);
    }

    private async Task StageAdmissionsAsync(OutpostSaveMetadata metadata, string transitionId, CancellationToken ct)
    {
        foreach (var receipt in metadata.Receipts.Where(r => r.DeliveryAttemptId.Length == 0 && r.OrdinaryReturnAttemptId.Length == 0
            && r.CancellationKind == OutpostCancelKind.Unspecified))
        {
            var operation = await db.OutpostOperations.AsNoTracking().SingleAsync(o => o.Id == receipt.TransitionId, ct);
            foreach (var worker in OutpostAssignmentService.ParseIntent(operation).Workers)
                await db.PawnCustody.Where(p => p.Id == worker.PawnCustodyId && p.LocationKind == "Prepared" && p.LocationId == operation.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.LocationKind, "Removed").SetProperty(p => p.AssignmentId, worker.AssignmentId)
                        .SetProperty(p => p.Tombstone, true).SetProperty(p => p.FrontierTransitionId, transitionId).SetProperty(p => p.Revision, p => p.Revision + 1), ct);
        }
    }

    private async Task ValidateAdmissionStoredAsync(Dictionary<string, OutpostTransition> transitions, List<OutpostSnapshotLink> links,
        IReadOnlyDictionary<string, OutpostAdminSemanticView> adminSemantics, CancellationToken ct)
    {
        var operations = await db.OutpostOperations.AsNoTracking().Where(o => o.Kind == "Found" || o.Kind == "Assign").ToDictionaryAsync(o => o.Id, ct);
        foreach (var operation in operations.Values)
        {
            if (operation.Intent.Length is < 1 or > Limits.EconomyMaxParcelBytes || operation.Result.Length is < 1 or > Limits.MaxMessageBytes)
                throw Unavailable("required-admission-evidence-unavailable");
            var intent = OutpostAssignmentService.ParseIntent(operation);
            var result = OutpostOperationReply.Parser.ParseFrom(operation.Result);
            var pre = links.SingleOrDefault(l => l.WorldId == operation.WorldId && l.PlayerId == operation.PlayerId && l.SaveGeneration == operation.SaveGeneration
                && l.SnapshotId == intent.PreRemoval?.SnapshotId && l.SaveWriteEpoch == operation.SaveWriteEpoch && l.RestoreBoundary == operation.RestoreBoundary);
            if (pre is null || !pre.RawSha256.AsSpan().SequenceEqual(intent.PreRemoval.RawSha256.Span) || !ParseMetadata(pre.Metadata).Scope.Equals(intent.Scope)
                || !OutpostRequestIdentity.Digest(intent).Equals(intent.IntentDigest) || !intent.IntentDigest.Span.SequenceEqual(operation.IntentDigest)
                || intent.ClientRequestId != operation.RequestId || intent.Kind.ToString() != operation.Kind || intent.OutpostId != operation.OutpostId
                || !transitions.TryGetValue(operation.Id, out var prepared) || prepared.Kind != "Prepare" || !prepared.Intent.AsSpan().SequenceEqual(operation.Intent)
                || prepared.WorldId != operation.WorldId || prepared.PlayerId != operation.PlayerId || prepared.SaveGeneration != operation.SaveGeneration || prepared.RestoreBoundary != operation.RestoreBoundary
                || !transitions.TryGetValue(operation.TransitionId ?? "", out var latest) || latest.WorldId != operation.WorldId || latest.PlayerId != operation.PlayerId
                || latest.SaveGeneration != operation.SaveGeneration || latest.RestoreBoundary != operation.RestoreBoundary
                || !(adminSemantics.TryGetValue(latest.Id, out var semantic) ? semantic.BusinessIntent is { } bytes && bytes.AsSpan().SequenceEqual(operation.Intent)
                    : latest.Intent.AsSpan().SequenceEqual(operation.Intent))
                || result.TransitionId != operation.TransitionId || result.TransitionDigest.Length != 0 || !result.Scope.Equals(intent.Scope)
                || result.ClientRequestId != operation.RequestId || !result.IntentDigest.Equals(intent.IntentDigest) || !result.PreparedIntent.Equals(intent)
                || operation.State is not ("Prepared" or "CancelFenced" or "Committed" or "Cancelled")
                || operation.ReservedSilver != (operation.Kind == "Found" && operation.State is "Prepared" or "CancelFenced" ? 500 : 0)
                || !ParseMetadata(pre.Metadata).Caravans.Any(c => c.Equals(intent.Caravan))
                || intent.Workers.Any(w => !ParseMetadata(pre.Metadata).WorkerCaptures.Any(c => c.Equals(w))))
                throw Unavailable("required-admission-evidence-unavailable");
            string[] kinds = operation.State switch { "Prepared" => ["Prepare"], "CancelFenced" => ["CancelFence"], "Committed" => ["Commit"], _ => ["AbortNoRemoval", "AbortWithReturn"] };
            string? kind = adminSemantics.TryGetValue(latest.Id, out var validated) ? validated.BusinessKind : latest.Kind;
            if (!kinds.Contains(kind) || !await db.Outposts.AnyAsync(o => o.Id == operation.OutpostId && o.PlayerId == operation.PlayerId && o.WorldId == operation.WorldId, ct))
                throw Unavailable("required-admission-evidence-unavailable");
        }
        foreach (var assignment in await db.OutpostAssignments.AsNoTracking().ToListAsync(ct))
        {
            if (!operations.TryGetValue(assignment.PreparedOperationId, out var operation)) throw Unavailable("required-assignment-evidence-unavailable");
            var capture = OutpostAssignmentService.ParseIntent(operation).Workers.SingleOrDefault(w => w.AssignmentId == assignment.Id && w.PawnCustodyId == assignment.PawnCustodyId);
            if (capture is null || assignment.OutpostId != operation.OutpostId || !capture.Pawn.ToByteArray().AsSpan().SequenceEqual(assignment.Blob)
                || assignment.BlobDigest.Length != 32 || !SHA256.HashData(assignment.Blob).AsSpan().SequenceEqual(assignment.BlobDigest)
                || !JsonParser.Default.Parse<OutpostWorkerCapture>(assignment.FactsJson).Equals(capture))
                throw Unavailable("required-assignment-evidence-unavailable");
        }
        foreach (var reservation in await db.OutpostReservations.AsNoTracking().ToListAsync(ct))
            if (!operations.TryGetValue(reservation.OperationId, out var operation) || operation.Kind != "Found"
                || reservation.WorldId != operation.WorldId || reservation.PlayerId != operation.PlayerId || reservation.SaveGeneration != operation.SaveGeneration
                || reservation.Tile != OutpostAssignmentService.ParseIntent(operation).Tile
                || reservation.Active != (operation.State != "Cancelled" && await db.Outposts.AnyAsync(o => o.Id == operation.OutpostId && o.OccupiesSlot, ct)))
                throw Unavailable("required-reservation-evidence-unavailable");
    }
}
