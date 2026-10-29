using Verse;

namespace Rimlink.Client.Game.Economy;

/// <summary>Where parcels land on this map (<see cref="Incoming.DropCenter"/>); a new spot replaces the old one.</summary>
public sealed class Building_DeliverySpot : Building
{
    public override void SpawnSetup(Map map, bool respawningAfterLoad)
    {
        base.SpawnSetup(map, respawningAfterLoad);
        if (respawningAfterLoad) return;
        foreach (var other in map.listerThings.ThingsOfDef(def).Where(t => t != this).ToList())
            other.Destroy(DestroyMode.Vanish);
    }
}
