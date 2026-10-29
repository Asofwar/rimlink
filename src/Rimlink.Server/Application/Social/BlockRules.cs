using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Application.Social;

/// <summary>
/// The block check shared by chat, transfers and trades. Static so <c>TradeService</c> can use it without depending on
/// <see cref="SocialService"/>, which itself cancels trades on a block.
/// </summary>
public static class BlockRules
{
    /// <summary>Refuses with <c>BLOCKED</c> when either player blocked the other; the text tells which side did.</summary>
    public static async Task RequireNotBlocked(RimlinkDbContext db, long actorId, long otherId)
    {
        var blockers = await db.PlayerBlocks.AsNoTracking()
            .Where(b => (b.BlockerPlayerId == actorId && b.BlockedPlayerId == otherId) || (b.BlockerPlayerId == otherId && b.BlockedPlayerId == actorId))
            .Select(b => b.BlockerPlayerId).ToListAsync();
        if (blockers.Contains(actorId)) throw RpcErrors.Failed(ErrorCode.Blocked, "you blocked this player");
        if (blockers.Count > 0) throw RpcErrors.Failed(ErrorCode.Blocked, "the player does not accept this from you");
    }
}
