using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Lot = Rimlink.Server.Storage.Entities.Lot;

namespace Rimlink.Server.Application.Economy;

/// <summary>
/// What a player holds on the server, in silver: the wallet (never below zero), open lots, their sides of open trades and
/// parcels on the way to them.
/// The storyteller, the loot cap and the poor-colony threshold count it as colony wealth, so it cannot be hidden here.
/// Raid escrow is not counted: those pawns are fighting. Declared values come from the client and saturate instead of overflowing.
/// </summary>
public sealed class ServerWealth(RimlinkDbContext db, IOptions<EconomyOptions> options)
{
    /// <summary>The sum already scaled by <see cref="EconomyOptions.ServerWealthPercent"/>; 0 while the economy is disabled.</summary>
    public async Task<long> Of(long playerId)
    {
        var o = options.Value;
        if (!o.Enabled) return 0;

        decimal sum = Math.Max(0, (await db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.PlayerId == playerId))?.Balance ?? 0);
        foreach (var lot in await db.Lots.AsNoTracking().Where(l => l.SellerPlayerId == playerId && l.Status == LotStatusDb.Open).ToListAsync())
            sum += ValueOf(lot);
        foreach (var trade in await db.Trades.AsNoTracking().Where(t => t.Status == TradeStatusDb.Open && (t.InitiatorPlayerId == playerId || t.PartnerPlayerId == playerId)).ToListAsync())
        {
            bool initiator = trade.InitiatorPlayerId == playerId;
            sum += initiator ? trade.InitiatorSilver : trade.PartnerSilver;
            sum += (decimal)ValueOf(ItemList.Parser.ParseFrom(initiator ? trade.InitiatorItems : trade.PartnerItems))
                + ValueOf(PawnList.Parser.ParseFrom(initiator ? trade.InitiatorPawns : trade.PartnerPawns));
        }
        foreach (var parcel in await db.Parcels.AsNoTracking().Where(p => p.ToPlayerId == playerId && p.DeliveredAt == null).ToListAsync())
        {
            sum += parcel.Silver;
            if (!DeclaredByAnother(parcel.Kind))
                sum += (decimal)ValueOf(ItemList.Parser.ParseFrom(parcel.Items)) + ValueOf(PawnList.Parser.ParseFrom(parcel.Pawns));
        }

        return Saturate(sum * Math.Clamp(o.ServerWealthPercent, 0, 1000) / 100m);
    }

    /// <summary>
    /// A gift's values are the sender's, a trade parcel's the partner's, loot's, a raid return's and a captive's the other side's: a third party could inflate
    /// them to strip the recipient of the poor-colony protection or raise its loot cap. Only their silver counts.
    /// </summary>
    public static bool DeclaredByAnother(ParcelKindDb kind) =>
        kind is ParcelKindDb.Gift or ParcelKindDb.Trade or ParcelKindDb.Loot or ParcelKindDb.RaidReturn or ParcelKindDb.Captive;

    public static long ValueOf(ItemList? items) =>
        Saturate(items?.Items.Sum(i => (decimal)i.MarketValueX100 * Math.Max(0, i.Count)) / 100m ?? 0);

    public static long ValueOf(PawnBlob? pawn) => pawn is null ? 0 : Saturate(pawn.MarketValueX100 / 100m);

    public static long ValueOf(PawnList? pawns) => Saturate(pawns?.Pawns.Sum(p => (decimal)ValueOf(p)) ?? 0);

    /// <summary>Items, or the pawn's value; a pawn blob from before declared values counts at the lot's price.</summary>
    public static long ValueOf(Lot lot)
    {
        if (lot.Pawn is not { Length: > 0 } pawnBytes) return ValueOf(ItemList.Parser.ParseFrom(lot.Items));
        long pawn = ValueOf(PawnBlob.Parser.ParseFrom(pawnBytes));
        return pawn > 0 ? pawn : lot.Price;
    }

    private static long Saturate(decimal value) => value <= 0 ? 0 : value >= long.MaxValue ? long.MaxValue : (long)Math.Floor(value);
}
