using Microsoft.EntityFrameworkCore;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class PawnCustodyBridge
{
    /// <summary>Acquire A/O/S before LiveStateLock, but begin the database transaction only after Live is held.</summary>
    public async Task<CustodyEntry> EnterAsync(IReadOnlyList<long> owners, bool tagged, CancellationToken ct = default)
    {
        long[] ids = owners.Where(id => id > 0).Distinct().Order().ToArray();
        if (!tagged && !await db.PawnCustody.AsNoTracking().AnyAsync(p => ids.Contains(p.PlayerId), ct)) return new(this, null, false);
        return new(this, await authority.AcquireAsync(ids, ct), true);
    }

    public CustodyEntry UseHeldEntry(OutpostLease lease)
    { lease.Require(authority); return new(this, lease, false); }

    public sealed class CustodyEntry : IAsyncDisposable
    {
        private readonly PawnCustodyBridge bridge;
        private readonly OutpostLease? lease;
        private readonly bool ownsLease;
        internal CustodyEntry(PawnCustodyBridge bridge, OutpostLease? lease, bool ownsLease)
        { this.bridge = bridge; this.lease = lease; this.ownsLease = ownsLease; }
        public CustodyEntry Borrow() => new(bridge, lease, false);
        public Task<CustodyTransaction> BeginRoutesAsync(IReadOnlyList<CustodyRoute> routes, CancellationToken ct = default) =>
            bridge.BeginRoutesCoreAsync(routes, lease, allowAcquire: false, ct);
        public async ValueTask DisposeAsync() { if (ownsLease && lease is not null) await lease.DisposeAsync(); }
    }
}
