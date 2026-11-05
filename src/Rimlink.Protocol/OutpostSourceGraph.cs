using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Protocol;

/// <summary>Structural bounds and typed references only; committed source bytes remain server authority.</summary>
public static class OutpostSourceGraph
{
    public const int MaxDepth = 16;
    public const int MaxNodes = 256;
    public const int MaxProjectedEntries = 10_000;
    public const int MaxGraphBytes = 4 * 1024 * 1024;

    public static void Validate(OutpostSourceProvenance graph)
    {
        if (graph is null || graph.SchemaVersion != 1 || graph.CalculateSize() > MaxGraphBytes
            || graph.ActualSources.Count + graph.AuthorizedViews.Count is < 1 or > MaxNodes
            || graph.AuthorizedViews.Sum(v => (long)v.RegistryMap.Count) > MaxProjectedEntries) throw Invalid();
        var sources = new Dictionary<string, OutpostActualSnapshotSource>(StringComparer.Ordinal);
        var views = new Dictionary<string, OutpostAuthorizedSnapshotView>(StringComparer.Ordinal);
        foreach (var source in graph.ActualSources)
        {
            if (source.Snapshot is not { } proof || !SnapshotId(proof.SnapshotId) || !Hash(proof.RawSha256)
                || !Scope(proof.Scope) || !Hash(source.LocalFrontierDigest) || !sources.TryAdd(proof.SnapshotId, source)) throw Invalid();
        }
        foreach (var view in graph.AuthorizedViews)
        {
            if (!Uuid(view.LifecycleOperationId) || !Uuid(view.TransitionId) || !Hash(view.TransitionDigest)
                || !Hash(view.LocalFrontierDigest) || !Scope(view.SourceScope) || !Scope(view.EffectiveScope)
                || !Family(view.SourceScope, view.EffectiveScope) || !views.TryAdd(view.TransitionId, view)) throw Invalid();
            if (view.Kind == OutpostSourceAuthorizationKind.Restore)
            {
                if (view.SourceScope.RestoreBoundary == view.EffectiveScope.RestoreBoundary
                    || view.SourceScope.SaveWriteEpoch != view.EffectiveScope.SaveWriteEpoch || view.PreviousCurrentScope is not null
                    || !Hash(view.SelectedCutHeadDigest) || !Hash(view.SelectedManifestDigest)) throw Invalid();
            }
            else if (view.Kind == OutpostSourceAuthorizationKind.Rollback)
            {
                var previous = view.PreviousCurrentScope;
                if (!Scope(previous) || !Family(view.SourceScope, previous!)
                    || previous!.RestoreBoundary != view.SourceScope.RestoreBoundary
                    || view.EffectiveScope.RestoreBoundary != view.SourceScope.RestoreBoundary
                    || previous.SaveWriteEpoch < view.SourceScope.SaveWriteEpoch || previous.SaveWriteEpoch == long.MaxValue
                    || view.EffectiveScope.SaveWriteEpoch != previous.SaveWriteEpoch + 1
                    || view.SelectedCutHeadDigest.Length != 0 || view.SelectedManifestDigest.Length != 0) throw Invalid();
            }
            else throw Invalid();
            var ids = new HashSet<string>(StringComparer.Ordinal); var references = new HashSet<string>(StringComparer.Ordinal);
            foreach (var map in view.RegistryMap)
            {
                var before = map.Before; var after = map.After;
                if (before is null || after is null || before.SchemaVersion != 1 || before.Tombstone || before.NonAuthoritativeMirror
                    || !Uuid(before.PawnCustodyId) || before.AssignmentId.Length != 0 && !Uuid(before.AssignmentId)
                    || before.PawnReferenceToken.Length is < 1 or > 256 || before.PawnReferenceToken.Any(char.IsControl)
                    || before.Scope is null || !before.Scope.Equals(view.SourceScope) || !ids.Add(before.PawnCustodyId) || !references.Add(before.PawnReferenceToken)) throw Invalid();
                var expected = before.Clone(); expected.Scope = view.EffectiveScope.Clone();
                if (!after.Equals(expected)) throw Invalid();
            }
        }
        var completed = new Dictionary<string, int>(StringComparer.Ordinal); var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (string source in sources.Keys) VisitSource(source);
        foreach (string view in views.Keys) VisitView(view);

        int VisitSource(string id)
        {
            string key = "s:" + id;
            if (completed.TryGetValue(key, out int done)) return done;
            if (!sources.TryGetValue(id, out var source) || !visiting.Add(key)) throw Invalid();
            int depth = 0;
            if (source.HistoricalAuthorization is { } historical)
            {
                var previous = Resolve(historical); depth = VisitView(previous.TransitionId);
                if (!previous.EffectiveScope.Equals(source.Snapshot.Scope)) throw Invalid();
            }
            visiting.Remove(key); completed.Add(key, depth); return depth;
        }
        int VisitView(string id)
        {
            string key = "v:" + id;
            if (completed.TryGetValue(key, out int done)) return done;
            if (!views.TryGetValue(id, out var view) || !visiting.Add(key)) throw Invalid();
            int depth; OutpostScopeMessage scope; ByteString frontier;
            if (view.Source?.SourceCase == OutpostSourceReference.SourceOneofCase.ActualSnapshotId)
            {
                string sourceId = view.Source.ActualSnapshotId;
                depth = VisitSource(sourceId);
                var source = sources[sourceId]; scope = source.Snapshot.Scope; frontier = source.LocalFrontierDigest;
            }
            else if (view.Source?.SourceCase == OutpostSourceReference.SourceOneofCase.AuthorizedView)
            {
                var previous = Resolve(view.Source.AuthorizedView); depth = VisitView(previous.TransitionId);
                scope = previous.EffectiveScope; frontier = previous.LocalFrontierDigest;
                var originals = previous.RegistryMap.ToDictionary(m => m.After.PawnCustodyId, m => m.After, StringComparer.Ordinal);
                if (view.RegistryMap.Any(m => !originals.TryGetValue(m.Before.PawnCustodyId, out var exact) || !exact.Equals(m.Before))) throw Invalid();
            }
            else throw Invalid();
            if (!scope.Equals(view.SourceScope) || !frontier.Equals(view.LocalFrontierDigest) || ++depth > MaxDepth) throw Invalid();
            visiting.Remove(key); completed.Add(key, depth); return depth;
        }
        OutpostAuthorizedSnapshotView Resolve(OutpostAuthorizationReference reference)
        {
            if (reference is null || !Uuid(reference.TransitionId) || !Hash(reference.TransitionDigest)
                || !views.TryGetValue(reference.TransitionId, out var exact) || !exact.TransitionDigest.Equals(reference.TransitionDigest)) throw Invalid();
            return exact;
        }
    }

    public static OutpostAuthorizedSnapshotView ValidateRoot(OutpostRollbackBridge bridge)
    {
        if (bridge is null || bridge.CalculateSize() > MaxGraphBytes) throw Invalid();
        var view = Root(bridge.SourceProvenance, bridge.CanonicalSnapshot,
            new() { TransitionId = bridge.TransitionId, TransitionDigest = bridge.TransitionDigest },
            bridge.SourcePathDigest, OutpostSourceAuthorizationKind.Rollback);
        if (view.Source.SourceCase != OutpostSourceReference.SourceOneofCase.ActualSnapshotId
            || view.Source.ActualSnapshotId != bridge.CanonicalSnapshot.SnapshotId
            || view.LifecycleOperationId != bridge.LifecycleOperationId || !view.SourceScope.Equals(bridge.SourceScope)
            || !view.EffectiveScope.Equals(bridge.CurrentScope) || !view.LocalFrontierDigest.Equals(bridge.LocalFrontierDigest)
            || !bridge.CanonicalSnapshot.Scope.Equals(bridge.SourceScope)
            || !view.RegistryMap.Select(m => m.Before).SequenceEqual(bridge.MutableRegistry)) throw Invalid();
        return view;
    }

    public static OutpostAuthorizedSnapshotView ValidateRoot(OutpostRestoreBridge bridge)
    {
        if (bridge is null || bridge.CalculateSize() > MaxGraphBytes) throw Invalid();
        return Root(bridge.SourceProvenance, bridge.CanonicalSnapshot, bridge.ActiveAuthorization, bridge.SourcePathDigest, OutpostSourceAuthorizationKind.Restore);
    }
    public static OutpostAuthorizedSnapshotView ValidateRoot(OutpostRollbackFromViewBridge bridge)
    {
        if (bridge is null || bridge.CalculateSize() > MaxGraphBytes) throw Invalid();
        var view = Root(bridge.SourceProvenance, bridge.CanonicalSnapshot, bridge.ActiveAuthorization, bridge.SourcePathDigest, OutpostSourceAuthorizationKind.Rollback);
        // An actual source still uses v1; a view is never a relabelled OutpostSnapshotProof.
        if (view.Source.SourceCase != OutpostSourceReference.SourceOneofCase.AuthorizedView) throw Invalid();
        return view;
    }
    /// <summary>Binds a header's actual-source history to the root physically embedded in those source bytes.</summary>
    public static void ValidateEmbeddedHistory(OutpostSourceProvenance graph, OutpostSnapshotProof actual, OutpostSaveMetadata original)
    {
        Validate(graph);
        var source = graph.ActualSources.SingleOrDefault(s => s.Snapshot.Equals(actual));
        if (source is null || !actual.Scope.Equals(original.Scope)) throw Invalid();
        OutpostAuthorizedSnapshotView? historical = null; OutpostSourceProvenance? embedded = null;
        if (original.RestoreBridge is { } restore)
        { historical = ValidateRoot(restore); embedded = restore.SourceProvenance; }
        else if (original.RollbackFromViewBridge is { } fromView)
        { historical = ValidateRoot(fromView); embedded = fromView.SourceProvenance; }
        else if (original.RollbackBridge is { } rollback)
        {
            if (rollback.SourceProvenance is not null || rollback.SourcePathDigest.Length != 0)
            { historical = ValidateRoot(rollback); embedded = rollback.SourceProvenance; }
            else
            {
                // Structural mirror of the unmodified old v1 root; never relabel its physical proof.
                var mirror = rollback.Clone(); mirror.SourceProvenance = graph;
                mirror.SourcePathDigest = ByteString.CopyFrom(SHA256.HashData(graph.ToByteArray())); historical = ValidateRoot(mirror);
            }
        }
        if (historical is null)
        { if (source.HistoricalAuthorization is not null) throw Invalid(); return; }
        if (source.HistoricalAuthorization is not { } reference || reference.TransitionId != historical.TransitionId
            || !reference.TransitionDigest.Equals(historical.TransitionDigest)
            || !graph.AuthorizedViews.Single(v => v.TransitionId == reference.TransitionId).Equals(historical)) throw Invalid();
        if (embedded is null) return;
        foreach (var required in embedded.ActualSources)
            if (!required.Equals(graph.ActualSources.SingleOrDefault(s => s.Snapshot.SnapshotId == required.Snapshot.SnapshotId))) throw Invalid();
        foreach (var required in embedded.AuthorizedViews)
            if (!required.Equals(graph.AuthorizedViews.SingleOrDefault(v => v.TransitionId == required.TransitionId))) throw Invalid();
    }

    private static OutpostAuthorizedSnapshotView Root(OutpostSourceProvenance graph, OutpostSnapshotProof canonical,
        OutpostAuthorizationReference active, ByteString pathDigest, OutpostSourceAuthorizationKind kind)
    {
        Validate(graph);
        if (canonical is null || active is null || !Hash(pathDigest) || !Hash(active.TransitionDigest)
            || !SHA256.HashData(graph.ToByteArray()).AsSpan().SequenceEqual(pathDigest.Span)) throw Invalid();
        var source = graph.ActualSources.SingleOrDefault(s => s.Snapshot.SnapshotId == canonical.SnapshotId);
        var view = graph.AuthorizedViews.SingleOrDefault(v => v.TransitionId == active.TransitionId);
        if (source is null || !source.Snapshot.Equals(canonical) || view is null || view.Kind != kind
            || !view.TransitionDigest.Equals(active.TransitionDigest)) throw Invalid();
        var cursor = view;
        while (cursor.Source.SourceCase == OutpostSourceReference.SourceOneofCase.AuthorizedView)
            cursor = graph.AuthorizedViews.Single(v => v.TransitionId == cursor.Source.AuthorizedView.TransitionId);
        if (cursor.Source.ActualSnapshotId != canonical.SnapshotId) throw Invalid();
        return view;
    }

    private static bool Scope(OutpostScopeMessage? value) => value is { OwnerPlayerId: > 0, SaveGeneration: >= 0, SaveWriteEpoch: >= 0 }
        && value.WorldInstanceId.Length is > 0 and <= 128 && !value.WorldInstanceId.Any(char.IsControl)
        && Uuid(value.RestoreBoundary);
    private static bool Family(OutpostScopeMessage a, OutpostScopeMessage b) => a.WorldInstanceId == b.WorldInstanceId
        && a.OwnerPlayerId == b.OwnerPlayerId && a.SaveGeneration == b.SaveGeneration;
    private static bool Uuid(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString("D");
    private static bool SnapshotId(string value) => Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty && value == id.ToString("N");
    private static bool Hash(ByteString value) => value.Length == 32;
    private static InvalidDataException Invalid() => new("invalid outpost source provenance");
}
