using Rimlink.Protocol.V1;

namespace Rimlink.Client.Outposts;

/// <summary>A detached read model. Reading it cannot authorize a mutation or an import.</summary>
public sealed class OutpostState
{
    private readonly object gate = new();
    private OutpostStateReply? snapshot;
    private OutpostScopeMessage? scope;
    public OutpostStateReply? Snapshot { get { lock (gate) return snapshot?.Clone(); } }
    public OutpostScopeMessage? Scope { get { lock (gate) return scope?.Clone(); } }
    public long AuthoritativeRevision { get { lock (gate) return snapshot?.AuthoritativeRevision ?? 0; } }
    public IReadOnlyList<OutpostSummary> Outposts { get { lock (gate) return snapshot?.Outposts.Select(o => o.Clone()).ToArray() ?? []; } }
    public IReadOnlyList<OutpostOperationReply> PendingOperations { get { lock (gate) return snapshot?.PendingOperations.Select(o => o.Clone()).ToArray() ?? []; } }
    public void BindScope(OutpostScopeMessage current)
    {
        if (!OutpostReconciliation.ValidScope(current)) throw new InvalidDataException("invalid outpost preview scope");
        lock (gate)
        {
            if (!OutpostReconciliation.SameScope(scope, current)) snapshot = null;
            scope = current.Clone();
        }
    }
    public void Load(OutpostStateReply value)
    {
        var copy = value.Clone();
        if (!OutpostReconciliation.ValidScope(copy.Scope) || copy.AuthoritativeRevision < 0
            || copy.Outposts.Count > 10000 || copy.PendingOperations.Count > 10000)
            throw new InvalidDataException("invalid outpost preview");
        lock (gate)
        {
            if (scope is not null && !OutpostReconciliation.SameScope(scope, copy.Scope))
                throw new InvalidDataException("outpost preview belongs to another boundary");
            if (snapshot is not null && copy.AuthoritativeRevision < snapshot.AuthoritativeRevision) return;
            scope = copy.Scope.Clone(); snapshot = copy;
        }
    }
    public void Reset() { lock (gate) { snapshot = null; scope = null; } }
}
