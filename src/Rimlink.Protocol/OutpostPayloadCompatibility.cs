using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Protocol;

/// <summary>Pure wire-shape gate, not custody authorization or proof of delivery.</summary>
public static class OutpostPayloadCompatibility
{
    public const uint SupportedVersion = 1;

    /// <summary>Null permits parsing only. Callers still validate catalog, scope, ledger and size limits.</summary>
    public static ErrorCode? Refusal(Parcel parcel, uint capability)
    {
        ArgumentNullException.ThrowIfNull(parcel);
        if (!Enum.IsDefined(parcel.Kind)) return ErrorCode.OutpostPayloadUnsupported;
        var pawns = parcel.Pawns?.Pawns;
        bool tagged = pawns?.Any(p => p.Registry is not null) == true;
        var payload = parcel.OutpostPayload;
        bool protectedPayload = tagged || payload is not null || parcel.Kind == ParcelKind.ParcelOutpost;
        if (!protectedPayload) return null; // Genuine legacy parcels retain their previous contract.
        if (capability != SupportedVersion) return ErrorCode.OutpostClientRequired;

        var identities = new HashSet<Guid>();
        var assignments = new HashSet<Guid>();
        if (pawns is not null)
        {
            foreach (var pawn in pawns)
            {
                var registry = pawn.Registry;
                if (registry is null) continue;
                if (registry.SchemaVersion != SupportedVersion || Unknown(registry, PawnRegistryEnvelope.Parser))
                    return ErrorCode.OutpostPayloadUnsupported;
                if (!Uuid(registry.PawnCustodyId) || !Uuid(registry.AssignmentId) || !Scope(registry.Scope)
                    || string.IsNullOrWhiteSpace(registry.PawnReferenceToken)
                    || registry.NonAuthoritativeMirror || registry.Tombstone
                    || !identities.Add(Guid.Parse(registry.PawnCustodyId))
                    || !assignments.Add(Guid.Parse(registry.AssignmentId)))
                    return ErrorCode.OutpostPayloadInvalid;
            }
        }
        if (payload is null)
            return parcel.Kind == ParcelKind.ParcelOutpost ? ErrorCode.OutpostPayloadInvalid : null;
        if (payload.SchemaVersion != SupportedVersion || Unknown(payload, OutpostParcelPayload.Parser)
            || payload.Kind is not (OutpostPayloadKind.Resources or OutpostPayloadKind.Workers or OutpostPayloadKind.ReturnBatch)
            || payload.Resources.Any(r => r.SchemaVersion != SupportedVersion))
            return ErrorCode.OutpostPayloadUnsupported;
        if (parcel.Kind != ParcelKind.ParcelOutpost || !Scope(payload.Scope)
            || !Uuid(payload.OutpostId) || !Uuid(payload.BatchOperationId) || !Uuid(payload.ManifestId)
            || payload.PayloadDigest.Length != 32 || payload.ManifestDigest.Length != 32 || payload.ValuationRevision <= 0
            || parcel.Items?.Items.Count > 0 || parcel.Silver != 0
            || payload.Resources.Any(r => r.DefName is not ("WoodLog" or "Steel") || r.Count <= 0 || r.ImportValueX100 < 0)
            || payload.Resources.Select(r => r.DefName).Distinct(StringComparer.Ordinal).Count() != payload.Resources.Count)
            return ErrorCode.OutpostPayloadInvalid;

        int pawnCount = pawns?.Count ?? 0;
        if (payload.PawnCustodyIds.Count != pawnCount || payload.AssignmentIds.Count != pawnCount)
            return ErrorCode.OutpostPayloadInvalid;
        for (int i = 0; i < pawnCount; i++)
        {
            var registry = pawns![i].Registry;
            if (registry is null || !registry.Scope.Equals(payload.Scope)
                || registry.PawnCustodyId != payload.PawnCustodyIds[i] || registry.AssignmentId != payload.AssignmentIds[i])
                return ErrorCode.OutpostPayloadInvalid;
        }
        return payload.Kind switch
        {
            OutpostPayloadKind.Resources when payload.Resources.Count > 0 && pawnCount == 0 => null,
            OutpostPayloadKind.Workers when payload.Resources.Count == 0 && pawnCount > 0 => null,
            OutpostPayloadKind.ReturnBatch when payload.Resources.Count > 0 || pawnCount > 0 => null,
            _ => ErrorCode.OutpostPayloadInvalid
        };
    }

    private static bool Scope(OutpostScopeMessage? scope) => scope is not null
        && !string.IsNullOrWhiteSpace(scope.WorldInstanceId) && scope.OwnerPlayerId > 0
        && scope.SaveGeneration >= 0 && scope.SaveWriteEpoch >= 0 && !string.IsNullOrWhiteSpace(scope.RestoreBoundary);

    private static bool Uuid(string value) => Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty;

    // DiscardUnknownFields also applies recursively. Size cannot stay equal if any unknown bytes were dropped.
    // Inspect only protected envelopes, not opaque Scribe blobs or unrelated legacy extensions.
    private static bool Unknown<T>(T message, MessageParser<T> parser) where T : IMessage<T> =>
        message.CalculateSize() != parser.WithDiscardUnknownFields(true).ParseFrom(message.ToByteArray()).CalculateSize();
}
