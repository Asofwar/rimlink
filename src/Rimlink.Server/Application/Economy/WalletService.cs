using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Wallet = Rimlink.Server.Storage.Entities.Wallet;

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

    public Task<List<WalletEntry>> History(long playerId, int limit = 200) =>
        db.WalletEntries.AsNoTracking().Where(e => e.PlayerId == playerId).OrderByDescending(e => e.Id).Take(limit).ToListAsync();
}
