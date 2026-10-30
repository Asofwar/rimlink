using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Tests.Support;

/// <summary>
/// Holds the first save of a live session that <paramref name="match"/> picks until <see cref="Release"/>: the gap between a service's
/// read and its write, opened wide so a test can run something else inside it (or stand in for a slow database).
/// </summary>
public sealed class LiveWriteHold(Func<LiveSession, bool> match) : SaveChangesInterceptor
{
    /// <summary>Holds the first save that moves a session to <paramref name="state"/>.</summary>
    public LiveWriteHold(LiveStateDb state) : this(s => s.State == state) { }

    private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DbContext? held;
    private int taken;

    public Task Reached => reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
    public Task Saved => saved.Task.WaitAsync(TimeSpan.FromSeconds(15));

    public void Release() => release.TrySetResult();

    /// <summary>Adds the hold to a fixture's database contexts.</summary>
    public ServerFixture On(ServerFixture fixture) => fixture.WithServices(s => s.ConfigureDbContext<RimlinkDbContext>(o => o.AddInterceptors(this)));

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<LiveSession>().Any(e => e.State == EntityState.Modified && match(e.Entity)) && Interlocked.Exchange(ref taken, 1) == 0)
        {
            held = eventData.Context;
            reached.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (ReferenceEquals(eventData.Context, held)) saved.TrySetResult();
        return ValueTask.FromResult(result);
    }
}
