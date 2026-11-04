using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostRollbackMetadataTests
{
    private static OutpostSaveMetadata Metadata(bool capture = true)
    {
        var f = OutpostRollbackBridgeTests.Fixture();
        var metadata = new OutpostSaveMetadata { SchemaVersion = 1, Scope = f.Current.Clone(), RollbackBridge = f.Bridge.Clone(), Registry = { f.Registry.Select(r => r.Clone()) } };
        metadata.Registry[0].Scope = f.Current.Clone();
        if (capture)
        {
            var original = new TransferRequest { ClientRequestId = "99999999999999999999999999999999", ToPlayerId = 2,
                Pawns = new() { Pawns = { new PawnBlob { Registry = f.Registry[0].Clone(), ScribeXmlZstd = ByteString.CopyFrom([1, 2, 3]) } } } };
            metadata.OrdinaryOutgoing.Add(OrdinaryCapture.Create(original, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", f.Bridge.SourceScope));
        }
        return metadata;
    }

    [Fact]
    public void Structurally_valid_rollback_preserves_original_capture_and_foreign_tombstone_scopes_without_rebasing()
    {
        var metadata = Metadata(); byte[] original = metadata.ToByteArray();
        new OutpostRecoveryData { Metadata = metadata }.Validate();
        Assert.Equal(original, metadata.ToByteArray()); Assert.Equal(4, metadata.OrdinaryOutgoing[0].Scope.SaveWriteEpoch);
        Assert.Equal(4, metadata.Registry[1].Scope.SaveWriteEpoch); Assert.Equal(4, metadata.Registry[2].Scope.SaveWriteEpoch);
    }

    [Theory]
    [InlineData("no-bridge")][InlineData("world")][InlineData("owner")][InlineData("generation")][InlineData("boundary")][InlineData("future")]
    public void Historical_capture_needs_a_well_formed_same_namespace_bridge_but_the_echo_is_not_server_authority(string fault)
    {
        var metadata = Metadata(); var capture = metadata.OrdinaryOutgoing[0];
        switch (fault)
        {
            case "no-bridge": metadata.RollbackBridge = null; break;
            case "world": capture.Scope.WorldInstanceId = "other"; break;
            case "owner": capture.Scope.OwnerPlayerId++; break;
            case "generation": capture.Scope.SaveGeneration++; break;
            case "boundary": capture.Scope.RestoreBoundary = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"; break;
            case "future": capture.Scope.SaveWriteEpoch = 6; break;
        }
        capture.Pawns[0].Pawn.Registry.Scope = capture.Scope.Clone();
        capture.Pawns[0].BlobSha256 = ByteString.CopyFrom(SHA256.HashData(capture.Pawns[0].Pawn.ToByteArray()));
        Assert.Throws<InvalidDataException>(() => new OutpostRecoveryData { Metadata = metadata }.Validate());
    }

    [Theory]
    [InlineData("operation")][InlineData("transition")][InlineData("digest")][InlineData("frontier")][InlineData("proof")][InlineData("current")][InlineData("epoch")]
    public void Malformed_bridge_is_rejected_even_without_any_historical_capture(string fault)
    {
        var metadata = Metadata(capture: false); var bridge = metadata.RollbackBridge;
        switch (fault)
        {
            case "operation": bridge.LifecycleOperationId = ""; break;
            case "transition": bridge.TransitionId = ""; break;
            case "digest": bridge.TransitionDigest = ByteString.Empty; break;
            case "frontier": bridge.LocalFrontierDigest = ByteString.Empty; break;
            case "proof": bridge.CanonicalSnapshot.RawSha256 = ByteString.Empty; break;
            case "current": bridge.CurrentScope.OwnerPlayerId++; break;
            case "epoch": bridge.SourceScope.SaveWriteEpoch = metadata.Scope.SaveWriteEpoch; bridge.CanonicalSnapshot.Scope = bridge.SourceScope.Clone(); break;
        }
        Assert.Throws<InvalidDataException>(() => new OutpostRecoveryData { Metadata = metadata }.Validate());
    }
}
