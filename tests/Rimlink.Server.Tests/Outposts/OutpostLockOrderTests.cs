using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostLockOrderTests
{
    [Fact]
    public async Task Lease_is_typed_sorted_distinct_and_disposal_is_idempotent()
    {
        using var h = new AuthorityHarness();
        var lease = await h.Authority.AcquireAsync([7, 2, 7], CancellationToken.None);
        Assert.Equal(new long[] { 2, 7 }, lease.OwnerIds);
        Assert.True(lease.CoversOwner(2));
        Assert.False(lease.CoversOwner(5));
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => lease.CoversOwner(2));
        await using var reused = await h.Authority.AcquireAsync([2], CancellationToken.None);
        Assert.True(reused.CoversOwner(2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancelled_owner_or_storage_wait_releases_only_its_acquired_prefix(bool blockOwner)
    {
        using var h = new AuthorityHarness();
        using var blocker = blockOwner ? await h.Owners.Hold(2) : await h.Files.PerStoreLock.HoldAsync();
        using var cancellation = new CancellationTokenSource();
        Task<OutpostLease> waiting = h.Authority.AcquireAsync([2, 7], cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        blocker.Dispose();
        await using var reused = await h.Authority.AcquireAsync([7, 2], CancellationToken.None);
        Assert.True(reused.CoversOwner(2));
    }

    [Fact]
    public async Task Lease_from_another_authority_is_not_a_lock_held_boolean()
    {
        using var h = new AuthorityHarness();
        using var other = new AuthorityHarness();
        await using var lease = await h.Authority.AcquireAsync([], CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => lease.Require(other.Authority));
    }

    [Fact]
    public async Task Cancelled_wait_does_not_release_another_authority_lease()
    {
        using var h = new AuthorityHarness();
        await using var held = await h.Authority.AcquireAsync([7, 2], CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task<OutpostLease> waiting = h.Authority.AcquireAsync([2, 7], cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(held.CoversOwner(7));
    }
}
