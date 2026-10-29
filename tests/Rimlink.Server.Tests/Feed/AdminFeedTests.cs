using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Feed;
using Rimlink.Server.Hosting;
using Xunit;

namespace Rimlink.Server.Tests.Feed;

public class AdminFeedTests
{
    private static AdminFeed Feed(int buffer = 1000) =>
        new(Options.Create(new AdminOptions { FeedBufferSize = buffer }), TimeProvider.System, NullLogger<AdminFeed>.Instance);

    private static void PublishMany(AdminFeed feed, int count)
    {
        for (int i = 0; i < count; i++) feed.Publish("test", new { n = i });
    }

    [Fact]
    public void FirstEventIsServerStarted()
    {
        var feed = Feed();

        using var sub = feed.Subscribe($"{feed.Epoch}:0");

        Assert.False(sub.Gap);
        var first = Assert.Single(sub.Backlog);
        Assert.Equal(FeedTypes.ServerStarted, first.Type);
        Assert.Equal(1, first.Seq);
        Assert.Equal($"{feed.Epoch}:1", first.Id);
    }

    [Fact]
    public void NoAfterMeansOnlyNewEvents()
    {
        var feed = Feed();
        PublishMany(feed, 2);

        using var sub = feed.Subscribe(null);
        feed.Publish("test", new { n = 9 });

        Assert.False(sub.Gap);
        Assert.Empty(sub.Backlog);
        Assert.True(sub.Live.TryRead(out var live));
        Assert.Equal(4, live.Seq);
    }

    [Fact]
    public void AfterResumesWithoutLossOrDuplicates()
    {
        var feed = Feed();
        PublishMany(feed, 5); // seqs 2..6

        using var sub = feed.Subscribe($"{feed.Epoch}:3");
        feed.Publish("test", new { n = 9 });

        Assert.False(sub.Gap);
        Assert.Equal([4L, 5L, 6L], sub.Backlog.Select(e => e.Seq));
        Assert.True(sub.Live.TryRead(out var live));
        Assert.Equal(7, live.Seq);
        Assert.False(sub.Live.TryRead(out _));
    }

    [Fact]
    public void ForeignEpochIsAGap()
    {
        var feed = Feed();

        using var sub = feed.Subscribe($"{feed.Epoch - 1}:1");

        Assert.True(sub.Gap);
        Assert.Equal([1L], sub.Backlog.Select(e => e.Seq));
    }

    [Fact]
    public void MalformedAfterIsAGap()
    {
        var feed = Feed();

        using var sub = feed.Subscribe("abc");

        Assert.True(sub.Gap);
        Assert.Single(sub.Backlog);
    }

    [Fact]
    public void EvictedSeqIsAGap()
    {
        var feed = Feed(buffer: 3);
        PublishMany(feed, 9); // seqs 1..10, buffer keeps 8..10

        using var evicted = feed.Subscribe($"{feed.Epoch}:2");
        using var kept = feed.Subscribe($"{feed.Epoch}:7");

        Assert.True(evicted.Gap);
        Assert.Equal([8L, 9L, 10L], evicted.Backlog.Select(e => e.Seq));
        Assert.False(kept.Gap);
        Assert.Equal([8L, 9L, 10L], kept.Backlog.Select(e => e.Seq));
    }

    [Fact]
    public void SlowSubscriberIsCompleted()
    {
        var feed = Feed(buffer: 3);
        using var sub = feed.Subscribe(null);

        PublishMany(feed, 4);

        Assert.Equal(0, feed.SubscriberCount);
        int read = 0;
        while (sub.Live.TryRead(out _)) read++;
        Assert.Equal(3, read); // what fit before the cut-off is still delivered, then the stream ends
        Assert.True(sub.Live.Completion.IsCompleted);
    }

    [Fact]
    public void DisposedSubscriptionIsRemoved()
    {
        var feed = Feed();
        var sub = feed.Subscribe(null);
        Assert.Equal(1, feed.SubscriberCount);

        sub.Dispose();

        Assert.Equal(0, feed.SubscriberCount);
    }

    [Fact]
    public async Task ConcurrentPublishDuringSubscribeLosesNothing()
    {
        var feed = Feed(buffer: 5000);
        var publisher = Task.Run(() => PublishMany(feed, 2000)); // seqs 2..2001
        await Task.Delay(1);

        string after = feed.LastId;
        using var sub = feed.Subscribe(after);
        long from = long.Parse(after.Split(':')[1]);
        await publisher;

        var seqs = sub.Backlog.Select(e => e.Seq).ToList();
        while (sub.Live.TryRead(out var e)) seqs.Add(e.Seq);

        Assert.Equal(Enumerable.Range((int)from + 1, 2001 - (int)from).Select(i => (long)i), seqs);
    }
}
