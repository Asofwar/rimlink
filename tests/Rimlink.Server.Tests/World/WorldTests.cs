using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.World;

public class WorldTests
{
    private static ServerFixture Server() => new ServerFixture()
        .WithoutWorld()
        .WithSetting("Auth:AllowPassword", "true")
        .WithSetting("Auth:AdminLogins:0", "admin");

    private static CreateWorldRequest SampleWorld(params int[] npcTiles)
    {
        var request = new CreateWorldRequest { Params = new WorldParams { Seed = "Rimlink", PlanetCoverage = 0.3f, Rainfall = 1, Temperature = 1, Population = 1, ScenarioDef = "Crashlanded", StorytellerDef = "Cassandra", DifficultyDef = "Rough" } };
        request.NpcTiles.AddRange(npcTiles);
        return request;
    }

    private static async Task<(ServerFixture fixture, Protocol.V1.World.WorldClient world, LoginReply admin)> ServerWithWorld(params int[] npcTiles)
    {
        var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var world = new Protocol.V1.World.WorldClient(fixture.CreateChannel());
        await world.CreateAsync(SampleWorld(npcTiles), Bearer(admin.SessionToken));
        return (fixture, world, admin);
    }

    [Fact]
    public async Task NonAdminLoginFailsUntilWorldCreated()
    {
        using var fixture = Server();
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());
        await auth.RegisterAsync(new RegisterRequest { Login = "nova", Password = "pw", PlayerName = "Nova" }).ResponseAsync.ContinueWith(_ => { });

        var ex = await Assert.ThrowsAsync<RpcException>(() => auth.LoginAsync(PasswordLogin("nova", "pw")).ResponseAsync);

        Assert.Equal(ErrorCode.WorldNotCreated, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task AdminCreatesWorldAndOthersCanLogin()
    {
        var (fixture, world, admin) = await ServerWithWorld(42);
        using var _ = fixture;

        var nova = await fixture.RegisterAndLogin("nova");
        var info = await world.GetAsync(new Empty(), Bearer(nova.SessionToken));

        Assert.True(nova.WorldExists);
        Assert.True(info.Exists);
        Assert.Equal("Rimlink", info.Params.Seed);
        Assert.Equal([42], info.NpcTiles);
    }

    [Fact]
    public async Task NonAdminCannotCreateWorld()
    {
        var (fixture, world, _) = await ServerWithWorld();
        using var _f = fixture;
        var nova = await fixture.RegisterAndLogin("nova");

        var ex = await Assert.ThrowsAsync<RpcException>(() => world.CreateAsync(SampleWorld(), Bearer(nova.SessionToken)).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task SettleBroadcastsToOtherPlayers()
    {
        var (fixture, world, _) = await ServerWithWorld();
        using var _f = fixture;
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        using var bobEvents = new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), Bearer(bob.SessionToken));

        var settlement = await world.SettleAsync(new SettleRequest { Tile = 100, Name = "Nova Home" }, Bearer(nova.SessionToken));

        Assert.Equal(100, settlement.Tile);
        Assert.Equal(nova.Player.PlayerId, settlement.Owner.PlayerId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ServerEvent? added = null;
        while (added is null && await bobEvents.ResponseStream.MoveNext(cts.Token))
            if (bobEvents.ResponseStream.Current.BodyCase == ServerEvent.BodyOneofCase.WorldObjectAdded) added = bobEvents.ResponseStream.Current;
        Assert.Equal(settlement.Id, added!.WorldObjectAdded.Object.Id);
        var listed = await world.ListObjectsAsync(new Empty(), Bearer(bob.SessionToken));
        Assert.Single(listed.Objects, o => o.Id == settlement.Id);
        var relogin = await fixture.RegisterAndLogin("nova");
        Assert.True(relogin.HasSettlement);
    }

    [Fact]
    public async Task SettleOnNpcTileIsTileTaken()
    {
        var (fixture, world, _) = await ServerWithWorld(7);
        using var _f = fixture;
        var nova = await fixture.RegisterAndLogin("nova");

        var ex = await Assert.ThrowsAsync<RpcException>(() => world.SettleAsync(new SettleRequest { Tile = 7, Name = "Camp" }, Bearer(nova.SessionToken)).ResponseAsync);

        Assert.Equal(ErrorCode.TileTaken, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task SecondSettlementExceedsLimit()
    {
        var (fixture, world, _) = await ServerWithWorld();
        using var _f = fixture;
        var nova = await fixture.RegisterAndLogin("nova");
        await world.SettleAsync(new SettleRequest { Tile = 1, Name = "One" }, Bearer(nova.SessionToken));

        var ex = await Assert.ThrowsAsync<RpcException>(() => world.SettleAsync(new SettleRequest { Tile = 2, Name = "Two" }, Bearer(nova.SessionToken)).ResponseAsync);

        Assert.Equal(ErrorCode.SettlementLimit, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task InvalidSettlementNameIsRejected()
    {
        var (fixture, world, _) = await ServerWithWorld();
        using var _f = fixture;
        var nova = await fixture.RegisterAndLogin("nova");

        var ex = await Assert.ThrowsAsync<RpcException>(() => world.SettleAsync(new SettleRequest { Tile = 1, Name = "a\nb" }, Bearer(nova.SessionToken)).ResponseAsync);

        Assert.Equal(ErrorCode.NameInvalid, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task ConcurrentSettleOnSameTileSucceedsOnce()
    {
        var (fixture, world, _) = await ServerWithWorld();
        using var _f = fixture;
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");

        var results = await Task.WhenAll(
            Attempt(world.SettleAsync(new SettleRequest { Tile = 777, Name = "Nova" }, Bearer(nova.SessionToken)).ResponseAsync),
            Attempt(world.SettleAsync(new SettleRequest { Tile = 777, Name = "Bob" }, Bearer(bob.SessionToken)).ResponseAsync));

        Assert.Equal(1, results.Count(r => r.ok));
        Assert.Equal(ErrorCode.TileTaken, results.Single(r => !r.ok).error?.Code);
        Assert.Single((await world.ListObjectsAsync(new Empty(), Bearer(nova.SessionToken))).Objects);
    }

    [Fact]
    public async Task AbandonByStrangerIsPermissionDeniedAndOwnerCanAbandon()
    {
        var (fixture, world, _) = await ServerWithWorld();
        using var _f = fixture;
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var settlement = await world.SettleAsync(new SettleRequest { Tile = 5, Name = "Home" }, Bearer(nova.SessionToken));

        var ex = await Assert.ThrowsAsync<RpcException>(() => world.AbandonAsync(new AbandonRequest { ObjectId = settlement.Id }, Bearer(bob.SessionToken)).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);

        await world.AbandonAsync(new AbandonRequest { ObjectId = settlement.Id }, Bearer(nova.SessionToken));
        Assert.Empty((await world.ListObjectsAsync(new Empty(), Bearer(nova.SessionToken))).Objects);
    }

    private static async Task<(bool ok, ErrorDetail? error)> Attempt(Task<WorldObject> call)
    {
        try { await call; return (true, null); }
        catch (RpcException ex) { return (false, ex.ErrorDetail()); }
    }
}
