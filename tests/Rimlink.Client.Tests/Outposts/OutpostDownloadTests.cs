using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.Outposts;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostDownloadTests
{
    private static (SaveHeader Header, SaveRecoveryData Recovery, byte[] Hash, LoginReply Authentication) Fixture()
    {
        var f = OutpostRollbackBridgeTests.Fixture();
        var metadata = new OutpostSaveMetadata { SchemaVersion = 1, Scope = f.Bridge.SourceScope.Clone(), Registry = { f.Registry } };
        f.Bridge.LocalFrontierDigest = ByteString.CopyFrom(SHA256.HashData(new OutpostSaveMetadata().ToByteArray()));
        return (new SaveHeader { SnapshotId = f.Downloaded.SnapshotId, RecoveryVersion = 1, SaveGeneration = 3, SaveWriteEpoch = 5, OutpostRollbackBridge = f.Bridge },
            new SaveRecoveryData { Version = 1, SnapshotId = f.Downloaded.SnapshotId, Generation = 3, WriteEpoch = 4, Outposts = new() { Metadata = metadata } },
            f.Downloaded.RawSha256.ToByteArray(), new LoginReply { Player = new() { PlayerId = 1 }, OutpostVersion = 1, SaveGeneration = 3, SaveWriteEpoch = 5,
                OutpostRestoreBoundary = f.Current.RestoreBoundary });
    }

    [Fact]
    public void Only_the_exact_authenticated_download_can_propose_mutable_registry_scope_updates()
    {
        var f = Fixture(); byte[] before = f.Recovery.Outposts.Metadata.ToByteArray();
        var result = OutpostDownloadChecks.Validate(f.Header, f.Recovery, f.Hash, f.Authentication);
        Assert.True(result.Allowed); Assert.Equal(f.Header.OutpostRollbackBridge.CurrentScope, result.Registry[0].Scope);
        Assert.Equal(before, f.Recovery.Outposts.Metadata.ToByteArray()); Assert.Equal(4, f.Recovery.WriteEpoch);
        Assert.Equal(f.Recovery.Outposts.Metadata.Registry[1], result.Registry[1]);
    }

    [Theory]
    [InlineData("auth-owner")][InlineData("auth-generation")][InlineData("auth-epoch")][InlineData("auth-boundary")][InlineData("auth-version")]
    [InlineData("header-generation")][InlineData("header-epoch")][InlineData("header-snapshot")][InlineData("hash")]
    [InlineData("missing-bridge")][InlineData("frontier")][InlineData("registry")][InlineData("missing-auth")]
    public void A_mismatched_header_or_authentication_never_grants_any_registry_updates(string fault)
    {
        var f = Fixture(); LoginReply? authentication = f.Authentication;
        switch (fault)
        {
            case "auth-owner": authentication.Player.PlayerId++; break;
            case "auth-generation": authentication.SaveGeneration++; break;
            case "auth-epoch": authentication.SaveWriteEpoch++; break;
            case "auth-boundary": authentication.OutpostRestoreBoundary = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"; break;
            case "auth-version": authentication.OutpostVersion = 0; break;
            case "header-generation": f.Header.SaveGeneration++; break;
            case "header-epoch": f.Header.SaveWriteEpoch++; break;
            case "header-snapshot": f.Header.SnapshotId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; break;
            case "hash": f.Hash[0] = 7; break;
            case "missing-bridge": f.Header.OutpostRollbackBridge = null; break;
            case "frontier": f.Header.OutpostRollbackBridge.LocalFrontierDigest = ByteString.CopyFrom(new byte[32]); break;
            case "registry": f.Header.OutpostRollbackBridge.MutableRegistry[0].PawnReferenceToken = "Thing_Human99"; break;
            case "missing-auth": authentication = null; break;
        }
        var result = OutpostDownloadChecks.Validate(f.Header, f.Recovery, f.Hash, authentication);
        Assert.False(result.Allowed); Assert.NotNull(result.RefusalKey); Assert.Empty(result.Registry);
    }

    [Fact]
    public void Already_fresh_current_epoch_download_needs_no_reusable_mutation_bridge()
    {
        var f = Fixture(); var metadata = f.Recovery.Outposts.Metadata.Clone(); var bridge = f.Header.OutpostRollbackBridge;
        metadata.Scope = bridge.CurrentScope.Clone(); metadata.RollbackBridge = bridge.Clone(); metadata.Registry[0].Scope = metadata.Scope.Clone();
        var recovery = f.Recovery with { WriteEpoch = 5, Outposts = new() { Metadata = metadata } }; f.Header.OutpostRollbackBridge = null;
        var result = OutpostDownloadChecks.Validate(f.Header, recovery, f.Hash, f.Authentication);
        Assert.True(result.Allowed); Assert.Equal(metadata.Registry, result.Registry);
    }
}
