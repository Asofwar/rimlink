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
public sealed class EconomyGate(IOptions<EconomyOptions> options, FreezeState freeze)
{
    public void RequireOpen()
    {
        if (!options.Value.Enabled) throw RpcErrors.Failed(ErrorCode.EconomyFrozen, RefusalKeys.EconomyDisabled, "the economy is disabled on this server");
        if (freeze.Frozen) throw RpcErrors.Failed(ErrorCode.EconomyFrozen, RefusalKeys.EconomyPaused, "trading is paused: " + (freeze.Reason ?? "maintenance"), freeze.Reason ?? "maintenance");
    }
}
