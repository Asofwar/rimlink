using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Game.Patches;

/// <summary>Any removal of the player's own settlement (abandon, destroyed) is reported to the server.</summary>
[HarmonyPatch(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.Remove))]
public static class Patch_WorldObjectsHolder_Remove
{
    public static void Prefix(RimWorld.Planet.WorldObject o)
    {
        if (!RimlinkSession.Active || o is not Settlement settlement || settlement.Faction != Faction.OfPlayer) return;
        if (ForeignSettlements.OwnObjectIdAt(settlement.Tile) is not { } objectId) return;
        var connection = RimlinkSession.Connection!;

        _ = Task.Run(async () =>
        {
            try
            {
                await connection.World.AbandonAsync(new AbandonRequest { ObjectId = objectId });
                RimlinkSession.Inbox.Enqueue(new ServerEvent { WorldObjectRemoved = new WorldObjectRemoved { ObjectId = objectId } });
            }
            catch (Exception ex)
            {
                RimlinkLog.Instance.Warn($"could not report abandoned settlement: {ex.Message}");
            }
        });
    }
}
