using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Chat;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Chat;

public class ChatTests
{
    private static ServerFixture Server() => new ServerFixture().WithSetting("Auth:AllowPassword", "true");

    private static async Task<List<ChatMessageEvent>> ReadChat(AsyncServerStreamingCall<ServerEvent> call, int count, Func<ChatMessageEvent, bool>? where = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = new List<ChatMessageEvent>();
        while (result.Count < count && await call.ResponseStream.MoveNext(cts.Token))
        {
            var ev = call.ResponseStream.Current;
            if (ev.BodyCase == ServerEvent.BodyOneofCase.Chat && (where is null || where(ev.Chat))) result.Add(ev.Chat);
        }
        return result;
    }

    [Fact]
    public async Task MessageReachesEveryoneIncludingSender()
    {
        using var fixture = Server();
        var alice = await fixture.RegisterAndLogin("alice");
        var bob = await fixture.RegisterAndLogin("bob");
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var aliceStream = events.Subscribe(new Empty(), Bearer(alice.SessionToken));
        using var bobStream = events.Subscribe(new Empty(), Bearer(bob.SessionToken));
        var chat = new Protocol.V1.Chat.ChatClient(fixture.CreateChannel());

        await chat.SendAsync(new SendRequest { Text = "  hello all  " }, Bearer(alice.SessionToken));

        var gotBob = await ReadChat(bobStream, 1, m => m.Kind == MessageKind.Player);
        var gotAlice = await ReadChat(aliceStream, 1, m => m.Kind == MessageKind.Player);
        Assert.Equal("hello all", gotBob[0].Text);
        Assert.Equal(alice.Player.PlayerId, gotBob[0].From.PlayerId);
        Assert.Equal(0, gotBob[0].ToPlayerId);
        Assert.Equal(gotBob[0].Id, gotAlice[0].Id);
    }

    [Fact]
    public async Task DirectMessageReachesOnlyRecipientAndSender()
    {
        using var fixture = Server();
        var alice = await fixture.RegisterAndLogin("alice");
        var bob = await fixture.RegisterAndLogin("bob");
        var carol = await fixture.RegisterAndLogin("carol");
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var bobStream = events.Subscribe(new Empty(), Bearer(bob.SessionToken));
        using var carolStream = events.Subscribe(new Empty(), Bearer(carol.SessionToken));
        var chat = new Protocol.V1.Chat.ChatClient(fixture.CreateChannel());

        await chat.SendAsync(new SendRequest { Text = "psst", ToPlayerId = bob.Player.PlayerId }, Bearer(alice.SessionToken));
        await chat.SendAsync(new SendRequest { Text = "public" }, Bearer(alice.SessionToken));

        Assert.Equal(["psst", "public"], (await ReadChat(bobStream, 2, m => m.Kind == MessageKind.Player)).Select(m => m.Text));
        Assert.Equal(["public"], (await ReadChat(carolStream, 1, m => m.Kind == MessageKind.Player)).Select(m => m.Text));
    }

    [Fact]
    public async Task HistoryReturnsFiftyLatestPublicPlusOwnDirect()
    {
        using var fixture = Server();
        var alice = await fixture.RegisterAndLogin("alice");
        var bob = await fixture.RegisterAndLogin("bob");
        var chat = new Protocol.V1.Chat.ChatClient(fixture.CreateChannel());
        for (int i = 1; i <= 60; i++) await chat.SendAsync(new SendRequest { Text = $"m{i}" }, Bearer(alice.SessionToken));
        await chat.SendAsync(new SendRequest { Text = "dm", ToPlayerId = bob.Player.PlayerId }, Bearer(alice.SessionToken));

        var bobHistory = (await chat.HistoryAsync(new Empty(), Bearer(bob.SessionToken))).Messages;
        var publicOnly = bobHistory.Where(m => m.Kind == MessageKind.Player && m.ToPlayerId == 0).ToList();

        Assert.Equal(50, publicOnly.Count);
        Assert.Equal("m11", publicOnly[0].Text);
        Assert.Equal("m60", publicOnly[^1].Text);
        Assert.Contains(bobHistory, m => m.Text == "dm");
        var carol = await fixture.RegisterAndLogin("carol");
        Assert.DoesNotContain((await chat.HistoryAsync(new Empty(), Bearer(carol.SessionToken))).Messages, m => m.Text == "dm");
    }

    [Fact]
    public async Task TooLongOrEmptyMessageIsInvalidArgument()
    {
        using var fixture = Server();
        var alice = await fixture.RegisterAndLogin("alice");
        var chat = new Protocol.V1.Chat.ChatClient(fixture.CreateChannel());

        var tooLong = await Assert.ThrowsAsync<RpcException>(() => chat.SendAsync(new SendRequest { Text = new string('a', 501) }, Bearer(alice.SessionToken)).ResponseAsync);
        var empty = await Assert.ThrowsAsync<RpcException>(() => chat.SendAsync(new SendRequest { Text = "   " }, Bearer(alice.SessionToken)).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, tooLong.StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, empty.StatusCode);
    }

    [Fact]
    public async Task MutedPlayerGetsMuted()
    {
        using var fixture = Server();
        var alice = await fixture.RegisterAndLogin("alice");
        var chat = new Protocol.V1.Chat.ChatClient(fixture.CreateChannel());
        using (var scope = fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ChatService>().Mute(alice.Player.PlayerId, TimeSpan.FromMinutes(10));
        }

        var ex = await Assert.ThrowsAsync<RpcException>(() => chat.SendAsync(new SendRequest { Text = "hi" }, Bearer(alice.SessionToken)).ResponseAsync);

        Assert.Equal(ErrorCode.Muted, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task SixthMessageInFiveSecondsIsResourceExhausted()
    {
        using var fixture = Server().WithRateLimits();
        var alice = await fixture.RegisterAndLogin("alice");
        var chat = new Protocol.V1.Chat.ChatClient(fixture.CreateChannel());

        for (int i = 0; i < 5; i++) await chat.SendAsync(new SendRequest { Text = $"m{i}" }, Bearer(alice.SessionToken));
        var ex = await Assert.ThrowsAsync<RpcException>(() => chat.SendAsync(new SendRequest { Text = "m5" }, Bearer(alice.SessionToken)).ResponseAsync);

        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
    }

    [Fact]
    public async Task JoinAndLeaveProduceSystemMessagesAndPlayerEvents()
    {
        using var fixture = Server();
        var alice = await fixture.RegisterAndLogin("alice");
        using var stream = new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), Bearer(alice.SessionToken));

        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).LogoutAsync(new Empty(), Bearer(bob.SessionToken));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var seen = new List<ServerEvent>();
        while (seen.Count(e => e.BodyCase is ServerEvent.BodyOneofCase.PlayerJoined or ServerEvent.BodyOneofCase.PlayerLeft) < 2 && await stream.ResponseStream.MoveNext(cts.Token))
            seen.Add(stream.ResponseStream.Current);
        Assert.Contains(seen, e => e.BodyCase == ServerEvent.BodyOneofCase.PlayerJoined && e.PlayerJoined.Player.Name == "Bob");
        Assert.Contains(seen, e => e.BodyCase == ServerEvent.BodyOneofCase.PlayerLeft && e.PlayerLeft.Player.Name == "Bob");
        Assert.Contains(seen, e => e.BodyCase == ServerEvent.BodyOneofCase.Chat && e.Chat.Kind == MessageKind.System && e.Chat.Text.Contains("Bob"));
    }
}
