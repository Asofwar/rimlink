using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.Outposts;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostSourceBridgeTests
{
    internal static OutpostRestoreBridge Restore(OutpostSourceProvenance? graph = null)
    {
        graph ??= OutpostSourceGraphTests.Fixture(); var active = graph.AuthorizedViews.Last();
        return new() { CanonicalSnapshot = graph.ActualSources[0].Snapshot.Clone(), SourceProvenance = graph,
            ActiveAuthorization = new() { TransitionId = active.TransitionId, TransitionDigest = active.TransitionDigest },
            SourcePathDigest = ByteString.CopyFrom(SHA256.HashData(graph.ToByteArray())) };
    }
    internal static OutpostRollbackFromViewBridge Rollback(OutpostSourceProvenance? graph = null)
    {
        graph ??= OutpostSourceGraphTests.Fixture();
        OutpostSourceGraphTests.Append(graph, true, OutpostSourceGraphTests.Boundary1, 9);
        var restore = Restore(graph);
        return new() { CanonicalSnapshot = restore.CanonicalSnapshot, SourceProvenance = graph,
            ActiveAuthorization = restore.ActiveAuthorization, SourcePathDigest = restore.SourcePathDigest };
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Whole_plan_changes_only_authorized_live_scope_and_never_relabels_actual_source(bool rollback)
    {
        var input = OutpostRollbackBridgeTests.Fixture(); var graph = OutpostSourceGraphTests.Fixture();
        SourceBridgeDecision decision; byte[] encoded;
        if (rollback)
        {
            var bridge = Rollback(graph); encoded = bridge.ToByteArray(); var current = graph.AuthorizedViews.Last().EffectiveScope.Clone();
            decision = OutpostSourceBridgeChecks.Validate(bridge, input.Downloaded, current, input.Registry);
            Assert.Equal(encoded, bridge.ToByteArray());
            Assert.Equal(9, decision.Registry[0].Scope.SaveWriteEpoch);
        }
        else
        {
            var bridge = Restore(graph); encoded = bridge.ToByteArray();
            decision = OutpostSourceBridgeChecks.Validate(bridge, input.Downloaded, graph.AuthorizedViews[0].EffectiveScope, input.Registry);
            Assert.Equal(encoded, bridge.ToByteArray());
            Assert.Equal(4, decision.Registry[0].Scope.SaveWriteEpoch);
        }
        Assert.True(decision.Allowed); Assert.Null(decision.RefusalKey);
        var expected = input.Registry[0].Clone(); expected.Scope.RestoreBoundary = OutpostSourceGraphTests.Boundary1;
        expected.Scope.SaveWriteEpoch = rollback ? 9 : 4;
        Assert.Equal(expected, decision.Registry[0]); Assert.Equal(input.Registry[1], decision.Registry[1]); Assert.Equal(input.Registry[2], decision.Registry[2]);
        Assert.Equal(OutpostSourceGraphTests.Boundary0, input.Downloaded.Scope.RestoreBoundary);
        Assert.Equal(OutpostSourceGraphTests.Boundary0, input.Registry[0].Scope.RestoreBoundary);
        decision.Registry[0].PawnReferenceToken = "caller-mutated";
        Assert.Equal("Thing_Human1", input.Registry[0].PawnReferenceToken);
    }

    [Theory]
    [InlineData("proof-scope")][InlineData("proof-hash")][InlineData("proof-id")][InlineData("current-owner")][InlineData("current-epoch")]
    [InlineData("path-digest")][InlineData("root-digest")][InlineData("root-kind")][InlineData("missing-root")][InlineData("canonical-not-in-graph")]
    [InlineData("registry-assignment")][InlineData("registry-token")][InlineData("registry-scope")][InlineData("registry-duplicate")]
    [InlineData("registry-missing")][InlineData("registry-tombstone")][InlineData("registry-mirror")]
    public void No_partial_plan_is_exposed_for_changed_actual_bytes_authentication_or_bindings(string damage)
    {
        var input = OutpostRollbackBridgeTests.Fixture(); var bridge = Restore(); var current = bridge.SourceProvenance.AuthorizedViews[0].EffectiveScope.Clone();
        var registry = input.Registry.ToList();
        switch (damage)
        {
            case "proof-scope": input.Downloaded.Scope.RestoreBoundary = OutpostSourceGraphTests.Boundary1; break;
            case "proof-hash": input.Downloaded.RawSha256 = OutpostSourceGraphTests.Hash(4); break;
            case "proof-id": input.Downloaded.SnapshotId = "99999999999999999999999999999999"; break;
            case "current-owner": current.OwnerPlayerId++; break;
            case "current-epoch": current.SaveWriteEpoch++; break;
            case "path-digest": bridge.SourcePathDigest = OutpostSourceGraphTests.Hash(4); break;
            case "root-digest": bridge.ActiveAuthorization.TransitionDigest = OutpostSourceGraphTests.Hash(4); break;
            case "root-kind": bridge.SourceProvenance.AuthorizedViews[0].Kind = OutpostSourceAuthorizationKind.Rollback; break;
            case "missing-root": bridge.ActiveAuthorization.TransitionId = "99999999-9999-9999-9999-999999999999"; break;
            case "canonical-not-in-graph": bridge.CanonicalSnapshot.SnapshotId = "99999999999999999999999999999999"; break;
            case "registry-assignment": registry[0].AssignmentId = "99999999-9999-9999-9999-999999999999"; break;
            case "registry-token": registry[0].PawnReferenceToken = "wrong"; break;
            case "registry-scope": registry[0].Scope.RestoreBoundary = OutpostSourceGraphTests.Boundary1; break;
            case "registry-duplicate": registry.Add(registry[0].Clone()); break;
            case "registry-missing": registry.RemoveAt(0); break;
            case "registry-tombstone": registry[0].Tombstone = true; break;
            case "registry-mirror": registry[0].NonAuthoritativeMirror = true; break;
        }
        var result = OutpostSourceBridgeChecks.Validate(bridge, input.Downloaded, current, registry);
        Assert.False(result.Allowed); Assert.NotNull(result.RefusalKey); Assert.Empty(result.Registry);
    }

    [Fact]
    public void Actual_fresh_source_retains_historical_graph_but_never_reapplies_it()
    {
        var input = OutpostRollbackBridgeTests.Fixture(); var graph = OutpostSourceGraphTests.Fixture(); var old = graph.AuthorizedViews[0];
        var fresh = new OutpostSnapshotProof { SnapshotId = "99999999999999999999999999999999", RawSha256 = OutpostSourceGraphTests.Hash(7), Scope = old.EffectiveScope.Clone() };
        graph.ActualSources.Add(new OutpostActualSnapshotSource { Snapshot = fresh, LocalFrontierDigest = OutpostSourceGraphTests.Hash(6),
            HistoricalAuthorization = new() { TransitionId = old.TransitionId, TransitionDigest = old.TransitionDigest } });
        var next = OutpostSourceGraphTests.Append(graph, false, OutpostSourceGraphTests.Boundary2, 4);
        next.Source = new() { ActualSnapshotId = fresh.SnapshotId }; next.LocalFrontierDigest = OutpostSourceGraphTests.Hash(6);
        var bridge = Restore(graph); bridge.CanonicalSnapshot = fresh.Clone(); input.Registry[0].Scope = fresh.Scope.Clone();
        var result = OutpostSourceBridgeChecks.Validate(bridge, fresh, next.EffectiveScope, input.Registry);
        Assert.True(result.Allowed); Assert.Equal(OutpostSourceGraphTests.Boundary2, result.Registry[0].Scope.RestoreBoundary);
        Assert.Equal(fresh, bridge.CanonicalSnapshot); Assert.Equal(OutpostSourceGraphTests.Boundary0, graph.ActualSources[0].Snapshot.Scope.RestoreBoundary);
    }

    [Fact]
    public void Rollback_from_actual_fresh_must_use_the_unchanged_v1_contract()
    {
        var input = OutpostRollbackBridgeTests.Fixture(); var bridge = Rollback(); var active = bridge.SourceProvenance.AuthorizedViews.Last();
        active.Source = new() { ActualSnapshotId = input.Downloaded.SnapshotId }; active.SourceScope = input.Downloaded.Scope.Clone();
        active.EffectiveScope.RestoreBoundary = input.Downloaded.Scope.RestoreBoundary; active.PreviousCurrentScope.RestoreBoundary = input.Downloaded.Scope.RestoreBoundary;
        active.RegistryMap[0].Before.Scope = input.Downloaded.Scope.Clone(); active.RegistryMap[0].After.Scope = active.EffectiveScope.Clone();
        bridge.SourcePathDigest = ByteString.CopyFrom(SHA256.HashData(bridge.SourceProvenance.ToByteArray()));
        Assert.Throws<InvalidDataException>(() => OutpostSourceGraph.ValidateRoot(bridge));
        Assert.False(OutpostSourceBridgeChecks.Validate(bridge, input.Downloaded, active.EffectiveScope, input.Registry).Allowed);
        input.Bridge.SourceScope.RestoreBoundary = OutpostSourceGraphTests.Boundary1;
        Assert.False(OutpostRollbackBridgeChecks.Validate(input.Bridge, input.Downloaded, input.Current, input.Registry).Allowed);
    }
}
