using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using Role = Rimlink.Server.Storage.Entities.Role;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.RaidFixtures;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.Visits;

public class ShopTests
{
    private sealed record Setup(ServerFixture Fixture, LoginReply Nova, LoginReply Bob, RaidClients N, RaidClients B, long VisitId);

    private static ShopItem Entry(long thingId, string def, int count, long price) => new()
    {
        ThingId = thingId, DefName = def, Count = count, Quality = -1, HitPoints = 0, Price = price, SaveTick = 10, Label = def,
    };

    /// <summary>Bob (host) has a save and sells 3 Steel at 100 each; Nova is visiting him with 1000 silver.</summary>
    private static async Task<Setup> Visiting(ServerFixture? fixture = null, int count = 3, bool visit = true)
    {
        fixture ??= EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var n = RaidClientsOf(fixture, nova.SessionToken);
        var b = RaidClientsOf(fixture, bob.SessionToken);
        byte[] save = MakeSave(ServerFixture.WorldSeed, "Bob", tick: 10);
        await Upload(b.Saves, bob.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "Bob", tick: 10));
        await b.Shop.PublishAsync(new ShopList { Items = { Entry(501, "Steel", count, 100) } }, b.Auth);
        await Fund(fixture, nova.Player.PlayerId, 1000);
        long visitId = 0;
        if (visit) visitId = (await n.Visits.BeginAsync(new VisitRequest { TargetPlayerId = bob.Player.PlayerId, GameTick = 5 }, n.Auth)).VisitId;
        return new Setup(fixture, nova, bob, n, b, visitId);
    }

    private static BuyRequest Buy(long host, long thingId, int count, string def = "Steel", string? requestId = null) => new()
    {
        ClientRequestId = requestId ?? Guid.NewGuid().ToString("N"), HostPlayerId = host, ThingId = thingId, Count = count, Blob = Item(def, count),
    };

    [Fact]
    public async Task PublishReplacesTheListAndOfShowsUnsoldOnly()
    {
        var s = await Visiting(visit: false);
        using var fixture = s.Fixture;

        await s.B.Shop.PublishAsync(new ShopList { Items = { Entry(502, "Gold", 1, 900), Entry(503, "Wood", 50, 2) } }, s.B.Auth);
        var list = await s.N.Shop.OfAsync(new PlayerRef { PlayerId = s.Bob.Player.PlayerId }, s.N.Auth);

        Assert.Equal("Bob", list.Host.Name);
        Assert.Equal([502L, 503L], list.Items.Select(i => i.ThingId).OrderBy(x => x));
    }

    [Fact]
    public async Task BuyPaysCreditsAndParcelsWithinAVisit()
    {
        var s = await Visiting(EconomyServer().WithSetting("Economy:MarketFeePercent", "10"));
        using var fixture = s.Fixture;
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var bobStream = events.Subscribe(new Empty(), s.B.Auth);

        var parcel = await s.N.Shop.BuyAsync(Buy(s.Bob.Player.PlayerId, 501, 2), s.N.Auth);
        fixture.Services.GetRequiredService<EventBus>().Broadcast(new ServerEvent { Notice = new Notice { Text = "fence" } });

        Assert.Equal(ParcelKind.ParcelPurchase, parcel.Kind);
        Assert.Equal(800, (await s.N.Wallet.GetAsync(new Empty(), s.N.Auth)).Balance);
        Assert.Equal(180, (await s.B.Wallet.GetAsync(new Empty(), s.B.Auth)).Balance);
        var mine = Assert.Single((await s.N.Mailbox.ListAsync(new Empty(), s.N.Auth)).Parcels);
        Assert.Equal(2, mine.Items.Items[0].Count);
        Assert.Equal(1, Assert.Single((await s.N.Shop.OfAsync(new PlayerRef { PlayerId = s.Bob.Player.PlayerId }, s.N.Auth)).Items).Count);
        var removal = Assert.Single((await s.B.Shop.PendingRemovalsAsync(new Empty(), s.B.Auth)).Removals);
        Assert.Equal((501L, 2), (removal.ThingId, removal.Count));
        // Both only after the commit: the wallet event first (flushed with the transaction), then the sale notice.
        Assert.Equal(180, await ReadEvent(bobStream, ServerEvent.BodyOneofCase.WalletChanged, e => e.WalletChanged.Balance));
        var sold = await ReadEvent(bobStream, ServerEvent.BodyOneofCase.ItemSold, e => e.ItemSold);
        Assert.Equal((501L, 2, 200L, "Nova"), (sold.ThingId, sold.Count, sold.Price, sold.Buyer.Name));
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Equal(20, (await db.Wallets.SingleAsync(w => w.PlayerId == 0)).Balance);
    }

    [Fact]
    public async Task BuyWithoutAVisitIsRefused()
    {
        var s = await Visiting(visit: false);
        using var fixture = s.Fixture;

        var ex = await Assert.ThrowsAsync<RpcException>(() => s.N.Shop.BuyAsync(Buy(s.Bob.Player.PlayerId, 501, 1), s.N.Auth).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Equal(1000, (await s.N.Wallet.GetAsync(new Empty(), s.N.Auth)).Balance);
    }

    [Fact]
    public async Task TwoBuyersRaceOnOneItemOnlyOnePays()
    {
        var s = await Visiting(count: 1);
        using var fixture = s.Fixture;
        var alice = await fixture.RegisterAndLogin("alice", playerName: "Alice");
        await Fund(fixture, alice.Player.PlayerId, 1000);
        var a = RaidClientsOf(fixture, alice.SessionToken);
        await a.Visits.BeginAsync(new VisitRequest { TargetPlayerId = s.Bob.Player.PlayerId, GameTick = 5 }, a.Auth);

        var results = await Task.WhenAll(
            s.N.Shop.BuyAsync(Buy(s.Bob.Player.PlayerId, 501, 1), s.N.Auth).ResponseAsync.ContinueWith(t => t.Exception?.InnerException),
            a.Shop.BuyAsync(Buy(s.Bob.Player.PlayerId, 501, 1), a.Auth).ResponseAsync.ContinueWith(t => t.Exception?.InnerException));

        Assert.Equal(1, results.Count(r => r is null));
        var refused = Assert.IsType<RpcException>(results.Single(r => r is not null));
        Assert.Equal(ErrorCode.ItemSold, refused.ErrorDetail()!.Code);
        long nova = (await s.N.Wallet.GetAsync(new Empty(), s.N.Auth)).Balance, al = (await a.Wallet.GetAsync(new Empty(), a.Auth)).Balance;
        Assert.Equal(1900, nova + al);
        Assert.Equal(100, (await s.B.Wallet.GetAsync(new Empty(), s.B.Auth)).Balance);
        Assert.Single((await s.B.Shop.PendingRemovalsAsync(new Empty(), s.B.Auth)).Removals);
        Assert.Empty((await s.N.Shop.OfAsync(new PlayerRef { PlayerId = s.Bob.Player.PlayerId }, s.N.Auth)).Items);
    }

    [Fact]
    public async Task BuyWithAMismatchedBlobIsInvalidArgument()
    {
        var s = await Visiting();
        using var fixture = s.Fixture;

        var ex = await Assert.ThrowsAsync<RpcException>(() => s.N.Shop.BuyAsync(Buy(s.Bob.Player.PlayerId, 501, 1, def: "Gold"), s.N.Auth).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        var tooMany = await Assert.ThrowsAsync<RpcException>(() => s.N.Shop.BuyAsync(Buy(s.Bob.Player.PlayerId, 501, 4), s.N.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.ItemSold, tooMany.ErrorDetail()!.Code);
    }

    [Fact]
    public async Task SameClientRequestIdReturnsTheSameParcel()
    {
        var s = await Visiting();
        using var fixture = s.Fixture;

        var first = await s.N.Shop.BuyAsync(Buy(s.Bob.Player.PlayerId, 501, 1, requestId: "b1"), s.N.Auth);
        var second = await s.N.Shop.BuyAsync(Buy(s.Bob.Player.PlayerId, 501, 1, requestId: "b1"), s.N.Auth);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(900, (await s.N.Wallet.GetAsync(new Empty(), s.N.Auth)).Balance);
    }

    [Fact]
    public async Task AckRemovalStampsTheHost()
    {
        var s = await Visiting();
        using var fixture = s.Fixture;
        await s.N.Shop.BuyAsync(Buy(s.Bob.Player.PlayerId, 501, 3), s.N.Auth);
        var removal = Assert.Single((await s.B.Shop.PendingRemovalsAsync(new Empty(), s.B.Auth)).Removals);

        await s.B.Shop.AckRemovalAsync(new RemovalAck { Ids = { removal.Id }, GameTick = 900 }, s.B.Auth);

        Assert.Empty((await s.B.Shop.PendingRemovalsAsync(new Empty(), s.B.Auth)).Removals);
        using (var scope = fixture.Services.CreateScope())
            Assert.Equal(900, (await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Players.SingleAsync(p => p.Id == s.Bob.Player.PlayerId)).LastOutgoingTick);
        // A publish from an older save must not bring the sold thing back.
        await s.B.Shop.PublishAsync(new ShopList { Items = { new ShopItem { ThingId = 501, DefName = "Steel", Count = 3, Quality = -1, Price = 100, SaveTick = 800, Label = "Steel" } } }, s.B.Auth);
        Assert.Empty((await s.N.Shop.OfAsync(new PlayerRef { PlayerId = s.Bob.Player.PlayerId }, s.N.Auth)).Items);
    }

    [Fact]
    public async Task ResetClearsShopVisitsAndRemovals()
    {
        var s = await Visiting(EconomyServer().WithSetting("Auth:AdminLogins:0", "admin"));
        using var fixture = s.Fixture;
        var admin = await fixture.RegisterAndLogin("admin");
        var actor = new Actor(await fixture.AccountIdOf("admin"), admin.Player.PlayerId, Role.Admin, "admin");
        await s.N.Shop.BuyAsync(Buy(s.Bob.Player.PlayerId, 501, 1), s.N.Auth);

        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(actor, s.Bob.Player.PlayerId);

        Assert.Empty((await s.N.Shop.OfAsync(new PlayerRef { PlayerId = s.Bob.Player.PlayerId }, s.N.Auth)).Items);
        var again = await fixture.RegisterAndLogin("bob");
        var b = RaidClientsOf(fixture, again.SessionToken);
        Assert.Empty((await b.Shop.PendingRemovalsAsync(new Empty(), b.Auth)).Removals);
        var ex = await Assert.ThrowsAsync<RpcException>(() => VisitTests.DownloadOf(s.N, s.Bob.Player.PlayerId));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }
}
