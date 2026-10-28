using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.UI.Raids;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Raids;

/// <summary>"Go there and attack": on arrival at another player's settlement the raid window opens for the caravan's members.</summary>
public sealed class CaravanArrivalAction_RimlinkRaid : CaravanArrivalAction
{
    private PlayerSettlement? settlement;

    public CaravanArrivalAction_RimlinkRaid() { }

    public CaravanArrivalAction_RimlinkRaid(PlayerSettlement settlement) => this.settlement = settlement;

    public override string Label => "Rimlink.Raid.CaravanRaid".Translate(settlement?.OwnerName ?? "?");

    public override string ReportString => "Rimlink.Raid.CaravanRaid".Translate(settlement?.OwnerName ?? "?");

    public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile)
    {
        var report = base.StillValid(caravan, destinationTile);
        if (!report) return report;
        if (settlement is null || !settlement.Spawned || settlement.Tile != destinationTile) return false;
        return CanRaid(caravan, settlement);
    }

    public override void Arrived(Caravan caravan)
    {
        if (settlement is not null) Open(caravan, settlement);
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_References.Look(ref settlement, "settlement");
    }

    public static FloatMenuAcceptanceReport CanRaid(Caravan caravan, PlayerSettlement settlement)
    {
        if (!RimlinkSession.Active || RimlinkSession.Raids is null) return false;
        if (settlement.ObjectId == 0 || RimlinkSession.State.Objects.TryGet(settlement.ObjectId)?.Owner is null) return false;
        if (RimlinkSession.State.Raids.Flags.Peaceful) return FloatMenuAcceptanceReport.WithFailMessage("Rimlink.Raid.YouArePeaceful".Translate());
        return true;
    }

    public static void Open(Caravan caravan, PlayerSettlement settlement)
    {
        PlayerRef? owner = RimlinkSession.State.Objects.TryGet(settlement.ObjectId)?.Owner;
        if (owner is null || !RimlinkSession.Active) return;
        Find.WindowStack.Add(new Dialog_Raid(owner, caravan.PawnsListForReading.ToList(), fromPods: false,
            (pawns, mode, timing) => RaidOutgoing.LaunchFromCaravan(caravan, owner, pawns, mode, timing), onCancel: null));
    }
}

/// <summary>Transport pods launched at another player's settlement: on arrival the pawns inside raid it (Real mode).</summary>
public sealed class TransportersArrivalAction_RimlinkRaid : TransportersArrivalAction
{
    private PlayerSettlement? settlement;

    public TransportersArrivalAction_RimlinkRaid() { }

    public TransportersArrivalAction_RimlinkRaid(PlayerSettlement settlement) => this.settlement = settlement;

    public override bool GeneratesMap => false;

    public override FloatMenuAcceptanceReport StillValid(IEnumerable<IThingHolder> pods, PlanetTile destinationTile)
    {
        var report = base.StillValid(pods, destinationTile);
        if (!report) return report;
        if (settlement is null || !settlement.Spawned || settlement.Tile != destinationTile) return false;
        return CanRaid(pods, settlement);
    }

    public override void Arrived(List<ActiveTransporterInfo> transporters, PlanetTile tile)
    {
        if (settlement is null) return;
        PlayerRef? owner = RimlinkSession.State.Objects.TryGet(settlement.ObjectId)?.Owner;
        var pawns = new List<Pawn>();
        var cargo = new List<Thing>();
        foreach (var pod in transporters)
        {
            foreach (var thing in pod.innerContainer.ToList())
            {
                pod.innerContainer.Remove(thing);
                if (thing is Pawn pawn) pawns.Add(pawn); else cargo.Add(thing);
            }
        }
        if (pawns.Count == 0) return;
        // A caravan on the tile holds the pawns (and the cargo) while the window is open: a save, a crash or a lost
        // connection in between leaves them a caravan to walk home with, not pawns that exist nowhere.
        var caravan = CaravanMaker.MakeCaravan(pawns, Faction.OfPlayer, tile, addToWorldPawnsIfNotAlready: true);
        foreach (var thing in cargo) CaravanInventoryUtility.GiveThing(caravan, thing);
        if (owner is null || !RimlinkSession.Active)
        {
            RaidOutgoing.ReturnPodPawnsHome(caravan);
            return;
        }
        Find.WindowStack.Add(new Dialog_Raid(owner, pawns, fromPods: true,
            (_, _, timing) => RaidOutgoing.LaunchFromPods(caravan, owner, timing),
            onCancel: () => RaidOutgoing.ReturnPodPawnsHome(caravan)));
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_References.Look(ref settlement, "settlement");
    }

    public static FloatMenuAcceptanceReport CanRaid(IEnumerable<IThingHolder> pods, PlayerSettlement settlement)
    {
        if (!RimlinkSession.Active || RimlinkSession.Raids is null) return false;
        if (settlement.ObjectId == 0 || RimlinkSession.State.Objects.TryGet(settlement.ObjectId)?.Owner is null) return false;
        if (RimlinkSession.State.Raids.Flags.Peaceful) return FloatMenuAcceptanceReport.WithFailMessage("Rimlink.Raid.YouArePeaceful".Translate());
        if (!TransportersArrivalActionUtility.AnyNonDownedColonist(pods)) return false;
        if (pods.Any(p => p.GetDirectlyHeldThings().Any(t => t is not Pawn))) return FloatMenuAcceptanceReport.WithFailMessage("Rimlink.Raid.PodsOnlyPawns".Translate());
        return true;
    }
}
