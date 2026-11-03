using Rimlink.Client.Influence;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Influence;

public sealed class InfluenceCacheTests
{
    private static readonly string World = Guid.NewGuid().ToString("N"), Process = Guid.NewGuid().ToString("N");
    private static InfluenceSnapshot Snapshot(long revision) => new() { WorldInstanceId = World, ProcessInstanceId = Process, Revision = revision, AlgorithmVersion = 1 };
    private static InfluenceCache Cache()
    {
        var cache = new InfluenceCache(); cache.SetContext(World, Process); return cache;
    }

    [Fact]
    public void InvalidationNewerThanResponseDoesNotFinishRefresh()
    {
        var cache = Cache();
        Assert.True(cache.TryAcceptCandidate(Snapshot(9), cache.BeginFetch(), out var first));
        Assert.True(cache.TryPublish(first));
        var fetch = cache.BeginFetch();
        cache.Invalidate(new InfluenceChanged { WorldInstanceId = World, ProcessInstanceId = Process, Revision = 11 });
        Assert.False(cache.TryAcceptCandidate(Snapshot(10), fetch, out _));
        Assert.True(cache.Stale);
        Assert.Equal(11, cache.HighWater);
        Assert.Equal(9, cache.Current!.Revision);
        Assert.True(cache.TryAcceptCandidate(Snapshot(11), cache.BeginFetch(), out var ready));
        Assert.True(cache.TryPublish(ready));
        Assert.False(cache.Stale);
    }

    [Fact]
    public void InvalidationWithoutRevisionAlsoRejectsCandidateAndMeshSwap()
    {
        var cache = Cache();
        Assert.True(cache.TryAcceptCandidate(Snapshot(10), cache.BeginFetch(), out var ready));
        cache.Invalidate(new InfluenceChanged { WorldInstanceId = World, ProcessInstanceId = Process });
        Assert.False(cache.TryPublish(ready));
        Assert.Null(cache.Current);
        Assert.True(cache.Stale);
    }

    [Fact]
    public void SnapshotCannotSelectItsOwnEpochAndRestoredRevisionMayGoBackOnlyAfterTrustedContext()
    {
        var cache = Cache();
        var oldFetch = cache.BeginFetch();
        var next = Snapshot(0); next.ProcessInstanceId = Guid.NewGuid().ToString("N");
        Assert.False(cache.TryAcceptCandidate(next, oldFetch, out _));
        cache.SetContext(World, next.ProcessInstanceId);
        Assert.False(cache.TryAcceptCandidate(Snapshot(100), oldFetch, out _));
        Assert.True(cache.TryAcceptCandidate(next, cache.BeginFetch(), out var ready));
        Assert.True(cache.TryPublish(ready));
        Assert.Equal(0, cache.Current!.Revision);
        cache.Reset();
        Assert.False(cache.TryPublish(ready));
        Assert.Null(cache.Current);
    }
}
