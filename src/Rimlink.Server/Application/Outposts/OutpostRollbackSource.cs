using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

internal sealed record RollbackSourceSelection(OutpostSnapshotProof Actual, LifecycleRollbackSource? Authorization,
    OutpostSaveMetadata ValidationMetadata);

public sealed partial class OutpostRestoreEvidence
{
    internal async Task<RollbackSourceSelection> ResolveRollbackSourceAsync(SaveVersion target, OutpostScopeMessage current, CancellationToken ct)
    {
        var resolver = new SourceResolver(this, ct); var actual = await resolver.ActualAsync(target);
        OutpostSourceReference reference;
        var physical = actual.Snapshot.Scope;
        if (physical.WorldInstanceId != current.WorldInstanceId || physical.OwnerPlayerId != current.OwnerPlayerId
            || physical.SaveGeneration != current.SaveGeneration || physical.SaveWriteEpoch > current.SaveWriteEpoch)
            throw Invalid("rollback-source-family-mismatch");
        if (physical.RestoreBoundary == current.RestoreBoundary) reference = new() { ActualSnapshotId = actual.Snapshot.SnapshotId };
        else reference = await resolver.EffectiveAsync(target, current);
        var graph = resolver.Graph(); var metadata = ReadActualDownload(target).Outposts.Metadata.Clone();
        if (reference.SourceCase == OutpostSourceReference.SourceOneofCase.AuthorizedView)
        {
            var view = graph.AuthorizedViews.Single(v => v.TransitionId == reference.AuthorizedView.TransitionId);
            var steps = new List<OutpostAuthorizedSnapshotView>(); var cursor = view;
            while (true)
            {
                steps.Add(cursor);
                if (cursor.Source.SourceCase == OutpostSourceReference.SourceOneofCase.ActualSnapshotId) break;
                cursor = graph.AuthorizedViews.Single(v => v.TransitionId == cursor.Source.AuthorizedView.TransitionId);
            }
            steps.Reverse();
            foreach (var step in steps)
                foreach (var map in step.RegistryMap)
                {
                    var matches = metadata.Registry.Where(e => e.Equals(map.Before)).ToArray();
                    if (matches.Length != 1) throw Invalid("rollback-source-registry-mismatch");
                    metadata.Registry[metadata.Registry.IndexOf(matches[0])] = map.After.Clone();
                }
            metadata.Scope = view.EffectiveScope.Clone(); metadata.RestoreBridge = null;
            metadata.RollbackBridge = null; metadata.RollbackFromViewBridge = null;
            if (view.Kind == OutpostSourceAuthorizationKind.Restore)
                metadata.RestoreBridge = SourceResolver.Bridge(actual.Snapshot, view, graph);
            else if (view.Source.SourceCase == OutpostSourceReference.SourceOneofCase.ActualSnapshotId)
                metadata.RollbackBridge = SourceResolver.RollbackBridge(actual.Snapshot, view, graph, actual.HistoricalAuthorization is not null);
            else
                metadata.RollbackFromViewBridge = SourceResolver.FromViewBridge(actual.Snapshot, view, graph);
        }
        LifecycleRollbackSource? authorization = graph.AuthorizedViews.Count == 0 ? null : new(reference.ToByteArray(), graph.ToByteArray());
        return new(actual.Snapshot.Clone(), authorization, metadata);
    }

    internal static OutpostScopeMessage RollbackEffectiveScope(OutpostSnapshotProof proof, LifecycleRollbackSource? source)
    {
        if (source is null) return proof.Scope;
        if (source.Reference.Length is 0 or > 4096 || source.Provenance.Length is 0 or > OutpostSourceGraph.MaxGraphBytes)
            throw Invalid("rollback-source-limit");
        var reference = OutpostSourceReference.Parser.ParseFrom(source.Reference);
        var graph = OutpostSourceProvenance.Parser.ParseFrom(source.Provenance); OutpostSourceGraph.Validate(graph);
        var actual = graph.ActualSources.SingleOrDefault(s => s.Snapshot.SnapshotId == proof.SnapshotId);
        if (actual is null || !actual.Snapshot.Equals(proof)) throw Invalid("rollback-source-actual-mismatch");
        if (reference.SourceCase == OutpostSourceReference.SourceOneofCase.ActualSnapshotId)
        {
            if (reference.ActualSnapshotId != proof.SnapshotId) throw Invalid("rollback-source-reference-mismatch");
            return proof.Scope;
        }
        if (reference.SourceCase != OutpostSourceReference.SourceOneofCase.AuthorizedView) throw Invalid("rollback-source-reference-missing");
        var view = graph.AuthorizedViews.SingleOrDefault(v => v.TransitionId == reference.AuthorizedView.TransitionId);
        if (view is null || !view.TransitionDigest.Equals(reference.AuthorizedView.TransitionDigest)) throw Invalid("rollback-source-view-missing");
        var cursor = view;
        while (cursor.Source.SourceCase == OutpostSourceReference.SourceOneofCase.AuthorizedView)
            cursor = graph.AuthorizedViews.Single(v => v.TransitionId == cursor.Source.AuthorizedView.TransitionId);
        if (cursor.Source.ActualSnapshotId != proof.SnapshotId) throw Invalid("rollback-source-canonical-mismatch");
        return view.EffectiveScope;
    }

    internal async Task ValidateRollbackSourceAsync(OutpostSnapshotProof proof, LifecycleRollbackSource? source, CancellationToken ct)
    {
        if (source is null) return; // Unchanged v1 path still proves both original links and receipts independently.
        var scope = RollbackEffectiveScope(proof, source);
        var link = await new OutpostLifecycleEvidence(db, files).RollbackLinkAsync(proof, ct);
        var located = await new OutpostLifecycleEvidence(db, files).ResolveSnapshotAsync(link, ct);
        if (located.Archive is not null) throw Invalid("rollback-source-retired-locator-required");
        var expected = await ResolveRollbackSourceAsync(located.Version, scope, ct);
        if (!expected.Actual.Equals(proof) || !Same(expected.Authorization, source)) throw Invalid("rollback-source-provenance-mismatch");
    }
}
