using HarmonyLib;
using Rimlink.Client.Game.Economy;
using RimWorld;

namespace Rimlink.Client.Game.Patches;

/// <summary>A pawn arriving from another game lost its ideo on the way: the load fallback gives it the ideo of the faction it joins.</summary>
[HarmonyPatch(typeof(Pawn_IdeoTracker), "FallbackIdeo")]
public static class Patch_Pawn_IdeoTracker_FallbackIdeo
{
    public static bool Prefix(Ideo excludeIdeo, ref Ideo __result)
    {
        if (ThingBlobs.ArrivalIdeo is not { } ideo || ideo == excludeIdeo) return true;
        __result = ideo;
        return false;
    }
}
