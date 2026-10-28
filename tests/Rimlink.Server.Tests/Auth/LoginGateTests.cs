using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Auth;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Auth;

public class LoginGateTests
{
    private const long SteamId = 76561198000000001;
    private const long OwnerId = 76561198000000002;

    private static ServerFixture PasswordServer() => new ServerFixture().WithSetting("Auth:AllowPassword", "true");

    private static LoginRequest SteamLogin(byte[] ticket, string? serverPassword = null) => new()
    {
        ProtocolVersion = ProtocolVersion.Current,
        SteamTicket = ByteString.CopyFrom(ticket),
        ServerPassword = serverPassword ?? "",
    };

    private static async Task<BanService> BanServiceOf(ServerFixture fixture, IServiceScope scope) =>
        await Task.FromResult(scope.ServiceProvider.GetRequiredService<BanService>());

    [Fact]
    public async Task BannedAccountCannotLogin()
    {
        using var fixture = PasswordServer();
        var reply = await fixture.RegisterAndLogin("nova");
        using (var scope = fixture.Services.CreateScope())
        {
            await (await BanServiceOf(fixture, scope)).Ban(new BanRequest(AccountId: await fixture.AccountIdOf("nova"), Reason: "spam", Duration: null, IssuedBy: 0));
        }
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() => auth.LoginAsync(PasswordLogin("nova", "pw")).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Equal(ErrorCode.Banned, ex.ErrorDetail()?.Code);
        Assert.Contains("spam", ex.ErrorDetail()?.Message);
    }

    [Fact]
    public async Task BanWhileOnlineEndsStreamAndInvalidatesToken()
    {
        using var fixture = PasswordServer();
        var reply = await fixture.RegisterAndLogin("nova");
        var channel = fixture.CreateChannel();
        var events = new Protocol.V1.Events.EventsClient(channel);
        using var stream = events.Subscribe(new Empty(), Bearer(reply.SessionToken));
        fixture.Services.GetRequiredService<Server.Application.Events.EventBus>().Broadcast(new ServerEvent { Notice = new Notice { Text = "live" } });
        Assert.True(await stream.ResponseStream.MoveNext(CancellationToken.None));

        using (var scope = fixture.Services.CreateScope())
        {
            await (await BanServiceOf(fixture, scope)).Ban(new BanRequest(AccountId: await fixture.AccountIdOf("nova"), Reason: "cheating", Duration: TimeSpan.FromDays(1), IssuedBy: 0));
        }

        var streamEx = await Assert.ThrowsAsync<RpcException>(() => DrainUntilClosed(stream));
        Assert.Equal(StatusCode.Unavailable, streamEx.StatusCode);
        var callEx = await Assert.ThrowsAsync<RpcException>(() => new Protocol.V1.Auth.AuthClient(channel).LogoutAsync(new Empty(), Bearer(reply.SessionToken)).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, callEx.StatusCode);
    }

    [Fact]
    public async Task ExpiredBanDoesNotBlockLogin()
    {
        using var fixture = PasswordServer();
        await fixture.RegisterAndLogin("nova");
        using (var scope = fixture.Services.CreateScope())
        {
            await (await BanServiceOf(fixture, scope)).Ban(new BanRequest(AccountId: await fixture.AccountIdOf("nova"), Reason: "old", Duration: TimeSpan.FromSeconds(-1), IssuedBy: 0));
        }
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var reply = await auth.LoginAsync(PasswordLogin("nova", "pw"));

        Assert.NotEmpty(reply.SessionToken);
    }

    [Fact]
    public async Task ServerFullAfterMaxPlayers()
    {
        using var fixture = PasswordServer().WithSetting("Server:MaxPlayers", "1");
        await fixture.RegisterAndLogin("first");
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());
        await auth.RegisterAsync(new RegisterRequest { Login = "second", Password = "pw", PlayerName = "Second" })
            .ResponseAsync.ContinueWith(_ => { }); // register may itself be refused as full; login below is what we test

        var ex = await Assert.ThrowsAsync<RpcException>(() => auth.LoginAsync(PasswordLogin("second", "pw")).ResponseAsync);

        Assert.Equal(ErrorCode.ServerFull, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task WrongServerPasswordRejected()
    {
        using var fixture = new ServerFixture().WithSetting("Server:ServerPassword", "hunter2");
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() => auth.LoginAsync(SteamLogin(FakeSteamVerifier.Ticket(SteamId), "wrong")).ResponseAsync);
        Assert.Equal(ErrorCode.WrongPassword, ex.ErrorDetail()?.Code);

        var ok = await auth.LoginAsync(SteamLogin(FakeSteamVerifier.Ticket(SteamId), "hunter2"));
        Assert.NotEmpty(ok.SessionToken);
    }

    [Fact]
    public async Task SecondLoginReplacesFirst()
    {
        using var fixture = PasswordServer();
        var first = await fixture.RegisterAndLogin("nova");
        var channel = fixture.CreateChannel();
        using var firstStream = new Protocol.V1.Events.EventsClient(channel).Subscribe(new Empty(), Bearer(first.SessionToken));
        fixture.Services.GetRequiredService<Server.Application.Events.EventBus>().Broadcast(new ServerEvent { Notice = new Notice { Text = "live" } });
        Assert.True(await firstStream.ResponseStream.MoveNext(CancellationToken.None));

        var second = await new Protocol.V1.Auth.AuthClient(channel).LoginAsync(PasswordLogin("nova", "pw"));

        Assert.True(await ReadUntil(firstStream, ServerEvent.BodyOneofCase.SessionReplaced));
        var ex = await Assert.ThrowsAsync<RpcException>(() => new Protocol.V1.Auth.AuthClient(channel).LogoutAsync(new Empty(), Bearer(first.SessionToken)).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        await new Protocol.V1.Auth.AuthClient(channel).LogoutAsync(new Empty(), Bearer(second.SessionToken));
    }

    /// <summary>Reads (and ignores) queued events until the server closes the stream; returns normally only on a clean end.</summary>
    private static async Task DrainUntilClosed(AsyncServerStreamingCall<ServerEvent> call)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await call.ResponseStream.MoveNext(cts.Token)) { }
    }

    private static async Task<bool> ReadUntil(AsyncServerStreamingCall<ServerEvent> call, ServerEvent.BodyOneofCase body)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await call.ResponseStream.MoveNext(cts.Token))
            if (call.ResponseStream.Current.BodyCase == body) return true;
        return false;
    }

    [Fact]
    public async Task ReplacedSessionIsNotAnnouncedAsLeaveOrSecondJoin()
    {
        using var fixture = PasswordServer();
        var alice = await fixture.RegisterAndLogin("alice");
        var channel = fixture.CreateChannel();
        using var aliceStream = new Protocol.V1.Events.EventsClient(channel).Subscribe(new Empty(), Bearer(alice.SessionToken));

        await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await fixture.RegisterAndLogin("bob"); // replaces the first session
        await Task.Delay(300); // let fire-and-forget announcements land
        fixture.Services.GetRequiredService<Server.Application.Events.EventBus>().Broadcast(new ServerEvent { Notice = new Notice { Text = "fence" } });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var seen = new List<ServerEvent>();
        while (await aliceStream.ResponseStream.MoveNext(cts.Token))
        {
            seen.Add(aliceStream.ResponseStream.Current);
            if (seen[^1].BodyCase == ServerEvent.BodyOneofCase.Notice && seen[^1].Notice.Text == "fence") break;
        }
        Assert.Equal(1, seen.Count(e => e.BodyCase == ServerEvent.BodyOneofCase.PlayerJoined && e.PlayerJoined.Player.Name == "Bob"));
        Assert.DoesNotContain(seen, e => e.BodyCase == ServerEvent.BodyOneofCase.PlayerLeft && e.PlayerLeft.Player.Name == "Bob");
    }

    [Fact]
    public async Task IpBanBlocksLogin()
    {
        using var fixture = PasswordServer();
        await fixture.RegisterAndLogin("nova");
        using (var scope = fixture.Services.CreateScope())
        {
            await (await BanServiceOf(fixture, scope)).Ban(new BanRequest(Ip: "127.0.0.1", Reason: "ip", Duration: null, IssuedBy: 0));
        }
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() => auth.LoginAsync(PasswordLogin("nova", "pw")).ResponseAsync);

        Assert.Equal(ErrorCode.Banned, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task FamilySharingOwnerBanBlocksLogin()
    {
        using var fixture = new ServerFixture();
        using (var scope = fixture.Services.CreateScope())
        {
            await (await BanServiceOf(fixture, scope)).Ban(new BanRequest(SteamId: OwnerId, Reason: "owner banned", Duration: null, IssuedBy: 0));
        }
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() => auth.LoginAsync(SteamLogin(FakeSteamVerifier.Ticket(SteamId, OwnerId))).ResponseAsync);

        Assert.Equal(ErrorCode.Banned, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task PardonLiftsBan()
    {
        using var fixture = PasswordServer();
        await fixture.RegisterAndLogin("nova");
        long accountId = await fixture.AccountIdOf("nova");
        using (var scope = fixture.Services.CreateScope())
        {
            var bans = await BanServiceOf(fixture, scope);
            var ban = await bans.Ban(new BanRequest(AccountId: accountId, Reason: "x", Duration: null, IssuedBy: 0));
            await bans.Pardon(ban.Id);
        }

        var reply = await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).LoginAsync(PasswordLogin("nova", "pw"));

        Assert.NotEmpty(reply.SessionToken);
    }
}
