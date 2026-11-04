namespace Rimlink.Server.Application.Outposts;

/// <summary>Exclusive A → sorted O → S scope. Not reentrant and not a substitute for custody authorization.</summary>
public sealed class OutpostLease : IAsyncDisposable
{
    private readonly OutpostAuthority owner;
    private readonly IReadOnlyList<long> ownerIds;
    private IDisposable[]? held;
    internal OutpostAnchorStore.OutpostAnchorLease Anchor { get; }
    internal string? LifecycleOperationId { get; set; }

    internal OutpostLease(OutpostAuthority owner, long[] ownerIds,
        OutpostAnchorStore.OutpostAnchorLease anchor, IDisposable[] held)
    {
        this.owner = owner;
        this.ownerIds = Array.AsReadOnly(ownerIds);
        Anchor = anchor;
        this.held = held;
    }

    public IReadOnlyList<long> OwnerIds { get { Require(owner); return ownerIds; } }
    public bool CoversOwner(long ownerId) { Require(owner); return ownerIds.Contains(ownerId); }

    internal void Require(OutpostAuthority expected)
    {
        ObjectDisposedException.ThrowIf(held is null, this);
        if (!ReferenceEquals(owner, expected)) throw new InvalidOperationException("outpost-lease-owner-mismatch");
    }

    public ValueTask DisposeAsync()
    {
        IDisposable[]? releases = Interlocked.Exchange(ref held, null);
        if (releases is not null)
        {
            List<Exception>? failures = null;
            for (int index = releases.Length - 1; index >= 0; index--)
            {
                try { releases[index].Dispose(); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            if (failures is not null) throw new AggregateException(failures);
        }
        return ValueTask.CompletedTask;
    }
}
