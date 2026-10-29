using HarmonyLib;
using RimWorld;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>
/// The storyteller also counts what this colony holds on the server (wallet, open lots, parcels in transit), so wealth cannot
/// be hidden there from threats. Only the player's own home map gets it, once, and not in fixed-wealth mode; during a visit or
/// as a live guest the loaded map is another colony's copy.
/// </summary>
[HarmonyPatch(typeof(Map), nameof(Map.PlayerWealthForStoryteller), MethodType.Getter)]
public static class Patch_Map_PlayerWealthForStoryteller
{
    public static void Postfix(Map __instance, ref float __result)
    {
        if (!RimlinkSession.Active || Visits.VisitSession.InVisit || Live.LiveGuest.Session is not null) return;
        if (__instance != Find.AnyPlayerHomeMap || !__instance.TreatAsPlayerHomeForThreatPoints || Find.Storyteller.difficulty.fixedWealthMode) return;
        __result += RimlinkSession.State.Economy.ServerWealth;
    }
}
