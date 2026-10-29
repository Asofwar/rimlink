using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Rimlink.Discord.Api;
using Rimlink.Discord.Chat;
using Rimlink.Discord.Feed;
using Rimlink.Discord.Tests.Support;
using Rimlink.Discord.Text;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Discord.Tests.Support.Events;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Discord.Tests.Chat;

public class ChatBridgeTests
{
    private const ulong ChatChannel = 14;

    private readonly FakeDiscordOut discord = new();
    private readonly OutboundQueue queue;

    public ChatBridgeTests() => queue = new OutboundQueue(discord, NullLogger<OutboundQueue>.Instance);

    private ChatBridge Bridge(RimlinkApi api, ulong chat = ChatChannel) =>
        new(new ChannelMap(10, 11, 12, 13, chat), queue, api, discord, new Strings("en"), NullLogger<ChatBridge>.Instance);

    private static DiscordChatMessage Msg(string text, ulong channel = ChatChannel, bool bot = false, int attachments = 0) =>
        new(channel, 777, bot, "Mod", text, attachments);

    private async Task<List<string>> Flushed()
    {
        await queue.FlushAsync(CancellationToken.None);
        return discord.In(ChatChannel);
    }

    private static async Task<List<JsonElement>> ServerChat(BotFixture bot) =>
        (await bot.Fixture.Rest().GetFromJsonAsync<JsonElement>("/api/admin/chat?after=0&limit=50")).EnumerateArray().ToList();

    [Fact]
    public async Task PlayerLineGoesToChatChannel()
    {
        using var bot = new BotFixture();

        Bridge(bot.Api()).FromGame(Ev(FeedTypes.ChatMessage, new { id = 1, kind = "player", from = P(1, "Nova"), author = (string?)null, text = "hi all" }));

        Assert.Equal(["**Nova**: hi all"], await Flushed());
    }

    [Fact]
    public async Task ExternalLinesAreNotEchoed()
    {
        using var bot = new BotFixture();
        using var probe = bot.Fixture.Probe();
        var bridge = Bridge(bot.Api());

        Assert.True(await bridge.FromDiscord(Msg("hello game"), CancellationToken.None));
        var echoed = await probe.Next(Server.Application.Feed.FeedTypes.ChatMessage);
        bridge.FromGame(new FeedEvent(echoed.Id, echoed.Type, JsonDocument.Parse(echoed.DataJson).RootElement.Clone()));

        Assert.Empty(await Flushed());
    }

    [Fact]
    public async Task SystemLinesAreSkipped()
    {
        using var bot = new BotFixture();

        Bridge(bot.Api()).FromGame(Ev(FeedTypes.ChatMessage, new { id = 1, kind = "system", from = (object?)null, author = (string?)null, text = "Nova joined the server" }));

        Assert.Empty(await Flushed());
    }

    [Fact]
    public async Task GameTextIsEscaped()
    {
        using var bot = new BotFixture();

        Bridge(bot.Api()).FromGame(Ev(FeedTypes.ChatMessage, new { id = 1, kind = "player", from = P(1, "@here"), author = (string?)null, text = "@everyone **x**" }));

        string line = Assert.Single(await Flushed());
        Assert.DoesNotContain("@everyone", line, StringComparison.Ordinal);
        Assert.DoesNotContain("@here", line, StringComparison.Ordinal);
        Assert.Contains(@"\*\*x\*\*", line);
    }

    [Fact]
    public async Task DiscordMessageIsPosted()
    {
        using var bot = new BotFixture();
        var nova = await bot.Fixture.RegisterAndLogin("nova", playerName: "Nova");

        Assert.True(await Bridge(bot.Api()).FromDiscord(Msg("hello game"), CancellationToken.None));

        var history = await new Protocol.V1.Chat.ChatClient(bot.Fixture.CreateChannel()).HistoryAsync(new Empty(), Bearer(nova.SessionToken));
        var line = history.Messages.Single(m => m.Text == "hello game");
        Assert.Equal(MessageKind.External, line.Kind);
        Assert.Equal("Mod", line.ExternalAuthor);
    }

    [Fact]
    public async Task BotsAndOtherChannelsAreIgnored()
    {
        using var bot = new BotFixture();
        var spy = new CountingHandler();
        var bridge = Bridge(bot.Api(spy: spy));

        Assert.False(await bridge.FromDiscord(Msg("from a bot", bot: true), CancellationToken.None));
        Assert.False(await bridge.FromDiscord(Msg("elsewhere", channel: 99), CancellationToken.None));
        Assert.False(await Bridge(bot.Api(spy: spy), chat: 0).FromDiscord(Msg("bridge off", channel: 0), CancellationToken.None));
        Assert.Equal(0, spy.Count);
    }

    [Fact]
    public async Task LongMessageIsCutToTheServerLimit()
    {
        using var bot = new BotFixture();

        Assert.True(await Bridge(bot.Api()).FromDiscord(Msg(new string('x', 600)), CancellationToken.None));

        string text = (await ServerChat(bot)).Single().GetProperty("text").GetString()!;
        Assert.Equal(ChatBridge.MaxLength, text.Length);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public async Task AttachmentIsMarked()
    {
        using var bot = new BotFixture();
        var bridge = Bridge(bot.Api());

        await bridge.FromDiscord(Msg("look", attachments: 1), CancellationToken.None);
        await bridge.FromDiscord(Msg("", attachments: 2), CancellationToken.None);

        Assert.Equal(["look [attachment]", "[attachment]"], (await ServerChat(bot)).Select(l => l.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task EmptyMessageIsNotSent()
    {
        using var bot = new BotFixture();
        var spy = new CountingHandler();

        Assert.False(await Bridge(bot.Api(spy: spy)).FromDiscord(Msg("   "), CancellationToken.None));
        Assert.Equal(0, spy.Count);
    }

    [Fact]
    public async Task RefusalReacts()
    {
        using var bot = new BotFixture();

        Assert.False(await Bridge(bot.Api("wrong-token-0123456789abcdef")).FromDiscord(Msg("hi"), CancellationToken.None));

        Assert.Equal((ChatChannel, 777UL, "❌"), Assert.Single(discord.Reactions));
    }
}
