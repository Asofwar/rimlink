using System.Net.Http.Json;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;
using Wallet = Rimlink.Server.Storage.Entities.Wallet;

namespace Rimlink.Server.Tests.Economy;

/// <summary>The storage fee is charged when a thing leaves the server (economy hardening spec §4); 0.5 %/day, no free days here.</summary>
public class StorageChargeTests
{
    private static ServerFixture Server() => EconomyServer().WithSetting("Economy:StorageFreeDays", "0").WithSetting("Economy:SendCooldownSeconds", "0")
        .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken).WithSetting("Auth:AdminLogins:0", "admin");

    private static PawnBlob PawnWorth(long silver)
    {
        var pawn = Pawn("Colonist", "Vivis");
        pawn.MarketValueX100 = silver * 100;
        return pawn;
    }

    private static ItemList Berries(int count, long silverEach)
    {
        var item = Item("RawBerries", count, marketValueX100: silverEach * 100);
        item.Perishable = true;
        return new ItemList { Items = { item } };
    }

    private static Task<Protocol.V1.Lot> PostPawn(EconomyClients c, long value, long price) =>
        c.Market.PostAsync(new PostRequest { ClientRequestId = Guid.NewGuid().ToString("N"), Pawn = PawnWorth(value), Price = price, GameTick = 100 }, c.Auth).ResponseAsync;

    private static Task<Protocol.V1.Lot> PostItems(EconomyClients c, ItemList items, long price) =>
        c.Market.PostAsync(new PostRequest { ClientRequestId = Guid.NewGuid().ToString("N"), Items = items, Price = price, GameTick = 100 }, c.Auth).ResponseAsync;

    private static async Task Age(ServerFixture fixture, Func<RimlinkDbContext, Task> age)
    {
        using var scope = fixture.Services.CreateScope();
        await age(scope.ServiceProvider.GetRequiredService<RimlinkDbContext>());
    }

    private static Task AgeLot(ServerFixture fixture, long lotId, double days = 2) =>
        Age(fixture, db => db.Lots.Where(l => l.Id == lotId).ExecuteUpdateAsync(s => s.SetProperty(l => l.CreatedAt, DateTime.UtcNow.AddDays(-days))));

    private static Task AgeParcel(ServerFixture fixture, long parcelId, double days = 2) =>
        Age(fixture, db => db.Parcels.Where(p => p.Id == parcelId).ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedAt, DateTime.UtcNow.AddDays(-days))));

    private static async Task<List<WalletEntry>> Entries(ServerFixture fixture, long playerId, string kind)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().WalletEntries.AsNoTracking()
            .Where(e => e.PlayerId == playerId && e.Kind == kind).OrderBy(e => e.Id).ToListAsync();
    }

    private static async Task<long> BalanceOf(ServerFixture fixture, long playerId)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WalletService>().Balance(playerId);
    }

    private static async Task<int> ExpireNow(ServerFixture fixture, long lotId)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        await db.Lots.Where(l => l.Id == lotId).ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        return await scope.ServiceProvider.GetRequiredService<MarketService>().ExpireDue(DateTime.UtcNow);
    }

    [Fact]
    public async Task SoldPawnLotPaysFromTheProceeds()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);
        await Fund(fixture, bob.Player.PlayerId, 1000);
        using var novaStream = new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), n.Auth);
        var lot = await PostPawn(n, value: 1000, price: 300);
        await AgeLot(fixture, lot.Id);

        await b.Market.BuyAsync(new LotRef { LotId = lot.Id }, b.Auth);

        Assert.Equal(290, await BalanceOf(fixture, nova.Player.PlayerId));
        Assert.Equal(-10, Assert.Single(await Entries(fixture, nova.Player.PlayerId, "storage")).Delta);
        Assert.Equal(10, Assert.Single(await Entries(fixture, WalletService.BankPlayerId, "storage")).Delta);
        var sold = await ReadEvent(novaStream, ServerEvent.BodyOneofCase.LotSold, e => e.LotSold);
        Assert.Equal(10, sold.StorageFee);
        Assert.Equal(290, sold.Proceeds);
    }

    [Fact]
    public async Task FeeAboveTheProceedsBecomesDebt()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);
        await Fund(fixture, bob.Player.PlayerId, 1000);
        var lot = await PostPawn(n, value: 100000, price: 50);
        await AgeLot(fixture, lot.Id);

        await b.Market.BuyAsync(new LotRef { LotId = lot.Id }, b.Auth);

        Assert.Equal(50 - 1000, await BalanceOf(fixture, nova.Player.PlayerId));
    }

    [Fact]
    public async Task WithdrawnAndExpiredLotsChargeTheSeller()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);
        using var novaStream = new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), n.Auth);
        var withdrawn = await PostItems(n, Berries(10, 100), 500);
        var expired = await PostItems(n, Berries(10, 100), 500);
        await AgeLot(fixture, withdrawn.Id);
        await AgeLot(fixture, expired.Id);

        await n.Market.WithdrawAsync(new LotRef { LotId = withdrawn.Id }, n.Auth);
        Assert.Equal(-10, await BalanceOf(fixture, nova.Player.PlayerId));
        Assert.Equal(1, await ExpireNow(fixture, expired.Id));

        Assert.Equal(-20, await BalanceOf(fixture, nova.Player.PlayerId));
        Assert.Equal(10, await ReadEvent(novaStream, ServerEvent.BodyOneofCase.LotClosed, e => e.LotClosed.StorageFee));
    }

    [Fact]
    public async Task AdminRefundChargesNothing()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);
        var lot = await PostPawn(n, value: 1000, price: 300);
        await AgeLot(fixture, lot.Id);

        (await fixture.Rest().PostAsync($"/api/admin/economy/refund/lot/{lot.Id}", null)).EnsureSuccessStatusCode();

        Assert.Empty(await Entries(fixture, nova.Player.PlayerId, "storage"));
    }

    [Fact]
    public async Task DeliveredGiftChargesTheRecipient()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        await SocialFixtures.AcceptGifts(fixture, bob.Player.PlayerId); // a gift waits for a decision otherwise
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);
        using var bobStream = new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), b.Auth);
        var parcel = await n.Transfer.SendAsync(Send(bob.Player.PlayerId, pawns: new PawnList { Pawns = { PawnWorth(2000) } }), n.Auth);
        await AgeParcel(fixture, parcel.Id);

        await b.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = parcel.Id, GameTick = 300 }, b.Auth);

        Assert.Equal(-20, await BalanceOf(fixture, bob.Player.PlayerId));
        var changed = await ReadEvent(bobStream, ServerEvent.BodyOneofCase.WalletChanged, e => e.WalletChanged);
        Assert.Equal("storage", changed.Kind);
        Assert.Equal(-20, changed.Delta);
        Assert.Equal(parcel.Id, changed.RefId);
    }

    [Fact]
    public async Task RedeliveryDoesNotPayAgain()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        await SocialFixtures.AcceptGifts(fixture, bob.Player.PlayerId); // a gift waits for a decision otherwise
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);
        var parcel = await n.Transfer.SendAsync(Send(bob.Player.PlayerId, pawns: new PawnList { Pawns = { PawnWorth(2000) } }), n.Auth);
        await AgeParcel(fixture, parcel.Id);

        await b.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = parcel.Id, GameTick = 300 }, b.Auth);
        await b.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = parcel.Id, GameTick = 400 }, b.Auth);

        Assert.Single(await Entries(fixture, bob.Player.PlayerId, "storage"));
    }

    [Fact]
    public async Task ExemptParcelsChargeNothing()
    {
        using var fixture = Server();
        var bob = await fixture.RegisterAndLogin("bob");
        var b = Clients(fixture, bob.SessionToken);
        long parcelId;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
            var parcel = new Parcel
            {
                ToPlayerId = bob.Player.PlayerId, Kind = ParcelKindDb.RaidReturn, Items = new ItemList().ToByteArray(),
                Pawns = new PawnList { Pawns = { PawnWorth(2000) } }.ToByteArray(), CreatedAt = DateTime.UtcNow.AddDays(-5),
            };
            db.Parcels.Add(parcel);
            await db.SaveChangesAsync();
            parcelId = parcel.Id;
        }

        await b.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = parcelId, GameTick = 300 }, b.Auth);

        Assert.Empty(await Entries(fixture, bob.Player.PlayerId, "storage"));
    }

    [Fact]
    public async Task ExpiryChargesASellerWithoutAWallet()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);
        var lot = await PostPawn(n, value: 1000, price: 300);
        await AgeLot(fixture, lot.Id);
        await Age(fixture, db => db.Wallets.Where(w => w.PlayerId == nova.Player.PlayerId).ExecuteDeleteAsync());

        Assert.Equal(1, await ExpireNow(fixture, lot.Id));

        Assert.Equal(-10, await BalanceOf(fixture, nova.Player.PlayerId));
    }

    [Fact]
    public async Task StatsReportStorageFees()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);
        await Fund(fixture, bob.Player.PlayerId, 1000);
        var lot = await PostPawn(n, value: 1000, price: 300);
        await AgeLot(fixture, lot.Id);
        await b.Market.BuyAsync(new LotRef { LotId = lot.Id }, b.Auth);

        var stats = await fixture.Rest().GetFromJsonAsync<JsonElement>("/api/admin/economy/stats");

        Assert.Equal(10, stats.GetProperty("storageFeesCollected").GetInt64());
    }
}
