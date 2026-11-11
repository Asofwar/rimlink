using Rimlink.Server.Application.GuildCapture;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class BattleExecutorRegistryTests
{
    private static readonly byte[] Digest = Enumerable.Repeat((byte)5, 32).ToArray();
    [Fact]
    public void Only_the_configured_actual_profile_can_register_one_process()
    {
        var time = new Clock(); var registry = new BattleExecutorRegistry(time, "arbiter-1", Digest);
        Assert.Null(registry.Available());
        Assert.Throws<InvalidDataException>(() => registry.RegisterAuthenticated("foreign", Digest));
        Assert.Throws<InvalidDataException>(() => registry.RegisterAuthenticated("arbiter-1", new byte[32]));
        var lease = registry.RegisterAuthenticated("arbiter-1", Digest);
        Assert.Equal(lease, registry.Available());
        Assert.Throws<InvalidDataException>(() => registry.RegisterAuthenticated("arbiter-1", Digest));
        var copy = registry.Available()!.ProfileDigest.ToByteArray(); copy[0]++;
        Assert.Equal(Digest, registry.Available()!.ProfileDigest.ToByteArray());
    }

    [Fact]
    public void Lease_expiry_uses_monotonic_time_and_a_replacement_never_accepts_old_heartbeats()
    {
        var time = new Clock(); var registry = new BattleExecutorRegistry(time, "arbiter-1", Digest);
        var first = registry.RegisterAuthenticated("arbiter-1", Digest);
        time.Utc = time.Utc.AddYears(5); Assert.NotNull(registry.Available());
        time.Ticks += TimeSpan.FromSeconds(31).Ticks; Assert.Null(registry.Available());
        var next = registry.RegisterAuthenticated("arbiter-1", Digest);
        Assert.NotEqual(first.LeaseId, next.LeaseId); Assert.True(next.Fence > first.Fence);
        Assert.Throws<InvalidDataException>(() => registry.HeartbeatAuthenticated(first, false));
        registry.HeartbeatAuthenticated(next, true); Assert.Null(registry.Available());
        registry.HeartbeatAuthenticated(next, false); Assert.NotNull(registry.Available());
    }

    [Fact]
    public void Revocation_and_cleanup_hold_block_new_starts_without_rewriting_bookings()
    {
        var registry = new BattleExecutorRegistry(new Clock(), "arbiter-1", Digest);
        var lease = registry.RegisterAuthenticated("arbiter-1", Digest);
        registry.HoldCleanup(lease); registry.HeartbeatAuthenticated(lease, false);
        Assert.Null(registry.Available());
        registry.Revoke(); Assert.Null(registry.Available());
        Assert.Throws<InvalidDataException>(() => registry.HeartbeatAuthenticated(lease, false));
    }

    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        internal DateTimeOffset Utc = DateTimeOffset.Parse("2030-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
}
