using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Outposts;

public sealed record SourceBridgeDecision(bool Allowed, string? RefusalKey, IReadOnlyList<PawnRegistryEnvelope> Registry);

/// <summary>Compose scope-only plans from an authenticated header and the actual immutable download.</summary>
public static class OutpostSourceBridgeChecks
{
    public static SourceBridgeDecision Validate(OutpostRestoreBridge bridge, OutpostSnapshotProof downloaded,
        OutpostScopeMessage current, IReadOnlyList<PawnRegistryEnvelope> registry) => ValidateCore(bridge?.SourceProvenance,
            bridge?.CanonicalSnapshot, () => OutpostSourceGraph.ValidateRoot(bridge!), downloaded, current, registry, "Rimlink.Outposts.RestoreIncompatible");
    public static SourceBridgeDecision Validate(OutpostRollbackFromViewBridge bridge, OutpostSnapshotProof downloaded,
        OutpostScopeMessage current, IReadOnlyList<PawnRegistryEnvelope> registry) => ValidateCore(bridge?.SourceProvenance,
            bridge?.CanonicalSnapshot, () => OutpostSourceGraph.ValidateRoot(bridge!), downloaded, current, registry, "Rimlink.Outposts.RollbackIncompatible");

    private static SourceBridgeDecision ValidateCore(OutpostSourceProvenance? graph, OutpostSnapshotProof? canonical,
        Func<OutpostAuthorizedSnapshotView> validate, OutpostSnapshotProof downloaded, OutpostScopeMessage current,
        IReadOnlyList<PawnRegistryEnvelope> registry, string refusal)
    {
        SourceBridgeDecision Refuse() => new(false, refusal, []);
        if (graph is null || !OutpostReconciliation.ValidProof(downloaded) || !downloaded.Equals(canonical)
            || !OutpostReconciliation.ValidScope(current) || registry is null || registry.Count > OutpostRecoveryData.MaxEntries) return Refuse();
        try
        {
            var active = validate();
            if (!active.EffectiveScope.Equals(current)) return Refuse();
            new OutpostRecoveryData { Metadata = new() { SchemaVersion = 1, Scope = downloaded.Scope.Clone(), Registry = { registry } } }.Validate();
            var path = new List<OutpostAuthorizedSnapshotView>(); var cursor = active;
            while (true)
            {
                path.Add(cursor);
                if (cursor.Source.SourceCase == OutpostSourceReference.SourceOneofCase.ActualSnapshotId) break;
                cursor = graph.AuthorizedViews.Single(v => v.TransitionId == cursor.Source.AuthorizedView.TransitionId);
            }
            path.Reverse();
            var result = registry.Select(r => r.Clone()).ToArray();
            var indices = result.Select((entry, index) => (entry.PawnCustodyId, index)).ToDictionary(p => p.PawnCustodyId, p => p.index, StringComparer.Ordinal);
            foreach (var step in path)
            {
                foreach (var map in step.RegistryMap)
                {
                    if (!indices.TryGetValue(map.Before.PawnCustodyId, out int index) || !result[index].Equals(map.Before)) return Refuse();
                    result[index] = map.After.Clone();
                }
            }
            return new(true, null, result);
        }
        catch (InvalidDataException) { return Refuse(); }
    }
}
