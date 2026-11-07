using Rimlink.Protocol.V1;
namespace Rimlink.Client.Outposts;

public static class OutpostAdmissionCancellation
{
    public static OutpostCancelRequest CreateRequest(OutpostPrepareRequest request, OutpostCancelKind kind, long expectedRevision) => new()
    {
        Scope = request.Scope.Clone(),
        ClientRequestId = request.ClientRequestId,
        IntentDigest = request.IntentDigest,
        Kind = kind,
        // Keep the original proof durable until the fresh cancellation snapshot is accepted.
        CancellationProof = request.PreRemoval.Clone(),
        ExpectedRevision = expectedRevision,
        ContinuousBranchId = kind == OutpostCancelKind.AbortNoRemoval ? request.Caravan.GameBranchId : ""
    };

    public static OutpostCancelKind Select(OutpostPrepareRequest request, OutpostSaveMetadata metadata, string gameBranchId, bool removedLocally)
    {
        if (!OutpostReconciliation.SameScope(request.Scope, metadata.Scope) || request.Workers.Count == 0) throw new InvalidDataException("cancellation has no exact owner evidence");
        var rows = request.Workers.Select(w => metadata.Registry.SingleOrDefault(e => e.PawnCustodyId == w.PawnCustodyId)).ToArray();
        if (rows.Any(e => e is null || e.NonAuthoritativeMirror)) throw new InvalidDataException("cancellation has ambiguous pawn identity");
        if (!removedLocally)
        {
            if (gameBranchId != request.Caravan.GameBranchId || rows.Any(e => e!.Tombstone || e.PawnReferenceToken.Length == 0))
                throw new InvalidDataException("no-removal cancellation requires the same continuous live branch");
            return OutpostCancelKind.AbortNoRemoval;
        }
        var receipt = metadata.Receipts.SingleOrDefault(r => r.ClientRequestId == request.ClientRequestId && r.IntentDigest.Equals(request.IntentDigest)
            && r.CancellationKind == OutpostCancelKind.Unspecified && r.DeliveryAttemptId.Length == 0);
        if (receipt is null || !receipt.PawnCustodyIds.Order().SequenceEqual(request.Workers.Select(w => w.PawnCustodyId).Order())
            || !receipt.AssignmentIds.Order().SequenceEqual(request.Workers.Select(w => w.AssignmentId).Order())
            || request.Workers.Any(w => rows.Single(e => e!.PawnCustodyId == w.PawnCustodyId) is not { Tombstone: true, PawnReferenceToken.Length: 0 } row || row.AssignmentId != w.AssignmentId))
            throw new InvalidDataException("removed cancellation requires exact post-removal receipt and tombstones");
        return OutpostCancelKind.ReturnAfterRemoval;
    }
}
