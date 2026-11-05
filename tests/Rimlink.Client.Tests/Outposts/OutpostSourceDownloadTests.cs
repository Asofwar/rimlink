using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.Outposts;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostSourceDownloadTests
{
    internal static (SaveHeader Header, SaveRecoveryData Recovery, byte[] Hash, LoginReply Auth) Fixture(bool rollback = false)
    {
        var input = OutpostRollbackBridgeTests.Fixture(); var graph = OutpostSourceGraphTests.Fixture();
        ByteString frontier = ByteString.CopyFrom(SHA256.HashData(new OutpostSaveMetadata().ToByteArray()));
        graph.ActualSources[0].LocalFrontierDigest = frontier; graph.AuthorizedViews[0].LocalFrontierDigest = frontier;
        var restored = rollback ? null : OutpostSourceBridgeTests.Restore(graph);
        var rolledBack = rollback ? OutpostSourceBridgeTests.Rollback(graph) : null;
        var current = graph.AuthorizedViews.Last().EffectiveScope;
        return (new SaveHeader { SnapshotId = input.Downloaded.SnapshotId, RecoveryVersion = 1, SaveGeneration = 3,
                SaveWriteEpoch = current.SaveWriteEpoch, OutpostVersion = 1, OutpostRestoreBoundary = current.RestoreBoundary,
                OutpostRestoreBridge = restored, OutpostRollbackFromViewBridge = rolledBack },
            new SaveRecoveryData { Version = 1, SnapshotId = input.Downloaded.SnapshotId, Generation = 3, WriteEpoch = 4,
                Outposts = new() { Metadata = new() { SchemaVersion = 1, Scope = input.Downloaded.Scope.Clone(), Registry = { input.Registry } } } },
            input.Downloaded.RawSha256.ToByteArray(), new LoginReply { Player = new() { PlayerId = 1 }, OutpostVersion = 1, SaveGeneration = 3,
                SaveWriteEpoch = current.SaveWriteEpoch, OutpostRestoreBoundary = current.RestoreBoundary });
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Authenticated_header_uses_one_active_root_and_actual_original_bytes(bool rollback)
    {
        var f = Fixture(rollback); byte[] original = f.Recovery.Outposts.Metadata.ToByteArray();
        var result = OutpostDownloadChecks.Validate(f.Header, f.Recovery, f.Hash, f.Auth);
        Assert.True(result.Allowed); Assert.Equal(OutpostSourceGraphTests.Boundary1, result.Registry[0].Scope.RestoreBoundary);
        Assert.Equal(rollback ? 9 : 4, result.Registry[0].Scope.SaveWriteEpoch);
        Assert.Equal(original, f.Recovery.Outposts.Metadata.ToByteArray()); Assert.Equal(4, f.Recovery.WriteEpoch);
    }

    [Theory]
    [InlineData("v1-plus-restore")][InlineData("restore-plus-view")][InlineData("legacy-with-root")][InlineData("header-boundary")]
    [InlineData("header-boundary-empty")][InlineData("auth-owner")][InlineData("auth-epoch")][InlineData("auth-boundary")]
    [InlineData("frontier")][InlineData("raw-hash")][InlineData("snapshot-id")][InlineData("registry")][InlineData("different-canonical-source")]
    public void Any_wrong_active_header_cut_refuses_without_priority_or_partial_application(string damage)
    {
        var f = Fixture(); var recovery = f.Recovery;
        switch (damage)
        {
            case "v1-plus-restore": f.Header.OutpostRollbackBridge = OutpostRollbackBridgeTests.Fixture().Bridge; break;
            case "restore-plus-view": f.Header.OutpostRollbackFromViewBridge = OutpostSourceBridgeTests.Rollback(); break;
            case "legacy-with-root": recovery = recovery with { Outposts = new() }; break;
            case "header-boundary": f.Header.OutpostRestoreBoundary = OutpostSourceGraphTests.Boundary2; break;
            case "header-boundary-empty": f.Header.OutpostRestoreBoundary = ""; break;
            case "auth-owner": f.Auth.Player.PlayerId++; break;
            case "auth-epoch": f.Auth.SaveWriteEpoch++; break;
            case "auth-boundary": f.Auth.OutpostRestoreBoundary = OutpostSourceGraphTests.Boundary2; break;
            case "frontier": recovery.Outposts.Metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = "cccccccc-cccc-cccc-cccc-cccccccccccc", TransitionDigest = OutpostSourceGraphTests.Hash(5) }); break;
            case "raw-hash": f.Hash[0] = 1; break;
            case "snapshot-id": f.Header.SnapshotId = "99999999999999999999999999999999"; break;
            case "different-canonical-source":
                var graph = f.Header.OutpostRestoreBridge.SourceProvenance;
                graph.ActualSources[0].Snapshot.SnapshotId = "99999999999999999999999999999999";
                graph.AuthorizedViews[0].Source.ActualSnapshotId = graph.ActualSources[0].Snapshot.SnapshotId;
                f.Header.OutpostRestoreBridge.CanonicalSnapshot = graph.ActualSources[0].Snapshot.Clone();
                f.Header.OutpostRestoreBridge.SourcePathDigest = ByteString.CopyFrom(SHA256.HashData(graph.ToByteArray())); break;
            case "registry": recovery.Outposts.Metadata.Registry[0].PawnReferenceToken = "wrong"; break;
        }
        var result = OutpostDownloadChecks.Validate(f.Header, recovery, f.Hash, f.Auth);
        Assert.False(result.Allowed); Assert.NotNull(result.RefusalKey); Assert.Empty(result.Registry);
    }

    [Fact]
    public void Already_actual_fresh_download_does_not_reactivate_the_echoed_restore_root()
    {
        var f = Fixture(); var metadata = OutpostSourceMetadataTests.Metadata(false);
        var recovery = f.Recovery with { Outposts = new() { Metadata = metadata }, SnapshotId = "99999999999999999999999999999999" };
        f.Header.OutpostRestoreBridge = null; f.Header.SnapshotId = recovery.SnapshotId;
        var result = OutpostDownloadChecks.Validate(f.Header, recovery, f.Hash, f.Auth);
        Assert.True(result.Allowed); Assert.Equal(metadata.Registry, result.Registry);
    }
}
