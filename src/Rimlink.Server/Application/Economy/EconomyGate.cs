using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Application.Economy;

/// <summary>Admin-set pause of all trading (deliveries stay allowed).</summary>
public sealed class FreezeState
{
    public volatile bool Frozen;
    public volatile string? Reason;
}

/// <summary>Refuses economy operations while the economy is disabled or frozen.</summary>
public sealed class EconomyGate(IOptions<EconomyOptions> options, FreezeState freeze, PlayerFreeze players)
{
    public void RequireOpen()
    {
        if (!options.Value.Enabled) throw RpcErrors.Failed(ErrorCode.EconomyFrozen, RefusalKeys.EconomyDisabled, "the economy is disabled on this server");
        if (freeze.Frozen) throw RpcErrors.Failed(ErrorCode.EconomyFrozen, RefusalKeys.EconomyPaused, "trading is paused: " + (freeze.Reason ?? "maintenance"), freeze.Reason ?? "maintenance");
    }

    /// <summary>
    /// The player who starts the operation must not be frozen. Checked after the replay lookup by client_request_id: a replay
    /// of a request applied before the freeze returns its result, so PLAYER_FROZEN always means "not applied".
    /// </summary>
    public Task RequireNotFrozen(long playerId) => players.Require(playerId);

    public Task RequireSellerOpen(long sellerId) => players.RequireSeller(sellerId);
}
