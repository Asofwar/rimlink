using System.Data;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Lot = Rimlink.Server.Storage.Entities.Lot;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;
using Transfer = Rimlink.Server.Storage.Entities.Transfer;
using Rimlink.Server.Application.Feed;

namespace Rimlink.Server.Application.Economy;

/// <summary>The board: a lot holds the seller's items in escrow until bought, withdrawn or expired.</summary>
public sealed class MarketService(RimlinkDbContext db, WalletService wallet, MailboxService mailbox, ParcelValidator validator, EconomyGate gate,
    TransferService transfers, EventBus events, AdminFeed feed, IOptions<EconomyOptions> options, ILogger<MarketService> log)
{
    public const int PageSize = 50;

    public async Task<Lot> Post(long sellerId, PostRequest request)
    {
        gate.RequireOpen();
        bool hasItems = request.Items is { Items.Count: > 0 }, hasPawn = request.Pawn is { KindDef.Length: > 0 };
        if (hasItems == hasPawn) throw RpcErrors.Failed(ErrorCode.Unspecified, RefusalKeys.EconomyLotContents, "a lot holds either items or one pawn");
        if (request.Price < 1) throw RpcErrors.Failed(ErrorCode.Unspecified, RefusalKeys.EconomyPriceTooLow, "price must be at least 1 silver");
        var pawns = hasPawn ? new PawnList { Pawns = { request.Pawn } } : null;
        if (validator.Validate(hasItems ? request.Items : null, pawns) is { } reason) throw RpcErrors.Failed(reason.Code, reason.Key, reason.Message, reason.Args);

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        if (!string.IsNullOrEmpty(request.ClientRequestId))
        {
            var existing = await db.Lots.AsNoTracking().FirstOrDefaultAsync(l => l.ClientRequestId == request.ClientRequestId);
            if (existing is not null) return existing;
        }
        await gate.RequireNotFrozen(sellerId);
        int open = await db.Lots.CountAsync(l => l.SellerPlayerId == sellerId && l.Status == LotStatusDb.Open);
        if (open >= options.Value.MaxLotsPerPlayer)
            throw RpcErrors.Failed(ErrorCode.Unspecified, RefusalKeys.EconomyLotLimit, $"you already have {open} open lots (limit {options.Value.MaxLotsPerPlayer})", open, options.Value.MaxLotsPerPlayer);

        var now = DateTime.UtcNow;
        var lot = new Lot
        {
            SellerPlayerId = sellerId,
            Items = hasItems ? request.Items.ToByteArray() : new ItemList().ToByteArray(),
            Pawn = hasPawn ? request.Pawn.ToByteArray() : null,
            Price = request.Price,
            CreatedAt = now,
            ExpiresAt = now.AddDays(options.Value.LotLifetimeDays),
            Status = LotStatusDb.Open,
            ClientRequestId = string.IsNullOrEmpty(request.ClientRequestId) ? null : request.ClientRequestId,
        };
        db.Lots.Add(lot);
        await transfers.StampOutgoing(sellerId, request.GameTick);
        db.Transfers.Add(new Transfer { FromPlayerId = sellerId, Kind = "post", GameTick = request.GameTick, Summary = TransferService.Summary(hasItems ? request.Items : null, pawns, 0) + $" @ {request.Price}", At = now });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        EconomyMetrics.Trade("post");
        string sellerName = await db.Players.AsNoTracking().Where(p => p.Id == sellerId).Select(p => p.Name).FirstAsync();
        feed.Publish(FeedTypes.LotPosted, new LotData(lot.Id, new FeedPlayer(sellerId, sellerName), null, lot.Price, LotSummary.Of(hasItems ? request.Items : null, hasPawn ? request.Pawn : null)));
        return lot;
    }

    /// <summary>Open lots; a frozen seller's lots are not for sale and only that seller sees them.</summary>
    public async Task<LotList> List(MarketQuery query, long viewerId)
    {
        var lots = db.Lots.AsNoTracking().Where(l => l.Status == LotStatusDb.Open);
        if (query.SellerPlayerId != 0) lots = lots.Where(l => l.SellerPlayerId == query.SellerPlayerId);
        lots = lots.Where(l => l.SellerPlayerId == viewerId || !db.Players.Any(p => p.Id == l.SellerPlayerId && p.EconomyFrozenAt != null));
        var all = await lots.OrderByDescending(l => l.Id).ToListAsync();
        var sellers = await db.Players.AsNoTracking().Where(p => all.Select(l => l.SellerPlayerId).Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        var protos = all.Select(l => ToProto(l, sellers.GetValueOrDefault(l.SellerPlayerId), includeBlobs: false));
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            string text = query.Text.Trim();
            protos = protos.Where(p =>
                p.Items.Items.Any(i => i.Label.Contains(text, StringComparison.OrdinalIgnoreCase) || i.DefName.Contains(text, StringComparison.OrdinalIgnoreCase))
                || (p.Pawn is not null && (p.Pawn.Label.Contains(text, StringComparison.OrdinalIgnoreCase) || p.Pawn.KindDef.Contains(text, StringComparison.OrdinalIgnoreCase)))
                || (p.Seller is not null && p.Seller.Name.Contains(text, StringComparison.OrdinalIgnoreCase)));
        }
        var filtered = protos.ToList();
        var result = new LotList { Total = filtered.Count };
        result.Lots.AddRange(filtered.Skip(Math.Max(0, query.Page) * PageSize).Take(PageSize));
        return result;
    }

    public async Task<Parcel> Withdraw(long sellerId, long lotId, bool bypassGate = false)
    {
        if (!bypassGate) gate.RequireOpen();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var lot = await db.Lots.FirstOrDefaultAsync(l => l.Id == lotId && l.SellerPlayerId == sellerId && l.Status == LotStatusDb.Open)
            ?? throw RpcErrors.Failed(ErrorCode.LotGone, RefusalKeys.EconomyLotClosed, "this lot is no longer open");
        var parcel = await Close(lot, LotStatusDb.Withdrawn, ParcelKindDb.LotReturn, "withdraw");
        await tx.CommitAsync();
        await mailbox.Announce(parcel);
        return parcel;
    }

    public async Task<Parcel> Buy(long buyerId, long lotId)
    {
        gate.RequireOpen();
        await gate.RequireNotFrozen(buyerId); // no replay key: a repeated buy of a sold lot is refused either way
        Parcel parcel;
        LotSold sold;
        long sellerId;
        string lotSummary;
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            var lot = await db.Lots.FirstOrDefaultAsync(l => l.Id == lotId) ?? throw RpcErrors.Failed(ErrorCode.LotGone, RefusalKeys.EconomyLotUnknown, "no such lot");
            if (lot.SellerPlayerId == buyerId) throw RpcErrors.Failed(ErrorCode.SelfTrade, RefusalKeys.EconomySelfBuy, "you cannot buy your own lot");
            if (lot.Status != LotStatusDb.Open) throw RpcErrors.Failed(ErrorCode.LotGone, RefusalKeys.EconomyLotGone, "this lot was already sold or withdrawn");
            await gate.RequireSellerOpen(lot.SellerPlayerId);

            long fee = lot.Price * Math.Clamp(options.Value.MarketFeePercent, 0, 100) / 100;
            await wallet.Apply(buyerId, -lot.Price, "purchase", lot.Id);
            await wallet.Apply(lot.SellerPlayerId, lot.Price - fee, "sale", lot.Id);
            if (fee > 0) await wallet.Apply(WalletService.BankPlayerId, fee, "fee", lot.Id);

            lot.Status = LotStatusDb.Sold;
            lot.BuyerPlayerId = buyerId;
            lot.ClosedAt = DateTime.UtcNow;
            sellerId = lot.SellerPlayerId;
            var items = ItemList.Parser.ParseFrom(lot.Items);
            var pawns = lot.Pawn is null ? new PawnList() : new PawnList { Pawns = { PawnBlob.Parser.ParseFrom(lot.Pawn) } };
            parcel = await mailbox.Create(buyerId, lot.SellerPlayerId, ParcelKindDb.Purchase, items, pawns, 0, null);
            lotSummary = LotSummary.Of(items, pawns.Pawns.FirstOrDefault());
            string summary = TransferService.Summary(items, pawns, 0) + $" @ {lot.Price}";
            db.Transfers.Add(new Transfer { FromPlayerId = buyerId, ToPlayerId = lot.SellerPlayerId, Kind = "buy", GameTick = 0, Summary = summary, At = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            wallet.FlushEvents();

            var buyer = await db.Players.AsNoTracking().FirstAsync(p => p.Id == buyerId);
            sold = new LotSold { LotId = lot.Id, Buyer = buyer.ToRef(), Price = lot.Price, Proceeds = lot.Price - fee };
            log.LogInformation("Lot {Lot} sold by player {Seller} to player {Buyer} for {Price}", lot.Id, lot.SellerPlayerId, buyerId, lot.Price);
        }
        EconomyMetrics.Trade("buy", sold.Price);
        events.SendTo(sellerId, new ServerEvent { LotSold = sold });
        string seller = await db.Players.AsNoTracking().Where(p => p.Id == sellerId).Select(p => p.Name).FirstAsync();
        feed.Publish(FeedTypes.LotSold, new LotData(sold.LotId, new FeedPlayer(sellerId, seller), new FeedPlayer(buyerId, sold.Buyer.Name), sold.Price, lotSummary));
        await mailbox.Announce(parcel);
        return parcel;
    }

    /// <summary>Returns every lot past its expiry to its seller's mailbox; safe when the seller was reset.</summary>
    public async Task<int> ExpireDue(DateTime now)
    {
        EconomyMetrics.ParcelsPending.Set(await db.Parcels.CountAsync(p => p.DeliveredAt == null));
        var due = await db.Lots.Where(l => l.Status == LotStatusDb.Open && l.ExpiresAt <= now).ToListAsync();
        var announcements = new List<(Parcel Parcel, long Seller, long LotId)>();
        foreach (var lot in due)
        {
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                var parcel = await Close(lot, LotStatusDb.Expired, ParcelKindDb.LotReturn, "expire");
                await tx.CommitAsync();
                announcements.Add((parcel, lot.SellerPlayerId, lot.Id));
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not expire lot {Lot}", lot.Id);
            }
        }
        foreach (var (parcel, seller, lotId) in announcements)
        {
            events.SendTo(seller, new ServerEvent { LotClosed = new LotClosed { LotId = lotId, Reason = "expired" } });
            await mailbox.Announce(parcel);
        }
        return announcements.Count;
    }

    private async Task<Parcel> Close(Lot lot, LotStatusDb status, ParcelKindDb kind, string transferKind)
    {
        lot.Status = status;
        lot.ClosedAt = DateTime.UtcNow;
        var items = ItemList.Parser.ParseFrom(lot.Items);
        var pawns = lot.Pawn is null ? new PawnList() : new PawnList { Pawns = { PawnBlob.Parser.ParseFrom(lot.Pawn) } };
        var parcel = await mailbox.Create(lot.SellerPlayerId, null, kind, items, pawns, 0, null, ignoreMailboxLimit: true);
        db.Transfers.Add(new Transfer { FromPlayerId = lot.SellerPlayerId, Kind = transferKind, GameTick = 0, Summary = $"lot {lot.Id}", At = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return parcel;
    }

    public static Protocol.V1.Lot ToProto(Lot l, Player? seller, bool includeBlobs = true) => new()
    {
        Id = l.Id,
        Seller = seller?.ToRef(),
        Items = MailboxService.StripBlobs(ItemList.Parser.ParseFrom(l.Items), includeBlobs),
        Pawn = l.Pawn is null ? null : MailboxService.StripBlobs(PawnBlob.Parser.ParseFrom(l.Pawn), includeBlobs),
        Price = l.Price,
        CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(l.CreatedAt, DateTimeKind.Utc)),
        ExpiresAt = Timestamp.FromDateTime(DateTime.SpecifyKind(l.ExpiresAt, DateTimeKind.Utc)),
        Status = (LotStatus)l.Status,
    };
}

/// <summary>Sweep of expired lots at start (a restart does not postpone it), then hourly.</summary>
public sealed class LotExpiryService(IServiceScopeFactory scopes, ILogger<LotExpiryService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            do
            {
                using var scope = scopes.CreateScope();
                int n = await scope.ServiceProvider.GetRequiredService<MarketService>().ExpireDue(DateTime.UtcNow);
                if (n > 0) log.LogInformation("Expired {Count} market lots", n);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
