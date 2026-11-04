using Rimlink.Client.Outposts;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class PawnRegistryTests
{
    private const string First = "11111111-1111-1111-1111-111111111111";
    private const string Second = "22222222-2222-2222-2222-222222222222";
    private static PawnRegistryEnvelope Envelope(string id = First, string token = "Thing_Human12") => new()
    {
        SchemaVersion = 1, PawnCustodyId = id, PawnReferenceToken = token,
        Scope = new() { WorldInstanceId = "world-1", OwnerPlayerId = 7, SaveGeneration = 3,
            SaveWriteEpoch = 4, RestoreBoundary = "boundary-1" }
    };

    [Fact]
    public void Duplicate_uuid_with_distinct_objects_is_recovery_required()
    {
        var value = PawnRegistryChecks.Validate([new(Envelope(), "reference-A", "Thing_Human12"),
            new(Envelope(), "reference-B", "Thing_Human12")]);
        Assert.True(value.RecoveryRequired); Assert.Empty(value.Registry);
        Assert.Contains(value.Issues, p => p.Kind == "duplicate-custody" && p.CustodyId == First);
    }

    [Fact]
    public void Same_reference_seen_in_two_containers_is_one_pawn()
    {
        var value = PawnRegistryChecks.Validate([new(Envelope(), "reference-A", "Thing_Human12"),
            new(Envelope(), "reference-A", "Thing_Human12")]);
        Assert.False(value.RecoveryRequired); Assert.Equal(First, Assert.Single(value.Registry).PawnCustodyId);
    }

    [Fact]
    public void Known_dead_or_transferred_tombstone_is_not_corruption()
    {
        var envelope = Envelope(); envelope.Tombstone = true;
        var value = PawnRegistryChecks.Validate([new(envelope, KnownTombstone: true)]);
        Assert.False(value.RecoveryRequired); Assert.True(Assert.Single(value.Registry).Tombstone);
    }

    [Fact]
    public void Id_reuse_does_not_mint_new_uuid()
    {
        var old = Envelope(); old.Tombstone = true;
        var value = PawnRegistryChecks.Validate([new(old, KnownTombstone: true),
            new(Envelope(Second), "new-unrelated-object", "Thing_Human12")]);
        Assert.True(value.RecoveryRequired); Assert.Empty(value.Registry);
        Assert.Contains(value.Issues, p => p.Kind == "reused-reference-token");
    }

    [Fact]
    public void Returned_changed_thing_id_preserves_custody()
    {
        var value = PawnRegistryChecks.Validate([new(Envelope(token: "Thing_Human55"), "new-home-reference", "Thing_Human55")]);
        Assert.False(value.RecoveryRequired); var entry = Assert.Single(value.Registry);
        Assert.Equal(First, entry.PawnCustodyId); Assert.Equal("Thing_Human55", entry.PawnReferenceToken);
    }

    [Fact]
    public void Mirror_never_registers_authority()
    {
        var mirror = Envelope(); mirror.NonAuthoritativeMirror = true;
        var value = PawnRegistryChecks.Validate([new(mirror, "mirror-copy", "Thing_Human12")]);
        Assert.False(value.RecoveryRequired); Assert.Empty(value.Registry);
    }

    [Theory]
    [InlineData("missing-object")]
    [InlineData("missing-tombstone-evidence")]
    [InlineData("reference-changed")]
    [InlineData("schema")]
    [InlineData("uuid")]
    [InlineData("scope")]
    public void Ambiguous_or_malformed_registry_never_exports_a_partial_authority(string fault)
    {
        var envelope = Envelope(); string? identity = "reference-A", token = "Thing_Human12";
        switch (fault)
        {
            case "missing-object": identity = null; break;
            case "missing-tombstone-evidence": identity = null; envelope.Tombstone = true; break;
            case "reference-changed": token = "Thing_Human99"; break;
            case "schema": envelope.SchemaVersion = 2; break;
            case "uuid": envelope.PawnCustodyId = "not-a-guid"; break;
            case "scope": envelope.Scope.RestoreBoundary = ""; break;
        }
        var value = PawnRegistryChecks.Validate([new(envelope, identity, token)]);
        Assert.True(value.RecoveryRequired); Assert.Empty(value.Registry);
    }

    [Fact]
    public void Distinct_uuids_cannot_claim_the_same_actual_object()
    {
        var value = PawnRegistryChecks.Validate([new(Envelope(), "reference-A", "Thing_Human12"),
            new(Envelope(Second), "reference-A", "Thing_Human12")]);
        Assert.True(value.RecoveryRequired); Assert.Empty(value.Registry);
    }

    [Fact]
    public void Valid_export_is_detached_from_mutable_game_observations()
    {
        var envelope = Envelope(); var value = PawnRegistryChecks.Validate([new(envelope, "reference-A", "Thing_Human12")]);
        Assert.False(value.RecoveryRequired); envelope.PawnCustodyId = Second;
        Assert.Equal(First, Assert.Single(value.Registry).PawnCustodyId);
    }
}
