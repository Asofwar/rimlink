using Google.Protobuf;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Storage.Entities;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using Lot = Rimlink.Server.Storage.Entities.Lot;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;

namespace Rimlink.Server.Tests.Economy;

/// <summary>Pawns and perishables kept on the server cost silver after the free days (economy hardening spec §4).</summary>
public class StorageFeeTests
{
    private static readonly DateTime Stored = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ItemBlob Perishable(string def, int count, long valueX100)
    {
        var item = Item(def, count, marketValueX100: valueX100);
        item.Perishable = true;
        return item;
    }

    private static ItemList ItemsOf(params ItemBlob[] items)
    {
        var list = new ItemList();
        list.Items.AddRange(items);
        return list;
    }

    [Fact]
    public void NothingInsideTheFreeDays() =>
        Assert.Equal(0, StorageFee.For(1000, Stored, Stored.AddDays(2.9), 0.5, 3));

    [Fact]
    public void FractionalDaysAfterTheFreeDays()
    {
        Assert.Equal(10, StorageFee.For(1000, Stored, Stored.AddDays(5), 0.5, 3));
        Assert.Equal(7, StorageFee.For(1000, Stored, Stored.AddDays(4.5), 0.5, 3));
    }

    [Fact]
    public void ZeroRateIsFree() =>
        Assert.Equal(0, StorageFee.For(1000, Stored, Stored.AddDays(100), 0, 3));

    [Fact]
    public void LotBaseCountsPawnsAndPerishablesOnly()
    {
        var items = new Lot { Items = ItemsOf(Perishable("RawBerries", 10, 500), Item("Steel", 5, marketValueX100: 1000)).ToByteArray(), Price = 999 };
        var oldPawn = new Lot { Items = new ItemList().ToByteArray(), Pawn = Pawn("Colonist", "Vivis").ToByteArray(), Price = 400 };

        Assert.Equal(50, StorageFee.LotBase(items));
        Assert.Equal(400, StorageFee.LotBase(oldPawn));
    }

    [Fact]
    public void ParcelBaseExemptsSystemKinds()
    {
        var pawn = Pawn("Colonist", "Vivis");
        pawn.MarketValueX100 = 200000;
        Parcel Of(ParcelKindDb kind) => new()
        {
            Kind = kind, Items = ItemsOf(Perishable("RawBerries", 10, 500), Item("Steel", 5, marketValueX100: 1000)).ToByteArray(),
            Pawns = new PawnList { Pawns = { pawn } }.ToByteArray(), Silver = 70,
        };

        foreach (var kind in new[] { ParcelKindDb.Loot, ParcelKindDb.RaidReturn, ParcelKindDb.Captive, ParcelKindDb.LotReturn, ParcelKindDb.Withdraw, ParcelKindDb.Admin })
            Assert.Equal(0, StorageFee.ParcelBase(Of(kind)));
        Assert.Equal(2050, StorageFee.ParcelBase(Of(ParcelKindDb.Gift)));
        Assert.Equal(2050, StorageFee.ParcelBase(Of(ParcelKindDb.Purchase)));
    }
}
