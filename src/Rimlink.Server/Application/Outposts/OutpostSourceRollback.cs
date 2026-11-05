using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostRestoreEvidence
{
    private sealed partial class SourceResolver
    {
        internal async Task<OutpostAuthorizedSnapshotView> RollbackAsync(OutpostLifecycleOperation operation, LifecycleIntent intent,
            OutpostTransition transition, OutpostSnapshotProof actual, OutpostScopeMessage current)
        {
            if (views.TryGetValue(transition.Id, out var cached))
            {
                if (!cached.EffectiveScope.Equals(current)) throw Invalid("source-view-conflict");
                return cached;
            }
            string key = "v:" + transition.Id; Enter(key);
            try
            {
                var expected = OutpostSnapshotProof.Parser.ParseFrom(intent.Rollback!.Target);
                if (!expected.Equals(actual) || !OutpostScopeMessage.Parser.ParseFrom(intent.ResultScopes![0].Scope).Equals(current))
                    throw Invalid("source-rollback-canonical-mismatch");
                var version = await owner.SourceVersionAsync(actual, ct);
                var sourceScope = RollbackEffectiveScope(actual, intent.Rollback.TargetSource).Clone();
                OutpostSourceReference source;
                if (intent.Rollback.TargetSource is { } authorization
                    && OutpostSourceReference.Parser.ParseFrom(authorization.Reference).SourceCase == OutpostSourceReference.SourceOneofCase.AuthorizedView)
                    source = await EffectiveAsync(version, sourceScope);
                else
                {
                    await ActualAsync(version); source = new() { ActualSnapshotId = actual.SnapshotId };
                }
                var (before, frontier) = Projection(source);
                if (!frontier.Span.SequenceEqual(intent.Rollback.FrontierDigest)) throw Invalid("source-rollback-frontier-mismatch");
                var view = new OutpostAuthorizedSnapshotView { Kind = OutpostSourceAuthorizationKind.Rollback,
                    LifecycleOperationId = operation.Id, TransitionId = transition.Id, TransitionDigest = ByteString.CopyFrom(transition.Digest),
                    Source = source, SourceScope = sourceScope, EffectiveScope = current.Clone(), LocalFrontierDigest = frontier,
                    PreviousCurrentScope = OutpostScopeMessage.Parser.ParseFrom(intent.Scopes[0].Scope) };
                foreach (var entry in before)
                {
                    var after = entry.Clone(); after.Scope = current.Clone();
                    view.RegistryMap.Add(new OutpostRegistryProjection { Before = entry.Clone(), After = after });
                }
                views.Add(view.TransitionId, view); Bound(); return view;
            }
            finally { Leave(key); }
        }

        internal static OutpostRollbackBridge RollbackBridge(OutpostSnapshotProof canonical, OutpostAuthorizedSnapshotView view,
            OutpostSourceProvenance graph, bool supplement) => new()
        {
            LifecycleOperationId = view.LifecycleOperationId, TransitionId = view.TransitionId, TransitionDigest = view.TransitionDigest,
            CanonicalSnapshot = canonical.Clone(), SourceScope = view.SourceScope.Clone(), CurrentScope = view.EffectiveScope.Clone(),
            LocalFrontierDigest = view.LocalFrontierDigest, MutableRegistry = { view.RegistryMap.Select(m => m.Before.Clone()) },
            SourceProvenance = supplement ? graph : null,
            SourcePathDigest = supplement ? ByteString.CopyFrom(SHA256.HashData(graph.ToByteArray())) : ByteString.Empty
        };
        internal static OutpostRollbackFromViewBridge FromViewBridge(OutpostSnapshotProof canonical, OutpostAuthorizedSnapshotView view,
            OutpostSourceProvenance graph) => new()
        {
            CanonicalSnapshot = canonical.Clone(), ActiveAuthorization = Reference(view), SourceProvenance = graph,
            SourcePathDigest = ByteString.CopyFrom(SHA256.HashData(graph.ToByteArray()))
        };
    }

    private async Task<SaveVersion> SourceVersionAsync(OutpostSnapshotProof proof, CancellationToken ct)
    {
        var codec = new OutpostLifecycleEvidence(db, files); var link = await codec.RollbackLinkAsync(proof, ct);
        var source = await codec.ResolveSnapshotAsync(link, ct);
        if (source.Archive is not null) throw Invalid("source-retired-locator-required");
        return source.Version;
    }

    private async Task<(OutpostLifecycleOperation Row, LifecycleIntent Intent, OutpostTransition Transition)> SourceRollbackAsync(string operationId, CancellationToken ct)
    {
        var row = await db.OutpostLifecycleOperations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == operationId && r.Kind == "Rollback", ct)
            ?? throw Invalid("source-rollback-operation-missing");
        var intent = OutpostLifecycleEvidence.Intent(row);
        if (intent.Rollback!.Kind != "ProtectedSnapshot") throw Invalid("source-rollback-protected-proof-missing");
        var transitions = await db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        await new OutpostLifecycleEvidence(db, files).ValidateRollbackStoredAsync(row, intent, transitions, [], ct);
        var progress = OutpostLifecycleEvidence.Decode<LifecycleProgress>(row.Progress);
        return (row, intent, transitions[progress.TransitionId]);
    }

    internal async Task<(OutpostRollbackBridge? Actual, OutpostRollbackFromViewBridge? View)> CreateRollbackSourceBridgeAsync(
        string operationId, SaveVersion target, OutpostScopeMessage current, bool requireCurrentCustody, CancellationToken ct)
    {
        var (row, intent, transition) = await SourceRollbackAsync(operationId, ct);
        var resolver = new SourceResolver(this, ct); var actual = await resolver.ActualAsync(target);
        var view = await resolver.RollbackAsync(row, intent, transition, actual.Snapshot, current); var graph = resolver.Graph();
        if (requireCurrentCustody)
            foreach (var map in view.RegistryMap)
            {
                var entry = map.Before;
                var pawn = await db.PawnCustody.AsNoTracking().SingleOrDefaultAsync(p => p.Id == entry.PawnCustodyId, ct);
                if (pawn is null || pawn.Tombstone || pawn.LocationKind != "Home" || pawn.WorldId != current.WorldInstanceId
                    || pawn.PlayerId != current.OwnerPlayerId || pawn.SaveGeneration != current.SaveGeneration
                    || pawn.RestoreBoundary != current.RestoreBoundary || (pawn.AssignmentId ?? "") != entry.AssignmentId
                    || pawn.LocationId != entry.PawnReferenceToken) throw Invalid("rollback-download-live-registry-mismatch");
            }
        if (view.Source.SourceCase == OutpostSourceReference.SourceOneofCase.ActualSnapshotId)
        {
            var bridge = SourceResolver.RollbackBridge(actual.Snapshot, view, graph, actual.HistoricalAuthorization is not null);
            if (bridge.SourceProvenance is not null) _ = OutpostSourceGraph.ValidateRoot(bridge);
            return (bridge, null);
        }
        var fromView = SourceResolver.FromViewBridge(actual.Snapshot, view, graph); _ = OutpostSourceGraph.ValidateRoot(fromView);
        return (null, fromView);
    }
}
