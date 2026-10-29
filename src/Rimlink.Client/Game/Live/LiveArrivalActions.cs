using RimWorld;
using RimWorld.Planet;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Live;

/// <summary>"Live raid"/"Live visit": on arrival at another player's settlement the whole caravan is proposed for a live session.</summary>
public sealed class CaravanArrivalAction_RimlinkLive : CaravanArrivalAction
{
    private PlayerSettlement? settlement;
    private LiveKind kind;

    public CaravanArrivalAction_RimlinkLive() { }

    public CaravanArrivalAction_RimlinkLive(PlayerSettlement settlement, LiveKind kind)
    {
        this.settlement = settlement;
        this.kind = kind;
    }

    public override string Label => (kind == LiveKind.LiveRaid ? "Rimlink.Live.CaravanRaid" : "Rimlink.Live.CaravanVisit").Translate(settlement?.OwnerName ?? "?");

    public override string ReportString => Label;

    public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile)
    {
        var report = base.StillValid(caravan, destinationTile);
        if (!report) return report;
        if (settlement is null || !settlement.Spawned || settlement.Tile != destinationTile) return false;
        return CanPropose(settlement, kind);
    }

    public override void Arrived(Caravan caravan)
    {
        if (settlement is not null) Open(caravan, settlement, kind);
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_References.Look(ref settlement, "settlement");
        Scribe_Values.Look(ref kind, "kind");
    }

    public static FloatMenuAcceptanceReport CanPropose(PlayerSettlement settlement, LiveKind kind)
    {
        if (!RimlinkSession.Active || RimlinkSession.Live is null) return false;
        if (settlement.ObjectId == 0 || RimlinkSession.State.Objects.TryGet(settlement.ObjectId)?.Owner is not { } owner) return false;
        if (!RimlinkSession.State.Players.Online.Any(p => p.PlayerId == owner.PlayerId)) return FloatMenuAcceptanceReport.WithFailMessage("Rimlink.Live.HostOffline".Translate(owner.Name));
        if (kind == LiveKind.LiveRaid && RimlinkSession.State.Raids.Flags.Peaceful) return FloatMenuAcceptanceReport.WithFailMessage("Rimlink.Raid.YouArePeaceful".Translate());
        return true;
    }

    public static void Open(Caravan caravan, PlayerSettlement settlement, LiveKind kind)
    {
        PlayerRef? owner = RimlinkSession.State.Objects.TryGet(settlement.ObjectId)?.Owner;
        if (owner is null || !RimlinkSession.Active) return;
        LiveGuest.ProposeFromCaravan(caravan, owner, kind, caravan.PawnsListForReading.ToList(), RaidTiming.TimingInstant);
    }
}

/// <summary>"Live raid"/"Live visit" by transport pods: on arrival the pawns inside are proposed for a live session (spec §2).</summary>
public sealed class TransportersArrivalAction_RimlinkLive : TransportersArrivalAction
{
    private PlayerSettlement? settlement;
    private LiveKind kind;

    public TransportersArrivalAction_RimlinkLive() { }

    public TransportersArrivalAction_RimlinkLive(PlayerSettlement settlement, LiveKind kind)
    {
        this.settlement = settlement;
        this.kind = kind;
    }

    public override bool GeneratesMap => false;

    public override FloatMenuAcceptanceReport StillValid(IEnumerable<IThingHolder> pods, PlanetTile destinationTile)
    {
        var report = base.StillValid(pods, destinationTile);
        if (!report) return report;
        if (settlement is null || !settlement.Spawned || settlement.Tile != destinationTile) return false;
        return CanPropose(pods, settlement, kind);
    }

    /// <summary>Someone has to control the pawns: pods without an able colonist (cargo only) are not offered.</summary>
    public static FloatMenuAcceptanceReport CanPropose(IEnumerable<IThingHolder> pods, PlayerSettlement settlement, LiveKind kind)
    {
        if (!TransportersArrivalActionUtility.AnyNonDownedColonist(pods)) return false;
        return CaravanArrivalAction_RimlinkLive.CanPropose(settlement, kind);
    }

    public override void Arrived(List<ActiveTransporterInfo> transporters, PlanetTile tile)
    {
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
        if (pawns.Count == 0)
        {
            // Not offered without a colonist; should it still happen, the cargo flies home instead of vanishing with the pods.
            var home = Find.AnyPlayerHomeMap;
            if (home is not null && cargo.Count > 0) DropPodUtility.DropThingsNear(DropCellFinder.TradeDropSpot(home), home, cargo, openDelay: 110, canInstaDropDuringInit: false, leaveSlag: false, canRoofPunch: false, forbid: false);
            return;
        }
        // Loose cargo rides in the pawns' inventories: it goes to the host and back with them.
        var carrier = pawns.FirstOrDefault(p => p.RaceProps.Humanlike) ?? pawns[0];
        foreach (var thing in cargo) carrier.inventory.innerContainer.TryAdd(thing);
        PlayerRef? owner = settlement is null ? null : RimlinkSession.State.Objects.TryGet(settlement.ObjectId)?.Owner;
        if (owner is null || !RimlinkSession.Active)
        {
            Raids.RaidOutgoing.ReturnPodPawnsHome(pawns);
            return;
        }
        LiveGuest.ProposeFromPods(pawns, owner, kind, RaidTiming.TimingInstant);
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_References.Look(ref settlement, "settlement");
        Scribe_Values.Look(ref kind, "kind");
    }
}
