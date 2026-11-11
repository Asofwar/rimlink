using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class GuildResourceContractsTests
{
    [Theory]
    [InlineData("Steel")][InlineData("WoodLog")]
    public void Typed_resource_round_trips_without_a_scribe_blob_or_pawn_registry(string resource)
    {
        var payload = Payload(); payload.DefName = resource;
        var decoded = GuildResourceParcelPayload.Parser.ParseFrom(payload.ToByteArray());
        Assert.Equal(1900, GuildResourceContracts.Validate(decoded));
        var parcel = Parcel.Parser.ParseFrom(new Parcel { Kind = ParcelKind.ParcelGuildResources, GuildResourcePayload = payload }.ToByteArray());
        Assert.Equal(decoded, parcel.GuildResourcePayload); Assert.Null(parcel.Items); Assert.Null(parcel.Pawns); Assert.Null(parcel.OutpostPayload);
    }

    [Theory]
    [InlineData("def")][InlineData("count")][InlineData("overflow")][InlineData("scope")]
    [InlineData("revision")][InlineData("manifest")][InlineData("version")][InlineData("origin")]
    public void Invalid_payload_is_refused_before_materialization(string change)
    {
        var payload = Payload();
        if (change == "def") payload.DefName = "Gold";
        if (change == "count") payload.Count = 1001;
        if (change == "overflow") payload.UnitValueX100 = long.MaxValue;
        if (change == "scope") payload.Recipient.PlayerId = 0;
        if (change == "revision") payload.OwnerRevision = 0;
        if (change == "manifest") payload.ManifestDigest = ByteString.Empty;
        if (change == "version") payload.SchemaVersion = 2;
        if (change == "origin") payload.OriginKind = "PlayerPawn";
        Assert.Throws<InvalidDataException>(() => GuildResourceContracts.Validate(payload));
    }

    private static GuildResourceParcelPayload Payload() => new()
    {
        SchemaVersion = 1, ClaimId = Guid.NewGuid().ToString("D"), AllianceId = 50,
        OriginKind = "Outpost", OriginId = Guid.NewGuid().ToString("D"), OwnerRevision = 1,
        Recipient = new() { WorldInstanceId = "world", PlayerId = 1, RestoreBoundary = Guid.NewGuid().ToString("D") },
        DefName = "Steel", Count = 10, UnitValueX100 = 190,
        ManifestId = Guid.NewGuid().ToString("D"), ManifestDigest = ByteString.CopyFrom(new byte[32]),
        ValuationRevision = 1, TransitionId = Guid.NewGuid().ToString("D")
    };
}
