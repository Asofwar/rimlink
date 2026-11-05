using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.Outposts;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostV1SourceSupplementTests
{
    internal static (OutpostRollbackBridge Bridge, SaveRecoveryData Original, byte[] Hash, LoginReply Auth) Fixture()
    {
        var metadata = OutpostSourceMetadataTests.Metadata(); var graph = metadata.RestoreBridge.SourceProvenance.Clone(); var old = graph.AuthorizedViews[0];
        var fresh = new OutpostActualSnapshotSource { Snapshot = new() { SnapshotId = "99999999999999999999999999999999",
            RawSha256 = OutpostSourceGraphTests.Hash(7), Scope = old.EffectiveScope.Clone() },
            LocalFrontierDigest = ByteString.CopyFrom(SHA256.HashData(new OutpostSaveMetadata().ToByteArray())),
            HistoricalAuthorization = new() { TransitionId = old.TransitionId, TransitionDigest = old.TransitionDigest } };
        graph.ActualSources.Add(fresh); var next = OutpostSourceGraphTests.Append(graph, true, OutpostSourceGraphTests.Boundary1, 9);
        next.Source = new() { ActualSnapshotId = fresh.Snapshot.SnapshotId }; next.LocalFrontierDigest = fresh.LocalFrontierDigest;
        var bridge = new OutpostRollbackBridge { LifecycleOperationId = next.LifecycleOperationId, TransitionId = next.TransitionId,
            TransitionDigest = next.TransitionDigest, CanonicalSnapshot = fresh.Snapshot.Clone(), SourceScope = fresh.Snapshot.Scope.Clone(),
            CurrentScope = next.EffectiveScope.Clone(), LocalFrontierDigest = fresh.LocalFrontierDigest,
            MutableRegistry = { next.RegistryMap.Select(m => m.Before.Clone()) }, SourceProvenance = graph,
            SourcePathDigest = ByteString.CopyFrom(SHA256.HashData(graph.ToByteArray())) };
        return (bridge, new SaveRecoveryData { Version = 1, Generation = 3, WriteEpoch = 4, SnapshotId = fresh.Snapshot.SnapshotId,
            Outposts = new() { Metadata = metadata } }, fresh.Snapshot.RawSha256.ToByteArray(),
            new LoginReply { Player = new() { PlayerId = 1 }, SaveGeneration = 3, SaveWriteEpoch = 9, OutpostVersion = 1,
                OutpostRestoreBoundary = OutpostSourceGraphTests.Boundary1 });
    }
    internal static SaveHeader Header(OutpostRollbackBridge bridge) => new() { OutpostRollbackBridge = bridge, OutpostVersion = 1,
        OutpostRestoreBoundary = bridge.CurrentScope.RestoreBoundary, SnapshotId = bridge.CanonicalSnapshot.SnapshotId,
        RecoveryVersion = 1, SaveGeneration = 3, SaveWriteEpoch = 9 };

    [Fact]
    public void Same_v1_grant_keeps_immediate_actual_S1_and_full_earlier_restore_closure()
    {
        var f = Fixture(); byte[] original = f.Bridge.ToByteArray(); var view = OutpostSourceGraph.ValidateRoot(f.Bridge);
        Assert.Equal(f.Bridge.TransitionId, view.TransitionId); Assert.Equal(f.Bridge.LifecycleOperationId, view.LifecycleOperationId);
        Assert.Equal("99999999999999999999999999999999", view.Source.ActualSnapshotId);
        Assert.Equal(OutpostSourceGraphTests.Boundary1, f.Bridge.CanonicalSnapshot.Scope.RestoreBoundary);
        Assert.Equal(OutpostSourceGraphTests.Boundary0, f.Bridge.SourceProvenance.ActualSources[0].Snapshot.Scope.RestoreBoundary);
        Assert.Equal(original, f.Bridge.ToByteArray());
        Assert.True(OutpostRollbackBridgeChecks.Validate(f.Bridge, f.Bridge.CanonicalSnapshot, f.Bridge.CurrentScope,
            f.Original.Outposts.Metadata.Registry).Allowed);
    }

    [Theory]
    [InlineData("missing-graph")][InlineData("missing-digest")][InlineData("different-digest")][InlineData("operation")][InlineData("transition")]
    [InlineData("transition-digest")][InlineData("canonical-oldest")][InlineData("source-scope")][InlineData("current-scope")]
    [InlineData("frontier")][InlineData("registry")][InlineData("view-not-actual")]
    public void Graph_mirror_must_be_the_exact_same_v1_actual_source_grant(string damage)
    {
        var f = Fixture(); var q = f.Bridge;
        switch (damage)
        {
            case "missing-graph": q.SourceProvenance = null; break;
            case "missing-digest": q.SourcePathDigest = ByteString.Empty; break;
            case "different-digest": q.SourcePathDigest = OutpostSourceGraphTests.Hash(3); break;
            case "operation": q.LifecycleOperationId = "cccccccc-cccc-cccc-cccc-cccccccccccc"; break;
            case "transition": q.TransitionId = "cccccccc-cccc-cccc-cccc-cccccccccccc"; break;
            case "transition-digest": q.TransitionDigest = OutpostSourceGraphTests.Hash(1); break;
            case "canonical-oldest": q.CanonicalSnapshot = q.SourceProvenance.ActualSources[0].Snapshot.Clone(); break;
            case "source-scope": q.SourceScope.SaveWriteEpoch--; break;
            case "current-scope": q.CurrentScope.SaveWriteEpoch++; break;
            case "frontier": q.LocalFrontierDigest = OutpostSourceGraphTests.Hash(1); break;
            case "registry": q.MutableRegistry[0].PawnReferenceToken = "wrong"; break;
            case "view-not-actual":
                var view = q.SourceProvenance.AuthorizedViews.Last(); var previous = q.SourceProvenance.AuthorizedViews[0];
                view.Source = new() { AuthorizedView = new() { TransitionId = previous.TransitionId, TransitionDigest = previous.TransitionDigest } };
                view.LocalFrontierDigest = previous.LocalFrontierDigest; q.LocalFrontierDigest = previous.LocalFrontierDigest;
                q.SourcePathDigest = ByteString.CopyFrom(SHA256.HashData(q.SourceProvenance.ToByteArray())); break;
        }
        Assert.Throws<InvalidDataException>(() => OutpostSourceGraph.ValidateRoot(q));
    }

    [Theory]
    [InlineData("missing-historical-ref")][InlineData("different-embedded-map")][InlineData("wrong-embedded-root")]
    [InlineData("no-embedded-root")][InlineData("required-supplement-missing")]
    public void Header_graph_must_join_the_exact_root_embedded_in_actual_S1(string damage)
    {
        var f = Fixture(); var graph = f.Bridge.SourceProvenance;
        switch (damage)
        {
            case "missing-historical-ref": graph.ActualSources.Last().HistoricalAuthorization = null; break;
            case "different-embedded-map":
                var embedded = f.Original.Outposts.Metadata.RestoreBridge;
                embedded.SourceProvenance.AuthorizedViews[0].SelectedManifestDigest = OutpostSourceGraphTests.Hash(99);
                embedded.SourcePathDigest = ByteString.CopyFrom(SHA256.HashData(embedded.SourceProvenance.ToByteArray())); break;
            case "wrong-embedded-root":
                var other = graph.AuthorizedViews[0].Clone(); other.TransitionId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
                graph.AuthorizedViews.Add(other); graph.ActualSources.Last().HistoricalAuthorization = new()
                    { TransitionId = other.TransitionId, TransitionDigest = other.TransitionDigest }; break;
            case "no-embedded-root":
                f.Original.Outposts.Metadata.RestoreBridge = null; f.Original.Outposts.Metadata.OrdinaryOutgoing.Clear(); break;
            case "required-supplement-missing": f.Bridge.SourceProvenance = null; break;
        }
        f.Bridge.SourcePathDigest = f.Bridge.SourceProvenance is null ? ByteString.Empty : ByteString.CopyFrom(SHA256.HashData(graph.ToByteArray()));
        Assert.False(OutpostDownloadChecks.Validate(Header(f.Bridge), f.Original, f.Hash, f.Auth).Allowed);
    }

    [Fact]
    public void Fresh_S2_keeps_one_Q_root_and_original_B0_capture_without_reapplying_R1()
    {
        var f = Fixture(); byte[] original = f.Original.Outposts.Metadata.ToByteArray();
        var loaded = OutpostLoadChecks.Prepare(Header(f.Bridge), f.Original, f.Hash, f.Auth);
        Assert.True(loaded.Allowed); Assert.True(loaded.RequiresFreshCapture);
        Assert.Null(loaded.Metadata!.RestoreBridge); Assert.Null(loaded.Metadata.RollbackFromViewBridge); Assert.Equal(f.Bridge, loaded.Metadata.RollbackBridge);
        Assert.Equal(9, loaded.Metadata.Scope.SaveWriteEpoch); Assert.Equal(OutpostSourceGraphTests.Boundary1, loaded.Metadata.Scope.RestoreBoundary);
        Assert.Equal(f.Original.Outposts.Metadata.OrdinaryOutgoing, loaded.Metadata.OrdinaryOutgoing);
        Assert.Equal(OutpostSourceGraphTests.Boundary0, loaded.Metadata.OrdinaryOutgoing[0].Scope.RestoreBoundary);
        Assert.Equal(original, f.Original.Outposts.Metadata.ToByteArray());
    }
}
