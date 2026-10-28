using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.Game.Raids;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Visits;

/// <summary>"Go there and visit": on arrival at another player's settlement copies of the caravan's members go visiting.</summary>
public sealed class CaravanArrivalAction_RimlinkVisit : CaravanArrivalAction
{
    private PlayerSettlement? settlement;

    public CaravanArrivalAction_RimlinkVisit() { }

    public CaravanArrivalAction_RimlinkVisit(PlayerSettlement settlement) => this.settlement = settlement;

    public override string Label => "Rimlink.Visit.CaravanVisit".Translate(settlement?.OwnerName ?? "?");

    public override string ReportString => "Rimlink.Visit.CaravanVisit".Translate(settlement?.OwnerName ?? "?");

    public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile)
    {
        var report = base.StillValid(caravan, destinationTile);
        if (!report) return report;
        if (settlement is null || !settlement.Spawned || settlement.Tile != destinationTile) return false;
        return CanVisit(settlement);
    }

    public override void Arrived(Caravan caravan)
    {
        if (settlement is null) return;
        PlayerRef? owner = RimlinkSession.State.Objects.TryGet(settlement.ObjectId)?.Owner;
        if (owner is null) return;
        VisitSession.Begin(owner, caravan.PawnsListForReading.ToList());
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_References.Look(ref settlement, "settlement");
    }

    public static FloatMenuAcceptanceReport CanVisit(PlayerSettlement settlement)
    {
        if (!RimlinkSession.Active || RimlinkSession.Visits is null || VisitSession.Active) return false;
        if (settlement.ObjectId == 0 || RimlinkSession.State.Objects.TryGet(settlement.ObjectId)?.Owner is null) return false;
        return true;
    }
}

/// <summary>Transport pods aimed at another player's settlement for a visit: the pods fly home again, copies of the pawns visit.</summary>
public sealed class TransportersArrivalAction_RimlinkVisit : TransportersArrivalAction
{
    private PlayerSettlement? settlement;

    public TransportersArrivalAction_RimlinkVisit() { }

    public TransportersArrivalAction_RimlinkVisit(PlayerSettlement settlement) => this.settlement = settlement;

    public override bool GeneratesMap => false;

    public override FloatMenuAcceptanceReport StillValid(IEnumerable<IThingHolder> pods, PlanetTile destinationTile)
    {
        var report = base.StillValid(pods, destinationTile);
        if (!report) return report;
        if (settlement is null || !settlement.Spawned || settlement.Tile != destinationTile) return false;
        if (!TransportersArrivalActionUtility.AnyNonDownedColonist(pods)) return false;
        return CaravanArrivalAction_RimlinkVisit.CanVisit(settlement);
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
        // Copies visit; everything that flew in flies back home.
        if (owner is not null) VisitSession.Begin(owner, pawns);
        RaidOutgoing.ReturnPodPawnsHome(pawns);
        var home = Find.AnyPlayerHomeMap;
        if (home is not null && cargo.Count > 0) DropPodUtility.DropThingsNear(DropCellFinder.TradeDropSpot(home), home, cargo, openDelay: 110, canInstaDropDuringInit: false, leaveSlag: false, canRoofPunch: false, forbid: false);
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_References.Look(ref settlement, "settlement");
    }
}
