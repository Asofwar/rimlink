using Rimlink.Client.Outposts;
using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

public static class GuildDeliveryEvidence
{
    public static OutpostSaveMetadata PrepareRecipient(OutpostSaveMetadata metadata, GuildColonyScope scope,
        long loadedGeneration, long loadedEpoch, bool malformed, bool knownPawnRegistry)
    {
        GuildResourceContracts.ValidateScope(scope);
        var result = OutpostDeliveryEvidence.PrepareRecipient(metadata, CommonScope(scope), loadedGeneration, loadedEpoch, malformed, knownPawnRegistry);
        if (result.GuildMetadata is null) result.GuildMetadata = new() { SchemaVersion = 1, Scope = scope.Clone() };
        else if (!result.GuildMetadata.Scope.Equals(scope)) throw Invalid();
        new OutpostRecoveryData { Metadata = result }.Validate();
        return result;
    }

    public static void ValidateIntent(Parcel parcel, GuildDeliveryRequest request)
    {
        if (parcel?.GuildResourcePayload is not { } payload || parcel.Kind != ParcelKind.ParcelGuildResources || parcel.CalculateSize() > 8192
            || parcel.Id <= 0 || parcel.AwaitingDecision || parcel.OutpostPayload is not null || parcel.Silver != 0
            || (parcel.Items?.Items.Count ?? 0) != 0 || (parcel.Pawns?.Pawns.Count ?? 0) != 0
            || request is null || request.CalculateSize() > 4096 || request.ParcelId != parcel.Id || !Uuid(request.ClientRequestId)) throw Invalid();
        _ = GuildResourceContracts.Validate(payload);
        if (!SHA256.HashData(payload.ToByteArray()).AsSpan().SequenceEqual(request.PayloadDigest.Span)
            || !GuildResourceContracts.DeliveryScope(parcel).Equals(request.Scope) || !Proof(request.BeforeSnapshot) || !request.BeforeSnapshot.Scope.Equals(request.Scope)) throw Invalid();
    }

    public static GuildDeliveryReceipt Receipt(Parcel parcel, GuildDeliveryRequest request, GuildDeliveryAuthorization authorization)
    {
        ValidateIntent(parcel, request);
        var payload = parcel.GuildResourcePayload;
        if (authorization is null || authorization.CalculateSize() > 8192 || !Uuid(authorization.AttemptId)
            || authorization.AttemptId != authorization.TransitionId || authorization.TransitionDigest.Length != 32
            || !request.Equals(authorization.Request) || !SHA256.HashData(request.ToByteArray()).AsSpan().SequenceEqual(authorization.IntentDigest.Span)
            || authorization.AckOnly != (authorization.AcceptedSnapshot is not null)
            || authorization.AcceptedSnapshot is { } accepted && (!Proof(accepted) || !accepted.Scope.Equals(request.Scope))) throw Invalid();
        return new() { SchemaVersion = 1, AttemptId = authorization.AttemptId, ClaimId = payload.ClaimId, ParcelId = parcel.Id,
            ClientRequestId = request.ClientRequestId, PayloadDigest = request.PayloadDigest, AuthorizationTransitionId = authorization.TransitionId,
            AuthorizationTransitionDigest = authorization.TransitionDigest, AuthorizationIntentDigest = authorization.IntentDigest,
            BeforeSnapshot = request.BeforeSnapshot.Clone() };
    }

    public static MaterializationDecision Decide(Parcel parcel, GuildDeliveryRequest request, GuildDeliveryAuthorization authorization,
        OutpostSaveMetadata metadata, string preparedBranch, string currentBranch, bool spawnStarted, bool safeHome, bool currentSession)
    {
        if (!safeHome || !currentSession || metadata?.GuildMetadata is not { } guild || request?.Scope is null
            || !metadata.Scope.Equals(CommonScope(request.Scope)) || !guild.Scope.Equals(request.Scope)) return MaterializationDecision.RecoveryRequired;
        GuildDeliveryReceipt expected;
        try
        {
            new OutpostRecoveryData { Metadata = metadata }.Validate();
            expected = Receipt(parcel, request, authorization);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException or ArgumentException)
        { return MaterializationDecision.RecoveryRequired; }
        var known = guild.Receipts.Where(r => r.AttemptId == expected.AttemptId || r.ClaimId == expected.ClaimId
            || r.ParcelId == expected.ParcelId || r.ClientRequestId == expected.ClientRequestId).ToArray();
        if (known.Length != 0) return known.Length == 1 && known[0].Equals(expected) ? MaterializationDecision.AckOnly : MaterializationDecision.RecoveryRequired;
        return !authorization.AckOnly && !spawnStarted && Uuid(preparedBranch) && preparedBranch == currentBranch
            ? MaterializationDecision.Materialize : MaterializationDecision.RecoveryRequired;
    }

    private static OutpostScopeMessage CommonScope(GuildColonyScope scope) => new() { WorldInstanceId = scope.WorldInstanceId, OwnerPlayerId = scope.PlayerId,
        SaveGeneration = scope.SaveGeneration, SaveWriteEpoch = scope.SaveWriteEpoch, RestoreBoundary = scope.RestoreBoundary };
    private static bool Proof(GuildSnapshotProof? proof)
    {
        if (proof is null || !Guid.TryParseExact(proof.SnapshotId, "N", out var id) || id == Guid.Empty || proof.RawSha256.Length != 32) return false;
        GuildResourceContracts.ValidateScope(proof.Scope); return true;
    }
    private static bool Uuid(string id) => Guid.TryParseExact(id, "D", out var parsed) && parsed != Guid.Empty;
    private static InvalidDataException Invalid() => new("guild-delivery-authorization-invalid");
}
