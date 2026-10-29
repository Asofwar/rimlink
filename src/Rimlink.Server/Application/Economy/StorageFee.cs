using Rimlink.Protocol.V1;
using Rimlink.Server.Storage.Entities;
using Lot = Rimlink.Server.Storage.Entities.Lot;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;

namespace Rimlink.Server.Application.Economy;

/// <summary>
/// The fee for keeping pawns and perishables on the server: the market is not a free freezer (economy hardening spec §4).
/// It is computed when a thing leaves the server, from how long it stayed; the free days cover ordinary delivery.
/// </summary>
public static class StorageFee
{
    /// <summary>floor(value × percent/100 × max(0, real days − free days)); 0 for a zero rate or value.</summary>
    public static long For(long valueSilver, DateTime storedAt, DateTime now, double percentPerDay, double freeDays)
    {
        double rate = Math.Clamp(percentPerDay, 0, 100), free = Math.Max(0, freeDays);
        double days = (now - storedAt).TotalDays - free;
        if (valueSilver <= 0 || rate <= 0 || days <= 0) return 0;
        decimal fee = Math.Floor(valueSilver * (decimal)rate / 100m * (decimal)days);
        return fee >= long.MaxValue ? long.MaxValue : (long)fee;
    }

    /// <summary>A pawn lot: the pawn's value (the price for an old blob without one); an item lot: its perishable stacks.</summary>
    public static long LotBase(Lot lot) =>
        lot.Pawn is { Length: > 0 } ? ServerWealth.ValueOf(lot) : Perishables(ItemList.Parser.ParseFrom(lot.Items));

    /// <summary>Gifts and purchases pay for pawns and perishables; returns, withdrawals, admin and raid parcels are free.</summary>
    public static long ParcelBase(Parcel parcel) => parcel.Kind is ParcelKindDb.Gift or ParcelKindDb.Purchase
        ? Sum(ServerWealth.ValueOf(PawnList.Parser.ParseFrom(parcel.Pawns)), Perishables(ItemList.Parser.ParseFrom(parcel.Items)))
        : 0;

    private static long Perishables(ItemList items)
    {
        var perishable = new ItemList();
        perishable.Items.AddRange(items.Items.Where(i => i.Perishable));
        return ServerWealth.ValueOf(perishable);
    }

    private static long Sum(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
}
