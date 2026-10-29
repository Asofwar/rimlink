using HarmonyLib;
using RimWorld;
using Rimlink.Client.Game.Live;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>Feeds the host's delta collector with what changed on the hosted map between full rescans.</summary>
[HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.Spawn), typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool))]
public static class Patch_GenSpawn_Spawn_Live
{
    public static void Postfix(Thing newThing, Map map)
    {
        if (LiveHost.Active && map == LiveHost.Map && newThing is not null) LiveHost.Collector?.Spawned(newThing);
    }
}

[HarmonyPatch(typeof(Thing), nameof(Thing.DeSpawn))]
public static class Patch_Thing_DeSpawn_Live
{
    public static void Prefix(Thing __instance)
    {
        if (LiveHost.Active && __instance.Map == LiveHost.Map) LiveHost.Collector?.Despawned(__instance);
    }
}

[HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
public static class Patch_Thing_TakeDamage_Live
{
    public static void Postfix(Thing __instance, DamageInfo dinfo, DamageWorker.DamageResult __result)
    {
        if (LiveHost.Active && __instance.Map == LiveHost.Map) LiveHost.Collector?.Damaged(__instance, dinfo, __result?.totalDamageDealt ?? 0f);
    }
}

[HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.SetRoof))]
public static class Patch_RoofGrid_SetRoof_Live
{
    public static void Postfix(RoofGrid __instance, IntVec3 c, RoofDef def)
    {
        if (LiveHost.Active && LiveHost.Map?.roofGrid == __instance) LiveHost.Collector?.CellChanged(c, "roof", def?.defName ?? "");
    }
}

[HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain))]
public static class Patch_TerrainGrid_SetTerrain_Live
{
    public static void Postfix(TerrainGrid __instance, IntVec3 c, TerrainDef newTerr)
    {
        if (LiveHost.Active && LiveHost.Map?.terrainGrid == __instance) LiveHost.Collector?.CellChanged(c, "terrain", newTerr?.defName ?? "");
    }
}

/// <summary>The host's speed wish is remembered; what the game runs at is the session's effective speed.</summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.CurTimeSpeed), MethodType.Setter)]
public static class Patch_TickManager_CurTimeSpeed_Live
{
    public static void Prefix(ref TimeSpeed value)
    {
        if (!LiveTempo.Active || LiveTempo.Applying) return;
        LiveTempo.HostWish = value;
        value = LiveTempo.Effective;
    }
}

/// <summary>The host neither selects nor commands the guest's pawns.</summary>
[HarmonyPatch(typeof(Selector), nameof(Selector.Select))]
public static class Patch_Selector_Select_Live
{
    public static bool Prefix(object obj)
    {
        if (!LiveHost.Active || obj is not Thing t || t.Map is not { } map || map != LiveHost.Map) return true;
        return !MapComponent_LiveOwners.Of(map).IsOwned(t);
    }
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
public static class Patch_Pawn_GetGizmos_Live
{
    public static void Postfix(Pawn __instance, ref IEnumerable<Gizmo> __result)
    {
        if (!LiveHost.Active || __instance.Map is not { } map || map != LiveHost.Map) return;
        if (MapComponent_LiveOwners.Of(map).IsOwned(__instance)) __result = [];
    }
}
