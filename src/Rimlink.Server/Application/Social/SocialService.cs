using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Raids;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Social;

/// <summary>A player's block list and the two personal flags of the safe trades spec (gifts without asking, no trade offers).</summary>
public sealed class SocialService(RimlinkDbContext db, EventBus events, Trades.TradeService trades, IOptions<SocialOptions> options)
{
    public async Task<List<PlayerRef>> Block(long blockerId, long blockedId)
    {
        if (blockerId == blockedId) throw RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.Unspecified, RefusalKeys.SocialSelf, "you cannot block yourself");
        var target = await db.Players.AsNoTracking().Include(p => p.Account).FirstOrDefaultAsync(p => p.Id == blockedId)
            ?? throw RpcErrors.With(StatusCode.NotFound, ErrorCode.Unspecified, RefusalKeys.NoSuchPlayer, "no such player");
        // Moderators and admins must be able to reach everyone.
        if (target.Account.Role != Storage.Entities.Role.Player) throw RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.Unspecified, RefusalKeys.SocialStaff, "moderators and admins cannot be blocked");

        if (!await db.PlayerBlocks.AnyAsync(b => b.BlockerPlayerId == blockerId && b.BlockedPlayerId == blockedId))
        {
            int max = options.Value.MaxBlocks;
            if (await db.PlayerBlocks.CountAsync(b => b.BlockerPlayerId == blockerId) >= max)
                throw RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.Unspecified, RefusalKeys.SocialListFull, $"block list is full ({max})", max);
            db.PlayerBlocks.Add(new PlayerBlock { BlockerPlayerId = blockerId, BlockedPlayerId = blockedId, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        // Open trades between the two end here, each side going back to its owner; gifts already waiting stay (they can be refused).
        await trades.CancelBetween(blockerId, blockedId, "blocked");
        return await List(blockerId);
    }

    public async Task<List<PlayerRef>> Unblock(long blockerId, long blockedId)
    {
        await db.PlayerBlocks.Where(b => b.BlockerPlayerId == blockerId && b.BlockedPlayerId == blockedId).ExecuteDeleteAsync();
        return await List(blockerId);
    }

    public async Task<List<PlayerRef>> List(long blockerId)
    {
        var players = await db.PlayerBlocks.AsNoTracking()
            .Where(b => b.BlockerPlayerId == blockerId)
            .Join(db.Players.AsNoTracking(), b => b.BlockedPlayerId, p => p.Id, (b, p) => p)
            .OrderBy(p => p.Name).ToListAsync();
        return players.Select(p => p.ToRef()).ToList();
    }

    /// <summary>Who blocked this player (their public lines do not reach these).</summary>
    public async Task<HashSet<long>> BlockersOf(long playerId) =>
        (await db.PlayerBlocks.AsNoTracking().Where(b => b.BlockedPlayerId == playerId).Select(b => b.BlockerPlayerId).ToListAsync()).ToHashSet();

    /// <summary>Whom this player blocked.</summary>
    public async Task<HashSet<long>> BlockedBy(long playerId) =>
        (await db.PlayerBlocks.AsNoTracking().Where(b => b.BlockerPlayerId == playerId).Select(b => b.BlockedPlayerId).ToListAsync()).ToHashSet();

    public Task<PlayerFlags> SetAcceptGifts(long playerId, bool on) => SetFlag(playerId, p => p.AcceptGifts = on);

    public Task<PlayerFlags> SetTradesClosed(long playerId, bool on) => SetFlag(playerId, p => p.TradesClosed = on);

    private async Task<PlayerFlags> SetFlag(long playerId, Action<Player> set)
    {
        var player = await db.Players.FirstAsync(p => p.Id == playerId);
        set(player);
        await db.SaveChangesAsync();
        var flags = RaidService.FlagsOf(player);
        events.SendTo(playerId, new ServerEvent { PlayerFlagsChanged = new PlayerFlagsChanged { Flags = flags } });
        return flags;
    }
}
