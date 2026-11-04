using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
namespace Rimlink.Client.Outposts;

public enum MaterializationDecision { RecoveryRequired, Materialize, AckOnly }

public static class OutpostDeliveryEvidence
{
    public static bool MayRetryBeforeSpawn(Exception error, bool spawnStarted)
        => !spawnStarted && error is Grpc.Core.RpcException rpc
            && (rpc.StatusCode is Grpc.Core.StatusCode.Unavailable or Grpc.Core.StatusCode.DeadlineExceeded
                    or Grpc.Core.StatusCode.Cancelled or Grpc.Core.StatusCode.ResourceExhausted
                || Economy.EconomyClient.CodeOf(rpc) == ErrorCode.OutpostUnavailable);

    public static OutpostSaveMetadata PrepareRecipient(OutpostSaveMetadata metadata, OutpostScopeMessage authenticatedScope,
        long loadedGeneration, long loadedEpoch, bool malformed, bool knownRegistry)
    {
        if (malformed || !OutpostReconciliation.ValidScope(authenticatedScope) || loadedGeneration != authenticatedScope.SaveGeneration
            || loadedEpoch != authenticatedScope.SaveWriteEpoch) throw new InvalidDataException("loaded colony does not match authenticated custody scope");
        if (metadata.SchemaVersion == 0)
        {
            if (knownRegistry || metadata.CalculateSize() != 0) throw new InvalidDataException("existing custody evidence cannot become an empty recipient");
            return new() { SchemaVersion = 1, Scope = authenticatedScope.Clone() };
        }
        new OutpostRecoveryData { Metadata = metadata }.Validate();
        if (!metadata.Scope.Equals(authenticatedScope)) throw new InvalidDataException("recipient evidence belongs to another boundary");
        return metadata.Clone();
    }

    public static OutpostTransitionReceipt Receipt(Parcel parcel, OutpostDeliveryRequest request, OutpostDeliveryReply reply)
    {
        if (parcel is null || request is null || reply is null || parcel.OutpostPayload is not { } payload
            || OutpostPayloadCompatibility.Refusal(parcel, 1) is not null || payload.ChildCount <= 0
            || !OutpostParcelContents.Match(payload, parcel.Items, parcel.Pawns, parcel.Silver)
            || !OutpostReconciliation.ValidScope(request.Scope) || !request.Scope.Equals(payload.Scope)
            || !OutpostReconciliation.ValidProof(request.CanonicalSnapshot) || !request.Scope.Equals(request.CanonicalSnapshot.Scope)
            || request.ParcelId != parcel.Id || parcel.Id <= 0 || !Uuid(request.ClientRequestId)
            || !request.PayloadDigest.Equals(payload.PayloadDigest) || !request.Equals(reply.Authorization)
            || !Uuid(reply.AttemptId) || reply.TransitionId != reply.AttemptId || reply.TransitionDigest.Length != 32
            || reply.PredecessorId.Length > 0 && !Uuid(reply.PredecessorId)) throw new InvalidDataException("delivery causal authorization mismatch");
        var original = payload.Clone(); original.PayloadDigest = ByteString.Empty;
        if (!SHA256.HashData(original.ToByteArray()).AsSpan().SequenceEqual(payload.PayloadDigest.Span))
            throw new InvalidDataException("delivery payload digest mismatch");
        return new() { TransitionId = reply.TransitionId, TransitionDigest = reply.TransitionDigest, PredecessorId = reply.PredecessorId,
            ClientRequestId = request.ClientRequestId, IntentDigest = ByteString.CopyFrom(SHA256.HashData(request.ToByteArray())),
            Snapshot = request.CanonicalSnapshot.Clone(), DeliveryAttemptId = reply.AttemptId, ParcelIds = { parcel.Id },
            PawnCustodyIds = { payload.PawnCustodyIds }, AssignmentIds = { payload.AssignmentIds } };
    }
    public static MaterializationDecision Decide(Parcel parcel, OutpostDeliveryRequest request, OutpostDeliveryReply reply,
        OutpostSaveMetadata metadata, string preparedBranch, string currentBranch, bool spawnStarted, bool safeHome, bool currentSession)
    {
        if (!safeHome || !currentSession || metadata?.SchemaVersion != 1 || !metadata.Scope.Equals(request?.Scope))
            return MaterializationDecision.RecoveryRequired;
        OutpostTransitionReceipt expected;
        try { expected = Receipt(parcel, request!, reply); }
        catch (InvalidDataException) { return MaterializationDecision.RecoveryRequired; }
        var known = metadata.Receipts.Where(r => r.ClientRequestId == request!.ClientRequestId
            || r.DeliveryAttemptId == reply.AttemptId || r.ParcelIds.Contains(parcel.Id)).ToArray();
        if (known.Length > 0) return known.Length == 1 && known[0].Equals(expected)
            ? MaterializationDecision.AckOnly : MaterializationDecision.RecoveryRequired;
        return !reply.AckOnly && !spawnStarted && Uuid(preparedBranch) && preparedBranch == currentBranch
            ? MaterializationDecision.Materialize : MaterializationDecision.RecoveryRequired;
    }
    private static bool Uuid(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty
        && (value == id.ToString("D") || value == id.ToString("N"));
}
