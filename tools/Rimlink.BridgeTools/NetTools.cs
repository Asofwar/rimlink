using RimBridgeServer.Sdk;
using Rimlink.Client.Game;

namespace Rimlink.BridgeTools;

public sealed class NetTools
{
    [Tool("rimlink/net_drop",
        Description = "Dev only: this client loses its server connection for the given seconds (open streams break, calls fail, the machine's network is untouched), then reconnects the usual way. For PeerLost/PeerBack and reconnect checks.",
        ResultDescription = "success/started; rimlink/status session.connection shows Reconnecting, then Connected")]
    public Task<object> NetDrop(IRimBridgeContext ctx,
        [ToolParameter(Description = "Seconds without a connection, 1..600", Required = true)] int seconds) => Bridge.OnMain(ctx, () =>
    {
        if (!RimlinkSession.Active || RimlinkSession.Connection is not { } connection) return Bridge.Fail("no Rimlink session");
        if (seconds is < 1 or > 600) return Bridge.Fail("seconds must be 1..600");
        connection.DropNetwork(TimeSpan.FromSeconds(seconds));
        return Targets.Started($"network down for {seconds} s");
    });
}
