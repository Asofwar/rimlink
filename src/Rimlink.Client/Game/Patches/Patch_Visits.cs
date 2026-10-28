using HarmonyLib;
using RimWorld;
using Rimlink.Client.Game.Shop;
using Rimlink.Client.Game.Visits;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>The host's save finished loading: turn it into a visit.</summary>
[HarmonyPatch(typeof(Verse.Game), nameof(Verse.Game.FinalizeInit))]
public static class Patch_Game_FinalizeInit_Visit
{
    public static void Postfix() => VisitSession.OnGameLoaded();
}

/// <summary>Nothing of a visited colony is ever saved (not even while it is still loading): a save in visit mode does nothing.</summary>
[HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame))]
public static class Patch_GameDataSaveLoader_SaveGame_Visit
{
    public static bool Prefix()
    {
        if (!VisitSession.InVisit) return true;
        Messages.Message("Rimlink.Visit.NoSaving".Translate(), MessageTypeDefOf.RejectInput, historical: false);
        return false;
    }
}

/// <summary>"For sale" at home, "Buy" while visiting, on item gizmos.</summary>
[HarmonyPatch(typeof(ThingWithComps), nameof(ThingWithComps.GetGizmos))]
public static class Patch_ThingWithComps_GetGizmos_Shop
{
    public static void Postfix(ThingWithComps __instance, ref IEnumerable<Gizmo> __result)
    {
        if (RimlinkSession.Active) __result = ShopGizmos.Append(__result, __instance);
    }
}
