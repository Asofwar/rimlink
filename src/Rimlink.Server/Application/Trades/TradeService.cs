using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Social;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;
using Trade = Rimlink.Server.Storage.Entities.Trade;
using Transfer = Rimlink.Server.Storage.Entities.Transfer;

namespace Rimlink.Server.Application.Trades;

/// <summary>
/// Two-way trades on server escrow (safe trades spec §4). A side's goods leave its owner's game when offered and sit in the
/// trade row; every change bumps the revision and unconfirms the other side; once both sides confirmed the same revision,
/// each party gets the other side as a <c>Trade</c> parcel. A close of any kind sends each side back as a <c>TradeReturn</c>
/// parcel and its silver to the wallet. Every mutation is one serializable transaction; events go out after the commit.
/// </summary>
public sealed class TradeService(
    RimlinkDbContext db, WalletService wallet, MailboxService mailbox, TransferService transfers, ParcelValidator validator, EconomyGate gate,
    EventBus events, IOptions<EconomyOptions> options, ILogger<TradeService> log)
{
    public const int MaxNoteLength = 200;
    private static readonly TimeSpan ListClosedFor = TimeSpan.FromDays(7);

    public async Task<Protocol.V1.Trade> Open(long playerId, TradeOpenRequest r)
    {
        string? requestId = string.IsNullOrEmpty(r.ClientRequestId) ? null : r.ClientRequestId;
        // A repeat first: the things already left the client's game, a refusal now would put them back a second time.
        if (requestId is not null && await db.Trades.AsNoTracking().FirstOrDefaultAsync(t => t.ClientRequestId == requestId) is { } done)
            return await ToProto(done);
        gate.RequireOpen();
        if (r.PartnerId == playerId) throw RpcErrors.Failed(ErrorCode.SelfTrade, RefusalKeys.TradesSelf, "you cannot trade with yourself");
        string note = CleanNote(r.Note);
        CheckGoods(r.Items, r.Pawns, r.Silver);

        Trade trade;
        await using (var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
        {
            if (requestId is not null && await db.Trades.AsNoTracking().FirstOrDefaultAsync(t => t.ClientRequestId == requestId) is { } raced)
                return await ToProto(raced);
            var partner = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == r.PartnerId)
                ?? throw RpcErrors.With(StatusCode.NotFound, ErrorCode.RecipientUnknown, RefusalKeys.EconomyRecipientUnknown, $"no player with id {r.PartnerId}", r.PartnerId);
            await BlockRules.RequireNotBlocked(db, playerId, partner.Id);
            if (partner.TradesClosed) throw RpcErrors.Failed(ErrorCode.TradesClosed, RefusalKeys.TradesClosed, $"{partner.Name} does not accept trade offers", partner.Name);
            int max = options.Value.MaxOpenTrades;
            if (await OpenCount(playerId) >= max) throw RpcErrors.Failed(ErrorCode.TradeLimit, RefusalKeys.TradesLimitOwn, $"you already have {max} open trades", max);
            if (await OpenCount(partner.Id) >= max) throw RpcErrors.Failed(ErrorCode.TradeLimit, RefusalKeys.TradesLimitPartner, $"{partner.Name} already has {max} open trades", partner.Name, max);

            var now = DateTime.UtcNow;
            trade = new Trade
            {
                InitiatorPlayerId = playerId,
                PartnerPlayerId = partner.Id,
                Status = TradeStatusDb.Open,
                Revision = 1,
                CreatedAt = now,
                ExpiresAt = now + TimeSpan.FromDays(options.Value.TradeLifetimeDays),
                ClientRequestId = requestId,
                InitiatorItems = (r.Items ?? new ItemList()).ToByteArray(),
                InitiatorPawns = (r.Pawns ?? new PawnList()).ToByteArray(),
                InitiatorSilver = r.Silver,
                InitiatorNote = note,
                // Not confirmed: an offer with nothing asked yet ("what will you give?") must not be taken by a bare Confirm.
                InitiatorConfirmed = false,
                PartnerItems = new ItemList().ToByteArray(),
                PartnerPawns = new PawnList().ToByteArray(),
            };
            db.Trades.Add(trade);
            await db.SaveChangesAsync();
            if (r.Silver > 0) await wallet.Apply(playerId, -r.Silver, "trade", trade.Id);
            if (HasGoods(r.Items, r.Pawns)) await transfers.StampOutgoing(playerId, r.GameTick);
            db.Transfers.Add(new Transfer { FromPlayerId = playerId, ToPlayerId = partner.Id, Kind = "trade-open", GameTick = r.GameTick, Summary = $"trade {trade.Id}: {TransferService.Summary(r.Items, r.Pawns, r.Silver)}", At = now });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        wallet.FlushEvents();
        TradeMetrics.Outcomes.WithLabels("opened").Inc();
        log.LogInformation("Player {From} opened trade {Trade} with player {To}", playerId, trade.Id, trade.PartnerPlayerId);
        return await Publish(trade);
    }

    public async Task<Protocol.V1.Trade> SetSide(long playerId, TradeSideRequest r)
    {
        string? requestId = string.IsNullOrEmpty(r.ClientRequestId) ? null : r.ClientRequestId;
        // A repeat first, whatever became of the trade since: it was applied, and its goods are accounted for.
        if (requestId is not null && await db.Transfers.AsNoTracking().AnyAsync(t => t.ClientRequestId == requestId))
            return await ToProto(await Party(playerId, r.TradeId));
        gate.RequireOpen();
        string note = CleanNote(r.Note);
        CheckGoods(r.Items, r.Pawns, r.Silver);

        Trade trade;
        Parcel? back = null;
        await using (var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
        {
            if (requestId is not null && await db.Transfers.AsNoTracking().AnyAsync(t => t.ClientRequestId == requestId))
                return await ToProto(await Party(playerId, r.TradeId));
            trade = await Party(playerId, r.TradeId, tracked: true);
            if (trade.Status != TradeStatusDb.Open) throw Gone();
            var side = new Side(trade, trade.InitiatorPlayerId == playerId);
            var other = new Side(trade, trade.InitiatorPlayerId != playerId);

            var items = r.Items ?? new ItemList();
            var pawns = r.Pawns ?? new PawnList();
            var oldItems = ItemList.Parser.ParseFrom(side.Items);
            var oldPawns = PawnList.Parser.ParseFrom(side.Pawns);
            if (r.ReturnGoods)
            {
                if (HasGoods(oldItems, oldPawns))
                    back = await mailbox.Create(playerId, null, ParcelKindDb.TradeReturn, oldItems, oldPawns, 0, null, ignoreMailboxLimit: true);
            }
            else
            {
                oldItems.Items.AddRange(items.Items);
                oldPawns.Pawns.AddRange(pawns.Pawns);
                items = oldItems;
                pawns = oldPawns;
                if (validator.Validate(items, pawns) is { } reason) throw RpcErrors.Failed(reason.Code, reason.Key, reason.Message, reason.Args);
            }

            long delta = r.Silver - side.Silver;
            if (delta > 0) await wallet.Apply(playerId, -delta, "trade", trade.Id);
            else if (delta < 0) await wallet.Apply(playerId, -delta, "refund", trade.Id);
            side.Items = items.ToByteArray();
            side.Pawns = pawns.ToByteArray();
            side.Silver = r.Silver;
            side.Note = note;
            // Confirmed only when made on the current revision: a change made on an older view never agrees to an unseen other side.
            side.Confirmed = r.BaseRevision == trade.Revision;
            other.Confirmed = false;
            trade.Revision++;
            if (HasGoods(r.Items, r.Pawns)) await transfers.StampOutgoing(playerId, r.GameTick);
            db.Transfers.Add(new Transfer
            {
                FromPlayerId = playerId,
                ToPlayerId = other.PlayerId,
                Kind = "trade-side",
                GameTick = r.GameTick,
                Summary = $"trade {trade.Id} r{trade.Revision}: {TransferService.Summary(items, pawns, r.Silver)}",
                At = DateTime.UtcNow,
                ClientRequestId = requestId,
            });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        wallet.FlushEvents();
        if (back is not null) await mailbox.Announce(back);
        return await Publish(trade);
    }

    public async Task<Protocol.V1.Trade> Confirm(long playerId, long tradeId, int revision)
    {
        Trade trade;
        var parcels = new List<Parcel>();
        await using (var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
        {
            trade = await Party(playerId, tradeId, tracked: true);
            if (trade.Status == TradeStatusDb.Completed) return await ToProto(trade); // a second press after the exchange
            if (trade.Status != TradeStatusDb.Open) throw Gone();
            gate.RequireOpen();
            if (revision != trade.Revision) throw RpcErrors.Failed(ErrorCode.TradeChanged, RefusalKeys.TradesChanged, "the trade changed; look at it again");
            var side = new Side(trade, trade.InitiatorPlayerId == playerId);
            var other = new Side(trade, trade.InitiatorPlayerId != playerId);
            if (side.Confirmed) return await ToProto(trade);
            side.Confirmed = true;

            if (other.Confirmed)
            {
                // The exchange: each party gets the other side, from the other party.
                foreach (var (from, to) in new[] { (side, other), (other, side) })
                {
                    var items = ItemList.Parser.ParseFrom(from.Items);
                    var pawns = PawnList.Parser.ParseFrom(from.Pawns);
                    if (HasGoods(items, pawns) || from.Silver > 0)
                        parcels.Add(await mailbox.Create(to.PlayerId, from.PlayerId, ParcelKindDb.Trade, items, pawns, from.Silver, null, ignoreMailboxLimit: true));
                    db.Transfers.Add(new Transfer { FromPlayerId = from.PlayerId, ToPlayerId = to.PlayerId, Kind = "trade", GameTick = 0, Summary = $"trade {trade.Id}: {TransferService.Summary(items, pawns, from.Silver)}", At = DateTime.UtcNow });
                }
                trade.Status = TradeStatusDb.Completed;
                trade.ClosedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        foreach (var parcel in parcels) await mailbox.Announce(parcel);
        if (trade.Status == TradeStatusDb.Completed)
        {
            TradeMetrics.Outcomes.WithLabels("completed").Inc();
            log.LogInformation("Trade {Trade} between players {A} and {B} completed", trade.Id, trade.InitiatorPlayerId, trade.PartnerPlayerId);
        }
        return await Publish(trade);
    }

    public async Task<Protocol.V1.Trade> Cancel(long playerId, long tradeId)
    {
        var trade = await Party(playerId, tradeId);
        if (trade.Status == TradeStatusDb.Open)
        {
            string name = await db.Players.AsNoTracking().Where(p => p.Id == playerId).Select(p => p.Name).FirstAsync();
            await Close(tradeId, TradeStatusDb.Cancelled, $"cancelled by {name}");
            trade = await db.Trades.AsNoTracking().FirstAsync(t => t.Id == tradeId);
        }
        return await ToProto(trade);
    }

    public async Task<TradeList> List(long playerId)
    {
        var since = DateTime.UtcNow - ListClosedFor;
        var rows = await db.Trades.AsNoTracking()
            .Where(t => (t.InitiatorPlayerId == playerId || t.PartnerPlayerId == playerId) && (t.Status == TradeStatusDb.Open || t.ClosedAt >= since))
            .OrderByDescending(t => t.Id).ToListAsync();
        var list = new TradeList();
        foreach (var t in rows) list.Trades.Add(await ToProto(t));
        return list;
    }

    /// <summary>Closes every open trade between the two players (a block); returns how many.</summary>
    public async Task<int> CancelBetween(long a, long b, string reason) =>
        await CloseAll(db.Trades.Where(t => t.Status == TradeStatusDb.Open
            && ((t.InitiatorPlayerId == a && t.PartnerPlayerId == b) || (t.InitiatorPlayerId == b && t.PartnerPlayerId == a))), TradeStatusDb.Cancelled, reason);

    /// <summary>Closes every open trade of the player (a colony reset); returns how many.</summary>
    public async Task<int> CancelAllOf(long playerId, string reason) =>
        await CloseAll(db.Trades.Where(t => t.Status == TradeStatusDb.Open && (t.InitiatorPlayerId == playerId || t.PartnerPlayerId == playerId)), TradeStatusDb.Cancelled, reason);

    public Task<bool> AdminCancel(long tradeId, string reason) => Close(tradeId, TradeStatusDb.Cancelled, reason);

    public async Task<int> ExpireDue(DateTime now) =>
        await CloseAll(db.Trades.Where(t => t.Status == TradeStatusDb.Open && t.ExpiresAt < now), TradeStatusDb.Expired, "expired");

    /// <summary>For admins: the player's trades (or all), newest first, at most 200.</summary>
    public async Task<List<Trade>> ListFor(long? playerId) =>
        await db.Trades.AsNoTracking()
            .Where(t => playerId == null || t.InitiatorPlayerId == playerId || t.PartnerPlayerId == playerId)
            .OrderByDescending(t => t.Id).Take(200).ToListAsync();

    private async Task<int> CloseAll(IQueryable<Trade> query, TradeStatusDb status, string reason)
    {
        int n = 0;
        foreach (long id in await query.Select(t => t.Id).ToListAsync())
            if (await Close(id, status, reason)) n++;
        return n;
    }

    /// <summary>Sends each side's goods back by parcel and its silver to the wallet; false when the trade was not open anymore.</summary>
    private async Task<bool> Close(long tradeId, TradeStatusDb status, string reason)
    {
        Trade? trade;
        var parcels = new List<Parcel>();
        await using (var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
        {
            trade = await db.Trades.FirstOrDefaultAsync(t => t.Id == tradeId && t.Status == TradeStatusDb.Open);
            if (trade is null) return false;
            foreach (var side in new[] { new Side(trade, true), new Side(trade, false) })
            {
                var items = ItemList.Parser.ParseFrom(side.Items);
                var pawns = PawnList.Parser.ParseFrom(side.Pawns);
                if (HasGoods(items, pawns))
                    parcels.Add(await mailbox.Create(side.PlayerId, null, ParcelKindDb.TradeReturn, items, pawns, 0, null, ignoreMailboxLimit: true));
                if (side.Silver > 0) await wallet.Apply(side.PlayerId, side.Silver, "refund", trade.Id);
                side.Confirmed = false;
                db.Transfers.Add(new Transfer { FromPlayerId = side.PlayerId, ToPlayerId = side.PlayerId, Kind = "trade-return", GameTick = 0, Summary = $"trade {trade.Id}: {TransferService.Summary(items, pawns, side.Silver)}", At = DateTime.UtcNow });
            }
            trade.Status = status;
            trade.ClosedAt = DateTime.UtcNow;
            trade.CloseReason = reason;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        wallet.FlushEvents();
        foreach (var parcel in parcels) await mailbox.Announce(parcel);
        TradeMetrics.Outcomes.WithLabels(status == TradeStatusDb.Expired ? "expired" : "cancelled").Inc();
        log.LogInformation("Trade {Trade} closed ({Status}): {Reason}", trade.Id, status, reason);
        await Publish(trade);
        return true;
    }

    private Task<int> OpenCount(long playerId) =>
        db.Trades.CountAsync(t => t.Status == TradeStatusDb.Open && (t.InitiatorPlayerId == playerId || t.PartnerPlayerId == playerId));

    /// <summary>The trade, if the player is one of its parties; otherwise TRADE_GONE (a stranger learns nothing about it).</summary>
    private async Task<Trade> Party(long playerId, long tradeId, bool tracked = false)
    {
        var query = tracked ? db.Trades : db.Trades.AsNoTracking();
        return await query.FirstOrDefaultAsync(t => t.Id == tradeId && (t.InitiatorPlayerId == playerId || t.PartnerPlayerId == playerId))
            ?? throw Gone();
    }

    private static RpcException Gone() => RpcErrors.Failed(ErrorCode.TradeGone, RefusalKeys.TradesGone, "no such open trade");

    private void CheckGoods(ItemList? items, PawnList? pawns, long silver)
    {
        if (silver < 0) throw RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.Unspecified, RefusalKeys.TradesNegativeSilver, "silver must not be negative");
        if (validator.Validate(items, pawns) is { } reason) throw RpcErrors.Failed(reason.Code, reason.Key, reason.Message, reason.Args);
    }

    private static bool HasGoods(ItemList? items, PawnList? pawns) => (items?.Items.Count ?? 0) > 0 || (pawns?.Pawns.Count ?? 0) > 0;

    private static string CleanNote(string? note)
    {
        note = (note ?? "").Trim();
        if (note.Length > MaxNoteLength) throw RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.Unspecified, RefusalKeys.TradesNoteTooLong, $"note longer than {MaxNoteLength} characters", MaxNoteLength);
        if (note.Any(c => char.IsControl(c) && c != '\n')) throw RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.Unspecified, RefusalKeys.TradesNoteControlChars, "note contains control characters");
        return note;
    }

    /// <summary>Sends the trade to both parties (descriptors only) and returns it.</summary>
    private async Task<Protocol.V1.Trade> Publish(Trade trade)
    {
        var proto = await ToProto(trade);
        events.SendTo(trade.InitiatorPlayerId, new ServerEvent { TradeUpdated = new TradeUpdated { Trade = proto } });
        events.SendTo(trade.PartnerPlayerId, new ServerEvent { TradeUpdated = new TradeUpdated { Trade = proto.Clone() } });
        return proto;
    }

    public async Task<Protocol.V1.Trade> ToProto(Trade t)
    {
        var players = await db.Players.AsNoTracking().Where(p => p.Id == t.InitiatorPlayerId || p.Id == t.PartnerPlayerId).ToDictionaryAsync(p => p.Id);
        var proto = new Protocol.V1.Trade
        {
            Id = t.Id,
            Status = (TradeStatus)t.Status,
            Revision = t.Revision,
            InitiatorSide = SideProto(new Side(t, true)),
            PartnerSide = SideProto(new Side(t, false)),
            CreatedAt = Stamp(t.CreatedAt),
            ExpiresAt = Stamp(t.ExpiresAt),
            CloseReason = t.CloseReason ?? "",
        };
        if (t.ClosedAt is { } closed) proto.ClosedAt = Stamp(closed);
        if (players.TryGetValue(t.InitiatorPlayerId, out var initiator)) proto.Initiator = initiator.ToRef();
        if (players.TryGetValue(t.PartnerPlayerId, out var partner)) proto.Partner = partner.ToRef();
        return proto;
    }

    private static TradeSide SideProto(Side s) => new()
    {
        Items = MailboxService.StripBlobs(ItemList.Parser.ParseFrom(s.Items), includeBlobs: false),
        Pawns = MailboxService.StripBlobs(PawnList.Parser.ParseFrom(s.Pawns), includeBlobs: false),
        Silver = s.Silver,
        Note = s.Note,
        Confirmed = s.Confirmed,
    };

    private static Timestamp Stamp(DateTime at) => Timestamp.FromDateTime(DateTime.SpecifyKind(at, DateTimeKind.Utc));

    /// <summary>One side of a trade row, by role.</summary>
    private sealed class Side(Trade t, bool initiator)
    {
        public long PlayerId => initiator ? t.InitiatorPlayerId : t.PartnerPlayerId;
        public byte[] Items { get => initiator ? t.InitiatorItems : t.PartnerItems; set { if (initiator) t.InitiatorItems = value; else t.PartnerItems = value; } }
        public byte[] Pawns { get => initiator ? t.InitiatorPawns : t.PartnerPawns; set { if (initiator) t.InitiatorPawns = value; else t.PartnerPawns = value; } }
        public long Silver { get => initiator ? t.InitiatorSilver : t.PartnerSilver; set { if (initiator) t.InitiatorSilver = value; else t.PartnerSilver = value; } }
        public string Note { get => initiator ? t.InitiatorNote : t.PartnerNote; set { if (initiator) t.InitiatorNote = value; else t.PartnerNote = value; } }
        public bool Confirmed { get => initiator ? t.InitiatorConfirmed : t.PartnerConfirmed; set { if (initiator) t.InitiatorConfirmed = value; else t.PartnerConfirmed = value; } }
    }
}

public static class TradeMetrics
{
    public static readonly Prometheus.Counter Outcomes = Prometheus.Metrics.CreateCounter("rimlink_player_trades_total", "Two-way trades by outcome", "outcome");
}
