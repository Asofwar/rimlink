using System.Data;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Modpack;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;
using Transfer = Rimlink.Server.Storage.Entities.Transfer;

namespace Rimlink.Server.Application.Procurement;

/// <summary>Resource supply only: item descriptors are checked, opaque game blobs have the existing economy trust boundary.</summary>
public sealed class ProcurementService(RimlinkDbContext db, WalletService wallet, MailboxService mailbox,
    ParcelValidator validator, EconomyGate gate, EconomyGuard guard, TransferService transfers, EventBus events,
    ModpackDefs defs, ForbiddenDefs forbidden, IOptions<EconomyOptions> options, TimeProvider time,
    ILogger<ProcurementService> log)
{
    public const int MaxQuantity = 1_000_000, MaxOpen = 20, MaxDeliveries = 100, PageSize = 50;
    public const long MaxFund = 1_000_000_000;
    private DateTime Now => time.GetUtcNow().UtcDateTime;
    private DbSet<ProcurementOrder> Orders => db.Set<ProcurementOrder>();
    private DbSet<ProcurementDelivery> Deliveries => db.Set<ProcurementDelivery>();

    private static RpcException Invalid() => RpcErrors.Failed(ErrorCode.ProcurementInvalid, ProcurementRefusalKeys.Invalid, "invalid procurement: use a resource def, 1-1000000 units, a fund up to 1000000000 silver and 1-720 hours");
    private static RpcException Access() => RpcErrors.Failed(ErrorCode.ProcurementAccess, ProcurementRefusalKeys.Access, "this procurement is not available to you");
    private static RpcException Gone() => RpcErrors.Failed(ErrorCode.ProcurementGone, ProcurementRefusalKeys.Gone, "this procurement is closed or past its deadline");

    public async Task<ProcurementEntry> Create(long player, ProcurementCreate r) => await Mutate(player, r.ClientRequestId, r, async parcels =>
    {
        gate.RequireOpen();
        await gate.RequireNotFrozen(player);
        await guard.RequireNoDebt(player);
        await guard.RequireNoThreat(player);
        if (!System.Enum.IsDefined(r.Kind) || r.Quantity is < 1 or > MaxQuantity || r.Reward is < 1 or > MaxFund
            || r.LifetimeHours is < 1 or > 720 || !ValidDef(r.DefName)
            || r.SupplierPlayerId < 0 || (r.Kind == ProcurementKind.SupplyContract) != (r.SupplierPlayerId > 0)) throw Invalid();
        if (!defs.IsEmpty && !defs.Contains(r.DefName)) throw RpcErrors.Failed(ErrorCode.DefNotInModpack, ProcurementRefusalKeys.Def, "this resource is not in the server modpack");
        if (forbidden.Contains(r.DefName)) throw RpcErrors.Failed(ErrorCode.DefForbidden, ProcurementRefusalKeys.Def, "this resource is forbidden for transfers");
        if (r.SupplierPlayerId == player) throw Access();
        if (r.SupplierPlayerId > 0)
        {
            if (!await db.Players.AnyAsync(p => p.Id == r.SupplierPlayerId)) throw Access();
            await gate.RequireTradePartnerOpen(r.SupplierPlayerId);
        }
        long fund = r.Kind == ProcurementKind.BuyOrder ? checked(r.Reward * r.Quantity) : r.Reward;
        if (fund > MaxFund) throw Invalid();
        if (await Orders.CountAsync(o => o.BuyerPlayerId == player && o.Status == ProcurementStatus.ProcurementOpen) >= Math.Min(MaxOpen, options.Value.MaxLotsPerPlayer))
            throw RpcErrors.Failed(ErrorCode.ProcurementLimit, ProcurementRefusalKeys.Limit, "too many open procurements or deliveries");
        var order = new ProcurementOrder {
            BuyerPlayerId = player, SupplierPlayerId = r.SupplierPlayerId == 0 ? null : r.SupplierPlayerId, Kind = r.Kind,
            DefName = r.DefName, Quantity = r.Quantity, Reward = r.Reward, Escrow = fund, CreatedAt = Now,
            ExpiresAt = Now.AddHours(r.LifetimeHours), Status = ProcurementStatus.ProcurementOpen,
        };
        Orders.Add(order);
        await db.SaveChangesAsync();
        await Money(player, -fund, "procurement-reserve", order.Id);
        return order;
    });

    public async Task<ProcurementEntry> Supply(long player, ProcurementSupply r) => await Mutate(player, r.ClientRequestId, r, async parcels =>
    {
        gate.RequireOpen();
        var order = await Orders.FirstOrDefaultAsync(o => o.Id == r.ProcurementId) ?? throw Gone();
        if (order.BuyerPlayerId == player || (order.SupplierPlayerId is { } supplier && supplier != player)) throw Access();
        if (order.Status != ProcurementStatus.ProcurementOpen || order.ExpiresAt <= Now) throw Gone();
        await gate.RequireNotFrozen(player);
        await gate.RequireTradePartnerOpen(order.BuyerPlayerId);
        await guard.RequireNoThreat(player);
        await guard.RequireNoDebt(player);
        if (r.GameTick < 0 || r.Items is null || r.Items.Items.Count == 0 || r.CalculateSize() > options.Value.MaxParcelBytes) throw Invalid();
        if (validator.Validate(r.Items, null, checkForbidden: true) is { } reason) throw RpcErrors.Failed(reason.Code, reason.Key, reason.Message, reason.Args);
        // Never trust a separate claimed quantity: count the accepted descriptors, and store these exact blobs for delivery/return.
        long units = 0;
        decimal declaredX100 = 0;
        foreach (var item in r.Items.Items)
        {
            if (item.DefName != order.DefName || !ValidDef(item.DefName) || item.StuffDef.Length > 128 || item.Label.Length > 256
                || item.MarketValueX100 < 0 || item.Quality is < -1 or > 6) throw RpcErrors.Failed(ErrorCode.ProcurementGoods, ProcurementRefusalKeys.Goods, "supply must contain only the requested resource and exact permitted quantity");
            declaredX100 += (decimal)item.MarketValueX100 * item.Count;
            if (declaredX100 > MaxFund * 100m) throw RpcErrors.Failed(ErrorCode.ProcurementGoods, ProcurementRefusalKeys.Value, "supply value exceeds the supported accounting limit");
            units += item.Count;
        }
        int remaining = order.Quantity - order.Supplied;
        if (units > remaining || (order.Kind == ProcurementKind.SupplyContract && units != remaining))
            throw RpcErrors.Failed(ErrorCode.ProcurementGoods, ProcurementRefusalKeys.Goods, "supply must contain only the requested resource and exact permitted quantity");
        if (await Deliveries.CountAsync(d => d.OrderId == order.Id) >= MaxDeliveries)
            throw RpcErrors.Failed(ErrorCode.ProcurementLimit, ProcurementRefusalKeys.Limit, "too many open procurements or deliveries");
        var delivery = new ProcurementDelivery { OrderId = order.Id, SupplierPlayerId = player, Quantity = (int)units, Items = r.Items.ToByteArray(), CreatedAt = Now };
        Deliveries.Add(delivery);
        order.Supplied += delivery.Quantity;
        await transfers.StampOutgoing(player, r.GameTick);
        db.Transfers.Add(new Transfer { FromPlayerId = player, ToPlayerId = order.BuyerPlayerId, Kind = "procurement-supply", GameTick = r.GameTick, Summary = $"{units}x {order.DefName} -> procurement {order.Id}", At = Now });
        await db.SaveChangesAsync();
        if (order.Kind != ProcurementKind.CommunityGoal)
        {
            long gross = order.Kind == ProcurementKind.BuyOrder ? checked(order.Reward * units) : order.Reward;
            delivery.Paid = await Pay(order, player, gross);
            parcels.Add(await mailbox.Create(order.BuyerPlayerId, player, ParcelKindDb.Trade, r.Items, null, 0, null, ignoreMailboxLimit: true));
        }
        if (order.Supplied == order.Quantity)
        {
            if (order.Kind == ProcurementKind.CommunityGoal) await CompleteGoal(order, parcels);
            order.Status = ProcurementStatus.ProcurementCompleted;
        }
        return order;
    });

    public async Task<ProcurementEntry> Cancel(long player, ProcurementCancel r) => await Mutate(player, r.ClientRequestId, r, async parcels =>
    {
        var order = await Orders.FirstOrDefaultAsync(o => o.Id == r.ProcurementId) ?? throw Gone();
        if (order.BuyerPlayerId != player) throw Access();
        // Returning escrow stays possible while disabled, frozen, in debt or under attack.
        if (order.Status == ProcurementStatus.ProcurementOpen)
            await Close(order, order.ExpiresAt <= Now ? ProcurementStatus.ProcurementExpired : ProcurementStatus.ProcurementCancelled, parcels);
        return order;
    });

    private async Task<ProcurementEntry> Mutate(long player, string requestId, IMessage request, Func<List<Parcel>, Task<ProcurementOrder>> apply)
    {
        if (requestId.Length is < 1 or > 64 || requestId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw Invalid();
        string intent = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.Descriptor.FullName).Concat(request.ToByteArray()).ToArray()));
        var parcels = new List<Parcel>();
        ProcurementOrder order;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var previous = await db.Set<ProcurementRequest>().AsNoTracking().FirstOrDefaultAsync(x => x.PlayerId == player && x.RequestId == requestId);
            if (previous is not null)
            {
                if (previous.IntentHash != intent) throw RpcErrors.Failed(ErrorCode.ProcurementIntent, ProcurementRefusalKeys.Intent, "request id was already used for a different procurement intent");
                return await ToProto(await Orders.AsNoTracking().FirstAsync(o => o.Id == previous.OrderId));
            }
            order = await apply(parcels);
            db.Set<ProcurementRequest>().Add(new ProcurementRequest { PlayerId = player, RequestId = requestId, IntentHash = intent, OrderId = order.Id });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            wallet.DiscardEvents();
            db.ChangeTracker.Clear();
            throw;
        }
        await Announce(order, parcels);
        return await ToProto(order);
    }

    private async Task CompleteGoal(ProcurementOrder order, List<Parcel> parcels)
    {
        var deliveries = await Deliveries.Where(d => d.OrderId == order.Id).OrderBy(d => d.Id).ToListAsync();
        var suppliers = deliveries.GroupBy(d => d.SupplierPlayerId).OrderBy(g => g.Key).ToList();
        foreach (var supplier in suppliers) await gate.RequireTradePartnerOpen(supplier.Key);
        var rewards = suppliers.ToDictionary(g => g.Key, g => (long)((decimal)order.Reward * g.Sum(d => d.Quantity) / order.Quantity));
        long remainder = order.Reward - rewards.Values.Sum();
        foreach (var supplier in suppliers)
        {
            long gross = rewards[supplier.Key] + (remainder-- > 0 ? 1 : 0);
            long net = await Pay(order, supplier.Key, gross);
            supplier.First().Paid = net;
        }
        foreach (var delivery in deliveries)
        {
            await ChargeHeld(delivery);
            parcels.Add(await mailbox.Create(order.BuyerPlayerId, delivery.SupplierPlayerId, ParcelKindDb.Trade,
                ItemList.Parser.ParseFrom(delivery.Items), null, 0, null, ignoreMailboxLimit: true));
        }
    }

    private async Task<long> Pay(ProcurementOrder order, long supplier, long gross)
    {
        if (gross < 0 || gross > order.Escrow) throw Invalid();
        long fee = (long)((decimal)gross * Math.Clamp(options.Value.MarketFeePercent, 0, 100) / 100m);
        await Money(supplier, gross - fee, "procurement-payment", order.Id);
        if (fee > 0) await Money(WalletService.BankPlayerId, fee, "fee", order.Id);
        order.Escrow -= gross;
        return gross - fee;
    }

    private async Task ChargeHeld(ProcurementDelivery delivery)
    {
        var items = ItemList.Parser.ParseFrom(delivery.Items);
        var perishable = new ItemList(); perishable.Items.AddRange(items.Items.Where(i => i.Perishable));
        long storage = StorageFee.For(ServerWealth.ValueOf(perishable), delivery.CreatedAt, Now, options.Value.StorageFeePercentPerDay, options.Value.StorageFreeDays);
        if (storage == 0) return;
        decimal balance = await wallet.Balance(delivery.SupplierPlayerId);
        if (balance - storage < long.MinValue || (decimal)await wallet.Balance(WalletService.BankPlayerId) + storage > long.MaxValue) throw Invalid();
        await wallet.Charge(delivery.SupplierPlayerId, storage, "storage", delivery.OrderId);
    }

    private async Task Money(long player, long delta, string kind, long id)
    {
        decimal balance = (decimal)await wallet.Balance(player) + delta;
        if (balance > long.MaxValue || balance < long.MinValue) throw Invalid();
        await wallet.Apply(player, delta, kind, id);
    }

    private async Task Close(ProcurementOrder order, ProcurementStatus status, List<Parcel> parcels, bool chargeStorage = true)
    {
        if (order.Kind == ProcurementKind.CommunityGoal)
            foreach (var delivery in await Deliveries.Where(d => d.OrderId == order.Id).OrderBy(d => d.Id).ToListAsync())
            {
                if (chargeStorage) await ChargeHeld(delivery);
                parcels.Add(await mailbox.Create(delivery.SupplierPlayerId, null, ParcelKindDb.TradeReturn,
                    ItemList.Parser.ParseFrom(delivery.Items), null, 0, null, ignoreMailboxLimit: true));
            }
        if (order.Escrow > 0) await Money(order.BuyerPlayerId, order.Escrow, "procurement-refund", order.Id);
        order.Escrow = 0;
        order.Status = status;
    }

    public async Task<bool> CancelAdmin(long id, ProcurementStatus status = ProcurementStatus.ProcurementCancelled)
    {
        var parcels = new List<Parcel>();
        ProcurementOrder? order;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            order = await Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order is null) return false;
            await db.Entry(order).ReloadAsync();
            if (order.Status != ProcurementStatus.ProcurementOpen) return false;
            await Close(order, status, parcels, chargeStorage: status == ProcurementStatus.ProcurementExpired);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch { wallet.DiscardEvents(); db.ChangeTracker.Clear(); throw; }
        await Announce(order, parcels);
        return true;
    }

    public async Task<int> ExpireDue(DateTime now)
    {
        int count = 0;
        foreach (long id in await Orders.AsNoTracking().Where(o => o.Status == ProcurementStatus.ProcurementOpen && o.ExpiresAt <= now).OrderBy(o => o.Id).Select(o => o.Id).ToListAsync())
        {
            try
            {
                if (await CancelAdmin(id, ProcurementStatus.ProcurementExpired)) count++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Procurement {Id} expiry deferred", id);
            }
        }
        return count;
    }

    public async Task CancelAllOf(long? player)
    {
        var orders = Orders.AsNoTracking().Where(o => o.Status == ProcurementStatus.ProcurementOpen);
        if (player is { } p) orders = orders.Where(o => o.BuyerPlayerId == p || o.SupplierPlayerId == p || (o.Kind == ProcurementKind.CommunityGoal && Deliveries.Any(d => d.OrderId == o.Id && d.SupplierPlayerId == p)));
        foreach (long id in await orders.Select(o => o.Id).ToListAsync()) await CancelAdmin(id);
    }

    public async Task<ProcurementList> List(long viewer, ProcurementQuery query, bool admin = false)
    {
        if (query.Page is < 0 or > 100000) throw Invalid();
        var orders = Orders.AsNoTracking();
        if (!admin)
        {
            orders = orders.Where(o => o.SupplierPlayerId == null || o.SupplierPlayerId == viewer || o.BuyerPlayerId == viewer);
            if (query.Mine) orders = orders.Where(o => o.BuyerPlayerId == viewer || o.SupplierPlayerId == viewer || Deliveries.Any(d => d.OrderId == o.Id && d.SupplierPlayerId == viewer));
            else orders = orders.Where(o => o.Status == ProcurementStatus.ProcurementOpen && o.ExpiresAt > Now);
            if (!query.Mine) orders = orders.Where(o => o.BuyerPlayerId == viewer || !db.Players.Any(p => p.Id == o.BuyerPlayerId && p.EconomyFrozenAt != null));
        }
        var result = new ProcurementList { Total = await orders.CountAsync() };
        foreach (var order in await orders.OrderByDescending(o => o.Id).Skip(query.Page * PageSize).Take(PageSize).ToListAsync()) result.Entries.Add(await ToProto(order));
        return result;
    }

    public async Task<ProcurementEntry> ToProto(ProcurementOrder o)
    {
        var deliveries = await Deliveries.AsNoTracking().Where(d => d.OrderId == o.Id).ToListAsync();
        var ids = deliveries.Select(d => d.SupplierPlayerId).Append(o.BuyerPlayerId).Append(o.SupplierPlayerId ?? 0).Distinct().ToArray();
        var names = await db.Players.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        PlayerRef Player(long id) => names.TryGetValue(id, out var p) ? p.ToRef() : new PlayerRef { PlayerId = id };
        var entry = new ProcurementEntry { Id = o.Id, Kind = o.Kind, Buyer = Player(o.BuyerPlayerId), Supplier = o.SupplierPlayerId is { } s ? Player(s) : null,
            DefName = o.DefName, Quantity = o.Quantity, Supplied = o.Supplied, Reward = o.Reward, Escrow = o.Escrow, Status = o.Status,
            ExpiresAt = Timestamp.FromDateTime(DateTime.SpecifyKind(o.ExpiresAt, DateTimeKind.Utc)) };
        entry.Contributions.AddRange(deliveries.GroupBy(d => d.SupplierPlayerId).OrderBy(g => g.Key).Select(g => new ProcurementContribution { Supplier = Player(g.Key), Quantity = g.Sum(d => d.Quantity), Paid = g.Sum(d => d.Paid) }));
        return entry;
    }

    private async Task Announce(ProcurementOrder order, List<Parcel> parcels)
    {
        wallet.FlushEvents();
        foreach (var parcel in parcels) await mailbox.Announce(parcel);
        var players = await Deliveries.AsNoTracking().Where(d => d.OrderId == order.Id).Select(d => d.SupplierPlayerId).ToListAsync();
        foreach (long p in players.Append(order.BuyerPlayerId).Append(order.SupplierPlayerId ?? order.BuyerPlayerId).Distinct())
            events.SendTo(p, new ServerEvent { ProcurementChanged = new ProcurementChanged { ProcurementId = order.Id } });
    }

    private static bool ValidDef(string def) => def.Length is > 0 and <= 128 && def.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>Escrow belongs to its buyer, held goal goods to the contributors, until completion or return.</summary>
    public static async Task<decimal> HeldWealth(RimlinkDbContext db, long player)
    {
        decimal value = await db.Set<ProcurementOrder>().Where(o => o.BuyerPlayerId == player && o.Status == ProcurementStatus.ProcurementOpen).SumAsync(o => (long?)o.Escrow) ?? 0;
        foreach (var delivery in await db.Set<ProcurementDelivery>().AsNoTracking().Where(d => d.SupplierPlayerId == player
            && db.Set<ProcurementOrder>().Any(o => o.Id == d.OrderId && o.Kind == ProcurementKind.CommunityGoal && o.Status == ProcurementStatus.ProcurementOpen)).ToListAsync())
            value += ServerWealth.ValueOf(ItemList.Parser.ParseFrom(delivery.Items));
        return value;
    }
}
