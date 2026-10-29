using HarmonyLib;
using RimWorld;
using Rimlink.Client.Game.Live;
using Verse;
using Verse.AI;

namespace Rimlink.Client.Game.Patches;

/// <summary>In mirror mode the game never ticks on its own: the clock follows the host's deltas.</summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
public static class Patch_TickManager_Update_Mirror
{
    public static bool Prefix()
    {
        if (!MirrorMode.Active) return true;
        MirrorMode.Advance();
        return false;
    }
}

/// <summary>The speed buttons send the guest's wish to the host; the local speed only ever comes from a delta.</summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.CurTimeSpeed), MethodType.Setter)]
public static class Patch_TickManager_CurTimeSpeed_Mirror
{
    public static bool Prefix(TimeSpeed value)
    {
        if (!MirrorMode.Active || MirrorMode.Applying) return true;
        if (!MirrorMode.SettlingIn) LiveGuest.SendSpeed(value);
        return false;
    }
}

/// <summary>Every ordered job (right-click menu, attack and equip gizmos) on one of our pawns becomes an order frame; nobody else's pawns take orders.</summary>
[HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob))]
public static class Patch_Pawn_JobTracker_TryTakeOrderedJob_Mirror
{
    public static bool Prefix(Pawn_JobTracker __instance, Job job, ref bool __result)
    {
        if (!MirrorMode.Active || MirrorMode.Applying) return true;
        var pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
        if (pawn is null) { __result = false; return false; }
        if (MirrorMode.IsOwned(pawn)) LiveGuest.SendJob(pawn, job);
        __result = MirrorMode.IsOwned(pawn);
        return false;
    }
}

[HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted), MethodType.Setter)]
public static class Patch_Pawn_DraftController_Drafted_Mirror
{
    public static bool Prefix(Pawn_DraftController __instance, bool value)
    {
        if (!MirrorMode.Active || MirrorMode.Applying) return true;
        if (MirrorMode.IsOwned(__instance.pawn)) LiveGuest.SendDraft(__instance.pawn, value);
        return false;
    }
}

[HarmonyPatch(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.SetPriority))]
public static class Patch_Pawn_WorkSettings_SetPriority_Mirror
{
    public static bool Prefix(Pawn_WorkSettings __instance, WorkTypeDef w, int priority)
    {
        if (!MirrorMode.Active || MirrorMode.Applying) return true;
        var pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
        if (pawn is null || !MirrorMode.IsOwned(pawn)) return false;
        LiveGuest.SendWorkPriority(pawn, w, priority);
        return true; // shown locally at once; the host confirms or refuses
    }
}

[HarmonyPatch(typeof(Pawn_TimetableTracker), nameof(Pawn_TimetableTracker.SetAssignment))]
public static class Patch_Pawn_TimetableTracker_SetAssignment_Mirror
{
    public static bool Prefix(Pawn_TimetableTracker __instance, int hour, TimeAssignmentDef ta)
    {
        if (!MirrorMode.Active || MirrorMode.Applying) return true;
        var pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
        if (pawn is null || !MirrorMode.IsOwned(pawn)) return false;
        LiveGuest.SendSchedule(pawn, hour, ta);
        return true;
    }
}

/// <summary>
/// The pawn texture atlas is keyed by pawn id; a pawn's entry can go stale (a pawn that left this game, a blob re-read
/// of the same pawn), after which the vanilla sweep throws on every frame. This sweep rebuilds the map by enumeration.
/// </summary>
[HarmonyPatch(typeof(PawnTextureAtlas), nameof(PawnTextureAtlas.GC))]
public static class Patch_PawnTextureAtlas_GC_Mirror
{
    public static bool Prefix(PawnTextureAtlas __instance)
    {
        if (!RimlinkSession.Active) return true;
        try
        {
            var field = Traverse.Create(__instance).Field<Dictionary<Pawn, PawnTextureAtlasFrameSet>>("frameAssignments");
            var free = Traverse.Create(__instance).Field<List<PawnTextureAtlasFrameSet>>("freeFrameSets").Value;
            var keep = new Dictionary<Pawn, PawnTextureAtlasFrameSet>();
            foreach (var kv in field.Value)
            {
                if (kv.Key.SpawnedOrAnyParentSpawned) keep[kv.Key] = kv.Value;
                else free.Add(kv.Value);
            }
            field.Value = keep;
        }
        catch (Exception ex) { Log.ErrorOnce("[Rimlink] mirror: pawn atlas sweep failed: " + ex.Message, 0x51A7E); }
        return false;
    }
}

/// <summary>Other people's pawns and the host's buildings can be looked at, not commanded.</summary>
[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
public static class Patch_Pawn_GetGizmos_Mirror
{
    public static void Postfix(Pawn __instance, ref IEnumerable<Gizmo> __result)
    {
        if (MirrorMode.Active && !MirrorMode.IsOwned(__instance)) __result = [];
    }
}

[HarmonyPatch(typeof(ThingWithComps), nameof(ThingWithComps.GetGizmos))]
[HarmonyPriority(Priority.High)] // before the shop postfix, so "Buy" is appended after the host's gizmos are gone
public static class Patch_ThingWithComps_GetGizmos_Mirror
{
    public static void Postfix(ThingWithComps __instance, ref IEnumerable<Gizmo> __result)
    {
        if (MirrorMode.Active && __instance is not Pawn) __result = [];
    }
}
