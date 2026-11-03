using Microsoft.Extensions.Logging.Abstractions;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

// Primitive coverage only: these tests do not establish the application's full lock graph.
public sealed class OutpostPrimitiveLockTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rimlink-tests", Guid.NewGuid().ToString("N"));

    public OutpostPrimitiveLockTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HoldAsync_cancellation_while_waiting_preserves_non_reentrancy_and_allows_reuse(bool separateLockObject)
    {
        var owner = new SaveStorageLock(directory);
        var contender = separateLockObject ? new SaveStorageLock(directory) : owner;
        var held = await owner.HoldAsync();
        using var cancellation = new CancellationTokenSource();
        Task<IDisposable>? waiting = null;
        try
        {
            waiting = contender.HoldAsync(cancellation.Token);
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Timeout));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally
        {
            held.Dispose();
            if (waiting is not null) await DisposeCompletedLeaseAsync(waiting);
        }

        using var deadline = new CancellationTokenSource(Timeout);
        using var reused = await contender.HoldAsync(deadline.Token);
        Assert.True(File.Exists(Path.Combine(directory, "save-storage.lock")));
    }

    [Fact]
    public async Task Disposing_old_lease_twice_does_not_release_the_current_lease()
    {
        var storage = new SaveStorageLock(directory);
        var old = await storage.HoldAsync();
        old.Dispose();
        using var current = await storage.HoldAsync();

        old.Dispose();

        var contender = new SaveStorageLock(directory);
        using var cancellation = new CancellationTokenSource();
        var waiting = contender.HoldAsync(cancellation.Token);
        try
        {
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Timeout));
        }
        finally
        {
            cancellation.Cancel();
            await DisposeCompletedLeaseAsync(waiting);
        }
    }

    [Fact]
    public async Task WaitAsync_and_Release_remain_compatible_with_leases()
    {
        var storage = new SaveStorageLock(directory);
        await storage.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        Task<IDisposable>? waiting = null;
        try
        {
            waiting = storage.HoldAsync(cancellation.Token);
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Timeout));
        }
        finally
        {
            cancellation.Cancel();
            storage.Release();
            if (waiting is not null) await DisposeCompletedLeaseAsync(waiting);
        }

        using (await storage.HoldAsync()) { }
        await storage.WaitAsync();
        storage.Release();
    }

    [Fact]
    public async Task Explicit_acquire_and_hosted_start_are_idempotent_and_refuse_competitors_until_stop()
    {
        var data = new DataDirectory(directory);
        using var owner = Server(data);
        using var contender = Server(data);
        owner.Acquire();
        owner.Acquire();
        Assert.Throws<InvalidOperationException>(() => contender.Acquire());

        await owner.StartAsync(CancellationToken.None);
        await owner.StartAsync(CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => contender.Acquire());

        await owner.StopAsync(CancellationToken.None);
        await owner.StopAsync(CancellationToken.None);
        Assert.True(File.Exists(data.LockFile));
        contender.Acquire();
        using var third = Server(data);
        Assert.Throws<InvalidOperationException>(() => third.Acquire());
    }

    [Fact]
    public async Task Dispose_releases_explicit_acquire_even_when_hosted_start_never_ran()
    {
        var data = new DataDirectory(directory);
        var owner = Server(data);
        using var contender = Server(data);
        try
        {
            owner.Acquire();
            Assert.Throws<InvalidOperationException>(() => contender.Acquire());
        }
        finally
        {
            owner.Dispose();
        }

        Assert.True(File.Exists(data.LockFile));
        contender.Acquire();
        owner.Dispose();
        await owner.StopAsync(CancellationToken.None);
        using var third = Server(data);
        Assert.Throws<InvalidOperationException>(() => third.Acquire());
    }

    [Fact]
    public async Task Hosted_start_without_explicit_acquire_holds_a_persistent_lock_file()
    {
        var data = new DataDirectory(directory);
        using var owner = Server(data);
        using var contender = Server(data);
        await owner.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => contender.StartAsync(CancellationToken.None));

        await owner.StopAsync(CancellationToken.None);

        Assert.True(File.Exists(data.LockFile));
        await contender.StartAsync(CancellationToken.None);
    }

    private static ServerLock Server(DataDirectory data) => new(data, NullLogger<ServerLock>.Instance);

    private static async Task DisposeCompletedLeaseAsync(Task<IDisposable> waiting)
    {
        try
        {
            using var lease = await waiting.WaitAsync(Timeout);
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
