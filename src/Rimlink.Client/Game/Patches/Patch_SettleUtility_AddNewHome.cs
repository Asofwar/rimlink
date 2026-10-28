using Grpc.Core;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>A new player home is registered on the server; if the server refuses, the local settlement is removed again.</summary>
[HarmonyPatch(typeof(SettleUtility), nameof(SettleUtility.AddNewHome))]
public static class Patch_SettleUtility_AddNewHome
{
    public static void Postfix(PlanetTile tile, Faction faction, Settlement __result)
    {
        if (!RimlinkSession.Active || faction != Faction.OfPlayer || __result == null) return;
        int tileId = tile;
        string name = __result.Name ?? Faction.OfPlayer.Name;
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
                    if (__result.Spawned) Find.WorldObjects.Remove(__result);
                    Find.WindowStack.Add(new Dialog_MessageBox("Rimlink.Settle.Refused".Translate(reason)));
                });
            }
        });
    }
}
