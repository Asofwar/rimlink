using HarmonyLib;
using RimWorld;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>
/// A pawn blob loads while the game is playing, which vanilla never does: the food need built during loading reads its max
/// level from stats, whose hediffs are not attached to the pawn yet (a chemical dependency then throws, the need is dropped as
/// null). While Scribe is loading, use the value vanilla's own loading uses (outside ProgramState.Playing).
/// </summary>
[HarmonyPatch(typeof(Need_Food), nameof(Need_Food.MaxLevel), MethodType.Getter)]
public static class Patch_Need_Food_MaxLevel
{
    private static readonly AccessTools.FieldRef<Need, Pawn> NeedPawn = AccessTools.FieldRefAccess<Need, Pawn>("pawn");

    public static bool Prefix(Need_Food __instance, ref float __result)
    {
        if (Scribe.mode is LoadSaveMode.Inactive or LoadSaveMode.Saving || NeedPawn(__instance) is not { } pawn || pawn.ageTracker?.CurLifeStage is not { } stage) return true;
        __result = pawn.BodySize * stage.foodMaxFactor;
        return false;
    }
}
