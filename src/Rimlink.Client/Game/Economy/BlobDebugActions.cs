using LudeonTK;
using RimWorld;
using Verse;

namespace Rimlink.Client.Game.Economy;

/// <summary>Dev-mode check of the blob round trip: the selected thing or pawn is serialised, removed and respawned from its blob.</summary>
public static class BlobDebugActions
{
    /// <summary>Dev convenience: the pickers list every haulable item on the map, not only those under powered beacons.</summary>
    public static bool PickAnywhere;

    [DebugAction("Rimlink", "Open economy window", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void OpenEconomyWindow()
    {
        if (!RimlinkSession.Active) { Log.Message("[Rimlink] no session"); return; }
        Find.WindowStack.Add(new UI.Economy.Dialog_Economy(Find.CurrentMap));
    }

    [DebugAction("Rimlink", "Toggle pick items anywhere", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void TogglePickAnywhere()
    {
        PickAnywhere = !PickAnywhere;
        Log.Message("[Rimlink] pick anywhere: " + PickAnywhere);
    }

    /// <summary>Dev shortcut past the caravan trip: raid another player with the colonists selected on the map (Snapshot or Real by the dialog).</summary>
    [DebugAction("Rimlink", "Raid a player with selected pawns", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void RaidPlayer()
    {
        if (!RimlinkSession.Active) { Log.Message("[Rimlink] no session"); return; }
        var pawns = Find.Selector.SelectedObjects.OfType<Pawn>().Where(p => p.IsColonist).ToList();
        if (pawns.Count == 0) { Log.Message("[Rimlink] select colonists first"); return; }
        var options = new List<FloatMenuOption>();
        foreach (var owner in Players())
            options.Add(new FloatMenuOption(owner.Name, () => Find.WindowStack.Add(new UI.Raids.Dialog_Raid(owner, pawns, fromPods: false,
                (chosen, mode, timing) => Raids.RaidOutgoing.LaunchFromCaravanless(Find.CurrentMap, owner, chosen, mode, timing), onCancel: null))));
        if (options.Count > 0) Find.WindowStack.Add(new FloatMenu(options));
    }

    /// <summary>Dev shortcut past the caravan trip: visit another player with the colonists selected on the map (copies).</summary>
    [DebugAction("Rimlink", "Visit a player with selected pawns", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void VisitPlayer()
    {
        if (!RimlinkSession.Active) { Log.Message("[Rimlink] no session"); return; }
        var pawns = Find.Selector.SelectedObjects.OfType<Pawn>().Where(p => p.IsColonist).ToList();
        if (pawns.Count == 0) { Log.Message("[Rimlink] select colonists first"); return; }
        var options = new List<FloatMenuOption>();
        foreach (var owner in Players()) options.Add(new FloatMenuOption(owner.Name, () => Visits.VisitSession.Begin(owner, pawns)));
        if (options.Count > 0) Find.WindowStack.Add(new FloatMenu(options));
    }

    private static IEnumerable<Rimlink.Protocol.V1.PlayerRef> Players()
    {
        long me = RimlinkSession.MyPlayerId;
        var seen = new Dictionary<long, Rimlink.Protocol.V1.PlayerRef>();
        foreach (var o in RimlinkSession.State.Objects.All) if (o.Owner is not null) seen[o.Owner.PlayerId] = o.Owner;
        foreach (var p in RimlinkSession.State.Players.Online) seen[p.PlayerId] = p;
        return seen.Values.Where(p => p.PlayerId != me).OrderBy(p => p.Name);
    }

    [DebugAction("Rimlink", "Blob round-trip selected thing", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void BlobRoundTripSelected()
    {
        foreach (var selected in Find.Selector.SelectedObjects.OfType<Thing>().ToList())
        {
            var map = selected.Map;
            var cell = selected.Position;
            try
            {
                if (selected is Pawn pawn)
                {
                    var blob = ThingBlobs.SerializePawn(pawn);
                    string label = pawn.LabelCap;
                    pawn.DeSpawn();
                    var again = ThingBlobs.DeserializePawn(blob);
                    GenSpawn.Spawn(again, cell, map);
                    pawn.Destroy();
                    Log.Message($"[Rimlink] blob round-trip ok: {label} → {again.LabelCap} {blob.ScribeXmlZstd.Length} B, skills {blob.Skills.Count}, hediffs {again.health.hediffSet.hediffs.Count}, apparel {again.apparel?.WornApparelCount ?? 0}");
                }
                else
                {
                    var blob = ThingBlobs.SerializeItem(selected);
                    string label = selected.LabelCap;
                    selected.DeSpawn();
                    var again = ThingBlobs.DeserializeItem(blob);
                    GenPlace.TryPlaceThing(again, cell, map, ThingPlaceMode.Direct);
                    selected.Destroy();
                    Log.Message($"[Rimlink] blob round-trip ok: {label} → {again.LabelCap} {blob.ScribeXmlZstd.Length} B, count {blob.Count}→{again.stackCount}, hp {blob.HitPoints}→{(again.def.useHitPoints ? again.HitPoints : 0)}, quality {blob.Quality}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Rimlink] blob round-trip FAILED for {selected}: {ex}");
            }
        }
    }
}
