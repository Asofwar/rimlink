using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Role = Rimlink.Server.Storage.Entities.Role;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Economy;

public class MarketTests
{
    private static PostRequest Post(ItemList items, long price, long tick = 100) => new()
    {
        ClientRequestId = Guid.NewGuid().ToString("N"),
        Items = items,
        Price = price,
        GameTick = tick,
    };

    [Fact]
    public async Task BuyMovesSilverClosesTheLotAndMailsTheItems()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await Fund(fixture, bob.Player.PlayerId, 1000);
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var novaStream = events.Subscribe(new Empty(), n.Auth);

        var lot = await n.Market.PostAsync(Post(Items(("ComponentIndustrial", 5)), 300), n.Auth);
        var parcel = await b.Market.BuyAsync(new LotRef { LotId = lot.Id }, b.Auth);

        Assert.Equal(700, (await b.Wallet.GetAsync(new Empty(), b.Auth)).Balance);
        Assert.Equal(300, (await n.Wallet.GetAsync(new Empty(), n.Auth)).Balance);
        Assert.Equal(ParcelKind.ParcelPurchase, parcel.Kind);
        Assert.Equal(5, parcel.Items.Items[0].Count);
        Assert.Empty((await b.Market.ListAsync(new MarketQuery(), b.Auth)).Lots);
        var sold = await ReadEvent(novaStream, ServerEvent.BodyOneofCase.LotSold, e => e.LotSold);
        Assert.Equal(lot.Id, sold.LotId);
        Assert.Equal("Bob", sold.Buyer.Name);
        Assert.Equal(300, sold.Proceeds);
        var bobBox = await b.Mailbox.ListAsync(new Empty(), b.Auth);
        Assert.Equal(parcel.Id, Assert.Single(bobBox.Parcels).Id);
    }

    [Fact]
    public async Task TwoBuyersRaceOnlyOnePays()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var eve = await fixture.RegisterAndLogin("eve");
        await Fund(fixture, bob.Player.PlayerId, 1000);
        await Fund(fixture, eve.Player.PlayerId, 1000);
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);
        var e = Clients(fixture, eve.SessionToken);
        var lot = await n.Market.PostAsync(Post(Items(("Gold", 10)), 500), n.Auth);

        var results = await Task.WhenAll(
            Try(() => b.Market.BuyAsync(new LotRef { LotId = lot.Id }, b.Auth).ResponseAsync),
            Try(() => e.Market.BuyAsync(new LotRef { LotId = lot.Id }, e.Auth).ResponseAsync));

        Assert.Equal(1, results.Count(r => r.ok));
        Assert.Equal(ErrorCode.LotGone, results.Single(r => !r.ok).error!.ErrorDetail()?.Code);
        long bobBalance = (await b.Wallet.GetAsync(new Empty(), b.Auth)).Balance;
        long eveBalance = (await e.Wallet.GetAsync(new Empty(), e.Auth)).Balance;
        Assert.Equal(1500, bobBalance + eveBalance);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Equal(1, await db.Parcels.CountAsync(p => p.Kind == Server.Storage.Entities.ParcelKindDb.Purchase));
    }

    [Fact]
    public async Task FeeGoesToTheBankAccount()
    {
        using var fixture = EconomyServer().WithSetting("Economy:MarketFeePercent", "10");
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        await Fund(fixture, bob.Player.PlayerId, 100);
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);

        var lot = await n.Market.PostAsync(Post(Items(("Steel", 1)), 100), n.Auth);
        await b.Market.BuyAsync(new LotRef { LotId = lot.Id }, b.Auth);

        Assert.Equal(90, (await n.Wallet.GetAsync(new Empty(), n.Auth)).Balance);
        using var scope = fixture.Services.CreateScope();
        Assert.Equal(10, await scope.ServiceProvider.GetRequiredService<WalletService>().Balance(WalletService.BankPlayerId));
    }

    [Fact]
    public async Task BuyingYourOwnLotIsSelfTrade()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        await Fund(fixture, nova.Player.PlayerId, 100);
        var n = Clients(fixture, nova.SessionToken);
        var lot = await n.Market.PostAsync(Post(Items(("Steel", 1)), 10), n.Auth);

        var ex = await Assert.ThrowsAsync<RpcException>(() => n.Market.BuyAsync(new LotRef { LotId = lot.Id }, n.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.SelfTrade, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task WithdrawReturnsTheItemsAsAParcel()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);
        var lot = await n.Market.PostAsync(Post(Items(("Steel", 7)), 10), n.Auth);

        var parcel = await n.Market.WithdrawAsync(new LotRef { LotId = lot.Id }, n.Auth);

        Assert.Equal(ParcelKind.ParcelLotReturn, parcel.Kind);
        Assert.Equal(7, parcel.Items.Items[0].Count);
        Assert.Empty((await n.Market.ListAsync(new MarketQuery(), n.Auth)).Lots);
        Assert.Single((await n.Mailbox.ListAsync(new Empty(), n.Auth)).Parcels);
    }

    [Fact]
    public async Task PostingBeyondMaxLotsIsRefused()
    {
        using var fixture = EconomyServer().WithSetting("Economy:MaxLotsPerPlayer", "2");
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);
        await n.Market.PostAsync(Post(Items(("Steel", 1)), 10), n.Auth);
        await n.Market.PostAsync(Post(Items(("Steel", 1)), 10), n.Auth);

        var ex = await Assert.ThrowsAsync<RpcException>(() => n.Market.PostAsync(Post(Items(("Steel", 1)), 10), n.Auth).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task ExpiredLotIsReturnedAndAnnounced()
    {
        using var fixture = EconomyServer().WithSetting("Economy:LotLifetimeDays", "0");
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var stream = events.Subscribe(new Empty(), n.Auth);
        var lot = await n.Market.PostAsync(Post(Items(("Steel", 2)), 10), n.Auth);

        using var scope = fixture.Services.CreateScope();
        int expired = await scope.ServiceProvider.GetRequiredService<MarketService>().ExpireDue(DateTime.UtcNow.AddDays(1));

        Assert.Equal(1, expired);
        var closed = await ReadEvent(stream, ServerEvent.BodyOneofCase.LotClosed, e => e.LotClosed);
        Assert.Equal(lot.Id, closed.LotId);
        Assert.Equal("expired", closed.Reason);
        var parcel = Assert.Single((await n.Mailbox.ListAsync(new Empty(), n.Auth)).Parcels);
        Assert.Equal(ParcelKind.ParcelLotReturn, parcel.Kind);
    }

    [Fact]
    public async Task ExpiredLotOfResetPlayerIsReturnedToTheirMailbox()
    {
        using var fixture = EconomyServer().WithSetting("Economy:LotLifetimeDays", "0").WithSetting("Auth:AdminLogins:0", "admin");
        var admin = await fixture.RegisterAndLogin("admin");
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);
        await n.Market.PostAsync(Post(Items(("Steel", 2)), 10), n.Auth);
        using (var scope = fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(new Actor(await fixture.AccountIdOf("admin"), admin.Player.PlayerId, Role.Admin, "admin"), nova.Player.PlayerId);
        }

        using var scope2 = fixture.Services.CreateScope();
        int expired = await scope2.ServiceProvider.GetRequiredService<MarketService>().ExpireDue(DateTime.UtcNow.AddDays(1));

        Assert.Equal(1, expired);
        var again = await fixture.RegisterAndLogin("nova");
        Assert.Single((await Clients(fixture, again.SessionToken).Mailbox.ListAsync(new Empty(), Bearer(again.SessionToken))).Parcels);
    }

    [Fact]
    public async Task BalanceEqualsTheSumOfEntriesAfterARandomSeries()
    {
        using var fixture = EconomyServer();
        var logins = new[] { await fixture.RegisterAndLogin("p1"), await fixture.RegisterAndLogin("p2"), await fixture.RegisterAndLogin("p3") };
        var clients = logins.Select(l => Clients(fixture, l.SessionToken)).ToArray();
        foreach (var l in logins) await Fund(fixture, l.Player.PlayerId, 500);
        var random = new Random(7);
        var openLots = new List<long>();

        for (int i = 0; i < 200; i++)
        {
            int who = random.Next(3);
            var c = clients[who];
            try
            {
                switch (random.Next(4))
                {
                    case 0: openLots.Add((await c.Market.PostAsync(Post(Items(("Steel", 1)), random.Next(1, 200)), c.Auth)).Id); break;
                    case 1: if (openLots.Count > 0) { long id = openLots[random.Next(openLots.Count)]; await c.Market.BuyAsync(new LotRef { LotId = id }, c.Auth); openLots.Remove(id); } break;
                    case 2: if (openLots.Count > 0) { long id = openLots[random.Next(openLots.Count)]; await c.Market.WithdrawAsync(new LotRef { LotId = id }, c.Auth); openLots.Remove(id); } break;
                    case 3: await c.Transfer.SendAsync(Send(logins[(who + 1) % 3].Player.PlayerId, silver: random.Next(1, 100)), c.Auth); break;
                }
            }
            catch (RpcException) { /* refusals (insufficient, self-trade, gone, lot limit) are part of the series */ }
        }

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        foreach (var wallet in await db.Wallets.ToListAsync())
        {
            long sum = await db.WalletEntries.Where(e => e.PlayerId == wallet.PlayerId).SumAsync(e => e.Delta);
            Assert.Equal(sum, wallet.Balance);
            Assert.True(wallet.Balance >= 0);
        }
    }

    [Fact]
    public async Task ListSearchesByLabelAndPages()
    {
        using var fixture = EconomyServer().WithSetting("Economy:MaxLotsPerPlayer", "100");
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);
        for (int i = 0; i < 60; i++) await n.Market.PostAsync(Post(Items((i % 3 == 0 ? "ComponentIndustrial" : "Steel", 1)), 10), n.Auth);

        var page1 = await n.Market.ListAsync(new MarketQuery { Page = 0 }, n.Auth);
        var page2 = await n.Market.ListAsync(new MarketQuery { Page = 1 }, n.Auth);
        var comps = await n.Market.ListAsync(new MarketQuery { Text = "comp" }, n.Auth);

        Assert.Equal(50, page1.Lots.Count);
        Assert.Equal(10, page2.Lots.Count);
        Assert.Equal(60, page1.Total);
        Assert.Equal(20, comps.Lots.Count);
    }

    private static async Task<(bool ok, RpcException? error)> Try(Func<Task<Parcel>> call)
    {
        try { await call(); return (true, null); }
        catch (RpcException ex) { return (false, ex); }
    }
}
