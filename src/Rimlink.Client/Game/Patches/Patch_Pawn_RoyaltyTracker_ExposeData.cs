using HarmonyLib;
using RimWorld;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>
/// A pawn that crossed games as a blob keeps royal titles and permits whose faction the blob could not resolve: they load with a
/// null faction, vanilla's PostLoadInit throws on it (AssignHeirIfNone) and the save keeps the null, so the error comes back at
/// every load. Before PostLoadInit, such a title or permit is given this world's faction that grants the title, or dropped.
/// </summary>
[HarmonyPatch(typeof(Pawn_RoyaltyTracker), nameof(Pawn_RoyaltyTracker.ExposeData))]
public static class Patch_Pawn_RoyaltyTracker_ExposeData
{
    private static readonly AccessTools.FieldRef<Pawn_RoyaltyTracker, List<FactionPermit>> Permits = AccessTools.FieldRefAccess<Pawn_RoyaltyTracker, List<FactionPermit>>("factionPermits");
    private static readonly AccessTools.FieldRef<FactionPermit, Faction> PermitFaction = AccessTools.FieldRefAccess<FactionPermit, Faction>("faction");
    private static readonly AccessTools.FieldRef<FactionPermit, RoyalTitleDef> PermitTitle = AccessTools.FieldRefAccess<FactionPermit, RoyalTitleDef>("title");

    public static void Prefix(Pawn_RoyaltyTracker __instance)
    {
        if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
        string who = __instance.pawn?.LabelShort ?? "?";
        __instance.AllTitlesForReading?.RemoveAll(title =>
        {
            if (title.faction is not null || title.def is null) return false;
            title.faction = Granting(title.def);
            RimlinkLog.Instance.Info(title.faction is null
                ? $"royal title {title.def.defName} of {who} dropped: no faction in this world grants it"
                : $"royal title {title.def.defName} of {who} given to {title.faction.Name}");
            return title.faction is null;
        });
        Permits(__instance)?.RemoveAll(permit =>
        {
            if (PermitFaction(permit) is not null) return false;
            if (PermitTitle(permit) is { } title) PermitFaction(permit) = Granting(title);
            return PermitFaction(permit) is null;
        });
    }

    /// <summary>This world's faction whose titles include the given one (the empire of the world all players share).</summary>
    private static Faction? Granting(RoyalTitleDef title) =>
        Find.FactionManager?.AllFactionsListForReading.FirstOrDefault(f => !f.IsPlayer && f.def.RoyalTitlesAllInSeniorityOrderForReading.Contains(title));
}
