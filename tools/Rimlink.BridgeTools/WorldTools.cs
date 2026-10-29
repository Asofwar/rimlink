using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.Game;
using Verse;

namespace Rimlink.BridgeTools;

/// <summary>
/// Caravans and transport pods on the world map, ending in the real right-click actions of the settlement on the target
/// tile (Rimlink's hand-over, raid, visit, live). The vanilla legwork (forming, the trip, building and fuelling pods) is
/// shortcut; the Rimlink arrival actions and the windows they open are the real ones.
/// </summary>
public sealed class WorldTools
{
    [Tool("rimlink/caravan_form", Description = "Form a caravan on the world map from our pawns on the current map, with items in its inventory (no forming walk)",
        ResultDescription = "success, caravan id, name and tile")]
    public Task<object> CaravanForm(IRimBridgeContext ctx,
        [ToolParameter(Description = "Comma-separated pawn ids or names (colonists and animals)", Required = true)] string pawns,
        [ToolParameter(Description = "Items as def:count, comma-separated")] string? items = null,
        [ToolParameter(Description = "Take items from anywhere on the map, not only under the beacons")] bool anywhere = true) => Bridge.OnMain(ctx, () =>
    {
        if (Targets.NotPlaying() is { } no) return no;
        var map = Find.CurrentMap;
        if (Targets.Pawns(pawns, out string? error) is not { } chosen) return Bridge.Fail(error!);
        List<Rimlink.Client.Game.Economy.Outgoing.Pick> picks = [];
        if (!string.IsNullOrWhiteSpace(items))
        {
            if (Targets.Items(map, items, anywhere, out string? itemError) is not { } found) return Bridge.Fail(itemError!);
            picks = found;
        }
        var carrier = chosen.FirstOrDefault(p => p.RaceProps.Humanlike) ?? chosen[0];
        foreach (var pick in picks) carrier.inventory.innerContainer.TryAdd(pick.Thing.SplitOff(pick.Count));
        var before = Find.WorldObjects.Caravans.ToHashSet();
        CaravanFormingUtility.FormAndCreateCaravan(chosen, Faction.OfPlayer, map.Tile, map.Tile, PlanetTile.Invalid);
        var caravan = Find.WorldObjects.Caravans.FirstOrDefault(c => !before.Contains(c) && c.PawnsListForReading.Contains(chosen[0]));
        if (caravan is null) return Bridge.Fail("the caravan was not formed");
        return new { success = true, caravan = caravan.GetUniqueLoadID(), name = caravan.Name, tile = (int)caravan.Tile, pawns = chosen.Select(p => p.LabelShort).ToList(), items = picks.Sum(p => p.Count) };
    });

    [Tool("rimlink/caravan_move", Description = "Move a caravan to a tile: instantly (dev teleport, default) or along a real route the game travels in time",
        ResultDescription = "success and the caravan's tile")]
    public Task<object> CaravanMove(IRimBridgeContext ctx,
        [ToolParameter(Description = "Caravan id (rimlink/caravan_form) or name", Required = true)] string caravan,
        [ToolParameter(Description = "Target tile id (rimlink/status settlements)", Required = true)] int tile,
        [ToolParameter(Description = "Teleport now instead of travelling")] bool instant = true) => Bridge.OnMain(ctx, () =>
    {
        if (FindCaravan(caravan) is not { } c) return Bridge.Fail($"no caravan '{caravan}'");
        if (tile < 0 || tile >= Find.WorldGrid.Surface.TilesCount) return Bridge.Fail($"no tile {tile}");
        if (instant)
        {
            c.pather.StopDead();
            c.Tile = tile;
            c.tweener.ResetTweenedPosToRoot();
            return new { success = true, caravan = c.GetUniqueLoadID(), tile = (int)c.Tile, moving = false };
        }
        if (!c.pather.StartPath(tile, null, repathImmediately: true)) return Bridge.Fail($"no route to tile {tile}");
        return new { success = true, caravan = c.GetUniqueLoadID(), tile = (int)c.Tile, moving = true };
    });

    [Tool("rimlink/caravan_options", Description = "The right-click options a caravan gets on a tile's world objects (a player's settlement: hand over, raid, visit, live)",
        ResultDescription = "success and options [{label, disabled, reason}]")]
    public Task<object> CaravanOptions(IRimBridgeContext ctx,
        [ToolParameter(Description = "Caravan id or name", Required = true)] string caravan,
        [ToolParameter(Description = "Tile; default the caravan's own")] int tile = -1) => Bridge.OnMain<object>(ctx, () =>
    {
        if (FindCaravan(caravan) is not { } c) return Bridge.Fail($"no caravan '{caravan}'");
        return new { success = true, options = Options(CaravanMenu(c, tile < 0 ? (int)c.Tile : tile)) };
    });

    [Tool("rimlink/caravan_order", Description = "Choose a caravan's right-click option on a tile by its label (as listed by rimlink/caravan_options). On its own tile the caravan arrives on the next game tick: unpause or step a tick",
        ResultDescription = "success/started and the top window")]
    public Task<object> CaravanOrder(IRimBridgeContext ctx,
        [ToolParameter(Description = "Caravan id or name", Required = true)] string caravan,
        [ToolParameter(Description = "Option label or a unique part of it (case-insensitive)", Required = true)] string label,
        [ToolParameter(Description = "Tile; default the caravan's own")] int tile = -1) => Bridge.OnMain(ctx, () =>
    {
        if (FindCaravan(caravan) is not { } c) return Bridge.Fail($"no caravan '{caravan}'");
        var options = CaravanMenu(c, tile < 0 ? (int)c.Tile : tile);
        if (Pick(options, label, out string? error) is not { } option) return Bridge.Fail(error!, new { options = Options(options) });
        return Targets.Act(option.action, $"'{option.Label}' ordered", new { caravan = c.GetUniqueLoadID() });
    });

    [Tool("rimlink/pods_launch", Description = "Load our pawns (and items) into a transport pod group and launch it at a tile with one of that tile's pod options by label (raid, visit, live). Dev shortcut: no launcher building or fuel; instant = the pods arrive at the next world-object tick (step about 120 game ticks or unpause)",
        ResultDescription = "success/started; on arrival the option's window or proposal follows")]
    public Task<object> PodsLaunch(IRimBridgeContext ctx,
        [ToolParameter(Description = "Comma-separated pawn ids or names", Required = true)] string pawns,
        [ToolParameter(Description = "Target tile id", Required = true)] int tile,
        [ToolParameter(Description = "Pod option label or a unique part of it; a wrong one lists the options", Required = true)] string label,
        [ToolParameter(Description = "Items as def:count, comma-separated")] string? items = null,
        [ToolParameter(Description = "Take items from anywhere on the map")] bool anywhere = true,
        [ToolParameter(Description = "Arrive at the next world-object tick instead of flying the distance")] bool instant = true) => Bridge.OnMain(ctx, () =>
    {
        if (Targets.NotPlaying() is { } no) return no;
        var map = Find.CurrentMap;
        if (tile < 0 || tile >= Find.WorldGrid.Surface.TilesCount) return Bridge.Fail($"no tile {tile}");
        if (Targets.Pawns(pawns, out string? error) is not { } chosen) return Bridge.Fail(error!);
        List<Rimlink.Client.Game.Economy.Outgoing.Pick> picks = [];
        if (!string.IsNullOrWhiteSpace(items))
        {
            if (Targets.Items(map, items, anywhere, out string? itemError) is not { } found) return Bridge.Fail(itemError!);
            picks = found;
        }

        // Loaded the way a launcher's pods are: the things leave the map into the pod's container.
        var pod = new ActiveTransporterInfo();
        var origins = new List<(Thing Thing, IntVec3 Cell)>();
        foreach (var pawn in chosen)
        {
            origins.Add((pawn, pawn.Position));
            pawn.DeSpawn();
            pod.innerContainer.TryAdd(pawn);
        }
        foreach (var pick in picks)
        {
            var cell = pick.Thing.Position;
            var thing = pick.Thing.SplitOff(pick.Count);
            origins.Add((thing, cell));
            pod.innerContainer.TryAdd(thing);
        }

        var options = new List<FloatMenuOption>();
        foreach (var obj in Find.WorldObjects.AllWorldObjects.Where(o => (int)o.Tile == tile).ToList())
            options.AddRange(obj.GetTransportersFloatMenuOptions([pod], (destination, arrival) => Launch(map, pod, destination, arrival, instant)));
        if (Pick(options, label, out string? pickError) is not { } option)
        {
            foreach (var (thing, cell) in origins)
            {
                pod.innerContainer.Remove(thing);
                GenSpawn.Spawn(thing, cell, map);
            }
            return Bridge.Fail(pickError!, new { options = Options(options) });
        }
        option.action();
        return Targets.Started($"pods launched: '{option.Label}'", new { pawns = chosen.Select(p => p.LabelShort).ToList(), items = picks.Sum(p => p.Count), instant });
    });

    /// <summary>As <see cref="FlyShipLeaving"/> hands its pods to the world map.</summary>
    private static void Launch(Map map, ActiveTransporterInfo pod, PlanetTile destination, TransportersArrivalAction arrival, bool instant)
    {
        var travelling = (TravellingTransporters)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.TravellingTransporters);
        travelling.SetFaction(Faction.OfPlayer);
        travelling.destinationTile = destination;
        travelling.arrivalAction = arrival;
        travelling.Tile = map.Tile;
        Find.WorldObjects.Add(travelling);
        travelling.AddTransporter(pod, justLeftTheMap: true);
        if (instant) AccessTools.Field(typeof(TravellingTransporters), "traveledPct").SetValue(travelling, 1f);
    }

    private static Caravan? FindCaravan(string idOrName) =>
        Find.WorldObjects.Caravans.FirstOrDefault(c => c.GetUniqueLoadID() == idOrName.Trim()
                                                       || string.Equals(c.Name, idOrName.Trim(), StringComparison.OrdinalIgnoreCase));

    private static List<FloatMenuOption> CaravanMenu(Caravan caravan, int tile)
    {
        var options = new List<FloatMenuOption>();
        foreach (var obj in Find.WorldObjects.AllWorldObjects.Where(o => (int)o.Tile == tile).ToList())
            options.AddRange(obj.GetFloatMenuOptions(caravan));
        return options;
    }

    /// <summary>The one enabled option whose label equals, or else uniquely contains, <paramref name="label"/>.</summary>
    private static FloatMenuOption? Pick(List<FloatMenuOption> options, string label, out string? error)
    {
        error = null;
        string wanted = label.Trim();
        var exact = options.Where(o => string.Equals(o.Label, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = exact.Count > 0 ? exact : options.Where(o => o.Label.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        if (matches.Count == 0) { error = $"no option matching '{label}'"; return null; }
        if (matches.Count > 1) { error = $"'{label}' matches {matches.Count} options"; return null; }
        if (matches[0].Disabled || matches[0].action is null) { error = $"'{matches[0].Label}' is disabled"; return null; }
        return matches[0];
    }

    private static List<object> Options(List<FloatMenuOption> options) =>
        options.Select(o => (object)new { label = o.Label, disabled = o.Disabled }).ToList();
}
