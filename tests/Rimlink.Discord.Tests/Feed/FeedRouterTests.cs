using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Rimlink.Discord.Api;
using Rimlink.Discord.Feed;
using Rimlink.Discord.Tests.Support;
using Rimlink.Discord.Text;
using Xunit;
using static Rimlink.Discord.Tests.Support.Events;

namespace Rimlink.Discord.Tests.Feed;

public class FeedRouterTests
{
    private const ulong Server = 10, Raids = 11, Market = 12, Moderation = 13, Chat = 14;

    private readonly FakeDiscordOut discord = new();
    private readonly FakeTimeProvider time = new();
    private readonly List<FeedEvent> chat = [];
    private readonly Strings strings = new("en");
    private readonly OutboundQueue queue;
    private readonly FeedRouter router;

    public FeedRouterTests()
    {
        queue = new OutboundQueue(discord, NullLogger<OutboundQueue>.Instance);
        router = Router(new ChannelMap(Server, Raids, Market, Moderation, Chat));
    }

    private FeedRouter Router(ChannelMap channels) => new(channels, new Formatter(strings), queue, strings, time, chat.Add);

    private async Task<List<string>> Flush()
    {
        await queue.FlushAsync(CancellationToken.None);
        return discord.Sent.SelectMany(s => s.Text.Split('\n')).ToList();
    }

    private static FeedSignal E(string type, object data) => new(FeedSignalKind.Event, Ev(type, data));

    private static readonly FeedSignal Connected = new(FeedSignalKind.Connected);
    private static readonly FeedSignal Disconnected = new(FeedSignalKind.Disconnected);

    [Fact]
    public async Task EventsGoToTheirGroupChannel()
    {
        router.Handle(E(FeedTypes.PlayerJoined, new { player = P(1, "Nova"), faction = "" }));
        router.Handle(E(FeedTypes.RaidLanded, new { raidId = 1, attacker = P(1, "Nova"), target = P(2, "Bob") }));
        router.Handle(E(FeedTypes.LotPosted, new { lotId = 1, seller = P(1, "Nova"), buyer = (object?)null, price = 5, summary = "Steel" }));
        router.Handle(E(FeedTypes.AdminAction, new { action = "kick", actor = "api", target = (object?)null, details = "" }));
        await Flush();

        Assert.Single(discord.In(Server));
        Assert.Single(discord.In(Raids));
        Assert.Single(discord.In(Market));
        Assert.Single(discord.In(Moderation));
    }

    [Fact]
    public async Task DisabledGroupIsSilent()
    {
        var silentMarket = Router(new ChannelMap(Server, Raids, 0, Moderation, Chat));

        silentMarket.Handle(E(FeedTypes.LotPosted, new { lotId = 1, seller = P(1, "Nova"), buyer = (object?)null, price = 5, summary = "Steel" }));

        Assert.Empty(await Flush());
    }

    [Fact]
    public void ChatEventsGoToTheChatHandler()
    {
        router.Handle(E(FeedTypes.ChatMessage, new { id = 1, kind = "player", from = P(1, "Nova"), author = (string?)null, text = "hi" }));

        Assert.Single(chat);
    }

    [Fact]
    public async Task GapGoesToModeration()
    {
        router.Handle(new FeedSignal(FeedSignalKind.Gap));
        await Flush();

        Assert.Equal([strings.T("feed.gap")], discord.In(Moderation));
    }

    [Fact]
    public async Task ShortOutageSaysNothing()
    {
        router.Handle(Disconnected);
        time.Advance(TimeSpan.FromSeconds(30));
        router.Tick();
        router.Handle(Connected);
        router.Handle(E(FeedTypes.Notice, new { text = "hi" }));
        time.Advance(TimeSpan.FromSeconds(10));
        router.Tick();

        var lines = await Flush();
        Assert.DoesNotContain(strings.T("feed.server.down"), lines);
        Assert.DoesNotContain(strings.T("feed.server.up"), lines);
    }

    [Fact]
    public async Task DownLineAfterSixtySecondsOnce()
    {
        router.Handle(Disconnected);
        time.Advance(TimeSpan.FromSeconds(59));
        router.Tick();
        Assert.Empty(await Flush());

        time.Advance(TimeSpan.FromSeconds(2));
        router.Tick();
        router.Handle(Disconnected);
        time.Advance(TimeSpan.FromMinutes(3));
        router.Tick();

        Assert.Equal([strings.T("feed.server.down")], await Flush());
    }

    [Fact]
    public async Task UpLineAfterOutage()
    {
        router.Handle(Disconnected);
        time.Advance(TimeSpan.FromSeconds(61));
        router.Tick();
        router.Handle(Connected);
        router.Handle(E(FeedTypes.Notice, new { text = "hi" }));

        Assert.Equal([strings.T("feed.server.down"), strings.T("feed.server.up"), strings.T("feed.notice", "hi")], await Flush());
    }

    [Fact]
    public async Task UpLineAfterAQuietReconnect()
    {
        router.Handle(Disconnected);
        time.Advance(TimeSpan.FromSeconds(61));
        router.Tick();
        router.Handle(Connected);
        time.Advance(TimeSpan.FromSeconds(6));
        router.Tick();

        Assert.Equal([strings.T("feed.server.down"), strings.T("feed.server.up")], await Flush());
    }

    [Fact]
    public async Task ServerStartedReplacesUpLine()
    {
        router.Handle(Disconnected);
        time.Advance(TimeSpan.FromSeconds(61));
        router.Tick();
        router.Handle(Connected);
        router.Handle(new FeedSignal(FeedSignalKind.Gap));
        router.Handle(E(FeedTypes.ServerStarted, new { version = "0.5.0" }));
        time.Advance(TimeSpan.FromSeconds(10));
        router.Tick();
        await Flush();

        Assert.Equal([strings.T("feed.server.down"), strings.T("feed.server.started", "0.5.0")], discord.In(Server).SelectMany(t => t.Split('\n')));
    }
}
