using Rimlink.Protocol.V1;
namespace Rimlink.Client.Outposts;

public static class OutpostAdmissionReceipts
{
    public static OutpostTransitionReceipt CreateNoRemoval(OutpostPrepareRequest request, OutpostOperationReply operation, OutpostOperationReply fence)
    {
        if (operation.Status == OutpostOperationStatus.Prepared && operation.TransitionId == fence.TransitionId)
            throw new InvalidDataException("cancellation fence transition is unavailable");
        return Create(request, fence, noRemoval: true);
    }

    public static OutpostTransitionReceipt Create(OutpostPrepareRequest request, OutpostOperationReply reply, bool noRemoval = false)
    {
        if (!OutpostReconciliation.SameScope(request.Scope, reply.Scope) || request.ClientRequestId != reply.ClientRequestId
            || !request.IntentDigest.Equals(reply.IntentDigest) || reply.TransitionDigest.Length != 32
            || !Guid.TryParse(reply.TransitionId, out _) || reply.PredecessorId.Length > 0 && !Guid.TryParse(reply.PredecessorId, out _)
            || !OutpostReconciliation.ValidProof(request.PreRemoval)
            || reply.Status != (noRemoval ? OutpostOperationStatus.RecoveryRequired : OutpostOperationStatus.Prepared)
            || noRemoval && !Guid.TryParse(request.Caravan?.GameBranchId, out _))
            throw new InvalidDataException("admission reply cannot authorize this exact snapshot receipt");
        var receipt = new OutpostTransitionReceipt { TransitionId = reply.TransitionId, PredecessorId = reply.PredecessorId,
            TransitionDigest = reply.TransitionDigest, ClientRequestId = request.ClientRequestId, IntentDigest = request.IntentDigest,
            Snapshot = request.PreRemoval.Clone(), CancellationKind = noRemoval ? OutpostCancelKind.AbortNoRemoval : OutpostCancelKind.Unspecified,
            NoRemovalBranchId = noRemoval ? request.Caravan.GameBranchId : "" };
        receipt.PawnCustodyIds.Add(request.Workers.Select(w => w.PawnCustodyId)); receipt.AssignmentIds.Add(request.Workers.Select(w => w.AssignmentId));
        return receipt;
    }
}
