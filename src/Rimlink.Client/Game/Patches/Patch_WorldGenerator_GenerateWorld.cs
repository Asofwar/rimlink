using Grpc.Core;
using HarmonyLib;
using RimWorld.Planet;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>The admin's freshly generated planet becomes the server's world (parameters + NPC tiles).</summary>
[HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GenerateWorld))]
public static class Patch_WorldGenerator_GenerateWorld
{
    public static void Postfix(RimWorld.Planet.World __result)
    {
        if (!RimlinkSession.Active || !RimlinkSession.IsAdmin || RimlinkSession.World is { Exists: true } || __result == null) return;
        var (parameters, npcTiles) = WorldSetup.CollectParams(__result);
        var connection = RimlinkSession.Connection!;
        var request = new CreateWorldRequest { Params = parameters };
        request.NpcTiles.AddRange(npcTiles);

        _ = Task.Run(async () =>
        {
            try
            {
                RimlinkSession.World = await connection.World.CreateAsync(request);
                RimlinkLog.Instance.Info($"world '{parameters.Seed}' registered on the server with {npcTiles.Count} NPC tiles");
            }
            catch (RpcException ex)
            {
                RimlinkLog.Instance.Error($"could not register the world: {ex.Status.Detail}");
                LongEventHandler.ExecuteWhenFinished(() => Find.WindowStack.Add(new Dialog_MessageBox("Rimlink.Flow.Error".Translate(ex.Status.Detail))));
            }
        });
    }
}
