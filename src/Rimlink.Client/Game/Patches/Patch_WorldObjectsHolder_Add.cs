using Grpc.Core;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>
/// A new player settlement is registered on the server; if the server refuses, the local settlement is removed again.
/// Patched at <see cref="WorldObjectsHolder.Add"/> because the starting colony is created by
/// <c>ScenPart_PlayerFaction.PreMapGenerate</c>, which bypasses <see cref="SettleUtility.AddNewHome"/>.
/// Loading a save does not go through Add, so loaded settlements are not re-registered.
/// </summary>
[HarmonyPatch(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.Add))]
public static class Patch_WorldObjectsHolder_Add
{
    public static void Postfix(RimWorld.Planet.WorldObject o)
    {
        // Faction.IsPlayer, not Faction.OfPlayer: NPC settlements are added during world generation before the player faction exists.
        if (!RimlinkSession.Active || o is not Settlement settlement || settlement.Faction is not { IsPlayer: true } faction) return;
        int tileId = settlement.Tile;
        string name = settlement.Name ?? faction.Name;
        var connection = RimlinkSession.Connection!;

        _ = Task.Run(async () =>
        {
            try
            {
                var created = await connection.World.SettleAsync(new SettleRequest { Tile = tileId, Name = name });
                RimlinkSession.Inbox.Enqueue(new ServerEvent { WorldObjectAdded = new WorldObjectAdded { Object = created } });
                RimlinkLog.Instance.Info($"settled tile {tileId} as '{name}' (server id {created.Id})");
            }
            catch (RpcException ex)
            {
                string reason = ex.Status.Detail;
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    if (settlement.Spawned) Find.WorldObjects.Remove(settlement);
                    Find.WindowStack.Add(new Dialog_MessageBox("Rimlink.Settle.Refused".Translate(reason)));
                });
            }
        });
    }
}
