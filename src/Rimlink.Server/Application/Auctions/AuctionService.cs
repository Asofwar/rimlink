using System.Data;
using System.Security.Cryptography;
using System.Text;
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
using Transfer = Rimlink.Server.Storage.Entities.Transfer;

namespace Rimlink.Server.Application.Auctions;

public sealed class AuctionService(RimlinkDbContext db, WalletService wallet, MailboxService mailbox,
    ParcelValidator validator, EconomyGate gate, EconomyGuard guard, TransferService transfers,
    EventBus events, IOptions<EconomyOptions> options, TimeProvider time, ILogger<AuctionService> log)
{
    public const int PageSize = 25;
    public const int ExtensionSeconds = 30;
    public const int ExtensionCapSeconds = 300;
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<AuctionList> List(long viewer, AuctionQuery request)
    {
        if (request.Page is < 0 or > 10000) throw Invalid();
        var query = db.Set<AuctionRow>().AsNoTracking();
        query = request.Mine
            ? query.Where(a => a.SellerPlayerId == viewer || a.BidderPlayerId == viewer)
            : query.Where(a => a.Status == 0 && !db.Players.Any(p => p.Id == a.SellerPlayerId && p.EconomyFrozenAt != null));
        var rows = await query.OrderByDescending(a => a.Id).Skip(request.Page * PageSize).Take(PageSize + 1).ToListAsync();
        var result = new AuctionList { HasMore = rows.Count > PageSize };
        foreach (var row in rows.Take(PageSize)) result.Auctions.Add(await Describe(row));
        return result;
    }

    public Task<Auction> Post(long playerId, AuctionPostRequest request) => Mutate(playerId, request.ClientRequestId, request, async () =>
    {
        if (request.StartPrice < 1 || request.BuyoutPrice < 0 || (request.BuyoutPrice > 0 && request.BuyoutPrice < request.StartPrice)
            || request.DurationSeconds < 60 || request.DurationSeconds > 30 * 86400 || request.GameTick < 0
            || request.Items is not { Items.Count: > 0 }) throw Invalid();
        if (request.DurationSeconds > Math.Max(1, options.Value.LotLifetimeDays) * 86400L) throw Invalid();
        RequireItems(request.Items);
        await RequireOutgoing(playerId);
        int open = await db.Set<AuctionRow>().CountAsync(a => a.SellerPlayerId == playerId && a.Status == 0)
            + await db.Lots.CountAsync(a => a.SellerPlayerId == playerId && a.Status == LotStatusDb.Open);
        if (open >= options.Value.MaxLotsPerPlayer)
            throw RpcErrors.Failed(ErrorCode.Unspecified, RefusalKeys.EconomyLotLimit, "too many open lots", open, options.Value.MaxLotsPerPlayer);
        var now = Now;
        var row = new AuctionRow
        {
            SellerPlayerId = playerId, Items = request.Items.ToByteArray(), StartPrice = request.StartPrice,
            BuyoutPrice = request.BuyoutPrice, CreatedAt = now, ExpiresAt = now.AddSeconds(request.DurationSeconds),
            MaxExpiresAt = now.AddSeconds(request.DurationSeconds + ExtensionCapSeconds),
        };
        db.Set<AuctionRow>().Add(row);
        await transfers.StampOutgoing(playerId, request.GameTick);
        db.Transfers.Add(new Transfer { FromPlayerId = playerId, Kind = "auction_post", GameTick = request.GameTick,
            Summary = TransferService.Summary(request.Items, null, 0), At = now });
        await db.SaveChangesAsync();
        return (row, (Parcel?)null, (long?)null);
    });

    public Task<Auction> Bid(long playerId, AuctionBidRequest request) => Mutate(playerId, request.ClientRequestId, request, async () =>
    {
        if (request.AuctionId < 1 || request.Amount < 1) throw Invalid();
        var row = await Open(request.AuctionId);
        if (row.SellerPlayerId == playerId)
            throw RpcErrors.Failed(ErrorCode.SelfTrade, RefusalKeys.EconomySelfBuy, "you cannot bid on your own auction");
        await RequireOutgoing(playerId);
        await gate.RequireSellerOpen(row.SellerPlayerId);
        await BlockRules.RequireNotBlocked(db, playerId, row.SellerPlayerId);
        if (row.ExpiresAt <= Now) throw Gone();
        if (request.Amount < row.StartPrice || request.Amount <= row.Bid
            || (request.Buyout && (row.BuyoutPrice == 0 || request.Amount != row.BuyoutPrice))
            || (!request.Buyout && row.BuyoutPrice > 0 && request.Amount >= row.BuyoutPrice))
            throw RpcErrors.Failed(ErrorCode.AuctionBidTooLow, RefusalKeys.AuctionsBidInvalid, "bid must beat the current bid; use buyout for the exact buyout price");
        long? oldBidder = row.BidderPlayerId;
        // Raising our own bid reserves only the difference, including a buyout by the current leader.
        if (oldBidder == playerId) await Apply(playerId, row.Bid - request.Amount, "auction_bid", row.Id);
        else
        {
            await Apply(playerId, -request.Amount, "auction_bid", row.Id);
            if (oldBidder is { } previous) await Apply(previous, row.Bid, "auction_refund", row.Id);
        }
        row.Bid = request.Amount;
        row.BidderPlayerId = playerId;
        Parcel? parcel = null;
        if (request.Buyout) parcel = await Close(row, AuctionStatus.AuctionSold, "buyout", chargeStorage: true);
        else if (row.ExpiresAt - Now <= TimeSpan.FromSeconds(ExtensionSeconds))
            row.ExpiresAt = new DateTime(Math.Min(row.MaxExpiresAt.Ticks, row.ExpiresAt.AddSeconds(ExtensionSeconds).Ticks), DateTimeKind.Utc);
        return (row, parcel, oldBidder);
    });

    public Task<Auction> Cancel(long playerId, AuctionCancelRequest request) => Mutate(playerId, request.ClientRequestId, request, async () =>
    {
        var row = await Open(request.AuctionId);
        if (row.SellerPlayerId != playerId) throw Gone();
        if (row.BidderPlayerId is not null) throw RpcErrors.Failed(ErrorCode.AuctionHasBids, RefusalKeys.AuctionsHasBids, "an auction with bids cannot be cancelled");
        return (row, (Parcel?)await Close(row, AuctionStatus.AuctionCancelled, "seller", chargeStorage: true), (long?)null);
    });

    private async Task<Auction> Mutate(long playerId, string requestId, IMessage request,
        Func<Task<(AuctionRow Row, Parcel? Parcel, long? Previous)>> action)
    {
        if (requestId.Length is < 1 or > 64 || requestId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw Invalid();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Descriptor.FullName).Concat(request.ToByteArray()).ToArray()));
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var replay = await db.Set<AuctionOperation>().AsNoTracking().FirstOrDefaultAsync(o => o.PlayerId == playerId && o.RequestId == requestId);
            if (replay is not null)
            {
                if (replay.IntentHash != hash) throw RpcErrors.Failed(ErrorCode.AuctionIntentMismatch, RefusalKeys.AuctionsIntentMismatch, "request id belongs to a different auction operation");
                return Auction.Parser.ParseFrom(replay.Response);
            }
            if (request.CalculateSize() > (long)options.Value.MaxParcelBytes + 4096) throw Invalid();
            gate.RequireOpen();
            await gate.RequireNotFrozen(playerId);
            var (row, parcel, previous) = await action();
            var response = await Describe(row);
            db.Set<AuctionOperation>().Add(new AuctionOperation { PlayerId = playerId, RequestId = requestId, IntentHash = hash, Response = response.ToByteArray() });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            wallet.FlushEvents();
            Announce(response, previous);
            if (parcel is not null) await mailbox.Announce(parcel);
            return response;
        }
        catch
        {
            wallet.DiscardEvents();
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<int> ExpireDue(DateTime now)
    {
        var due = await db.Set<AuctionRow>().AsNoTracking().Where(a => a.Status == 0 && a.ExpiresAt <= now)
            .OrderBy(a => a.Id).Select(a => a.Id).ToListAsync();
        int completed = 0;
        foreach (long id in due)
        {
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                var row = await db.Set<AuctionRow>().FirstOrDefaultAsync(a => a.Id == id);
                // A bid may have extended the deadline since the initial query.
                if (row is null || row.Status != 0 || row.ExpiresAt > now) continue;
                gate.RequireOpen();
                await gate.RequireNotFrozen(row.SellerPlayerId);
                if (row.BidderPlayerId is { } bidder) await gate.RequireNotFrozen(bidder);
                var parcel = await Close(row, row.BidderPlayerId is null ? AuctionStatus.AuctionExpired : AuctionStatus.AuctionSold,
                    "deadline", chargeStorage: true);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                wallet.FlushEvents();
                Announce(await Describe(row));
                await mailbox.Announce(parcel);
                completed++;
            }
            catch (Exception ex)
            {
                wallet.DiscardEvents();
                db.ChangeTracker.Clear();
                if (ex is not RpcException) log.LogWarning(ex, "Auction {Id} settlement deferred", id);
            }
        }
        return completed;
    }

    /// <summary>Admin/reset/archive returns both escrows, even after bids and while frozen.</summary>
    public async Task<bool> AdminCancel(long id, string? reason)
    {
        if (string.IsNullOrEmpty(reason) || reason.Length > 256 || reason.Any(char.IsControl)) throw Invalid();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var row = await db.Set<AuctionRow>().FirstOrDefaultAsync(a => a.Id == id && a.Status == 0);
            if (row is null) return false;
            var parcel = await Close(row, AuctionStatus.AuctionCancelled, reason, chargeStorage: false);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            wallet.FlushEvents();
            Announce(await Describe(row));
            await mailbox.Announce(parcel);
            return true;
        }
        catch { wallet.DiscardEvents(); db.ChangeTracker.Clear(); throw; }
    }

    public async Task CancelAllOf(long player, string reason)
    {
        var ids = await db.Set<AuctionRow>().AsNoTracking().Where(a => a.Status == 0 && (a.SellerPlayerId == player || a.BidderPlayerId == player)).Select(a => a.Id).ToListAsync();
        foreach (long id in ids) await AdminCancel(id, reason);
    }

    private async Task<Parcel> Close(AuctionRow row, AuctionStatus status, string reason, bool chargeStorage)
    {
        var items = ItemList.Parser.ParseFrom(row.Items);
        bool sold = status == AuctionStatus.AuctionSold;
        if (sold)
        {
            long fee = (long)((decimal)row.Bid * Math.Clamp(options.Value.MarketFeePercent, 0, 100) / 100m);
            await Apply(row.SellerPlayerId, row.Bid - fee, "auction_sale", row.Id);
            if (fee > 0) await Apply(WalletService.BankPlayerId, fee, "fee", row.Id);
        }
        else if (row.BidderPlayerId is { } bidder) await Apply(bidder, row.Bid, "auction_refund", row.Id);
        if (chargeStorage)
        {
            var perishables = new ItemList();
            perishables.Items.AddRange(items.Items.Where(i => i.Perishable));
            long storage = StorageFee.For(ServerWealth.ValueOf(perishables), row.CreatedAt, Now, options.Value.StorageFeePercentPerDay, options.Value.StorageFreeDays);
            if (storage > 0)
            {
                EnsureRange((decimal)await wallet.Balance(row.SellerPlayerId) - storage);
                EnsureRange((decimal)await wallet.Balance(WalletService.BankPlayerId) + storage);
                await wallet.Charge(row.SellerPlayerId, storage, "storage", row.Id);
            }
        }
        row.Status = (int)status;
        row.ClosedAt = Now;
        row.CloseReason = reason;
        // Delivery of already committed escrow must survive a full mailbox (as market returns do).
        var parcel = await mailbox.Create(sold ? row.BidderPlayerId!.Value : row.SellerPlayerId, sold ? row.SellerPlayerId : null,
            sold ? ParcelKindDb.Purchase : ParcelKindDb.LotReturn, items, null, 0, null, ignoreMailboxLimit: true);
        db.Transfers.Add(new Transfer { FromPlayerId = row.SellerPlayerId, ToPlayerId = row.BidderPlayerId,
            Kind = sold ? "auction_sale" : "auction_return", Summary = $"auction {row.Id}: {reason}", At = Now });
        return parcel;
    }

    private async Task Apply(long playerId, long delta, string kind, long id)
    {
        EnsureRange((decimal)await wallet.Balance(playerId) + delta);
        await wallet.Apply(playerId, delta, kind, id);
    }
    private static void EnsureRange(decimal value)
    {
        if (value < long.MinValue || value > long.MaxValue)
            throw RpcErrors.With(StatusCode.OutOfRange, ErrorCode.Unspecified, RefusalKeys.AuctionsOverflow, "wallet limit exceeded; escrow remains unchanged");
    }
    private async Task RequireOutgoing(long playerId)
    {
        await guard.RequireNoThreat(playerId);
        await guard.RequireNoDebt(playerId);
        if (await db.ArchivedColonies.AnyAsync(a => a.PlayerId == playerId && a.State == ArchiveState.Archived)
            && !await db.WorldObjects.AnyAsync(o => o.OwnerPlayerId == playerId && o.Type == WorldObjectTypes.Settlement))
            throw RpcErrors.Failed(ErrorCode.AuctionGone, RefusalKeys.AuctionsArchived, "restore your colony before using auctions");
    }
    private void RequireItems(ItemList items)
    {
        if (validator.Validate(items, null, checkForbidden: true) is { } refusal)
            throw RpcErrors.Failed(refusal.Code, refusal.Key, refusal.Message, refusal.Args);
        if (items.Items.Any(i => i.DefName.Length > 256 || i.StuffDef.Length > 256 || i.Label.Length > 256 || i.MarketValueX100 < 0)) throw Invalid();
        // Keep accepted descriptors within the existing wealth/storage int64 domain.
        decimal valueX100 = 0;
        foreach (var item in items.Items)
        {
            valueX100 += (decimal)item.MarketValueX100 * item.Count;
            if (valueX100 > long.MaxValue * 100m) throw Invalid();
        }
    }
    private async Task<AuctionRow> Open(long id) => await db.Set<AuctionRow>().FirstOrDefaultAsync(a => a.Id == id && a.Status == 0) ?? throw Gone();
    private static RpcException Gone() => RpcErrors.Failed(ErrorCode.AuctionGone, RefusalKeys.AuctionsGone, "auction is closed or expired");
    private static RpcException Invalid() => RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.Unspecified, RefusalKeys.AuctionsInvalid, "invalid auction: check prices, duration, goods and request id");

    public async Task<Auction> Describe(AuctionRow row)
    {
        var seller = await db.Players.AsNoTracking().FirstAsync(p => p.Id == row.SellerPlayerId);
        var bidder = row.BidderPlayerId is { } id ? await db.Players.AsNoTracking().FirstAsync(p => p.Id == id) : null;
        return new Auction { Id = row.Id, Seller = seller.ToRef(), Bidder = bidder?.ToRef(),
            Items = MailboxService.StripBlobs(ItemList.Parser.ParseFrom(row.Items), false), StartPrice = row.StartPrice, BuyoutPrice = row.BuyoutPrice,
            Bid = row.Bid, ExpiresAt = Timestamp.FromDateTime(DateTime.SpecifyKind(row.ExpiresAt, DateTimeKind.Utc)),
            MaxExpiresAt = Timestamp.FromDateTime(DateTime.SpecifyKind(row.MaxExpiresAt, DateTimeKind.Utc)), Status = (AuctionStatus)row.Status, CloseReason = row.CloseReason };
    }
    private void Announce(Auction auction, long? previous = null)
    {
        var ev = new ServerEvent { AuctionUpdated = new AuctionUpdated { Auction = auction } };
        foreach (long id in new long?[] { auction.Seller.PlayerId, auction.Bidder?.PlayerId, previous }.OfType<long>().Distinct()) events.SendTo(id, ev);
    }
}
