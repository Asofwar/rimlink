using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
namespace Rimlink.Client.Outposts;

public static class OrdinaryReturnEvidence
{
    public static OutpostTransitionReceipt Receipt(OrdinaryCancelRequest request, OrdinaryReturnProof proof)
    {
        try {
            var payload = OrdinaryReturnCodec.Payload(request);
            if (proof is null || !request.Equals(proof.Authorization) || proof.TransitionId != request.ReturnAttemptId
                || proof.TransitionDigest.Length != 32 || proof.PredecessorId.Length > 0 && !Uuid(proof.PredecessorId)
                || !OutpostReconciliation.ValidProof(request.RemovalSnapshot) || !request.Scope.Equals(request.RemovalSnapshot.Scope)
                || !OutpostReconciliation.ValidProof(request.CanonicalSnapshot) || !request.Scope.Equals(request.CanonicalSnapshot.Scope)
                || !payload.Equals(proof.Payload) || proof.PayloadDigest.Length != 32
                || !SHA256.HashData(payload.ToByteArray()).AsSpan().SequenceEqual(proof.PayloadDigest.Span)) throw new InvalidDataException();
            return new() { TransitionId = proof.TransitionId, TransitionDigest = proof.TransitionDigest, PredecessorId = proof.PredecessorId,
                ClientRequestId = request.Capture.ClientRequestId, IntentDigest = ByteString.CopyFrom(SHA256.HashData(request.ToByteArray())),
                Snapshot = request.CanonicalSnapshot.Clone(), OrdinaryReturnAttemptId = request.ReturnAttemptId,
                OrdinaryLocalOperationId = request.Capture.LocalOperationId,
                PawnCustodyIds = { request.Capture.Pawns.Select(p => p.Pawn.Registry.PawnCustodyId) },
                AssignmentIds = { request.Capture.Pawns.Select(p => p.Pawn.Registry.AssignmentId).Where(a => a.Length > 0).Distinct(StringComparer.Ordinal) } };
        }
        catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException or ArgumentException)
        { throw new InvalidDataException("ordinary return causal authorization mismatch", error); }
    }
    public static MaterializationDecision Decide(OrdinaryCancelRequest request, OrdinaryOperationReply reply, OutpostSaveMetadata metadata,
        string preparedBranch, string currentBranch, bool spawnStarted, bool safeContext, bool currentSession)
    {
        if (!safeContext || !currentSession || metadata?.SchemaVersion != 1 || request?.Scope is null
            || !metadata.Scope.Equals(request.Scope) || reply?.Status is not (OrdinaryOperationStatus.ReturnStarted or OrdinaryOperationStatus.ReturnAccepted))
            return MaterializationDecision.RecoveryRequired;
        OutpostTransitionReceipt expected;
        try { new OutpostRecoveryData { Metadata = metadata }.Validate(); expected = Receipt(request, reply.ReturnProof); }
        catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException or ArgumentException)
        { return MaterializationDecision.RecoveryRequired; }
        var known = metadata.Receipts.Where(r => r.OrdinaryReturnAttemptId == request.ReturnAttemptId
            || r.OrdinaryLocalOperationId == request.Capture.LocalOperationId || r.ClientRequestId == request.Capture.ClientRequestId).ToArray();
        if (known.Length > 0) return known.Length == 1 && known[0].Equals(expected) ? MaterializationDecision.AckOnly : MaterializationDecision.RecoveryRequired;
        return reply.Status == OrdinaryOperationStatus.ReturnStarted && !spawnStarted && Uuid(preparedBranch) && preparedBranch == currentBranch
            ? MaterializationDecision.Materialize : MaterializationDecision.RecoveryRequired;
    }
    private static bool Uuid(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString("D");
}
