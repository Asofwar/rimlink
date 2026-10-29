using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.Game;
using Verse;

namespace Rimlink.BridgeTools;

/// <summary>Readouts for the world-hygiene checks: other players' settlements as the map shows them, and a world camera jump for screenshots.</summary>
public sealed class HygieneTools
{
    [Tool("rimlink/foreign_settlements", Description = "Other players' settlements on the world map: owner, tile, online light, wealth tier, last seen, colony age and the inspect text",
        ResultDescription = "success plus one entry per settlement")]
    public Task<object> ForeignSettlementsList(IRimBridgeContext ctx) => Bridge.OnMain<object>(ctx, () =>
    {
        if (Find.World is null) return Bridge.Fail("no world");
        var settlements = Find.WorldObjects.AllWorldObjects.OfType<PlayerSettlement>().Select(s => new
        {
            owner = s.OwnerName,
            ownerId = s.OwnerPlayerId,
            faction = s.FactionName,
            tile = (int)s.Tile,
            online = s.OwnerOnline,
            wealthTier = s.WealthTier,
            lastSeen = s.OwnerLastSeen?.ToString("yyyy-MM-dd"),
            ageDays = s.ColonyGameDays,
            inspect = s.GetInspectString(),
        }).ToList();
        return new { success = true, settlements };
    });

    [Tool("rimlink/world_jump", Description = "Show the world and center its camera on the world object at a tile (dev; for screenshots of the world markers)",
        ResultDescription = "success plus the object's label")]
    public Task<object> WorldJump(IRimBridgeContext ctx,
        [ToolParameter(Description = "World tile of a world object (a settlement)", Required = true)] int tile) => Bridge.OnMain<object>(ctx, () =>
    {
        if (Find.World is null) return Bridge.Fail("no world");
        if (Find.WorldObjects.AllWorldObjects.FirstOrDefault(o => (int)o.Tile == tile) is not { } target) return Bridge.Fail($"no world object on tile {tile}");
        CameraJumper.TryJump(target);
        return new { success = true, label = target.LabelCap.ToString() };
    });
}
