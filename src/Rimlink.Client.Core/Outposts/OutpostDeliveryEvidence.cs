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

    // This checks the authenticated server echo's shape; the server independently joins its immutable restore evidence.
    public static bool MatchesScope(Parcel parcel, OutpostScopeMessage scope)
    {
        if (parcel.OutpostPayload is not { } payload || !OutpostReconciliation.ValidScope(scope)) return false;
        if (parcel.OutpostRestoreAuthorization is not { } grant) return payload.Scope.Equals(scope);
        return grant.CalculateSize() <= 16384 && Uuid(grant.AuthorizationId) && grant.RestoreTransition is { } root
            && Uuid(root.TransitionId) && root.TransitionDigest.Length == 32
            && OutpostReconciliation.ValidScope(grant.OriginalScope) && grant.OriginalScope.Equals(payload.Scope)
            && grant.RestoredScope is not null && grant.RestoredScope.Equals(scope)
            && grant.OriginalScope.RestoreBoundary != scope.RestoreBoundary
            && grant.OriginalScope.WorldInstanceId == scope.WorldInstanceId && grant.OriginalScope.OwnerPlayerId == scope.OwnerPlayerId
            && grant.OriginalScope.SaveGeneration == scope.SaveGeneration
            && grant.ParcelId == parcel.Id && grant.PayloadDigest.Length == 32 && grant.PayloadDigest.Equals(payload.PayloadDigest)
            && OutpostReconciliation.ValidProof(grant.SelectedSnapshot)
            && grant.SelectedSnapshot.Scope.WorldInstanceId == scope.WorldInstanceId
            && grant.SelectedSnapshot.Scope.OwnerPlayerId == scope.OwnerPlayerId && grant.SelectedSnapshot.Scope.SaveGeneration == scope.SaveGeneration;
    }

    public static OutpostTransitionReceipt Receipt(Parcel parcel, OutpostDeliveryRequest request, OutpostDeliveryReply reply)
    {
        if (parcel is null || request is null || reply is null || parcel.OutpostPayload is not { } payload
            || OutpostPayloadCompatibility.Refusal(parcel, 1) is not null || payload.ChildCount <= 0
            || !OutpostParcelContents.Match(payload, parcel.Items, parcel.Pawns, parcel.Silver)
            || !MatchesScope(parcel, request.Scope)
            || !OutpostReconciliation.ValidProof(request.CanonicalSnapshot) || !request.Scope.Equals(request.CanonicalSnapshot.Scope)
            || request.ParcelId != parcel.Id || parcel.Id <= 0 || !Uuid(request.ClientRequestId)
            || !request.PayloadDigest.Equals(payload.PayloadDigest) || !request.Equals(reply.Authorization)
            || !Uuid(reply.AttemptId) || reply.TransitionId != reply.AttemptId || reply.TransitionDigest.Length != 32
            || reply.PredecessorId.Length > 0 && !Uuid(reply.PredecessorId)
            || (parcel.OutpostRestoreAuthorization is { } restore
                ? request.RestoreAuthorizationId != restore.AuthorizationId || !restore.Equals(reply.RestoreAuthorization)
                    || request.CanonicalSnapshot.SnapshotId == restore.SelectedSnapshot.SnapshotId
                : request.RestoreAuthorizationId.Length != 0 || reply.RestoreAuthorization is not null))
            throw new InvalidDataException("delivery causal authorization mismatch");
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
