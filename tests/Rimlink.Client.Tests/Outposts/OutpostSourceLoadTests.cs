using Google.Protobuf;
using Rimlink.Client.Outposts;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostSourceLoadTests
{
    [Fact]
    public void Restored_guild_save_rebases_the_envelope_but_keeps_the_original_delivery_receipt()
    {
        var f = OutpostSourceDownloadTests.Fixture();
        var metadata = f.Recovery.Outposts.Metadata;
        var scope = new GuildColonyScope { WorldInstanceId = metadata.Scope.WorldInstanceId, PlayerId = metadata.Scope.OwnerPlayerId,
            SaveGeneration = metadata.Scope.SaveGeneration, SaveWriteEpoch = metadata.Scope.SaveWriteEpoch, RestoreBoundary = metadata.Scope.RestoreBoundary };
        var receipt = new GuildDeliveryReceipt { SchemaVersion = 1, AttemptId = Guid.NewGuid().ToString("D"), ClaimId = Guid.NewGuid().ToString("D"),
            ParcelId = 1, ClientRequestId = Guid.NewGuid().ToString("D"), PayloadDigest = ByteString.CopyFrom(new byte[32]),
            AuthorizationTransitionId = Guid.NewGuid().ToString("D"), AuthorizationTransitionDigest = ByteString.CopyFrom(new byte[32]),
            AuthorizationIntentDigest = ByteString.CopyFrom(new byte[32]), BeforeSnapshot = new() { Scope = scope.Clone(),
                SnapshotId = Guid.NewGuid().ToString("N"), RawSha256 = ByteString.CopyFrom(new byte[32]) } };
        metadata.GuildMetadata = new() { SchemaVersion = 1, Scope = scope.Clone(), Receipts = { receipt } };
        f.Header.GuildVersion = 1;
        f.Header.OutpostMetadata = metadata.Clone();
        f.Auth.GuildVersion = 1;
        var result = OutpostLoadChecks.Prepare(f.Header, f.Recovery, f.Hash, f.Auth);
        Assert.True(result.Allowed);
        Assert.True(result.RequiresFreshCapture);
        Assert.Equal(OutpostSourceGraphTests.Boundary1, result.Metadata!.GuildMetadata.Scope.RestoreBoundary);
        Assert.Equal(receipt, Assert.Single(result.Metadata.GuildMetadata.Receipts));
        Assert.Equal(scope, metadata.GuildMetadata.Scope);
        f.Auth.GuildVersion = 0;
        Assert.False(OutpostLoadChecks.Prepare(f.Header, f.Recovery, f.Hash, f.Auth).Allowed);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Restore_or_view_load_requires_new_actual_capture_even_when_epoch_did_not_change(bool rollback)
    {
        var f = OutpostSourceDownloadTests.Fixture(rollback);
        var capture = OutpostSourceMetadataTests.Metadata().OrdinaryOutgoing[0]; f.Recovery.Outposts.Metadata.OrdinaryOutgoing.Add(capture);
        byte[] original = f.Recovery.Outposts.Metadata.ToByteArray();
        var decision = OutpostLoadChecks.Prepare(f.Header, f.Recovery, f.Hash, f.Auth);
        Assert.True(decision.Allowed); Assert.True(decision.RequiresFreshCapture); Assert.NotNull(decision.Metadata);
        Assert.Equal(OutpostSourceGraphTests.Boundary1, decision.Metadata.Scope.RestoreBoundary);
        Assert.Equal(rollback ? 9 : 4, decision.Metadata.Scope.SaveWriteEpoch);
        Assert.Equal(capture.ToByteArray(), decision.Metadata.OrdinaryOutgoing[0].ToByteArray());
        Assert.Equal(original, f.Recovery.Outposts.Metadata.ToByteArray()); Assert.Equal(4, f.Recovery.WriteEpoch);
        Assert.Equal(rollback, decision.Metadata.RollbackFromViewBridge is not null);
        Assert.Equal(!rollback, decision.Metadata.RestoreBridge is not null); Assert.Null(decision.Metadata.RollbackBridge);
        Assert.Equal(f.Recovery.Outposts.Metadata.Registry[1], decision.Metadata.Registry[1]);
        Assert.Equal(f.Recovery.Outposts.Metadata.Registry[2], decision.Metadata.Registry[2]);
        decision.Metadata.Registry[0].PawnReferenceToken = "changed-by-caller";
        Assert.Equal("Thing_Human1", f.Recovery.Outposts.Metadata.Registry[0].PawnReferenceToken);
    }

    [Fact]
    public void Current_actual_echo_is_history_only_and_does_not_force_another_capture()
    {
        var f = OutpostSourceDownloadTests.Fixture(); var metadata = OutpostSourceMetadataTests.Metadata();
        var recovery = f.Recovery with { Outposts = new() { Metadata = metadata } }; f.Header.OutpostRestoreBridge = null;
        var result = OutpostLoadChecks.Prepare(f.Header, recovery, f.Hash, f.Auth);
        Assert.True(result.Allowed); Assert.False(result.RequiresFreshCapture); Assert.Equal(metadata, result.Metadata);
        Assert.NotSame(metadata, result.Metadata);
    }

    [Theory]
    [InlineData("authentication")][InlineData("duplicate-root")][InlineData("registry")][InlineData("hash")]
    public void Invalid_loaded_evidence_never_exposes_partial_mutable_metadata(string damage)
    {
        var f = OutpostSourceDownloadTests.Fixture(); byte[] original = f.Recovery.Outposts.Metadata.ToByteArray();
        switch (damage)
        {
            case "authentication": f.Auth.SaveWriteEpoch++; break;
            case "duplicate-root": f.Header.OutpostRollbackBridge = OutpostRollbackBridgeTests.Fixture().Bridge; break;
            case "registry": f.Header.OutpostRestoreBridge.SourceProvenance.AuthorizedViews[0].RegistryMap[0].Before.AssignmentId = "99999999-9999-9999-9999-999999999999"; break;
            case "hash": f.Hash[0] = 1; break;
        }
        var result = OutpostLoadChecks.Prepare(f.Header, f.Recovery, f.Hash, f.Auth);
        Assert.False(result.Allowed); Assert.NotNull(result.RefusalKey); Assert.Null(result.Metadata); Assert.False(result.RequiresFreshCapture);
        Assert.Equal(original, f.Recovery.Outposts.Metadata.ToByteArray());
    }
}
