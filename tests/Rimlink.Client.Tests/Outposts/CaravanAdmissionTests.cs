using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.Outposts;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class CaravanAdmissionTests
{
    private static CaravanAdmissionFacts Facts(bool remaining = true)
    {
        const string custody = "11111111-1111-1111-1111-111111111111";
        var worker = new OutpostWorkerCapture { PawnCustodyId = custody, AssignmentId = Guid.NewGuid().ToString("D"),
            Pawn = new() { Registry = new() { SchemaVersion = 1, PawnCustodyId = custody, PawnReferenceToken = "Thing_Human1",
                Scope = new() { WorldInstanceId = "world-1", OwnerPlayerId = 1, RestoreBoundary = "b" } }, ScribeXmlZstd = ByteString.CopyFromUtf8("captured inventory") },
            AdultLifeStage = true, FreeColonist = true, PlayerOwned = true, BaselineHuman = true,
            Consciousness = 1, Manipulation = 1, Moving = 1, PlantsCapable = true, MiningCapable = true };
        worker.BlobSha256 = ByteString.CopyFrom(SHA256.HashData(worker.Pawn.ToByteArray()));
        var caravan = new OutpostCaravanAttestation { CaravanId = "Caravan_1", SurfaceId = "planet", Tile = 10,
            RosterReferenceTokens = { "Thing_Human1" }, SelectedCustodyIds = { custody }, SelectedCargoCaptured = true,
            ManifestDigest = ByteString.CopyFrom(new byte[32]), ClientRequestId = Guid.NewGuid().ToString("D"), GameBranchId = Guid.NewGuid().ToString("D") };
        if (remaining) { caravan.RosterReferenceTokens.Add("Thing_Human2"); caravan.RemainingReferenceTokens.Add("Thing_Human2"); caravan.RemainingMobile = true; }
        return new(caravan, [worker], OutpostProfile.Wood);
    }
    [Fact] public void Last_mobile_pawn_cannot_leave_immobile_remaining_group()
    { var f = Facts(); f.Caravan.RemainingMobile = false; Assert.False(CaravanAdmission.Check(f).Allowed); }
    [Fact] public void Empty_caravan_without_unrepresented_cargo_can_be_removed()
    { var f = Facts(false); var d = CaravanAdmission.Check(f); Assert.True(d.Allowed); Assert.True(d.RemoveEmptyCaravan); Assert.False(CaravanAdmission.Check(f with { LooseCargoRemains = true }).Allowed); }
    [Fact] public void Personal_inventory_captured_in_blob_is_not_discarded_loose_cargo()
    { var f = Facts(); Assert.False(f.Caravan.SelectedCargoEmpty); Assert.True(CaravanAdmission.Check(f).Allowed); f.Caravan.SelectedCargoCaptured = false; Assert.False(CaravanAdmission.Check(f).Allowed); }
    [Theory] [InlineData("foreign")] [InlineData("moving")] [InlineData("surface")] [InlineData("manifest")] [InlineData("tile")] [InlineData("roster")] [InlineData("worker")] [InlineData("hash")]
    public void Admission_requires_actual_arrival_and_complete_capture(string fault)
    {
        var f = Facts(); f = fault switch { "foreign" => f with { OwnCaravan = false }, "moving" => f with { Arrived = false },
            "manifest" => f with { ManifestMatches = false }, "tile" => f with { TileAvailable = false }, _ => f };
        if (fault == "surface") f.Caravan.SurfaceId = "orbit";
        if (fault == "roster") f.Caravan.RosterReferenceTokens.Add("Thing_Unknown");
        if (fault == "worker") f.Workers[0].Downed = true;
        if (fault == "hash") f.Workers[0].Pawn.ScribeXmlZstd = ByteString.CopyFromUtf8("changed after capture");
        Assert.False(CaravanAdmission.Check(f).Allowed);
    }
    [Fact] public void Prepared_caravan_change_refuses_removal_without_local_restore()
    { var f = Facts(); var after = f with { Caravan = f.Caravan.Clone() }; after.Caravan.Tile++; Assert.False(CaravanAdmission.Revalidate(f, after).Allowed); Assert.True(CaravanAdmission.Revalidate(f, f).Allowed); }
    [Fact] public void Double_click_uses_one_operation_id_and_no_second_removal_run()
    { var gate = new CaravanAdmissionGate(); Assert.True(gate.TryBegin("world:owner:Caravan_1", out var first)); Assert.False(gate.TryBegin("world:owner:Caravan_1", out var second)); Assert.Equal(first, second); gate.Finish("world:owner:Caravan_1"); Assert.True(gate.TryBegin("world:owner:Caravan_1", out var third)); Assert.NotEqual(first, third); }
    [Fact] public void Intent_digest_covers_the_complete_capture_and_excludes_only_itself()
    { var f = Facts(); var request = new OutpostPrepareRequest { Caravan = f.Caravan, Workers = { f.Workers }, IntentDigest = ByteString.CopyFromUtf8("old") }; var plain = request.Clone(); plain.IntentDigest = ByteString.Empty;
        Assert.Equal(SHA256.HashData(plain.ToByteArray()), CaravanAdmission.IntentDigest(request).ToByteArray()); request.Caravan.Tile++; Assert.NotEqual(CaravanAdmission.IntentDigest(plain), CaravanAdmission.IntentDigest(request)); }
}
