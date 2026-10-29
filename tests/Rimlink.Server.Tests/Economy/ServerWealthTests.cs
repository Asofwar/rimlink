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
using Lot = Rimlink.Server.Storage.Entities.Lot;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;
using Raid = Rimlink.Server.Storage.Entities.Raid;
using Wallet = Rimlink.Server.Storage.Entities.Wallet;

namespace Rimlink.Server.Tests.Economy;

/// <summary>What a player holds on the server counts as colony wealth (economy hardening spec §2).</summary>
public class ServerWealthTests
{
    private static async Task Seed(ServerFixture fixture, Action<RimlinkDbContext> seed)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static async Task<long> WealthOf(ServerFixture fixture, long playerId)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ServerWealth>().Of(playerId);
    }

    private static Lot ItemLot(long seller, ItemList items, long price = 100, LotStatusDb status = LotStatusDb.Open) => new()
    {
        SellerPlayerId = seller, Items = items.ToByteArray(), Price = price, CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(30), Status = status,
    };

    private static Lot PawnLot(long seller, long pawnValueX100, long price) => new()
    {
        SellerPlayerId = seller, Items = new ItemList().ToByteArray(), Pawn = WithValue(Pawn("Colonist", "Vivis"), pawnValueX100).ToByteArray(),
        Price = price, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(30), Status = LotStatusDb.Open,
    };

    private static PawnBlob WithValue(PawnBlob pawn, long valueX100)
    {
        pawn.MarketValueX100 = valueX100;
        return pawn;
    }

    private static Parcel ParcelTo(long to, ItemList items, long silver, DateTime? deliveredAt = null) => new()
    {
        ToPlayerId = to, Kind = ParcelKindDb.Gift, Items = items.ToByteArray(), Pawns = new PawnList().ToByteArray(), Silver = silver,
        CreatedAt = DateTime.UtcNow, DeliveredAt = deliveredAt,
    };

    private static ItemList ItemsOf(params ItemBlob[] items)
    {
        var list = new ItemList();
        list.Items.AddRange(items);
        return list;
    }

    [Fact]
    public async Task CountsWalletLotsAndParcels()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        long id = nova.Player.PlayerId;
        await Fund(fixture, id, 300);
        await Seed(fixture, db =>
        {
            db.Lots.Add(ItemLot(id, ItemsOf(Item("Steel", 2, marketValueX100: 15000)), price: 150));
            db.Lots.Add(PawnLot(id, pawnValueX100: 50000, price: 900));
            db.Lots.Add(ItemLot(id, ItemsOf(Item("Gold", 1, marketValueX100: 999900)), status: LotStatusDb.Sold));
            db.Parcels.Add(ParcelTo(id, ItemsOf(Item("Steel", 10, marketValueX100: 1000)), silver: 40));
            db.Parcels.Add(ParcelTo(id, ItemsOf(Item("Gold", 1, marketValueX100: 999900)), silver: 500, deliveredAt: DateTime.UtcNow));
        });

        Assert.Equal(300 + 300 + 500 + 140, await WealthOf(fixture, id));
        var n = Clients(fixture, nova.SessionToken);
        Assert.Equal(1240, (await n.Wallet.GetAsync(new Empty(), n.Auth)).ServerWealth);
    }

    [Fact]
    public async Task OldPawnBlobUsesTheLotPrice()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        long id = nova.Player.PlayerId;
        await Seed(fixture, db => db.Lots.Add(PawnLot(id, pawnValueX100: 0, price: 700)));

        Assert.Equal(700, await WealthOf(fixture, id));
    }

    [Fact]
    public async Task NegativeBalanceCountsAsZero()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        long id = nova.Player.PlayerId;
        await Seed(fixture, db => db.Wallets.Add(new Wallet { PlayerId = id, Balance = -50, UpdatedAt = DateTime.UtcNow }));

        Assert.Equal(0, await WealthOf(fixture, id));
    }

    [Fact]
    public async Task PercentScalesTheSum()
    {
        using var fixture = EconomyServer().WithSetting("Economy:ServerWealthPercent", "50");
        var nova = await fixture.RegisterAndLogin("nova");
        await Fund(fixture, nova.Player.PlayerId, 300);

        Assert.Equal(150, await WealthOf(fixture, nova.Player.PlayerId));
    }

    [Fact]
    public async Task DisabledEconomyIsZero()
    {
        using var fixture = EconomyServer().WithSetting("Economy:Enabled", "false");
        var nova = await fixture.RegisterAndLogin("nova");
        await Fund(fixture, nova.Player.PlayerId, 300);

        Assert.Equal(0, await WealthOf(fixture, nova.Player.PlayerId));
    }

    [Fact]
    public async Task RaidEscrowIsNotCounted()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var ash = await fixture.RegisterAndLogin("ash");
        long id = nova.Player.PlayerId;
        var pawns = new PawnList { Pawns = { WithValue(Pawn("Colonist", "Vivis"), 500000) } };
        await Seed(fixture, db => db.Raids.Add(new Raid
        {
            AttackerPlayerId = id, TargetPlayerId = ash.Player.PlayerId, State = RaidStateDb.Queued, Pawns = pawns.ToByteArray(),
            CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(7),
        }));

        Assert.Equal(0, await WealthOf(fixture, id));
    }

    [Fact]
    public async Task HugeDeclaredValuesSaturate()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        long id = nova.Player.PlayerId;
        await Seed(fixture, db => db.Lots.Add(ItemLot(id, ItemsOf(Item("Gold", 200, marketValueX100: long.MaxValue)))));

        Assert.Equal(long.MaxValue, await WealthOf(fixture, id));
    }
}
