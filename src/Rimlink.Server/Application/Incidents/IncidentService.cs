using System.Data;
using Google.Protobuf.WellKnownTypes;
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

namespace Rimlink.Server.Application.Incidents;

/// <summary>
/// Paid incidents (paid incidents spec §2): a player buys an event against another player's colony; it waits for the target's
/// game, which announces and fires it. The silver goes to the bank; a refunded order gives it back.
/// </summary>
public sealed class IncidentService(RimlinkDbContext db, WalletService wallet, RaidRules rules, RaidGate gate, EconomyGuard guard, ServerWealth wealth,
    EventBus events, IOptions<IncidentOptions> options, ILogger<IncidentService> log)
{
    public static readonly IncidentStateDb[] Waiting = [IncidentStateDb.Queued, IncidentStateDb.Announced];

    public static IncidentKind KindOf(IncidentCatalogEntry entry) => entry.Kind switch
    {
        "Hostile" => IncidentKind.IncidentHostile,
        "Weather" => IncidentKind.IncidentWeather,
        _ => IncidentKind.IncidentKind,
    };

    public IncidentCatalog Catalog()
    {
        var catalog = new IncidentCatalog();
        catalog.Entries.AddRange(options.Value.EffectiveCatalog.Select(e => new CatalogEntry
        {
            Id = e.Id, Label = e.Id, Kind = KindOf(e), IncidentDef = e.IncidentDef, FactionDef = e.FactionDef ?? "", PodsAllowed = e.PodsMultiplier is not null,
        }));
        return catalog;
    }

    public async Task<IncidentQuote> Quote(long buyerId, QuoteRequest r)
    {
        var (entry, level) = Validate(buyerId, r.EntryId, r.Level, r.Pods, r.TargetPlayerId);
        var target = await TargetOf(r.TargetPlayerId);
        return new IncidentQuote { Price = await PriceOf(entry, level, r.Pods, target), Warned = IsWarned(entry, level) };
    }

    public async Task<IncidentOrder> Order(long buyerId, OrderIncidentRequest r)
    {
        var o = options.Value;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        // Replay first: an order the server already took is returned as it is, whatever changed since (balance, queue, rules).
        if (!string.IsNullOrEmpty(r.ClientRequestId) && await db.IncidentOrders.AsNoTracking().FirstOrDefaultAsync(i => i.BuyerPlayerId == buyerId && i.ClientRequestId == r.ClientRequestId) is { } existing)
            return existing;

        if (!o.Enabled) throw RpcErrors.Failed(ErrorCode.EconomyFrozen, RefusalKeys.IncidentsDisabled, "paid incidents are disabled on this server");
        var (entry, level) = Validate(buyerId, r.EntryId, r.Level, r.Pods, r.TargetPlayerId);
        var buyer = await db.Players.AsNoTracking().FirstAsync(p => p.Id == buyerId);
        var target = await TargetOf(r.TargetPlayerId);
        await guard.RequireNoDebt(buyerId);
        var now = DateTime.UtcNow;
        var kind = KindOf(entry);
        if (kind == IncidentKind.IncidentKind)
        {
            var since = now - TimeSpan.FromHours(o.KindCooldownHours);
            int gift = (int)IncidentKind.IncidentKind;
            if (await db.IncidentOrders.AnyAsync(i => i.BuyerPlayerId == buyerId && i.TargetPlayerId == target.Id && i.Kind == gift && i.CreatedAt >= since
                    && i.State != IncidentStateDb.Failed && i.State != IncidentStateDb.Expired && i.State != IncidentStateDb.Cancelled))
                throw RpcErrors.Failed(ErrorCode.RaidCooldown, RefusalKeys.IncidentsKindCooldown, $"you sent {target.Name} an event less than {o.KindCooldownHours} hours ago", target.Name, o.KindCooldownHours);
        }
        else
        {
            gate.RequireOpen();
            if (await rules.CheckTarget(buyer, target, now) is { } refusal) throw RpcErrors.Failed(refusal.Code, refusal.Key, refusal.Message, refusal.Args);
        }
        int waiting = await db.IncidentOrders.CountAsync(i => i.TargetPlayerId == target.Id && Waiting.Contains(i.State));
        if (waiting >= o.MaxQueuedPerTarget) throw RpcErrors.Failed(ErrorCode.IncidentQueueFull, RefusalKeys.IncidentsQueueFull, $"{target.Name} already has {waiting} events waiting", target.Name, waiting);

        var order = new IncidentOrder
        {
            BuyerPlayerId = buyerId, TargetPlayerId = target.Id, EntryId = entry.Id, IncidentDef = entry.IncidentDef, FactionDef = entry.FactionDef,
            Kind = (int)kind, Level = level, Pods = r.Pods, Price = await PriceOf(entry, level, r.Pods, target), State = IncidentStateDb.Queued,
            CreatedAt = now, ExpiresAt = now.AddDays(o.ExpireRealDays), ClientRequestId = string.IsNullOrEmpty(r.ClientRequestId) ? null : r.ClientRequestId,
        };
        db.IncidentOrders.Add(order);
        await db.SaveChangesAsync();
        await wallet.Apply(buyerId, -order.Price, "incident", order.Id);
        await wallet.Apply(WalletService.BankPlayerId, order.Price, "incident", order.Id);
        await tx.CommitAsync();
        wallet.FlushEvents();
        log.LogInformation("Player {Buyer} ordered incident {Incident} ({Entry} level {Level}) at player {Target} for {Price}", buyerId, order.Id, entry.Id, level, target.Id, order.Price);
        events.SendTo(target.Id, new ServerEvent { IncidentQueued = new IncidentQueued { Incident = await ToProto(order) } });
        return order;
    }

    /// <summary>The target's game saw the order: it fires now, or half a day later for a big hostile one.</summary>
    public async Task<IncidentOrder> Announce(long targetId, AnnounceIncident r)
    {
        var order = await db.IncidentOrders.FirstOrDefaultAsync(i => i.Id == r.IncidentId && i.TargetPlayerId == targetId)
            ?? throw RpcErrors.With(global::Grpc.Core.StatusCode.NotFound, ErrorCode.Unspecified, RefusalKeys.IncidentsNoSuchEvent, "no such event for you");
        if (order.State == IncidentStateDb.Announced) return order;
        if (order.State != IncidentStateDb.Queued) throw RpcErrors.Failed(ErrorCode.RaidGone, RefusalKeys.IncidentsGone, "that event is no longer waiting");
        var o = options.Value;
        bool warned = order.Kind == (int)IncidentKind.IncidentHostile && order.Level >= o.WarnFromLevel;
        order.FireTick = r.GameTick + (warned ? o.WarnTicks : 0);
        order.State = IncidentStateDb.Announced;
        order.ExpiresAt = DateTime.UtcNow.AddDays(o.ExpireRealDays);
        await db.SaveChangesAsync();
        return order;
    }

    /// <summary>The target's game ran the order (or could not: refunded). A report for a finished order changes nothing.</summary>
    public async Task Report(long targetId, IncidentReport r)
    {
        var order = await db.IncidentOrders.FirstOrDefaultAsync(i => i.Id == r.IncidentId && i.TargetPlayerId == targetId)
            ?? throw RpcErrors.With(global::Grpc.Core.StatusCode.NotFound, ErrorCode.Unspecified, RefusalKeys.IncidentsNoSuchEvent, "no such event for you");
        if (!Waiting.Contains(order.State)) return;
        if (r.Fired) await Close(order, IncidentStateDb.Fired, null, refund: false);
        else await Close(order, IncidentStateDb.Failed, string.IsNullOrWhiteSpace(r.Reason) ? "the event could not run" : r.Reason, refund: true);
    }

    /// <summary>Orders the target did not announce or fire in time go back to their buyers.</summary>
    public async Task<int> ExpireDue(DateTime now)
    {
        int n = 0;
        foreach (long id in await db.IncidentOrders.AsNoTracking().Where(i => Waiting.Contains(i.State) && i.ExpiresAt <= now).Select(i => i.Id).ToListAsync())
        {
            var order = await db.IncidentOrders.FirstAsync(i => i.Id == id);
            if (await Close(order, IncidentStateDb.Expired, "the target did not play in time", refund: true)) n++;
        }
        return n;
    }

    public async Task<bool> Cancel(long id, string reason)
    {
        var order = await db.IncidentOrders.FirstOrDefaultAsync(i => i.Id == id);
        return order is not null && await Close(order, IncidentStateDb.Cancelled, reason, refund: true);
    }

    /// <summary>The player's colony is gone (reset, world recreated): orders by or at them are cancelled and refunded.</summary>
    public async Task CancelAllOf(long playerId, string reason)
    {
        foreach (long id in await db.IncidentOrders.AsNoTracking().Where(i => (i.TargetPlayerId == playerId || i.BuyerPlayerId == playerId) && Waiting.Contains(i.State)).Select(i => i.Id).ToListAsync())
            await Cancel(id, reason);
    }

    /// <summary>False when the order finished between the caller's read and this transaction.</summary>
    private async Task<bool> Close(IncidentOrder order, IncidentStateDb state, string? reason, bool refund)
    {
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            await db.Entry(order).ReloadAsync();
            if (!Waiting.Contains(order.State)) return false;
            order.State = state;
            order.Reason = reason;
            order.ResolvedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            if (refund)
            {
                await wallet.Apply(WalletService.BankPlayerId, -order.Price, "incident-refund", order.Id);
                await wallet.Apply(order.BuyerPlayerId, order.Price, "incident-refund", order.Id);
            }
            await tx.CommitAsync();
        }
        wallet.FlushEvents();
        log.LogInformation("Incident {Incident} {State}{Reason}", order.Id, state, reason is null ? "" : ": " + reason);
        var proto = await ToProto(order);
        events.SendTo(order.BuyerPlayerId, new ServerEvent { IncidentResolved = new IncidentResolved { Incident = proto, Refund = refund ? order.Price : 0 } });
        // The target reported Fired and Failed itself; a cancelled or expired one must leave its queue (and its outflow block).
        if (state is IncidentStateDb.Cancelled or IncidentStateDb.Expired)
            events.SendTo(order.TargetPlayerId, new ServerEvent { IncidentResolved = new IncidentResolved { Incident = proto } });
        return true;
    }

    public Task<List<IncidentOrder>> Pending(long targetId) =>
        db.IncidentOrders.AsNoTracking().Where(i => i.TargetPlayerId == targetId && Waiting.Contains(i.State)).OrderBy(i => i.Id).ToListAsync();

    public Task<List<IncidentOrder>> Mine(long buyerId) =>
        db.IncidentOrders.AsNoTracking().Where(i => i.BuyerPlayerId == buyerId).OrderByDescending(i => i.Id).Take(50).ToListAsync();

    public async Task<Incident> ToProto(IncidentOrder o)
    {
        var players = await db.Players.AsNoTracking().Where(p => p.Id == o.BuyerPlayerId || p.Id == o.TargetPlayerId).ToDictionaryAsync(p => p.Id);
        return new Incident
        {
            Id = o.Id,
            Buyer = players.TryGetValue(o.BuyerPlayerId, out var buyer) ? buyer.ToRef() : new PlayerRef { PlayerId = o.BuyerPlayerId },
            Target = players.TryGetValue(o.TargetPlayerId, out var target) ? target.ToRef() : new PlayerRef { PlayerId = o.TargetPlayerId },
            EntryId = o.EntryId,
            IncidentDef = o.IncidentDef,
            FactionDef = o.FactionDef ?? "",
            Kind = (IncidentKind)o.Kind,
            Level = o.Level,
            Pods = o.Pods,
            Price = o.Price,
            State = (IncidentState)(int)o.State,
            FireTick = o.FireTick ?? 0,
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(o.CreatedAt, DateTimeKind.Utc)),
            Reason = o.Reason ?? "",
        };
    }

    private (IncidentCatalogEntry Entry, int Level) Validate(long buyerId, string entryId, int level, bool pods, long targetId)
    {
        var entry = options.Value.EffectiveCatalog.FirstOrDefault(e => e.Id == entryId)
            ?? throw RpcErrors.Failed(ErrorCode.Unspecified, RefusalKeys.IncidentsUnknownEntry, $"no event '{entryId}' on this server", entryId);
        if (targetId == buyerId) throw RpcErrors.Failed(ErrorCode.SelfTrade, RefusalKeys.IncidentsSelf, "you cannot order an event against yourself");
        if (pods && entry.PodsMultiplier is null) throw RpcErrors.Failed(ErrorCode.Unspecified, RefusalKeys.IncidentsNoPods, $"'{entry.Id}' cannot come by drop pods", entry.Id);
        if (KindOf(entry) != IncidentKind.IncidentHostile) return (entry, 0);
        if (level is < 1 or > 4) throw RpcErrors.Failed(ErrorCode.Unspecified, RefusalKeys.IncidentsLevel, "level is 1 to 4");
        return (entry, level);
    }

    private async Task<Player> TargetOf(long targetId) =>
        await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == targetId)
        ?? throw RpcErrors.With(global::Grpc.Core.StatusCode.NotFound, ErrorCode.RecipientUnknown, RefusalKeys.NoSuchPlayer, "no such player");

    private async Task<long> PriceOf(IncidentCatalogEntry entry, int level, bool pods, Player target) =>
        IncidentPrice.For(entry, Math.Max(1, level), pods, (decimal)target.WealthTotal + await wealth.Of(target.Id), options.Value);

    private bool IsWarned(IncidentCatalogEntry entry, int level) =>
        KindOf(entry) == IncidentKind.IncidentHostile && level >= options.Value.WarnFromLevel;
}
