using HarmonyLib;
using RimWorld;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>
/// A pawn blob loaded mid-game computes its life stage for the first time with no previous stage. The vanilla worker only
/// expects that outside <see cref="ProgramState.Playing"/> (game load) and otherwise dereferences the previous stage for
/// colonists, so every own pawn coming home logged two NullReferenceExceptions and skipped the rest of the ideo tracker's
/// PostLoadInit. While Scribe is loading, a missing previous stage is not a transition: keep only the base worker's effect.
/// </summary>
[HarmonyPatch(typeof(LifeStageWorker_HumanlikeAdult), nameof(LifeStageWorker_HumanlikeAdult.Notify_LifeStageStarted))]
public static class Patch_LifeStageWorker_HumanlikeAdult
{
    public static bool Prefix(Pawn pawn, LifeStageDef previousLifeStage)
    {
        if (previousLifeStage is not null || Scribe.mode == LoadSaveMode.Inactive) return true;
        pawn.health?.capacities?.Notify_CapacityLevelsDirty(); // what LifeStageWorker.Notify_LifeStageStarted does
        return false;
    }
}
