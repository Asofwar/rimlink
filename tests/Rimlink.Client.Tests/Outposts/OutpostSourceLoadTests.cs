using Google.Protobuf;
using Rimlink.Client.Outposts;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostSourceLoadTests
{
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
