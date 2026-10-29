using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Rimlink.Discord.Api;
using Rimlink.Discord.Feed;
using Rimlink.Discord.Gateway;
using Rimlink.Discord.Tests.Support;
using Rimlink.Discord.Text;
using AdminFeed = Rimlink.Server.Application.Feed.AdminFeed;
using NoticeData = Rimlink.Server.Application.Feed.NoticeData;
using ServerFeedTypes = Rimlink.Server.Application.Feed.FeedTypes;
using Xunit;

namespace Rimlink.Discord.Tests.Api;

/// <summary>The last feed id survives a bot restart (bot spec §8: a restarted bot catches up without duplicates).</summary>
public class FeedStateTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rimlink-bot-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void SavedIdIsLoadedBack()
    {
        var state = new FeedState(Path.Combine(dir, "feed-state"), NullLogger<FeedState>.Instance);

        Assert.Null(state.Load());
        state.Save("1700000000:42");

        Assert.Equal("1700000000:42", new FeedState(Path.Combine(dir, "feed-state"), NullLogger<FeedState>.Instance).Load());
    }

    [Fact]
    public void NoFileConfiguredKeepsNothing()
    {
        var state = new FeedState(null, NullLogger<FeedState>.Instance);

        state.Save("1:1");

        Assert.Null(state.Load());
    }

    [Fact]
    public async Task WorkerSavesTheLastIdAndARestartedReaderCatchesUp()
    {
        using var bot = new BotFixture();
        var feed = bot.Fixture.Services.GetRequiredService<AdminFeed>();
        string file = Path.Combine(dir, "feed-state");
        var strings = new Strings("en");
        var discord = new FakeDiscordOut();
        var queue = new OutboundQueue(discord, NullLogger<OutboundQueue>.Instance);
        var channels = new ChannelMap(10, 11, 12, 13, 0);
        var router = new FeedRouter(channels, new Formatter(strings), queue, strings, TimeProvider.System, _ => { });
        var options = new FeedReaderOptions { Backoff = [TimeSpan.FromMilliseconds(10)] };

        var first = new FeedReader(bot.FeedClient(), options, NullLogger<FeedReader>.Instance);
        var worker = new FeedWorker(first, router, queue, new FeedState(file, NullLogger<FeedState>.Instance), NullLogger<FeedWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await WaitFor(() => first.LastId is null && feed.SubscriberCount == 1);
        feed.Publish(ServerFeedTypes.Notice, new NoticeData("one"));
        await WaitFor(() => File.Exists(file) && File.ReadAllText(file).Trim() == feed.LastId);
        await worker.StopAsync(CancellationToken.None);

        feed.Publish(ServerFeedTypes.Notice, new NoticeData("missed while the bot was down"));
        var restarted = new FeedReader(bot.FeedClient(), options, NullLogger<FeedReader>.Instance) { LastId = new FeedState(file, NullLogger<FeedState>.Instance).Load() };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var signals = new List<FeedSignal>();
        await foreach (var signal in restarted.Run(cts.Token))
        {
            signals.Add(signal);
            if (signal.Kind == FeedSignalKind.Event) break;
        }

        Assert.Equal("missed while the bot was down", signals.Last().Event!.Data.GetProperty("text").GetString());
        Assert.DoesNotContain(signals, s => s.Kind == FeedSignalKind.Gap);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met in 10 s");
            await Task.Delay(50);
        }
    }
}
