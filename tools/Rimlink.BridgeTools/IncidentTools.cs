using Grpc.Core;
using RimBridgeServer.Sdk;
using Rimlink.Client.Economy;
using Rimlink.Client.Game;
using Rimlink.Protocol.V1;

namespace Rimlink.BridgeTools;

/// <summary>Paid incidents from the bridge: order one against another player, list the queue against us and our own orders.</summary>
public sealed class IncidentTools
{
    [Tool("rimlink/incident_order", Description = "Order a paid incident (catalog entry id: pirates, toxic-fallout, trader, …) against another player for wallet silver",
        ResultDescription = "success, the order id, its price and state")]
    public async Task<object> Order(IRimBridgeContext ctx,
        [ToolParameter(Description = "Target player name or id", Required = true)] string player,
        [ToolParameter(Description = "Catalog entry id", Required = true)] string entry,
        [ToolParameter(Description = "Level 1-4 for hostile entries (ignored otherwise)")] int level = 2,
        [ToolParameter(Description = "By drop pods (hostile entries that allow it)")] bool pods = false)
    {
        var target = await Bridge.OnMain(ctx, () => RimlinkSession.Active ? Targets.Player(player) : null);
        if (target is null) return Bridge.Fail(RimlinkSession.Active ? $"unknown player '{player}'" : "no Rimlink session");
        try
        {
            var order = await RimlinkSession.Incidents!.Order(new OrderIncidentRequest
            {
                ClientRequestId = Guid.NewGuid().ToString("N"),
                TargetPlayerId = target.PlayerId,
                EntryId = entry,
                Level = level,
                Pods = pods,
            }, CancellationToken.None);
            await Bridge.OnMain<object?>(ctx, () =>
            {
                var mine = RimlinkSession.State.Incidents.Mine;
                if (!mine.Any(i => i.Id == order.Id)) mine.Insert(0, order);
                return null;
            });
            return new { success = true, id = order.Id, price = order.Price, state = order.State.ToString() };
        }
        catch (RpcException ex)
        {
            return Bridge.Fail(EconomyClient.MessageOf(ex), new { code = EconomyClient.CodeOf(ex)?.ToString() });
        }
    }

    [Tool("rimlink/incidents", Description = "Paid incidents waiting for our colony and the ones we ordered, as this client holds them",
        ResultDescription = "success, incoming and mine [{id, buyer, target, entry, kind, level, pods, price, state, fireTick, reason}], fired ids not yet reported")]
    public Task<object> List(IRimBridgeContext ctx) => Bridge.OnMain<object>(ctx, () =>
    {
        if (!RimlinkSession.Active) return Bridge.Fail("no Rimlink session");
        var state = RimlinkSession.State.Incidents;
        var component = Verse.Current.Game?.GetComponent<RimlinkGameComponent>();
        return new
        {
            success = true,
            incoming = state.Incoming.Select(Row).ToList(),
            mine = state.Mine.Select(Row).ToList(),
            catalog = state.Catalog.Entries.Select(e => e.Id).ToList(),
            firedUnreported = component?.FiredIncidents.ToList() ?? [],
        };
    });

    private static object Row(Incident i) => new
    {
        id = i.Id,
        buyer = i.Buyer?.Name,
        target = i.Target?.Name,
        entry = i.EntryId,
        kind = i.Kind.ToString(),
        level = i.Level,
        pods = i.Pods,
        price = i.Price,
        state = i.State.ToString(),
        fireTick = i.FireTick,
        reason = i.Reason,
    };
}
