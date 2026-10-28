using System.Text;
using Grpc.Core;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.UI;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>
/// A new player settlement is registered on the server. Patched at <see cref="WorldObjectsHolder.Add"/> because the
/// starting colony is created by <c>ScenPart_PlayerFaction.PreMapGenerate</c>, which bypasses
/// <see cref="SettleUtility.AddNewHome"/>. Loading a save does not go through Add, so loaded settlements are not re-registered.
/// A refusal (tile taken, limit) never destroys a map: a settlement that already has one ends the session and returns
/// to the menu; one without a map (just placed from the world view) is removed again.
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
                RimlinkLog.Instance.Warn($"settlement on tile {tileId} refused: {reason}");
                MainThread.Post(() =>
                {
                    if (RimlinkSession.Connection != connection) return;
                    if (settlement.HasMap)
                    {
                        Find.TickManager.Pause();
                        var box = new Dialog_MessageBox("Rimlink.Settle.Refused".Translate(reason), "Rimlink.Net.ToMenu".Translate(), RimlinkGameComponent.AbortToMenu);
                        box.forcePause = true;
                        box.closeOnCancel = false;
                        Find.WindowStack.Add(box);
                    }
                    else
                    {
                        if (settlement.Spawned) Find.WorldObjects.Remove(settlement);
                        Find.WindowStack.Add(new Dialog_MessageBox("Rimlink.Settle.Refused".Translate(reason)));
                    }
                });
            }
        });
    }
}

/// <summary>Other players' settlements block a tile the same way NPC settlements do, so the site picker says so before the server has to.</summary>
[HarmonyPatch(typeof(TileFinder), nameof(TileFinder.IsValidTileForNewSettlement))]
public static class Patch_TileFinder_IsValidTileForNewSettlement
{
    public static void Postfix(PlanetTile tile, StringBuilder reason, ref bool __result)
    {
        if (!__result || !RimlinkSession.Active || Find.World == null) return;
        if (!Find.WorldObjects.AllWorldObjects.OfType<PlayerSettlement>().Any(s => s.Tile == tile)) return;
        __result = false;
        reason?.Append("Rimlink.Tile.Taken".Translate());
    }
}
