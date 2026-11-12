using HarmonyLib;
using RimWorld.Planet;
using Rimlink.Client.Game.Outposts;
using Rimlink.Client.UI;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>Expose passive outpost staffing only on a real own caravan.</summary>
[HarmonyPatch(typeof(Caravan), nameof(Caravan.GetGizmos))]
public static class Patch_Caravan_GetGizmos_Outposts
{
    public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Caravan __instance)
    {
        foreach (var gizmo in __result) yield return gizmo;
        if (!RimlinkSession.Active || !__instance.IsPlayerControlled || !PawnCustodyRegistry.IsOwnGame) yield break;
        if (RimlinkSession.Connection?.Login?.OutpostVersion != 1) yield break;
        var command = new Command_Action { defaultLabel = "Rimlink.Outposts.StaffingTitle".Translate(), defaultDesc = "Rimlink.Outposts.StaffingDescription".Translate(),
            action = () => Find.WindowStack.Add(new Dialog_OutpostStaffing(__instance)) };
        if (__instance.pather.Moving || OutpostOutgoing.Busy) command.Disable("Rimlink.Outposts.ArrivalRequired".Translate());
        yield return command;
    }
}
