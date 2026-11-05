using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostSourceGraphTests
{
    internal const string Boundary0 = "11111111-1111-1111-1111-111111111111";
    internal const string Boundary1 = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    internal const string Boundary2 = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    internal static ByteString Hash(byte value) => ByteString.CopyFrom(Enumerable.Repeat(value, 32).ToArray());
    internal static OutpostSourceProvenance Fixture()
    {
        var old = OutpostRollbackBridgeTests.Fixture();
        var target = old.Downloaded.Scope.Clone(); target.RestoreBoundary = Boundary1;
        var after = old.Registry[0].Clone(); after.Scope = target.Clone();
        return new() { SchemaVersion = 1,
            ActualSources = { new OutpostActualSnapshotSource { Snapshot = old.Downloaded.Clone(), LocalFrontierDigest = Hash(9) } },
            AuthorizedViews = { new OutpostAuthorizedSnapshotView {
                Kind = OutpostSourceAuthorizationKind.Restore, LifecycleOperationId = "77777777-7777-7777-7777-777777777777",
                TransitionId = "88888888-8888-8888-8888-888888888888", TransitionDigest = Hash(8),
                Source = new() { ActualSnapshotId = old.Downloaded.SnapshotId }, SourceScope = old.Downloaded.Scope.Clone(), EffectiveScope = target,
                LocalFrontierDigest = Hash(9), SelectedCutHeadDigest = Hash(2), SelectedManifestDigest = Hash(3),
                RegistryMap = { new OutpostRegistryProjection { Before = old.Registry[0].Clone(), After = after } } } } };
    }

    internal static OutpostAuthorizedSnapshotView Append(OutpostSourceProvenance graph, bool rollback, string boundary, long epoch)
    {
        var previous = graph.AuthorizedViews.Last(); var source = previous.EffectiveScope.Clone(); var target = source.Clone();
        target.RestoreBoundary = boundary; target.SaveWriteEpoch = epoch;
        var current = source.Clone(); current.SaveWriteEpoch = epoch - 1;
        var view = new OutpostAuthorizedSnapshotView { Kind = rollback ? OutpostSourceAuthorizationKind.Rollback : OutpostSourceAuthorizationKind.Restore,
            LifecycleOperationId = Guid.NewGuid().ToString("D"), TransitionId = Guid.NewGuid().ToString("D"), TransitionDigest = Hash(4),
            Source = new() { AuthorizedView = new() { TransitionId = previous.TransitionId, TransitionDigest = previous.TransitionDigest } },
            SourceScope = source, EffectiveScope = target, LocalFrontierDigest = previous.LocalFrontierDigest,
            SelectedCutHeadDigest = rollback ? ByteString.Empty : Hash(2), SelectedManifestDigest = rollback ? ByteString.Empty : Hash(3),
            PreviousCurrentScope = rollback ? current : null };
        foreach (var map in previous.RegistryMap)
        {
            var after = map.After.Clone(); after.Scope = target.Clone();
            view.RegistryMap.Add(new OutpostRegistryProjection { Before = map.After.Clone(), After = after });
        }
        graph.AuthorizedViews.Add(view); return view;
    }

    [Theory]
    [InlineData("restore")][InlineData("restore-rollback")][InlineData("rollback-restore")][InlineData("restore-restore")][InlineData("actual-fresh")]
    public void Actual_and_authorized_views_keep_distinct_scope_types_and_validate_flat_history(string path)
    {
        var graph = Fixture(); var original = graph.ActualSources[0].Snapshot.ToByteArray();
        if (path == "restore-rollback") Append(graph, true, Boundary1, 9);
        if (path == "rollback-restore")
        {
            var first = graph.AuthorizedViews[0]; first.Kind = OutpostSourceAuthorizationKind.Rollback;
            first.EffectiveScope.RestoreBoundary = Boundary0; first.EffectiveScope.SaveWriteEpoch = 9;
            first.PreviousCurrentScope = first.SourceScope.Clone(); first.PreviousCurrentScope.SaveWriteEpoch = 8;
            first.SelectedCutHeadDigest = ByteString.Empty; first.SelectedManifestDigest = ByteString.Empty;
            first.RegistryMap[0].After.Scope = first.EffectiveScope.Clone(); Append(graph, false, Boundary1, 9);
        }
        if (path == "restore-restore") Append(graph, false, Boundary2, 4);
        if (path == "actual-fresh")
        {
            var old = graph.AuthorizedViews[0];
            var fresh = new OutpostActualSnapshotSource { Snapshot = new() { Scope = old.EffectiveScope.Clone(), SnapshotId = "99999999999999999999999999999999", RawSha256 = Hash(7) },
                LocalFrontierDigest = old.LocalFrontierDigest, HistoricalAuthorization = new() { TransitionId = old.TransitionId, TransitionDigest = old.TransitionDigest } };
            graph.ActualSources.Add(fresh); var next = Append(graph, true, Boundary1, 9); next.Source = new() { ActualSnapshotId = fresh.Snapshot.SnapshotId };
        }
        OutpostSourceGraph.Validate(graph);
        Assert.Equal(original, graph.ActualSources[0].Snapshot.ToByteArray());
        Assert.Equal(Boundary0, graph.ActualSources[0].Snapshot.Scope.RestoreBoundary);
        Assert.Equal(4, graph.ActualSources[0].Snapshot.Scope.SaveWriteEpoch);
    }

    [Theory]
    [InlineData("version")][InlineData("duplicate-source")][InlineData("duplicate-view")][InlineData("kind")][InlineData("missing-source")]
    [InlineData("cycle")][InlineData("historical-cycle")][InlineData("predecessor-digest")][InlineData("frontier")][InlineData("source-scope")]
    [InlineData("owner")][InlineData("generation")][InlineData("restore-epoch")][InlineData("same-boundary")][InlineData("cut")]
    [InlineData("manifest")][InlineData("null-map-scope")][InlineData("assignment")][InlineData("reference")][InlineData("mirror")][InlineData("tombstone")]
    [InlineData("duplicate-map")][InlineData("rollback-boundary")][InlineData("rollback-no-advance")][InlineData("rollback-target-from-future")]
    public void Unknown_conflicting_or_semantically_wrong_edges_fail_closed(string damage)
    {
        var graph = Fixture(); var view = graph.AuthorizedViews[0]; var map = view.RegistryMap[0];
        switch (damage)
        {
            case "version": graph.SchemaVersion = 2; break;
            case "duplicate-source": graph.ActualSources.Add(graph.ActualSources[0].Clone()); break;
            case "duplicate-view": graph.AuthorizedViews.Add(view.Clone()); break;
            case "kind": view.Kind = (OutpostSourceAuthorizationKind)99; break;
            case "missing-source": view.Source.ActualSnapshotId = "99999999999999999999999999999999"; break;
            case "cycle": view.Source = new() { AuthorizedView = new() { TransitionId = view.TransitionId, TransitionDigest = view.TransitionDigest } }; break;
            case "historical-cycle": graph.ActualSources[0].HistoricalAuthorization = new() { TransitionId = view.TransitionId, TransitionDigest = view.TransitionDigest }; break;
            case "predecessor-digest": var second = Append(graph, false, Boundary2, 4); second.Source.AuthorizedView.TransitionDigest = Hash(0); break;
            case "frontier": view.LocalFrontierDigest = Hash(0); break;
            case "source-scope": view.SourceScope.RestoreBoundary = Boundary2; break;
            case "owner": view.EffectiveScope.OwnerPlayerId++; break;
            case "generation": view.EffectiveScope.SaveGeneration++; break;
            case "restore-epoch": view.EffectiveScope.SaveWriteEpoch++; break;
            case "same-boundary": view.EffectiveScope.RestoreBoundary = Boundary0; break;
            case "cut": view.SelectedCutHeadDigest = ByteString.Empty; break;
            case "manifest": view.SelectedManifestDigest = ByteString.Empty; break;
            case "null-map-scope": map.Before.Scope = null; break;
            case "assignment": map.After.AssignmentId = "cccccccc-cccc-cccc-cccc-cccccccccccc"; break;
            case "reference": map.After.PawnReferenceToken = "different"; break;
            case "mirror": map.Before.NonAuthoritativeMirror = true; break;
            case "tombstone": map.Before.Tombstone = true; break;
            case "duplicate-map": view.RegistryMap.Add(map.Clone()); break;
            case "rollback-boundary": Append(graph, true, Boundary2, 9); break;
            case "rollback-no-advance": var flat = Append(graph, true, Boundary1, 4); flat.PreviousCurrentScope.SaveWriteEpoch = 4; break;
            case "rollback-target-from-future": var future = Append(graph, true, Boundary1, 9); future.PreviousCurrentScope.SaveWriteEpoch = 3; break;
        }
        Assert.Throws<InvalidDataException>(() => OutpostSourceGraph.Validate(graph));
    }

    [Theory]
    [InlineData(16, true)][InlineData(17, false)]
    public void Authorization_depth_is_bounded_without_wire_recursion(int depth, bool allowed)
    {
        var graph = Fixture();
        for (int i = 1; i < depth; i++) Append(graph, false, Guid.NewGuid().ToString("D"), 4);
        if (allowed) OutpostSourceGraph.Validate(graph);
        else Assert.Throws<InvalidDataException>(() => OutpostSourceGraph.Validate(graph));
    }

    [Theory]
    [InlineData(256, true)][InlineData(257, false)]
    public void Every_distinct_actual_or_authorization_node_counts(int nodes, bool allowed)
    {
        var graph = Fixture();
        for (int i = 2; i < nodes; i++)
        {
            var source = graph.ActualSources[0].Clone(); source.Snapshot.SnapshotId = i.ToString("x32"); graph.ActualSources.Add(source);
        }
        if (allowed) OutpostSourceGraph.Validate(graph);
        else Assert.Throws<InvalidDataException>(() => OutpostSourceGraph.Validate(graph));
    }

    [Theory]
    [InlineData(10_000, false, true)][InlineData(10_001, false, false)][InlineData(10_000, true, false)]
    public void New_projected_maps_have_independent_aggregate_and_wire_budgets(int entries, bool large, bool allowed)
    {
        var graph = Fixture(); var view = graph.AuthorizedViews[0]; var template = view.RegistryMap[0].Clone(); view.RegistryMap.Clear();
        for (int i = 1; i <= entries; i++)
        {
            var map = template.Clone(); map.Before.PawnCustodyId = Guid.ParseExact(i.ToString("x32"), "N").ToString("D");
            map.Before.AssignmentId = ""; map.Before.PawnReferenceToken = (large ? new string('p', 240) : "p") + i;
            map.After = map.Before.Clone(); map.After.Scope = view.EffectiveScope.Clone(); view.RegistryMap.Add(map);
        }
        if (large) Assert.True(graph.CalculateSize() > 4 * 1024 * 1024);
        if (allowed) OutpostSourceGraph.Validate(graph);
        else Assert.Throws<InvalidDataException>(() => OutpostSourceGraph.Validate(graph));
    }
}
