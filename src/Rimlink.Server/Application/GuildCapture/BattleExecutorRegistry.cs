using Google.Protobuf;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record BattleExecutorLease(string ExecutorId, string LeaseId, long Fence, ByteString ProfileDigest);

/// <summary>Current process admission only. Future bookings remain in the common authority's GuildBattleSlot rows.</summary>
internal sealed partial class BattleExecutorRegistry
{
    private readonly TimeProvider time;
    private readonly string executorId;
    private readonly ByteString profile;
    private readonly object gate = new();
    private BattleExecutorLease? current;
    private long observed, fence;
    private bool busy, cleanupHeld;

    internal BattleExecutorRegistry(TimeProvider time)
    { this.time = time; executorId = ""; profile = ByteString.Empty; }

    // An empty digest pins nothing here: the arbiter's profile is then admitted by its recorded, unrevoked approval alone,
    // so a newly approved profile needs no configuration change and no server restart.
    internal BattleExecutorRegistry(TimeProvider time, string executorId, byte[] approvedProfileDigest)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (string.IsNullOrWhiteSpace(executorId) || executorId.Length > 128 || executorId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            || approvedProfileDigest is not ({ Length: 32 } or { Length: 0 })) throw Refused();
        this.time = time; this.executorId = executorId; profile = ByteString.CopyFrom(approvedProfileDigest);
    }

    // Only the dedicated executor credential handler may call these methods; player/admin login is not executor authentication.
    internal BattleExecutorLease RegisterAuthenticated(string id, byte[] actualProfileDigest)
    {
        lock (gate)
        {
            if (id != executorId || actualProfileDigest is not { Length: 32 } || !profile.IsEmpty && !profile.Span.SequenceEqual(actualProfileDigest)
                || Fresh() || executionBinding is not null || couriers.Count>0) throw Refused();
            if (busy) cleanupHeld = true;
            executionBinding = null; executionBoot = "";
            current = new(executorId, Guid.NewGuid().ToString("D"), checked(++fence), ByteString.CopyFrom(actualProfileDigest));
            observed = time.GetTimestamp(); busy = false;
            return current;
        }
    }

    internal void HeartbeatAuthenticated(BattleExecutorLease lease, bool hasUnfinishedProcess)
    {
        lock (gate)
        {
            Require(lease);
            if(couriers.TryGetValue(lease.LeaseId,out var courier)){couriers[lease.LeaseId]=courier with{Observed=time.GetTimestamp()};return;}
            busy = hasUnfinishedProcess; observed = time.GetTimestamp();
        }
    }

    internal BattleExecutorLease? Available()
    { lock (gate) return Fresh() && !busy && !cleanupHeld && (executionBinding is null || executionBinding.BattleId=="") ? current : null; }

    internal void RequireCurrent(BattleExecutorLease lease)
    { lock (gate) Require(lease); }

    internal void HoldCleanup(BattleExecutorLease lease)
    { lock (gate) { Require(lease); cleanupHeld = true; } }

    internal void RequireQuiescent(BattleExecutorLease lease)
    { lock (gate) { RequireOccupant(lease); if (busy) throw Refused(); } }
    internal void ReleaseCleanup(BattleExecutorLease lease)
    { lock (gate) { Require(lease); if (busy) throw Refused(); cleanupHeld = false; } }

    internal void Revoke()
    { lock (gate) { if (busy) cleanupHeld = true; current = null; couriers.Clear(); } }

    private bool Fresh()
    {
        var elapsed = time.GetElapsedTime(observed, time.GetTimestamp());
        return current is not null && elapsed >= TimeSpan.Zero && elapsed <= TimeSpan.FromSeconds(30);
    }
    private void Require(BattleExecutorLease lease)
    {
        if(couriers.TryGetValue(lease.LeaseId,out var courier))
        {if(courier.Lease!=lease || !FreshAt(courier.Observed))throw Refused();return;}
        if(!Fresh() || current!=lease)throw Refused();
    }
    private static InvalidDataException Refused() => new("battle-executor-lease-refused");
}
