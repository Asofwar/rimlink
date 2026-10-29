using System.Data;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Raids;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;
using ShopItem = Rimlink.Server.Storage.Entities.ShopItem;
using Transfer = Rimlink.Server.Storage.Entities.Transfer;
using Rimlink.Server.Application.Feed;

namespace Rimlink.Server.Application.Visits;

/// <summary>
/// The host publishes what is for sale from its save; a visitor buys from their copy of that save. The blob comes from
/// the visitor (checked against the host's descriptor); the host's game removes the sold thing at its next login.
/// </summary>
public sealed class ShopService(RimlinkDbContext db, WalletService wallet, MailboxService mailbox, VisitService visits, EconomyGate gate,
    EventBus events, AdminFeed feed, IOptions<EconomyOptions> options, ILogger<ShopService> log)
{
    /// <summary>Replaces the host's unsold list. Sold rows and rows with an unacknowledged removal stay as they are, so a list from an older save cannot resurrect them.</summary>
    public async Task Publish(long hostId, ShopList list)
    {
        var host = await db.Players.AsNoTracking().FirstAsync(p => p.Id == hostId);
        if (list.Items.Count > 0 && list.Items.Max(i => i.SaveTick) < host.LastOutgoingTick)
        {
            log.LogInformation("Shop list of player {Host} ignored: save tick older than the last sale", hostId);
            return;
        }
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var rows = await db.ShopItems.Where(s => s.HostPlayerId == hostId).ToDictionaryAsync(s => s.ThingId);
        var frozen = (await db.PendingRemovals.Where(r => r.HostPlayerId == hostId && r.AckedAt == null).Select(r => r.ThingId).ToListAsync()).ToHashSet();
        var incoming = new HashSet<long>();
        foreach (var item in list.Items)
        {
            if (item.Count < 1 || item.Price < 1 || string.IsNullOrEmpty(item.DefName)) continue;
            incoming.Add(item.ThingId);
            if (rows.TryGetValue(item.ThingId, out var row))
            {
                if (row.SoldAt is not null || frozen.Contains(row.ThingId)) continue;
                row.DefName = item.DefName; row.StuffDef = item.StuffDef; row.Count = item.Count; row.Quality = item.Quality;
                row.HitPoints = item.HitPoints; row.Price = item.Price; row.SaveTick = item.SaveTick; row.Label = item.Label;
            }
            else
            {
                db.ShopItems.Add(new ShopItem
                {
                    HostPlayerId = hostId, ThingId = item.ThingId, DefName = item.DefName, StuffDef = item.StuffDef, Count = item.Count, Quality = item.Quality,
                    HitPoints = item.HitPoints, Price = item.Price, SaveTick = item.SaveTick, Label = item.Label,
                });
            }
        }
        foreach (var row in rows.Values)
            if (!incoming.Contains(row.ThingId) && row.SoldAt is null && !frozen.Contains(row.ThingId)) db.ShopItems.Remove(row);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task<ShopList> Of(long hostId)
    {
        var host = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == hostId) ?? throw RpcErrors.Failed(ErrorCode.RecipientUnknown, RefusalKeys.NoSuchPlayer, "no such player");
        var list = new ShopList { Host = host.ToRef() };
        foreach (var row in await db.ShopItems.AsNoTracking().Where(s => s.HostPlayerId == hostId && s.SoldAt == null && s.Count > 0).OrderBy(s => s.ThingId).ToListAsync())
            list.Items.Add(ToProto(row));
        return list;
    }

    public async Task<Parcel> Buy(long buyerId, BuyRequest r)
    {
        gate.RequireOpen();
        if (r.HostPlayerId == buyerId) throw RpcErrors.Failed(ErrorCode.SelfTrade, RefusalKeys.ShopSelf, "you cannot buy from yourself");
        if (r.Count < 1) throw RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.Unspecified, RefusalKeys.ShopCountTooLow, "count must be at least 1");
        if (r.Blob is null || r.Blob.ScribeXmlZstd.Length == 0) throw RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.Unspecified, RefusalKeys.ShopNoItemData, "the purchase carries no item data");
        if (!await visits.IsVisiting(buyerId, r.HostPlayerId)) throw RpcErrors.With(StatusCode.FailedPrecondition, ErrorCode.Unspecified, RefusalKeys.ShopNotVisiting, "you are not visiting this player");

        Parcel parcel;
        ItemSold sold;
        string itemLabel;
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            if (!string.IsNullOrEmpty(r.ClientRequestId))
            {
                var existing = await db.Parcels.AsNoTracking().FirstOrDefaultAsync(p => p.ClientRequestId == r.ClientRequestId);
                if (existing is not null) return existing;
            }
            var row = await db.ShopItems.FirstOrDefaultAsync(s => s.HostPlayerId == r.HostPlayerId && s.ThingId == r.ThingId);
            if (row is null || row.SoldAt is not null || row.Count <= 0) throw RpcErrors.Failed(ErrorCode.ItemSold, RefusalKeys.ShopSold, "that item is no longer for sale");
            if (row.Count < r.Count) throw RpcErrors.Failed(ErrorCode.ItemSold, RefusalKeys.ShopOnlyLeft, $"only {row.Count} left", row.Count);
            if (r.Blob.DefName != row.DefName || r.Blob.StuffDef != row.StuffDef || r.Blob.Quality != row.Quality || r.Blob.Count != r.Count)
                throw RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.Unspecified, RefusalKeys.ShopMismatch, "the item does not match what is for sale");

            long price = row.Price * r.Count;
            long fee = price * Math.Clamp(options.Value.MarketFeePercent, 0, 100) / 100;
            await wallet.Apply(buyerId, -price, "buy", row.Id);
            await wallet.Apply(r.HostPlayerId, price - fee, "sale", row.Id);
            if (fee > 0) await wallet.Apply(WalletService.BankPlayerId, fee, "fee", row.Id);

            row.Count -= r.Count;
            if (row.Count == 0) row.SoldAt = DateTime.UtcNow;
            db.PendingRemovals.Add(new PendingRemoval { HostPlayerId = r.HostPlayerId, ThingId = r.ThingId, Count = r.Count, CreatedAt = DateTime.UtcNow });
            parcel = await mailbox.Create(buyerId, r.HostPlayerId, ParcelKindDb.Purchase, new ItemList { Items = { r.Blob } }, null, 0,
                string.IsNullOrEmpty(r.ClientRequestId) ? null : r.ClientRequestId);
            db.Transfers.Add(new Transfer { FromPlayerId = buyerId, ToPlayerId = r.HostPlayerId, Kind = "buy", GameTick = 0, Summary = $"{r.Count}x {row.DefName} @ {row.Price} (shop)", At = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            wallet.FlushEvents();

            var buyer = await db.Players.AsNoTracking().FirstAsync(p => p.Id == buyerId);
            sold = new ItemSold { ThingId = r.ThingId, Count = r.Count, Price = price, Buyer = buyer.ToRef() };
            itemLabel = string.IsNullOrEmpty(row.Label) ? row.DefName : row.Label;
            log.LogInformation("Player {Buyer} bought {Count}x {Def} from player {Host}'s shop for {Price}", buyerId, r.Count, row.DefName, r.HostPlayerId, price);
        }
        RaidMetrics.Sales.Inc();
        EconomyMetrics.Trade("shop", sold.Price);
        events.SendTo(r.HostPlayerId, new ServerEvent { ItemSold = sold });
        string host = await db.Players.AsNoTracking().Where(p => p.Id == r.HostPlayerId).Select(p => p.Name).FirstAsync();
        feed.Publish(FeedTypes.ShopSold, new ShopSoldData(new FeedPlayer(r.HostPlayerId, host), new FeedPlayer(buyerId, sold.Buyer.Name), itemLabel, sold.Count, sold.Price));
        await mailbox.Announce(parcel);
        return parcel;
    }

    public Task<List<PendingRemoval>> Pending(long hostId) =>
        db.PendingRemovals.AsNoTracking().Where(p => p.HostPlayerId == hostId && p.AckedAt == null).OrderBy(p => p.Id).ToListAsync();

    /// <summary>The host's game took the sold things off its map and saved: older saves are refused from now on.</summary>
    public async Task Ack(long hostId, IEnumerable<long> ids, long gameTick)
    {
        var list = ids.ToList();
        await db.PendingRemovals.Where(p => p.HostPlayerId == hostId && list.Contains(p.Id) && p.AckedAt == null).ExecuteUpdateAsync(s => s.SetProperty(p => p.AckedAt, DateTime.UtcNow));
        var host = await db.Players.FirstAsync(p => p.Id == hostId);
        if (gameTick > host.LastOutgoingTick) host.LastOutgoingTick = gameTick;
        await db.SaveChangesAsync();
    }

    public async Task ClearOf(long hostId)
    {
        await db.ShopItems.Where(s => s.HostPlayerId == hostId).ExecuteDeleteAsync();
        await db.PendingRemovals.Where(p => p.HostPlayerId == hostId).ExecuteDeleteAsync();
    }

    public static Protocol.V1.ShopItem ToProto(ShopItem s) => new()
    {
        ThingId = s.ThingId, DefName = s.DefName, StuffDef = s.StuffDef, Count = s.Count, Quality = s.Quality, HitPoints = s.HitPoints, Price = s.Price, SaveTick = s.SaveTick, Label = s.Label,
    };
}
