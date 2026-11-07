using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostRestoreEvidence
{
    // One bounded traversal for both actual embedded history and semantic views. Echo graphs are never inputs to construction.
    private sealed partial class SourceResolver(OutpostRestoreEvidence owner, CancellationToken ct)
    {
        private readonly Dictionary<string, OutpostActualSnapshotSource> sources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, OutpostSaveMetadata> metadata = new(StringComparer.Ordinal);
        private readonly Dictionary<string, OutpostAuthorizedSnapshotView> views = new(StringComparer.Ordinal);
        private readonly HashSet<string> visiting = new(StringComparer.Ordinal);
        private int depth;

        internal OutpostSourceProvenance Graph()
        {
            var graph = new OutpostSourceProvenance { SchemaVersion = 1 };
            graph.ActualSources.Add(sources.Values.Select(s => s.Clone()));
            graph.AuthorizedViews.Add(views.Values.Select(v => v.Clone()));
            OutpostSourceGraph.Validate(graph); return graph;
        }

        private void Enter(string key)
        {
            ct.ThrowIfCancellationRequested();
            if (++depth > OutpostSourceGraph.MaxDepth || !visiting.Add(key)) throw Invalid("source-provenance-cycle-or-depth");
        }
        private void Leave(string key) { visiting.Remove(key); depth--; }
        private void Bound()
        {
            if (sources.Count + views.Count > OutpostSourceGraph.MaxNodes
                || views.Values.Sum(v => (long)v.RegistryMap.Count) > OutpostSourceGraph.MaxProjectedEntries)
                throw Invalid("source-provenance-limit");
        }
        private static OutpostAuthorizationReference Reference(OutpostAuthorizedSnapshotView view) => new()
        { TransitionId = view.TransitionId, TransitionDigest = view.TransitionDigest };

        internal Task<OutpostActualSnapshotSource> ActualAsync((SaveVersion Version, string? Archive) located) =>
            ActualAsync(located.Version, located.Archive);

        internal async Task<OutpostActualSnapshotSource> ActualAsync(SaveVersion version, string? archive = null)
        {
            string id = version.SnapshotId ?? throw Invalid("source-actual-id-missing");
            if (sources.TryGetValue(id, out var cached))
            {
                if (cached.Snapshot.Scope.SaveGeneration != version.SaveGeneration || cached.Snapshot.Scope.SaveWriteEpoch != version.SaveWriteEpoch
                    || version.RawSha256 is null || !cached.Snapshot.RawSha256.Span.SequenceEqual(version.RawSha256))
                    throw Invalid("source-actual-conflict");
                return cached;
            }
            string key = "s:" + id; Enter(key);
            try
            {
                if (archive is null)
                {
                    var expected = SaveStore.RecoveryOf(version).Outposts;
                    if (!expected.Present) throw Invalid("source-actual-protected-metadata-missing");
                    var retained = await owner.SourceLinkAsync(version, expected.Metadata, ct);
                    archive = (await owner.ResolveSourceLocationAsync(retained, ct)).Archive;
                }
                var original = owner.ReadActualDownload(version, archive).Outposts;
                if (!original.Present) throw Invalid("source-actual-protected-metadata-missing");
                var value = original.Metadata;
                var link = await owner.SourceLinkAsync(version, value, ct);
                var proof = new OutpostSnapshotProof { Scope = value.Scope.Clone(), SnapshotId = id,
                    RawSha256 = ByteString.CopyFrom(version.RawSha256!) };
                var source = new OutpostActualSnapshotSource { Snapshot = proof,
                    LocalFrontierDigest = ByteString.CopyFrom(link.FrontierDigest) };
                if (value.RestoreBridge is { } embedded)
                {
                    var root = await owner.SourceRootAsync(embedded.ActiveAuthorization.TransitionId, ct);
                    var expectedView = await RestoreAsync(root, Plan(root), embedded.CanonicalSnapshot, value.Scope);
                    var graph = Graph();
                    var expected = Bridge(embedded.CanonicalSnapshot, expectedView, graph);
                    if (!embedded.Equals(expected)) throw Invalid("source-embedded-restore-mismatch");
                    source.HistoricalAuthorization = Reference(expectedView);
                }
                else if (value.RollbackBridge is { } rollback)
                {
                    var (operation, intent, transition) = await owner.SourceRollbackAsync(rollback.LifecycleOperationId, ct);
                    var canonicalVersion = await owner.SourceVersionAsync(rollback.CanonicalSnapshot, ct);
                    var canonical = await ActualAsync(canonicalVersion);
                    var expectedView = await RollbackAsync(operation, intent, transition, canonical.Snapshot, value.Scope);
                    if (expectedView.Source.SourceCase != OutpostSourceReference.SourceOneofCase.ActualSnapshotId)
                        throw Invalid("source-embedded-rollback-is-view");
                    var expected = RollbackBridge(canonical.Snapshot, expectedView, Graph(), canonical.HistoricalAuthorization is not null);
                    if (!rollback.Equals(expected)) throw Invalid("source-embedded-rollback-mismatch");
                    source.HistoricalAuthorization = Reference(expectedView);
                }
                else if (value.RollbackFromViewBridge is { } rollbackView)
                {
                    var viewRows = await owner.SourceRollbackRootsAsync(value.Scope, ct);
                    if (viewRows.Length != 1) throw Invalid("source-rollback-operation-missing-or-ambiguous");
                    var (operation, intent, transition) = await owner.SourceRollbackAsync(viewRows[0].Id, ct);
                    var canonicalVersion = await owner.SourceVersionAsync(rollbackView.CanonicalSnapshot, ct);
                    var canonical = await ActualAsync(canonicalVersion);
                    var expectedView = await RollbackAsync(operation, intent, transition, canonical.Snapshot, value.Scope);
                    if (expectedView.Source.SourceCase != OutpostSourceReference.SourceOneofCase.AuthorizedView)
                        throw Invalid("source-embedded-rollback-view-is-actual");
                    var expected = FromViewBridge(canonical.Snapshot, expectedView, Graph());
                    if (!rollbackView.Equals(expected)) throw Invalid("source-embedded-rollback-view-mismatch");
                    source.HistoricalAuthorization = Reference(expectedView);
                }
                sources.Add(id, source); metadata.Add(id, value); Bound(); return source;
            }
            finally { Leave(key); }
        }

        internal async Task<OutpostAuthorizedSnapshotView> RestoreAsync(OutpostTransition root, RestoreMainPlan plan,
            OutpostSnapshotProof actual, OutpostScopeMessage current)
        {
            if (views.TryGetValue(root.Id, out var cached))
            {
                if (!cached.EffectiveScope.Equals(current)) throw Invalid("source-view-conflict");
                return cached;
            }
            string key = "v:" + root.Id; Enter(key);
            try
            {
                var owners = plan.Players.Where(p => p.PlayerId == current.OwnerPlayerId).ToArray();
                if (owners.Length != 1 || owners[0].Generation != current.SaveGeneration || owners[0].WriteEpoch != current.SaveWriteEpoch
                    || plan.Cut.Authority.WorldId != current.WorldInstanceId || plan.Journal.Selection.TargetBoundary != current.RestoreBoundary
                    || owners[0].Canonical is not { } canonical || canonical.SnapshotId != actual.SnapshotId
                    || canonical.RawSha256 is null || !canonical.RawSha256.AsSpan().SequenceEqual(actual.RawSha256.Span))
                    throw Invalid("restore-download-canonical-not-in-cut");
                var sourceScope = current.Clone(); sourceScope.RestoreBoundary = plan.Cut.Authority.RestoreBoundary;
                var source = await EffectiveAsync(canonical, sourceScope);
                var (before, frontier) = Projection(source);
                var view = new OutpostAuthorizedSnapshotView { Kind = OutpostSourceAuthorizationKind.Restore,
                    LifecycleOperationId = root.Id, TransitionId = root.Id, TransitionDigest = ByteString.CopyFrom(root.Digest),
                    Source = source, SourceScope = sourceScope, EffectiveScope = current.Clone(), LocalFrontierDigest = frontier,
                    SelectedCutHeadDigest = ByteString.CopyFrom(plan.Cut.Authority.HeadDigest),
                    SelectedManifestDigest = ByteString.CopyFrom(plan.Journal.ManifestDigest) };
                foreach (var entry in before)
                {
                    var map = plan.Custody.SingleOrDefault(m => m.Before.Id == entry.PawnCustodyId);
                    if (map is null || map.Before.Tombstone || map.Before.LocationKind != "Home"
                        || map.Before.WorldId != sourceScope.WorldInstanceId || map.Before.PlayerId != sourceScope.OwnerPlayerId
                        || map.Before.SaveGeneration != sourceScope.SaveGeneration || map.Before.RestoreBoundary != sourceScope.RestoreBoundary
                        || (map.Before.AssignmentId ?? "") != entry.AssignmentId || map.Before.LocationId != entry.PawnReferenceToken)
                        throw Invalid("restore-download-live-registry-mismatch");
                    var after = entry.Clone(); after.Scope = current.Clone();
                    view.RegistryMap.Add(new OutpostRegistryProjection { Before = entry.Clone(), After = after });
                }
                views.Add(view.TransitionId, view); Bound(); return view;
            }
            finally { Leave(key); }
        }

        internal Task<OutpostSourceReference> EffectiveAsync((SaveVersion Version, string? Archive) located, OutpostScopeMessage scope) =>
            EffectiveAsync(located.Version, scope, located.Archive);

        internal async Task<OutpostSourceReference> EffectiveAsync(SaveVersion target, OutpostScopeMessage scope, string? archive = null)
        {
            var actual = await ActualAsync(target, archive);
            if (actual.Snapshot.Scope.Equals(scope)) return new() { ActualSnapshotId = actual.Snapshot.SnapshotId };
            var roots = await owner.SourceRestoreRootsAsync(scope, ct);
            var rollbacks = await owner.SourceRollbackRootsAsync(scope, ct);
            if (roots.Length + rollbacks.Length != 1) throw Invalid("source-view-authorization-missing-or-ambiguous");
            OutpostAuthorizedSnapshotView view;
            if (roots.Length == 1)
                view = await RestoreAsync(roots[0], Plan(roots[0]), actual.Snapshot, scope);
            else
            {
                var (operation, intent, transition) = await owner.SourceRollbackAsync(rollbacks[0].Id, ct);
                view = await RollbackAsync(operation, intent, transition, actual.Snapshot, scope);
            }
            return new() { AuthorizedView = Reference(view) };
        }

        private (IEnumerable<PawnRegistryEnvelope> Registry, ByteString Frontier) Projection(OutpostSourceReference source)
        {
            if (source.SourceCase == OutpostSourceReference.SourceOneofCase.AuthorizedView)
            {
                var view = views[source.AuthorizedView.TransitionId];
                return (view.RegistryMap.Select(m => m.After), view.LocalFrontierDigest);
            }
            var actual = sources[source.ActualSnapshotId]; var value = metadata[source.ActualSnapshotId];
            return (value.Registry.Where(e => !e.NonAuthoritativeMirror && !e.Tombstone && e.Scope.Equals(value.Scope)), actual.LocalFrontierDigest);
        }

        internal static OutpostRestoreBridge Bridge(OutpostSnapshotProof canonical, OutpostAuthorizedSnapshotView view, OutpostSourceProvenance graph) => new()
        {
            CanonicalSnapshot = canonical.Clone(), SourceProvenance = graph,
            ActiveAuthorization = Reference(view), SourcePathDigest = ByteString.CopyFrom(SHA256.HashData(graph.ToByteArray()))
        };
    }

    private async Task<OutpostSnapshotLink> SourceLinkAsync(SaveVersion target, OutpostSaveMetadata source, CancellationToken ct)
    {
        var proof = new OutpostSnapshotProof { Scope = source.Scope.Clone(), SnapshotId = target.SnapshotId!, RawSha256 = ByteString.CopyFrom(target.RawSha256!) };
        var link = await new OutpostLifecycleEvidence(db, files).RollbackLinkAsync(proof, ct);
        if (!link.Metadata.AsSpan().SequenceEqual(source.ToByteArray()) || !link.FrontierDigest.AsSpan().SequenceEqual(OutpostSnapshotGuard.FrontierDigest(source)))
            throw Invalid("source-actual-link-mismatch");
        return link;
    }
    private async Task<OutpostTransition> SourceRootAsync(string id, CancellationToken ct) =>
        await db.OutpostTransitions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.Kind == "BulkRestore", ct)
            ?? throw Invalid("source-restore-transition-missing");
    private async Task<OutpostLifecycleOperation[]> SourceRollbackRootsAsync(OutpostScopeMessage scope, CancellationToken ct) =>
        (await db.OutpostLifecycleOperations.AsNoTracking().Where(r => r.Kind == "Rollback").ToArrayAsync(ct)).Where(r =>
        {
            var intent = OutpostLifecycleEvidence.Intent(r);
            return intent.Rollback!.Kind == "ProtectedSnapshot" && intent.ResultScopes is { Length: 1 }
                && OutpostScopeMessage.Parser.ParseFrom(intent.ResultScopes[0].Scope).Equals(scope);
        }).ToArray();
    private async Task<OutpostTransition[]> SourceRestoreRootsAsync(OutpostScopeMessage scope, CancellationToken ct) =>
        (await db.OutpostTransitions.AsNoTracking().Where(t => t.Kind == "BulkRestore").ToArrayAsync(ct)).Where(t =>
        {
            var plan = Plan(t);
            return plan.Cut.Authority.WorldId == scope.WorldInstanceId && plan.Journal.Selection.TargetBoundary == scope.RestoreBoundary
                && plan.Players.Any(p => p.PlayerId == scope.OwnerPlayerId && p.Generation == scope.SaveGeneration && p.WriteEpoch == scope.SaveWriteEpoch);
        }).ToArray();
}
