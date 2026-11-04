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
                if (!Uuid(registry.PawnCustodyId) || registry.AssignmentId.Length > 0 && !Uuid(registry.AssignmentId) || !Scope(registry.Scope)
                    || string.IsNullOrWhiteSpace(registry.PawnReferenceToken)
                    || registry.NonAuthoritativeMirror || registry.Tombstone
                    || !identities.Add(Guid.Parse(registry.PawnCustodyId))
                    || registry.AssignmentId.Length > 0 && !assignments.Add(Guid.Parse(registry.AssignmentId)))
                    return ErrorCode.OutpostPayloadInvalid;
            }
        }
        if (payload is null)
            return parcel.Kind == ParcelKind.ParcelOutpost ? ErrorCode.OutpostPayloadInvalid : null;
        if (payload.SchemaVersion != SupportedVersion || Unknown(payload, OutpostParcelPayload.Parser)
            || payload.Kind is not (OutpostPayloadKind.Resources or OutpostPayloadKind.Workers or OutpostPayloadKind.ReturnBatch)
            || payload.Resources.Any(r => r.SchemaVersion != SupportedVersion))
            return ErrorCode.OutpostPayloadUnsupported;
        bool production = payload.ManifestId.Length > 0 || payload.OutpostId.Length > 0 || payload.Resources.Count > 0;
        if (!Scope(payload.Scope) || !Uuid(payload.BatchOperationId) || payload.PayloadDigest.Length != 32
            || production && (!Uuid(payload.OutpostId) || !Uuid(payload.ManifestId) || payload.ManifestDigest.Length != 32 || payload.ValuationRevision <= 0
                || parcel.Items?.Items.Count > 0 || parcel.Silver != 0 || parcel.Kind is not (ParcelKind.ParcelOutpost or ParcelKind.ParcelLotReturn))
            || !production && (payload.ManifestDigest.Length != 0 || payload.ValuationRevision != 0 || payload.Kind != OutpostPayloadKind.Workers)
            || payload.ChildCount is < 0 or > 4 || payload.ChildIndex < 0 || payload.ChildCount > 0 && payload.ChildIndex >= payload.ChildCount
            || payload.Resources.Any(r => r.DefName is not ("WoodLog" or "Steel") || r.Count <= 0 || r.ImportValueX100 < 0)
            || payload.Resources.Select(r => r.DefName).Distinct(StringComparer.Ordinal).Count() != payload.Resources.Count)
            return ErrorCode.OutpostPayloadInvalid;

        var taggedPawns = pawns?.Where(p => p.Registry is not null).ToArray() ?? [];
        int pawnCount = pawns?.Count ?? 0;
        if (production && pawnCount != taggedPawns.Length || payload.PawnCustodyIds.Count != taggedPawns.Length
            || !payload.AssignmentIds.SequenceEqual(taggedPawns.Select(p => p.Registry.AssignmentId).Where(id => id.Length > 0)))
            return ErrorCode.OutpostPayloadInvalid;
        for (int i = 0; i < taggedPawns.Length; i++)
        {
            var registry = taggedPawns[i].Registry;
            // Ordinary proven handoffs preserve original source scope; authorization binds the target separately.
            if (registry.Scope.WorldInstanceId != payload.Scope.WorldInstanceId || production && !registry.Scope.Equals(payload.Scope)
                || registry.PawnCustodyId != payload.PawnCustodyIds[i]) return ErrorCode.OutpostPayloadInvalid;
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

    private static bool Uuid(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty
        && (value == id.ToString("N") || value == id.ToString("D"));

    // DiscardUnknownFields also applies recursively. Size cannot stay equal if any unknown bytes were dropped.
    // Inspect only protected envelopes, not opaque Scribe blobs or unrelated legacy extensions.
    private static bool Unknown<T>(T message, MessageParser<T> parser) where T : IMessage<T> =>
        message.CalculateSize() != parser.WithDiscardUnknownFields(true).ParseFrom(message.ToByteArray()).CalculateSize();
}
