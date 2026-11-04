using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Outposts;

public sealed record RollbackBridgeDecision(bool Allowed, string? RefusalKey, IReadOnlyList<PawnRegistryEnvelope> Registry);

public static class OutpostRollbackBridgeChecks
{
    public static RollbackBridgeDecision Validate(OutpostRollbackBridge bridge, OutpostSnapshotProof downloaded,
        OutpostScopeMessage current, IReadOnlyList<PawnRegistryEnvelope> registry)
    {
        RollbackBridgeDecision Refuse() => new(false, "Rimlink.Outposts.RollbackIncompatible", []);
        if (bridge is null || !Uuid(bridge.LifecycleOperationId) || !Uuid(bridge.TransitionId) || bridge.TransitionDigest.Length != 32
            || bridge.LocalFrontierDigest.Length != 32 || !OutpostReconciliation.ValidProof(downloaded)
            || !downloaded.Equals(bridge.CanonicalSnapshot) || !downloaded.Scope.Equals(bridge.SourceScope)
            || !OutpostReconciliation.ValidScope(current) || !current.Equals(bridge.CurrentScope)
            || current.WorldInstanceId != bridge.SourceScope.WorldInstanceId || current.OwnerPlayerId != bridge.SourceScope.OwnerPlayerId
            || current.SaveGeneration != bridge.SourceScope.SaveGeneration || current.RestoreBoundary != bridge.SourceScope.RestoreBoundary
            || current.SaveWriteEpoch <= bridge.SourceScope.SaveWriteEpoch || registry is null
            || registry.Count > OutpostRecoveryData.MaxEntries || bridge.MutableRegistry.Count > OutpostRecoveryData.MaxEntries) return Refuse();
        try
        {
            new OutpostRecoveryData { Metadata = new() { SchemaVersion = 1, Scope = bridge.SourceScope.Clone(), Registry = { registry } } }.Validate();
        }
        catch (InvalidDataException) { return Refuse(); }
        var authorized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var original in bridge.MutableRegistry)
        {
            if (!authorized.Add(original.PawnCustodyId) || original.NonAuthoritativeMirror || original.Tombstone
                || !original.Scope.Equals(bridge.SourceScope)
                || registry.SingleOrDefault(r => r.PawnCustodyId == original.PawnCustodyId) is not { } exact || !exact.Equals(original)) return Refuse();
        }
        var result = registry.Select(r => r.Clone()).ToArray();
        foreach (var entry in result.Where(r => authorized.Contains(r.PawnCustodyId))) entry.Scope = current.Clone();
        return new(true, null, result);
    }
    private static bool Uuid(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString("D");
}
