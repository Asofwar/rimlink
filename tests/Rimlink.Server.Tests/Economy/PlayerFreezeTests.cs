using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.RaidFixtures;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.Economy;

public class PlayerFreezeTests
{
    private static PostRequest Post(ItemList items, long price) => new() { ClientRequestId = Guid.NewGuid().ToString("N"), Items = items, Price = price, GameTick = 100 };

    private static ShopItem Entry(long thingId, string def, int count, long price) => new()
    {
        ThingId = thingId, DefName = def, Count = count, Quality = -1, HitPoints = 0, Price = price, SaveTick = 10, Label = def,
    };

    private static BuyRequest ShopBuy(long host, long thingId) => new()
    {
        ClientRequestId = Guid.NewGuid().ToString("N"), HostPlayerId = host, ThingId = thingId, Count = 1, Blob = Item("Steel", 1),
    };

    private static async Task<bool> Freeze(ServerFixture fixture, long playerId, bool frozen, string? reason = "test")
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlayerFreeze>().Set(playerId, frozen, reason);
    }

    private static async Task AssertFrozen(Func<Task> call, string reasonPart = "test")
    {
        var ex = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal(ErrorCode.PlayerFrozen, ex.ErrorDetail()?.Code);
        Assert.Contains(reasonPart, ex.Status.Detail);
    }

    /// <summary>Nova and Kai with 1000 silver each; Kai has a save, a lot and a shop with Steel.</summary>
    private static async Task<(LoginReply Nova, LoginReply Kai, EconomyClients N, EconomyClients K, Lot KaiLot)> Players(ServerFixture fixture)
    {
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var kai = await fixture.RegisterAndLogin("kai", playerName: "Kai");
        await Fund(fixture, nova.Player.PlayerId, 1000);
        await Fund(fixture, kai.Player.PlayerId, 1000);
        var n = Clients(fixture, nova.SessionToken);
        var k = Clients(fixture, kai.SessionToken);
        var kr = RaidClientsOf(fixture, kai.SessionToken);
        byte[] save = MakeSave(ServerFixture.WorldSeed, "Kai", tick: 10);
        await Upload(kr.Saves, kai.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "Kai", tick: 10));
        await kr.Shop.PublishAsync(new ShopList { Items = { Entry(501, "Steel", 3, 100) } }, kr.Auth);
        var lot = await k.Market.PostAsync(Post(Items(("Steel", 5)), 200), k.Auth);
        return (nova, kai, n, k, lot);
    }

    [Fact]
    public async Task FrozenPlayerCannotSendDepositWithdrawPostBuyOrShop()
    {
        using var fixture = EconomyServer();
        var (nova, kai, n, _, kaiLot) = await Players(fixture);
        var nr = RaidClientsOf(fixture, nova.SessionToken);
        await nr.Visits.BeginAsync(new VisitRequest { TargetPlayerId = kai.Player.PlayerId, GameTick = 5 }, nr.Auth);

        Assert.True(await Freeze(fixture, nova.Player.PlayerId, true));

        await AssertFrozen(() => n.Transfer.SendAsync(Send(kai.Player.PlayerId, silver: 10), n.Auth).ResponseAsync);
        await AssertFrozen(() => n.Wallet.DepositAsync(new DepositRequest { ClientRequestId = "d", Silver = 10, GameTick = 100 }, n.Auth).ResponseAsync);
        await AssertFrozen(() => n.Wallet.WithdrawAsync(new WithdrawRequest { ClientRequestId = "w", Silver = 10 }, n.Auth).ResponseAsync);
        await AssertFrozen(() => n.Market.PostAsync(Post(Items(("Gold", 1)), 50), n.Auth).ResponseAsync);
        await AssertFrozen(() => n.Market.BuyAsync(new LotRef { LotId = kaiLot.Id }, n.Auth).ResponseAsync);
        await AssertFrozen(() => nr.Shop.BuyAsync(ShopBuy(kai.Player.PlayerId, 501), nr.Auth).ResponseAsync);
    }

    [Fact]
    public async Task FrozenPlayerCannotLaunchARaid()
    {
        using var fixture = RaidServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var kai = await fixture.RegisterAndLogin("kai", playerName: "Kai");
        await MakeTargetRaidable(fixture, kai.Player.PlayerId);
        var nr = RaidClientsOf(fixture, nova.SessionToken);
        await Freeze(fixture, nova.Player.PlayerId, true);

        await AssertFrozen(() => nr.Raids.LaunchAsync(Launch(kai.Player.PlayerId, tick: 500), nr.Auth).ResponseAsync);
    }

    [Fact]
    public async Task FrozenSellersLotsAreHiddenAndNotForSale()
    {
        using var fixture = EconomyServer();
        var (nova, kai, n, k, kaiLot) = await Players(fixture);
        await Freeze(fixture, kai.Player.PlayerId, true);

        var seen = await n.Market.ListAsync(new MarketQuery(), n.Auth);
        var own = await k.Market.ListAsync(new MarketQuery { SellerPlayerId = kai.Player.PlayerId }, k.Auth);

        Assert.DoesNotContain(seen.Lots, l => l.Id == kaiLot.Id);
        Assert.Contains(own.Lots, l => l.Id == kaiLot.Id);
        await AssertFrozen(() => n.Market.BuyAsync(new LotRef { LotId = kaiLot.Id }, n.Auth).ResponseAsync, "seller");
    }

    [Fact]
    public async Task FrozenHostsShopDoesNotSell()
    {
        using var fixture = EconomyServer();
        var (nova, kai, _, _, _) = await Players(fixture);
        var nr = RaidClientsOf(fixture, nova.SessionToken);
        await nr.Visits.BeginAsync(new VisitRequest { TargetPlayerId = kai.Player.PlayerId, GameTick = 5 }, nr.Auth);
        await Freeze(fixture, kai.Player.PlayerId, true);

        await AssertFrozen(() => nr.Shop.BuyAsync(ShopBuy(kai.Player.PlayerId, 501), nr.Auth).ResponseAsync, "seller");
    }

    [Fact]
    public async Task FrozenPlayerStillReceivesAndWithdrawsOwnLot()
    {
        using var fixture = EconomyServer();
        var (nova, kai, n, k, kaiLot) = await Players(fixture);
        await Freeze(fixture, kai.Player.PlayerId, true);

        await n.Transfer.SendAsync(Send(kai.Player.PlayerId, silver: 10), n.Auth);
        var returned = await k.Market.WithdrawAsync(new LotRef { LotId = kaiLot.Id }, k.Auth);

        var mailbox = await k.Mailbox.ListAsync(new Empty(), k.Auth);
        Assert.Contains(mailbox.Parcels, p => p.Silver == 10);
        Assert.Contains(mailbox.Parcels, p => p.Id == returned.Id);
    }

    [Fact]
    public async Task ReplaysOfRequestsAppliedBeforeTheFreezeReturnTheirResult()
    {
        using var fixture = EconomyServer();
        var (nova, kai, n, _, _) = await Players(fixture);
        var sent = await n.Transfer.SendAsync(Send(kai.Player.PlayerId, silver: 10, requestId: "send-1"), n.Auth);
        var withdrawn = await n.Wallet.WithdrawAsync(new WithdrawRequest { ClientRequestId = "wd-1", Silver = 10 }, n.Auth);
        var lot = await n.Market.PostAsync(new PostRequest { ClientRequestId = "post-1", Items = Items(("Gold", 1)), Price = 50, GameTick = 100 }, n.Auth);
        await n.Wallet.DepositAsync(new DepositRequest { ClientRequestId = "dep-1", Silver = 5, GameTick = 100 }, n.Auth);
        await Freeze(fixture, nova.Player.PlayerId, true);

        // An outbox replays a request whose answer was lost: it was applied, so a refusal now would make the game put the things back.
        Assert.Equal(sent.Id, (await n.Transfer.SendAsync(Send(kai.Player.PlayerId, silver: 10, requestId: "send-1"), n.Auth)).Id);
        Assert.Equal(withdrawn.Id, (await n.Wallet.WithdrawAsync(new WithdrawRequest { ClientRequestId = "wd-1", Silver = 10 }, n.Auth)).Id);
        Assert.Equal(lot.Id, (await n.Market.PostAsync(new PostRequest { ClientRequestId = "post-1", Items = Items(("Gold", 1)), Price = 50, GameTick = 100 }, n.Auth)).Id);
        await n.Wallet.DepositAsync(new DepositRequest { ClientRequestId = "dep-1", Silver = 5, GameTick = 100 }, n.Auth);
    }

    [Fact]
    public async Task GlobalFreezeIsReportedFirst()
    {
        using var fixture = EconomyServer();
        var (nova, kai, n, _, _) = await Players(fixture);
        await Freeze(fixture, nova.Player.PlayerId, true);
        fixture.Services.GetRequiredService<FreezeState>().Frozen = true;

        var ex = await Assert.ThrowsAsync<RpcException>(() => n.Transfer.SendAsync(Send(kai.Player.PlayerId, silver: 10), n.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.EconomyFrozen, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task UnfreezeOpensTradingAgainAndNotifiesOnce()
    {
        using var fixture = EconomyServer();
        var (nova, kai, n, _, _) = await Players(fixture);
        using var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), Bearer(nova.SessionToken));

        Assert.True(await Freeze(fixture, nova.Player.PlayerId, true, "dupe"));
        Assert.False(await Freeze(fixture, nova.Player.PlayerId, true, "dupe"));
        Assert.True(await Freeze(fixture, nova.Player.PlayerId, false, null));
        await n.Transfer.SendAsync(Send(kai.Player.PlayerId, silver: 10), n.Auth);

        string first = await ReadEvent(events, ServerEvent.BodyOneofCase.Notice, e => e.Notice.Text);
        string second = await ReadEvent(events, ServerEvent.BodyOneofCase.Notice, e => e.Notice.Text);
        Assert.Equal("Your trading is frozen: dupe", first);
        Assert.Equal("Your trading is open again", second);
        await Assert.ThrowsAnyAsync<Exception>(() => ReadEvent(events, ServerEvent.BodyOneofCase.Notice, e => e.Notice.Text, TimeSpan.FromSeconds(1)));
    }
}
