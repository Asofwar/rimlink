using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.UI.Economy;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Economy;

/// <summary>
/// "Go there and hand things over": on arrival at another player's settlement the economy window opens in caravan
/// mode. Player settlements are not saved, so a pending arrival action loaded from a save resolves to nothing and
/// reports itself invalid.
/// </summary>
public sealed class CaravanArrivalAction_RimlinkTransfer : CaravanArrivalAction
{
    private PlayerSettlement? settlement;

    public CaravanArrivalAction_RimlinkTransfer() { }

    public CaravanArrivalAction_RimlinkTransfer(PlayerSettlement settlement) => this.settlement = settlement;

    public override string Label => "Rimlink.Economy.CaravanTransfer".Translate(settlement?.OwnerName ?? "?");

    public override string ReportString => "Rimlink.Economy.CaravanTransfer".Translate(settlement?.OwnerName ?? "?");

    public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile)
    {
        var report = base.StillValid(caravan, destinationTile);
        if (!report) return report;
        if (settlement is null || !settlement.Spawned || settlement.Tile != destinationTile) return false;
        return CanTransfer(caravan, settlement);
    }

    public override void Arrived(Caravan caravan)
    {
        if (settlement is null) return;
        Open(caravan, settlement);
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_References.Look(ref settlement, "settlement");
    }

    public static FloatMenuAcceptanceReport CanTransfer(Caravan caravan, PlayerSettlement settlement)
    {
        if (!RimlinkSession.Active) return false;
        if (settlement.ObjectId == 0 || RimlinkSession.State.Objects.TryGet(settlement.ObjectId)?.Owner is null) return false;
        return true;
    }

    public static void Open(Caravan caravan, PlayerSettlement settlement)
    {
        PlayerRef? owner = RimlinkSession.State.Objects.TryGet(settlement.ObjectId)?.Owner;
        if (owner is null || !RimlinkSession.Active) return;
        Find.WindowStack.Add(new Dialog_Economy(caravan, owner));
    }
}
