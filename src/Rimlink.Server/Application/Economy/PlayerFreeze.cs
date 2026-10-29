using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Application.Economy;

/// <summary>
/// One player's economy frozen (integrity spec §2): set by an admin or by the integrity checks, lifted only by an admin.
/// The player cannot start trades, transfers or raids; deliveries to them go on.
/// </summary>
public sealed class PlayerFreeze(RimlinkDbContext db, EventBus events)
{
    /// <summary>Freezes or opens the player's economy and tells them. False when it already was in that state (nothing sent).</summary>
    public async Task<bool> Set(long playerId, bool frozen, string? reason)
    {
        var player = await db.Players.FirstAsync(p => p.Id == playerId);
        if ((player.EconomyFrozenAt is not null) == frozen) return false;
        player.EconomyFrozenAt = frozen ? DateTime.UtcNow : null;
        player.EconomyFreezeReason = frozen ? reason : null;
        await db.SaveChangesAsync();
        string text = frozen ? "Your trading is frozen: " + (reason ?? "by admin") : "Your trading is open again";
        events.SendTo(playerId, new ServerEvent { Notice = new Notice { Text = text } });
        return true;
    }

    public Task<bool> IsFrozen(long playerId) => db.Players.AnyAsync(p => p.Id == playerId && p.EconomyFrozenAt != null);

    /// <summary>Refuses with PLAYER_FROZEN when the player starts something while frozen.</summary>
    public async Task Require(long playerId)
    {
        var frozen = await db.Players.AsNoTracking().Where(p => p.Id == playerId && p.EconomyFrozenAt != null).Select(p => new { p.EconomyFreezeReason }).FirstOrDefaultAsync();
        if (frozen is null) return;
        string reason = frozen.EconomyFreezeReason ?? "by admin";
        throw RpcErrors.Failed(ErrorCode.PlayerFrozen, RefusalKeys.EconomyPlayerFrozen, "your trading is frozen: " + reason, reason);
    }

    /// <summary>Refuses with PLAYER_FROZEN when the other party of a trade is frozen: an exchange would move their goods.</summary>
    public async Task RequireTradePartner(long partnerId)
    {
        string? name = await db.Players.AsNoTracking().Where(p => p.Id == partnerId && p.EconomyFrozenAt != null).Select(p => p.Name).FirstOrDefaultAsync();
        if (name is not null) throw RpcErrors.Failed(ErrorCode.PlayerFrozen, RefusalKeys.TradesPartnerFrozen, $"{name}'s trading is frozen", name);
    }

    /// <summary>Refuses with PLAYER_FROZEN when the seller (lot owner, shop host) is frozen: their goods are not for sale meanwhile.</summary>
    public async Task RequireSeller(long sellerId)
    {
        if (await IsFrozen(sellerId)) throw RpcErrors.Failed(ErrorCode.PlayerFrozen, RefusalKeys.EconomySellerFrozen, "this seller's trading is frozen");
    }
}
