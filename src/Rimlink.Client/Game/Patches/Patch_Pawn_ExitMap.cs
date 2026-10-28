using HarmonyLib;
using Rimlink.Client.Game.Raids;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>A raider leaving the map takes its load along: record both before the game discards the pawn.</summary>
[HarmonyPatch(typeof(Pawn), nameof(Pawn.ExitMap))]
public static class Patch_Pawn_ExitMap
{
    public static void Prefix(Pawn __instance)
    {
        if (RimlinkSession.Active) RaidResolver.OnPawnExiting(__instance);
    }
}
