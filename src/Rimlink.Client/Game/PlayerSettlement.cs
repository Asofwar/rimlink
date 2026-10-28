using RimWorld.Planet;
using Rimlink.Client.Game.Economy;
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
    }

    /// <summary>A caravan already standing here can open the transfer window again.</summary>
    public override IEnumerable<Gizmo> GetCaravanGizmos(Caravan caravan)
    {
        foreach (var gizmo in base.GetCaravanGizmos(caravan)) yield return gizmo;
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
