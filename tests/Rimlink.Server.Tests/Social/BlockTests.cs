using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Chat;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Social;

public class BlockTests
{
    private static async Task<List<ChatMessageEvent>> ReadChat(AsyncServerStreamingCall<ServerEvent> call, int count, TimeSpan timeout, Func<ChatMessageEvent, bool>? where = null)
    {
        using var cts = new CancellationTokenSource(timeout);
        var result = new List<ChatMessageEvent>();
        try
        {
            while (result.Count < count && await call.ResponseStream.MoveNext(cts.Token))
            {
                var ev = call.ResponseStream.Current;
                if (ev.BodyCase == ServerEvent.BodyOneofCase.Chat && (where is null || where(ev.Chat))) result.Add(ev.Chat);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
        }
        catch (OperationCanceledException)
        {
        }
        return result;
    }

    private static async Task Block(ServerFixture fixture, LoginReply who, long target) =>
        await SocialFixtures.Social(fixture, who.SessionToken).Social.BlockAsync(new PlayerRef { PlayerId = target }, Bearer(who.SessionToken));

    [Fact]
    public async Task BlockedPlayersDirectMessageIsBlocked()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var chat = new Protocol.V1.Chat.ChatClient(fixture.CreateChannel());
        await Block(fixture, nova, bob.Player.PlayerId);

        var fromBob = await Assert.ThrowsAsync<RpcException>(() => chat.SendAsync(new SendRequest { Text = "hi", ToPlayerId = nova.Player.PlayerId }, Bearer(bob.SessionToken)).ResponseAsync);
        var fromNova = await Assert.ThrowsAsync<RpcException>(() => chat.SendAsync(new SendRequest { Text = "hi", ToPlayerId = bob.Player.PlayerId }, Bearer(nova.SessionToken)).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, fromBob.StatusCode);
        Assert.Equal(ErrorCode.Blocked, fromBob.ErrorDetail()?.Code);
        Assert.Equal("the player does not accept this from you", fromBob.Status.Detail);
        Assert.Equal(ErrorCode.Blocked, fromNova.ErrorDetail()?.Code);
        Assert.Equal("you blocked this player", fromNova.Status.Detail);
        using var scope = fixture.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().ChatMessages.CountAsync(m => m.Kind == Rimlink.Server.Storage.Entities.MessageKind.Player));
    }

    [Fact]
    public async Task BlockedPlayersGiftIsRefusedBeforeAnythingMoves()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await Fund(fixture, bob.Player.PlayerId, 100);
        await Block(fixture, nova, bob.Player.PlayerId);
        var c = Clients(fixture, bob.SessionToken);

        var ex = await Assert.ThrowsAsync<RpcException>(() => c.Transfer.SendAsync(Send(nova.Player.PlayerId, Items(("Steel", 10)), silver: 50, tick: 700), c.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.Blocked, ex.ErrorDetail()?.Code);
        Assert.Equal(100, (await c.Wallet.GetAsync(new Empty(), c.Auth)).Balance);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Equal(0, await db.Parcels.CountAsync());
        Assert.Equal(0, (await db.Players.SingleAsync(p => p.Id == bob.Player.PlayerId)).LastOutgoingTick);
    }

    [Fact]
    public async Task BlockedPlayersPublicLinesSkipTheBlocker()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var carol = await fixture.RegisterAndLogin("carol", playerName: "Carol");
        await Block(fixture, nova, bob.Player.PlayerId);
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var novaStream = events.Subscribe(new Empty(), Bearer(nova.SessionToken));
        using var carolStream = events.Subscribe(new Empty(), Bearer(carol.SessionToken));
        var chat = new Protocol.V1.Chat.ChatClient(fixture.CreateChannel());

        await chat.SendAsync(new SendRequest { Text = "from bob" }, Bearer(bob.SessionToken));
        await chat.SendAsync(new SendRequest { Text = "from carol" }, Bearer(carol.SessionToken));
        using (var scope = fixture.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<ChatService>().System("server line");

        var carolGot = await ReadChat(carolStream, 3, TimeSpan.FromSeconds(10), m => !m.Text.EndsWith("joined the server"));
        var novaGot = await ReadChat(novaStream, 2, TimeSpan.FromSeconds(10), m => !m.Text.EndsWith("joined the server"));
        Assert.Equal(["from bob", "from carol", "server line"], carolGot.Select(m => m.Text));
        Assert.Equal(["from carol", "server line"], novaGot.Select(m => m.Text));

        var novaHistory = await chat.HistoryAsync(new Empty(), Bearer(nova.SessionToken));
        var carolHistory = await chat.HistoryAsync(new Empty(), Bearer(carol.SessionToken));
        Assert.DoesNotContain(novaHistory.Messages, m => m.Text == "from bob");
        Assert.Contains(novaHistory.Messages, m => m.Text == "from carol");
        Assert.Contains(carolHistory.Messages, m => m.Text == "from bob");
    }

    [Fact]
    public async Task ModeratorsAndYourselfCannotBeBlocked()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var mod = await fixture.RegisterAndLogin("mod", playerName: "Mod");
        await SocialFixtures.SetRole(fixture, mod.Player.PlayerId, Rimlink.Server.Storage.Entities.Role.Moderator);

        var self = await Assert.ThrowsAsync<RpcException>(() => Block(fixture, nova, nova.Player.PlayerId));
        var moderator = await Assert.ThrowsAsync<RpcException>(() => Block(fixture, nova, mod.Player.PlayerId));
        var unknown = await Assert.ThrowsAsync<RpcException>(() => Block(fixture, nova, 9999));

        Assert.Equal(StatusCode.InvalidArgument, self.StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, moderator.StatusCode);
        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task BlockListIsCappedAndIdempotent()
    {
        using var fixture = EconomyServer().WithSetting("Social:MaxBlocks", "1");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var carol = await fixture.RegisterAndLogin("carol", playerName: "Carol");
        var social = SocialFixtures.Social(fixture, nova.SessionToken).Social;
        var auth = Bearer(nova.SessionToken);

        await social.BlockAsync(new PlayerRef { PlayerId = bob.Player.PlayerId }, auth);
        var twice = await social.BlockAsync(new PlayerRef { PlayerId = bob.Player.PlayerId }, auth);
        var full = await Assert.ThrowsAsync<RpcException>(() => social.BlockAsync(new PlayerRef { PlayerId = carol.Player.PlayerId }, auth).ResponseAsync);
        var after = await social.UnblockAsync(new PlayerRef { PlayerId = bob.Player.PlayerId }, auth);

        Assert.Equal("Bob", Assert.Single(twice.Players).Name);
        Assert.Equal(StatusCode.InvalidArgument, full.StatusCode);
        Assert.Contains("block list is full (1)", full.Status.Detail);
        Assert.Empty(after.Players);
        Assert.Empty((await social.ListBlocksAsync(new Empty(), auth)).Players);
    }
}
