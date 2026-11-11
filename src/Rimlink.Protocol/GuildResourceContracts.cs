using Rimlink.Protocol.V1;

namespace Rimlink.Protocol;

public static class GuildResourceContracts
{
    public const uint CurrentVersion = 1;
    public const int MaximumReceipts = 10_000;

    public static void ValidateMetadata(GuildSaveMetadata metadata)
    {
        if (metadata is null || metadata.SchemaVersion != CurrentVersion || metadata.CalculateSize() > OutpostRecoveryData.MaxMetadataBytes
            || metadata.Receipts.Count > MaximumReceipts) throw new InvalidDataException("guild-save-metadata-invalid");
        ValidateScope(metadata.Scope);
        var attempts = new HashSet<string>(StringComparer.Ordinal);
        var claims = new HashSet<string>(StringComparer.Ordinal);
        var requests = new HashSet<string>(StringComparer.Ordinal);
        var parcels = new HashSet<long>();
        foreach (var receipt in metadata.Receipts)
        {
            if (receipt.SchemaVersion != CurrentVersion || !Uuid(receipt.AttemptId) || !attempts.Add(receipt.AttemptId)
                || !Uuid(receipt.ClaimId) || !claims.Add(receipt.ClaimId) || receipt.ParcelId <= 0 || !parcels.Add(receipt.ParcelId)
                || !Uuid(receipt.ClientRequestId) || !requests.Add(receipt.ClientRequestId) || receipt.PayloadDigest.Length != 32
                || !Uuid(receipt.AuthorizationTransitionId) || receipt.AuthorizationTransitionDigest.Length != 32
                || receipt.AuthorizationIntentDigest.Length != 32 || receipt.BeforeSnapshot is not { } before
                || !Guid.TryParseExact(before.SnapshotId, "N", out _) || before.RawSha256.Length != 32)
                throw new InvalidDataException("guild-delivery-receipt-invalid");
            ValidateScope(before.Scope);
        }
    }
    public static long Validate(GuildResourceParcelPayload payload)
    {
        if (payload is null || payload.SchemaVersion != CurrentVersion || payload.CalculateSize() > 4096
            || !Uuid(payload.ClaimId) || payload.AllianceId <= 0 || payload.OriginKind is not ("Outpost" or "Obligation")
            || !Uuid(payload.OriginId) || payload.OwnerRevision <= 0 || payload.DefName is not ("Steel" or "WoodLog")
            || payload.Count is < 1 or > 1000 || payload.UnitValueX100 < 0 || payload.UnitValueX100 > long.MaxValue / 1000
            || !Uuid(payload.ManifestId) || payload.ManifestDigest.Length != 32 || payload.ValuationRevision <= 0
            || !Uuid(payload.TransitionId)) throw new InvalidDataException("guild-resource-payload-invalid");
        ValidateScope(payload.Recipient);
        return checked(payload.Count * payload.UnitValueX100);
    }

    public static void ValidateScope(GuildColonyScope scope)
    {
        if (scope is null || string.IsNullOrWhiteSpace(scope.WorldInstanceId) || scope.WorldInstanceId.Length > 256
            || scope.WorldInstanceId.Any(char.IsControl) || scope.PlayerId <= 0 || scope.SaveGeneration < 0
            || scope.SaveWriteEpoch < 0 || !Uuid(scope.RestoreBoundary)) throw new InvalidDataException("guild-colony-scope-invalid");
    }

    public static GuildColonyScope DeliveryScope(Parcel parcel)
    {
        var original = parcel.GuildResourcePayload?.Recipient ?? throw new InvalidDataException("guild-resource-payload-invalid");
        if (parcel.GuildRestoreScope is not { } current) return original;
        ValidateScope(current);
        if (current.PlayerId != original.PlayerId || current.WorldInstanceId != original.WorldInstanceId
            || current.SaveGeneration != original.SaveGeneration || current.SaveWriteEpoch != original.SaveWriteEpoch
            || current.RestoreBoundary == original.RestoreBoundary) throw new InvalidDataException("guild-delivery-restore-scope-invalid");
        return current;
    }

    private static bool Uuid(string id) => Guid.TryParseExact(id, "D", out var parsed) && parsed != Guid.Empty;
}
