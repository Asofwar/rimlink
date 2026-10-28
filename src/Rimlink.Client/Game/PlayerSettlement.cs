using RimWorld.Planet;
using Rimlink.Client.Game.Economy;
using Rimlink.Client.Game.Raids;
using Rimlink.Client.Game.Visits;
using UnityEngine;
using Verse;

namespace Rimlink.Client.Game;

/// <summary>Another player's settlement on the world map. Not saved: rebuilt from the server on every login.</summary>
public sealed class PlayerSettlement : WorldObject
{
    public long ObjectId;
    public string OwnerName = "";
    public string SettlementName = "";
    public uint ColorArgb = 0xFF4A90D9;

    public override string Label => SettlementName.Length > 0 ? SettlementName : OwnerName;

    public override Color ExpandingIconColor => new(
        ((ColorArgb >> 16) & 0xFF) / 255f,
        ((ColorArgb >> 8) & 0xFF) / 255f,
        (ColorArgb & 0xFF) / 255f,
        ((ColorArgb >> 24) & 0xFF) / 255f);

    public override string GetInspectString() => "Rimlink.Settlement.Inspect".Translate(OwnerName);

    public override bool SelectableNow => true;

    /// <summary>Right-click with a caravan selected: travel here and hand things over on arrival.</summary>
    public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan)
    {
        foreach (var option in base.GetFloatMenuOptions(caravan)) yield return option;
        foreach (var option in CaravanArrivalActionUtility.GetFloatMenuOptions(
                     () => CaravanArrivalAction_RimlinkTransfer.CanTransfer(caravan, this),
                     () => new CaravanArrivalAction_RimlinkTransfer(this),
                     "Rimlink.Economy.CaravanTransfer".Translate(OwnerName),
                     caravan, Tile, this))
        {
            yield return option;
        }
        foreach (var option in CaravanArrivalActionUtility.GetFloatMenuOptions(
                     () => CaravanArrivalAction_RimlinkRaid.CanRaid(caravan, this),
                     () => new CaravanArrivalAction_RimlinkRaid(this),
                     "Rimlink.Raid.CaravanRaid".Translate(OwnerName),
                     caravan, Tile, this))
        {
            yield return option;
        }
        foreach (var option in CaravanArrivalActionUtility.GetFloatMenuOptions(
                     () => CaravanArrivalAction_RimlinkVisit.CanVisit(this),
                     () => new CaravanArrivalAction_RimlinkVisit(this),
                     "Rimlink.Visit.CaravanVisit".Translate(OwnerName),
                     caravan, Tile, this))
        {
            yield return option;
        }
    }

    /// <summary>Transport pods aimed here: the pawns inside raid the settlement on arrival.</summary>
    public override IEnumerable<FloatMenuOption> GetTransportersFloatMenuOptions(IEnumerable<IThingHolder> pods, Action<PlanetTile, TransportersArrivalAction> launchAction)
    {
        foreach (var option in base.GetTransportersFloatMenuOptions(pods, launchAction)) yield return option;
        foreach (var option in TransportersArrivalActionUtility.GetFloatMenuOptions(
                     () => TransportersArrivalAction_RimlinkRaid.CanRaid(pods, this),
                     () => new TransportersArrivalAction_RimlinkRaid(this),
                     "Rimlink.Raid.PodRaid".Translate(OwnerName),
                     launchAction, Tile))
        {
            yield return option;
        }
        foreach (var option in TransportersArrivalActionUtility.GetFloatMenuOptions(
                     () => CaravanArrivalAction_RimlinkVisit.CanVisit(this),
                     () => new TransportersArrivalAction_RimlinkVisit(this),
                     "Rimlink.Visit.PodVisit".Translate(OwnerName),
                     launchAction, Tile))
        {
            yield return option;
        }
    }

    /// <summary>A caravan already standing here can open the transfer window again.</summary>
    public override IEnumerable<Gizmo> GetCaravanGizmos(Caravan caravan)
    {
        foreach (var gizmo in base.GetCaravanGizmos(caravan)) yield return gizmo;
        if (caravan.Tile == Tile && CaravanArrivalAction_RimlinkRaid.CanRaid(caravan, this))
        {
            yield return new Command_Action
            {
                defaultLabel = "Rimlink.Raid.CaravanRaidGizmo".Translate(),
                defaultDesc = "Rimlink.Raid.CaravanRaid".Translate(OwnerName),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/Attack", reportFailure: false),
                action = () => CaravanArrivalAction_RimlinkRaid.Open(caravan, this),
            };
        }
        if (caravan.Tile == Tile && CaravanArrivalAction_RimlinkVisit.CanVisit(this))
        {
            yield return new Command_Action
            {
                defaultLabel = "Rimlink.Visit.CaravanVisitGizmo".Translate(),
                defaultDesc = "Rimlink.Visit.CaravanVisit".Translate(OwnerName),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/Install", reportFailure: false),
                action = () => VisitSession.Begin(RimlinkSession.State.Objects.TryGet(ObjectId)!.Owner, caravan.PawnsListForReading.ToList()),
            };
        }
        if (caravan.Tile != Tile || !CaravanArrivalAction_RimlinkTransfer.CanTransfer(caravan, this)) yield break;
        yield return new Command_Action
        {
            defaultLabel = "Rimlink.Economy.CaravanTransferGizmo".Translate(),
            defaultDesc = "Rimlink.Economy.CaravanTransfer".Translate(OwnerName),
            icon = ContentFinder<Texture2D>.Get("UI/Commands/Trade", reportFailure: false),
            action = () => CaravanArrivalAction_RimlinkTransfer.Open(caravan, this),
        };
    }
}
