using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Chat;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Chat;

/// <summary>Live UX spec §2: a ping goes to one online player and is not kept anywhere.</summary>
public class PingTests
{
    private static ServerFixture Server() => new ServerFixture().WithSetting("Auth:AllowPassword", "true");

    private static async Task<List<PingEvent>> ReadPings(AsyncServerStreamingCall<ServerEvent> call, int count, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var result = new List<PingEvent>();
        try
        {
            while (result.Count < count && await call.ResponseStream.MoveNext(cts.Token))
                if (call.ResponseStream.Current.BodyCase == ServerEvent.BodyOneofCase.Ping) result.Add(call.ResponseStream.Current.Ping);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }
        return result;
    }

    private static Protocol.V1.Chat.ChatClient ChatOf(ServerFixture fixture) => new(fixture.CreateChannel());

    private static Task Ping(ServerFixture fixture, LoginReply from, long to, int tile, MapCell? cell = null) =>
        ChatOf(fixture).PingAsync(new PingRequest { ToPlayerId = to, Tile = tile, Cell = cell }, Bearer(from.SessionToken)).ResponseAsync;

    [Fact]
    public async Task PingReachesOnlyTheRecipient()
    {
        using var fixture = Server();
        var alice = await fixture.RegisterAndLogin("alice");
        var bob = await fixture.RegisterAndLogin("bob");
        var carol = await fixture.RegisterAndLogin("carol");
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var bobStream = events.Subscribe(new Empty(), Bearer(bob.SessionToken));
        using var carolStream = events.Subscribe(new Empty(), Bearer(carol.SessionToken));

        await Ping(fixture, alice, bob.Player.PlayerId, 85538, new MapCell { X = 12, Z = 34 });

        var ping = Assert.Single(await ReadPings(bobStream, 1, TimeSpan.FromSeconds(10)));
        Assert.Equal(alice.Player.PlayerId, ping.From.PlayerId);
        Assert.Equal((85538, 12, 34), (ping.Tile, ping.Cell.X, ping.Cell.Z));
        Assert.Empty(await ReadPings(carolStream, 1, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task PingWithoutCellIsATilePing()
    {
        using var fixture = Server();
        var alice = await fixture.RegisterAndLogin("alice");
        var bob = await fixture.RegisterAndLogin("bob");
        using var bobStream = new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), Bearer(bob.SessionToken));

        await Ping(fixture, alice, bob.Player.PlayerId, 7);

        var ping = Assert.Single(await ReadPings(bobStream, 1, TimeSpan.FromSeconds(10)));
        Assert.Null(ping.Cell);
        Assert.Equal(7, ping.Tile);
    }

    [Fact]
    public async Task PingIsNotKeptInHistory()
    {
        using var fixture = Server();
        var alice = await fixture.RegisterAndLogin("alice");
        var bob = await fixture.RegisterAndLogin("bob");

        await Ping(fixture, alice, bob.Player.PlayerId, 7);

        // Only the server's own "joined" lines are there.
        Assert.All((await ChatOf(fixture).HistoryAsync(new Empty(), Bearer(alice.SessionToken))).Messages, m => Assert.Equal(MessageKind.System, m.Kind));
        Assert.All((await ChatOf(fixture).HistoryAsync(new Empty(), Bearer(bob.SessionToken))).Messages, m => Assert.Equal(MessageKind.System, m.Kind));
    }

    [Fact]
    public async Task MutedPlayerCannotPing()
    {
        using var fixture = Server();
        var alice = await fixture.RegisterAndLogin("alice");
        var bob = await fixture.RegisterAndLogin("bob");
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ChatService>().Mute(alice.Player.PlayerId, TimeSpan.FromMinutes(10));

        var ex = await Assert.ThrowsAsync<RpcException>(() => Ping(fixture, alice, bob.Player.PlayerId, 7));

        Assert.Equal(ErrorCode.Muted, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task PingToAnOfflinePlayerIsRefused()
    {
        using var fixture = Server();
        var alice = await fixture.RegisterAndLogin("alice");
        var bob = await fixture.RegisterAndLogin("bob");
        await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).LogoutAsync(new Empty(), Bearer(bob.SessionToken));

        var ex = await Assert.ThrowsAsync<RpcException>(() => Ping(fixture, alice, bob.Player.PlayerId, 7));

        Assert.Equal(ErrorCode.RecipientUnknown, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task PingToOneselfIsRefused()
    {
        using var fixture = Server();
        var alice = await fixture.RegisterAndLogin("alice");

        var ex = await Assert.ThrowsAsync<RpcException>(() => Ping(fixture, alice, alice.Player.PlayerId, 7));

        Assert.Equal(ErrorCode.RecipientUnknown, ex.ErrorDetail()?.Code);
    }

    [Theory]
    [InlineData(-1, -1, -1)]
    [InlineData(10_000_000, -1, -1)]
    [InlineData(7, 1000, 0)]
    [InlineData(7, 0, -1)]
    public async Task PingOutOfRangeIsInvalid(int tile, int x, int z)
    {
        using var fixture = Server();
        var alice = await fixture.RegisterAndLogin("alice");
        var bob = await fixture.RegisterAndLogin("bob");
        var cell = x < 0 && z < 0 ? null : new MapCell { X = x, Z = z };

        var ex = await Assert.ThrowsAsync<RpcException>(() => Ping(fixture, alice, bob.Player.PlayerId, tile, cell));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task PingsAreRateLimited()
    {
        using var fixture = Server().WithRateLimits();
        var alice = await fixture.RegisterAndLogin("alice");
        var bob = await fixture.RegisterAndLogin("bob");

        for (int i = 0; i < 12; i++) await Ping(fixture, alice, bob.Player.PlayerId, 7);
        var ex = await Assert.ThrowsAsync<RpcException>(() => Ping(fixture, alice, bob.Player.PlayerId, 7));

        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
    }
}
