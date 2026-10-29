using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Economy;

/// <summary>
/// Refusals that depend on the player's situation rather than on the request: a colony under attack keeps what it has
/// (economy hardening spec §3). One definition of "under threat" for every outflow.
/// </summary>
public sealed class EconomyGuard(RimlinkDbContext db, IOptions<EconomyOptions> options)
{
    public const string UnderThreatMessage = "your colony is under attack: deposits, lots and sends wait until it is over";

    private static readonly RaidStateDb[] Incoming = [RaidStateDb.Queued, RaidStateDb.Announced, RaidStateDb.Landed];
    private static readonly LiveStateDb[] LiveOn = [LiveStateDb.Starting, LiveStateDb.Running, LiveStateDb.Waiting, LiveStateDb.Ending];

    /// <summary>
    /// A raid waiting for, or landed in, this player's game; or an accepted live raid they host. A live raid's own row is
    /// Queued from the proposal on, so live rows count only through their session.
    /// </summary>
    public async Task<bool> UnderThreat(long playerId)
    {
        int live = (int)RaidController.ControllerLive, liveRaid = (int)LiveKind.LiveRaid;
        return await db.Raids.AnyAsync(r => r.TargetPlayerId == playerId && r.Controller != live && Incoming.Contains(r.State))
            || await db.LiveSessions.AnyAsync(s => s.HostPlayerId == playerId && s.Kind == liveRaid && LiveOn.Contains(s.State));
    }

    public async Task RequireNoThreat(long playerId)
    {
        if (options.Value.BlockOutflowUnderThreat && await UnderThreat(playerId))
            throw RpcErrors.Failed(ErrorCode.UnderThreat, RefusalKeys.EconomyUnderThreat, UnderThreatMessage);
    }

    /// <summary>Storage fees left a debt: buying, new lots and sends wait until a deposit or a sale pays it.</summary>
    public async Task RequireNoDebt(long playerId)
    {
        long balance = (await db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.PlayerId == playerId))?.Balance ?? 0;
        if (balance < 0) throw RpcErrors.Failed(ErrorCode.InDebt, RefusalKeys.EconomyInDebt, $"you owe {-balance} silver in storage fees", -balance);
    }
}
