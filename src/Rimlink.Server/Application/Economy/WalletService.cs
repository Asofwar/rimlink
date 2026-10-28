using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Wallet = Rimlink.Server.Storage.Entities.Wallet;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;
using Transfer = Rimlink.Server.Storage.Entities.Transfer;

namespace Rimlink.Server.Application.Economy;

/// <summary>Silver balances. Every change is one <see cref="WalletEntry"/>; the balance column caches their sum.</summary>
public sealed class WalletService(RimlinkDbContext db, EventBus events)
{
    public const long BankPlayerId = 0;

    public async Task<long> Balance(long playerId) =>
        (await db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.PlayerId == playerId))?.Balance ?? 0;

    /// <summary>
    /// Applies a signed delta inside the caller's transaction (or on its own when there is none) and saves.
    /// A negative result is refused with WALLET_INSUFFICIENT before anything is written.
    /// </summary>
    public async Task<WalletEntry> Apply(long playerId, long delta, string kind, long refId)
    {
        var wallet = await db.Wallets.FirstOrDefaultAsync(w => w.PlayerId == playerId);
        if (wallet is null)
        {
            wallet = new Wallet { PlayerId = playerId, Balance = 0, UpdatedAt = DateTime.UtcNow };
            db.Wallets.Add(wallet);
        }
        if (wallet.Balance + delta < 0)
            throw RpcErrors.Failed(ErrorCode.WalletInsufficient, $"not enough silver: balance {wallet.Balance}, needed {-delta}");

        wallet.Balance += delta;
        wallet.UpdatedAt = DateTime.UtcNow;
        var entry = new WalletEntry { PlayerId = playerId, Delta = delta, Kind = kind, RefId = refId, At = DateTime.UtcNow };
        db.WalletEntries.Add(entry);
        await db.SaveChangesAsync();

        if (playerId != BankPlayerId)
            events.SendTo(playerId, new ServerEvent { WalletChanged = new WalletChanged { Balance = wallet.Balance, Delta = delta, Kind = kind } });
        return entry;
    }

    /// <summary>Silver taken off the map goes into the wallet; the save tick is stamped like any outgoing transfer.</summary>
    public async Task<long> Deposit(long playerId, DepositRequest request, TransferService transfers, EconomyGate gate)
    {
        gate.RequireOpen();
        if (request.Silver < 1) throw RpcErrors.Failed(ErrorCode.Unspecified, "silver must be at least 1");
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        if (!string.IsNullOrEmpty(request.ClientRequestId) && await db.Transfers.AnyAsync(t => t.ClientRequestId == request.ClientRequestId))
            return await Balance(playerId);
        await Apply(playerId, request.Silver, "deposit", 0);
        await transfers.StampOutgoing(playerId, request.GameTick);
        db.Transfers.Add(new Transfer { FromPlayerId = playerId, Kind = "deposit", GameTick = request.GameTick, Summary = $"{request.Silver} silver", At = DateTime.UtcNow, ClientRequestId = string.IsNullOrEmpty(request.ClientRequestId) ? null : request.ClientRequestId });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        EconomyMetrics.Trade("deposit", request.Silver);
        return await Balance(playerId);
    }

    /// <summary>Silver leaves the wallet now and arrives as a physical stack in a parcel; delivery credits nothing.</summary>
    public async Task<Parcel> Withdraw(long playerId, WithdrawRequest request, MailboxService mailbox, EconomyGate gate)
    {
        gate.RequireOpen();
        if (request.Silver < 1) throw RpcErrors.Failed(ErrorCode.Unspecified, "silver must be at least 1");
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        if (!string.IsNullOrEmpty(request.ClientRequestId))
        {
            var existing = await db.Parcels.AsNoTracking().FirstOrDefaultAsync(p => p.ClientRequestId == request.ClientRequestId);
            if (existing is not null) return existing;
        }
        long balance = await Balance(playerId);
        if (balance < request.Silver)
            throw RpcErrors.Failed(ErrorCode.WalletInsufficient, $"not enough silver: balance {balance}, needed {request.Silver}");
        var parcel = await mailbox.Create(playerId, null, ParcelKindDb.Withdraw, null, null, request.Silver, string.IsNullOrEmpty(request.ClientRequestId) ? null : request.ClientRequestId);
        await Apply(playerId, -request.Silver, "withdraw", parcel.Id);
        db.Transfers.Add(new Transfer { FromPlayerId = playerId, Kind = "withdraw", GameTick = 0, Summary = $"{request.Silver} silver", At = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        EconomyMetrics.Trade("withdraw", request.Silver);
        await mailbox.Announce(parcel);
        return parcel;
    }

    public Task<List<WalletEntry>> History(long playerId, int limit = 200) =>
        db.WalletEntries.AsNoTracking().Where(e => e.PlayerId == playerId).OrderByDescending(e => e.Id).Take(limit).ToListAsync();
}
