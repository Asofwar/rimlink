using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Feed;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Chat;

/// <summary>Chat lines from outside the game (Discord bot spec §3): POST /api/admin/chat, the EXTERNAL kind, the author field.</summary>
public class ChatRestTests
{
    private const string ReadToken = "read-token-0123456789abcdef";
    private const string ChatToken = "chat-token-0123456789abcdef";

    private static ServerFixture Server() => new ServerFixture()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken)
        .WithToken(0, "reader", ReadToken, "read")
        .WithToken(1, "chatter", ChatToken, "chat");

    private static Task<HttpResponseMessage> Post(ServerFixture fixture, string author, string text, string token = ChatToken) =>
        fixture.Rest(token).PostAsJsonAsync("/api/admin/chat", new { author, text });

    private static async Task<ChatMessageEvent> NextChat(AsyncServerStreamingCall<ServerEvent> stream, Func<ChatMessageEvent, bool> where)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await stream.ResponseStream.MoveNext(cts.Token))
        {
            var ev = stream.ResponseStream.Current;
            if (ev.BodyCase == ServerEvent.BodyOneofCase.Chat && where(ev.Chat)) return ev.Chat;
        }
        throw new InvalidOperationException("stream ended");
    }

    [Fact]
    public async Task PostedLineReachesSessionsAsExternal()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        using var stream = new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), Bearer(nova.SessionToken));

        var response = await Post(fixture, "Mod", "hello from discord");

        response.EnsureSuccessStatusCode();
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64() > 0);
        var line = await NextChat(stream, c => c.Kind == MessageKind.External);
        Assert.Equal("Mod", line.ExternalAuthor);
        Assert.Equal("hello from discord", line.Text);
        Assert.Null(line.From);
        Assert.Equal(0, line.ToPlayerId);
    }

    [Fact]
    public async Task PostedLineIsInHistoryWithAuthor()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");

        (await Post(fixture, "Mod", "remember me")).EnsureSuccessStatusCode();

        var history = await new Protocol.V1.Chat.ChatClient(fixture.CreateChannel()).HistoryAsync(new Empty(), Bearer(nova.SessionToken));
        var line = history.Messages.Single(m => m.Text == "remember me");
        Assert.Equal(MessageKind.External, line.Kind);
        Assert.Equal("Mod", line.ExternalAuthor);
    }

    [Theory]
    [InlineData("", "text")]
    [InlineData("123456789012345678901234567890123", "text")] // 33 characters
    [InlineData("a\u0001", "text")]
    [InlineData("Mod", "")]
    [InlineData("Mod", "   ")]
    [InlineData("Mod", "a\u0007")]
    public async Task PostValidatesAuthorAndText(string author, string text)
    {
        using var fixture = Server();

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(fixture, author, text)).StatusCode);
    }

    [Fact]
    public async Task PostRefusesTextOverTheChatLimit()
    {
        using var fixture = Server();

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(fixture, "Mod", new string('x', 501))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(fixture, "Mod", new string('x', 500))).StatusCode);
    }

    [Fact]
    public async Task PostChatNeedsChatScope()
    {
        using var fixture = Server();

        Assert.Equal(HttpStatusCode.Forbidden, (await Post(fixture, "Mod", "hi", ReadToken)).StatusCode);
    }

    [Fact]
    public async Task SlashLineIsNotACommand()
    {
        using var fixture = Server();

        (await Post(fixture, "Mod", "/shutdown 1")).EnsureSuccessStatusCode();

        var actions = await fixture.Rest().GetFromJsonAsync<JsonElement>("/api/admin/actions");
        Assert.Equal(0, actions.GetArrayLength());
        var chat = await fixture.Rest().GetFromJsonAsync<JsonElement>("/api/admin/chat?after=0");
        Assert.Contains(chat.EnumerateArray(), l => l.GetProperty("text").GetString() == "/shutdown 1");
    }

    [Fact]
    public async Task ChatListShowsExternalLines()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        await new Protocol.V1.Chat.ChatClient(fixture.CreateChannel()).SendAsync(new SendRequest { Text = "from the game" }, Bearer(nova.SessionToken));
        (await Post(fixture, "Mod", "from discord")).EnsureSuccessStatusCode();

        var chat = await fixture.Rest().GetFromJsonAsync<JsonElement>("/api/admin/chat?after=0");

        var external = chat.EnumerateArray().Single(l => l.GetProperty("text").GetString() == "from discord");
        Assert.Equal("external", external.GetProperty("kind").GetString());
        Assert.Equal("Mod", external.GetProperty("author").GetString());
        var player = chat.EnumerateArray().Single(l => l.GetProperty("text").GetString() == "from the game");
        Assert.Equal(JsonValueKind.Null, player.GetProperty("author").ValueKind);
    }

    [Fact]
    public async Task ChatLinesArePublishedToTheFeed()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await Task.Delay(300); // presence lines of the logins
        using var probe = fixture.Probe();
        var chat = new Protocol.V1.Chat.ChatClient(fixture.CreateChannel());

        await chat.SendAsync(new SendRequest { Text = "secret", ToPlayerId = bob.Player.PlayerId }, Bearer(nova.SessionToken));
        await chat.SendAsync(new SendRequest { Text = "public" }, Bearer(nova.SessionToken));
        (await Post(fixture, "Mod", "from discord")).EnsureSuccessStatusCode();

        var lines = (await probe.Drain(TimeSpan.FromMilliseconds(500))).Where(e => e.Type == FeedTypes.ChatMessage).Select(FeedProbe.Data).ToList();
        Assert.DoesNotContain(lines, l => l.GetProperty("text").GetString() == "secret");
        var player = lines.Single(l => l.GetProperty("text").GetString() == "public");
        Assert.Equal("player", player.GetProperty("kind").GetString());
        Assert.Equal("Nova", player.GetProperty("from").GetProperty("name").GetString());
        var external = lines.Single(l => l.GetProperty("text").GetString() == "from discord");
        Assert.Equal("external", external.GetProperty("kind").GetString());
        Assert.Equal("Mod", external.GetProperty("author").GetString());
    }
}
