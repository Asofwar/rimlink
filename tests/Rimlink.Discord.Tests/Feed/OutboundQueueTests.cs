using System.Net;
using Discord.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Rimlink.Discord.Feed;
using Rimlink.Discord.Tests.Support;
using Xunit;

namespace Rimlink.Discord.Tests.Feed;

public class OutboundQueueTests
{
    private readonly FakeDiscordOut discord = new();

    private OutboundQueue Queue(int maxLines = 500) => new(discord, NullLogger<OutboundQueue>.Instance, maxLines);

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task PermanentChannelFailureDisablesOnlyThatOutboundChannel(HttpStatusCode status)
    {
        var queue = Queue();
        queue.Enqueue(1, "unreachable");
        discord.Failure = new HttpException(status, null!);
        await queue.FlushAsync(CancellationToken.None);

        discord.Failure = null;
        queue.Enqueue(1, "still disabled");
        queue.Enqueue(2, "other channel");
        await queue.FlushAsync(CancellationToken.None);

        Assert.Empty(discord.In(1));
        Assert.Equal(["other channel"], discord.In(2));
        Assert.Equal(1, queue.Dropped);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task TemporaryDiscordFailureRetainsTheBatch(HttpStatusCode status)
    {
        var queue = Queue();
        queue.Enqueue(1, "retained");
        discord.Failure = new HttpException(status, null!);
        await queue.FlushAsync(CancellationToken.None);
        discord.Failure = null;
        await queue.FlushAsync(CancellationToken.None);
        Assert.Equal(["retained"], discord.In(1));
        Assert.Equal(0, queue.Dropped);
    }

    [Fact]
    public async Task LinesAreJoinedUpTo2000Chars()
    {
        var queue = Queue();
        for (int i = 0; i < 30; i++) queue.Enqueue(1, new string((char)('a' + i % 26), 100));

        await queue.FlushAsync(CancellationToken.None);
        await queue.FlushAsync(CancellationToken.None);

        Assert.Equal(2, discord.Sent.Count);
        Assert.All(discord.Sent, s => Assert.True(s.Text.Length <= 2000));
        Assert.Equal(30, discord.Sent.Sum(s => s.Text.Split('\n').Length));
    }

    [Fact]
    public async Task LongLineIsTruncated()
    {
        var queue = Queue();
        queue.Enqueue(1, new string('x', 2500));

        await queue.FlushAsync(CancellationToken.None);

        var text = Assert.Single(discord.Sent).Text;
        Assert.Equal(2000, text.Length);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public async Task OverflowDropsOldest()
    {
        var queue = Queue(maxLines: 3);
        for (int i = 1; i <= 5; i++) queue.Enqueue(1, $"line {i}");

        await queue.FlushAsync(CancellationToken.None);

        Assert.Equal("line 3\nline 4\nline 5", Assert.Single(discord.Sent).Text);
        Assert.Equal(2, queue.Dropped);
    }

    [Fact]
    public async Task ChannelsAreSeparate()
    {
        var queue = Queue();
        queue.Enqueue(1, "one");
        queue.Enqueue(2, "two");

        await queue.FlushAsync(CancellationToken.None);

        Assert.Equal(["one"], discord.In(1));
        Assert.Equal(["two"], discord.In(2));
    }

    [Fact]
    public async Task ZeroChannelIsIgnored()
    {
        var queue = Queue();
        queue.Enqueue(0, "nowhere");

        await queue.FlushAsync(CancellationToken.None);

        Assert.Empty(discord.Sent);
    }

    [Fact]
    public async Task FailedSendIsRetriedNextFlush()
    {
        var queue = Queue();
        queue.Enqueue(1, "kept");
        discord.Fail = true;
        await queue.FlushAsync(CancellationToken.None);

        discord.Fail = false;
        await queue.FlushAsync(CancellationToken.None);

        Assert.Equal(["kept"], discord.In(1));
    }
}
