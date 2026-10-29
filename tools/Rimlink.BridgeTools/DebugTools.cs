using RimBridgeServer.Sdk;
using RimWorld;
using Rimlink.Client.Game.Raids;
using Verse;

namespace Rimlink.BridgeTools;

/// <summary>
/// Set-ups and readouts for checks the flows alone cannot make: any pawn on the map (not only ours), a faction given an ideo
/// of its own (classic worlds share one ideo among all factions), selection for the dev actions that act on the selection.
/// </summary>
public sealed class DebugTools
{
    [Tool("rimlink/map_pawns", Description = "Every spawned pawn on the current map with its faction, kind and ideo (current and previous)",
        ResultDescription = "success plus one entry per pawn")]
    public Task<object> MapPawns(IRimBridgeContext ctx,
        [ToolParameter(Description = "Only pawns of this faction (load id, def name or name)")] string faction = "") => Bridge.OnMain<object>(ctx, () =>
    {
        if (NoMap() is { } no) return no;
        Faction? only = null;
        if (faction.Length > 0 && (only = FindFaction(faction)) is null) return Bridge.Fail($"unknown faction '{faction}'");
        var pawns = Find.CurrentMap.mapPawns.AllPawnsSpawned.Where(p => only is null || p.Faction == only).Select(p => new
        {
            id = p.GetUniqueLoadID(),
            name = p.LabelShort,
            faction = p.Faction is { } f ? $"{f.Name} ({f.GetUniqueLoadID()})" : null,
            kind = p.kindDef?.defName,
            ideo = p.Ideo is { } i ? IdeoLabel(i) : null,
            previousIdeos = p.ideo?.PreviousIdeos?.Select(IdeoLabel).ToList(),
        }).ToList();
        return new { success = true, playerIdeo = Faction.OfPlayer.ideos?.PrimaryIdeo is { } own ? IdeoLabel(own) : null, pawns };
    });

    [Tool("rimlink/select", Description = "Select things on the current map by load id, any faction (for dev actions on the selection)",
        ResultDescription = "success plus the selected labels")]
    public Task<object> Select(IRimBridgeContext ctx,
        [ToolParameter(Description = "Comma-separated load ids (rimlink/map_pawns), e.g. Thing_Human42", Required = true)] string things) => Bridge.OnMain<object>(ctx, () =>
    {
        if (NoMap() is { } no) return no;
        var map = Find.CurrentMap;
        var chosen = new List<Thing>();
        var missing = new List<string>();
        foreach (string id in things.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (map.listerThings.AllThings.FirstOrDefault(t => t.Spawned && t.GetUniqueLoadID() == id) is { } thing) chosen.Add(thing);
            else missing.Add(id);
        }
        if (missing.Count > 0) return Bridge.Fail($"not on the map: {string.Join(", ", missing)}");
        if (chosen.Count == 0) return Bridge.Fail("no things given");
        Find.Selector.ClearSelection();
        foreach (var thing in chosen) Find.Selector.Select(thing, playSound: false, forceDesignatorDeselect: false);
        return new { success = true, selected = Find.Selector.SelectedObjects.OfType<Thing>().Select(t => t.LabelCap.ToString()).ToList() };
    });

    [Tool("rimlink/faction_ideo", Description = "Give a faction a newly generated primary ideo (RimlinkRaiders creates the raider faction if it is missing)",
        ResultDescription = "success plus the faction, its previous and new primary ideo and the player's")]
    public Task<object> FactionIdeo(IRimBridgeContext ctx,
        [ToolParameter(Description = "Faction load id (Faction_12), def name or name", Required = true)] string faction) => Bridge.OnMain<object>(ctx, () =>
    {
        if (NoMap() is { } no) return no;
        if (!ModsConfig.IdeologyActive) return Bridge.Fail("Ideology is not active");
        var target = string.Equals(faction, RimlinkFactions.RaidersDefName, StringComparison.OrdinalIgnoreCase) ? RimlinkFactions.Raiders : FindFaction(faction);
        if (target is null) return Bridge.Fail($"unknown faction '{faction}'");
        if (target.IsPlayer) return Bridge.Fail("not the player's faction");
        if (target.ideos is null) return Bridge.Fail($"{target.Name} has no ideos");
        var before = target.ideos.PrimaryIdeo;
        var ideo = IdeoGenerator.GenerateIdeo(new IdeoGenerationParms(target.def));
        ideo.primaryFactionColor = target.Color;
        Find.IdeoManager.Add(ideo);
        target.ideos.SetPrimary(ideo);
        return new
        {
            success = true,
            faction = $"{target.Name} ({target.GetUniqueLoadID()})",
            before = before is null ? null : IdeoLabel(before),
            primary = IdeoLabel(ideo),
            playerIdeo = Faction.OfPlayer.ideos?.PrimaryIdeo is { } own ? IdeoLabel(own) : null,
        };
    });

    private static object? NoMap() =>
        Current.ProgramState != ProgramState.Playing || Find.CurrentMap is null ? Bridge.Fail($"no map: the game is in {Current.ProgramState}") : null;

    private static Faction? FindFaction(string token) => Find.FactionManager.AllFactions.FirstOrDefault(f =>
        f.GetUniqueLoadID() == token
        || string.Equals(f.def.defName, token, StringComparison.OrdinalIgnoreCase)
        || string.Equals(f.Name, token, StringComparison.OrdinalIgnoreCase));

    private static string IdeoLabel(Ideo ideo) => $"{ideo.name} ({ideo.GetUniqueLoadID()})";
}
