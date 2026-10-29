using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Live;

/// <summary>
/// Which vanilla jobs a guest may order for its own pawns on the host's map (spec §5): a whitelist, because any job not
/// listed here can touch the host's colony (kidnap, steal, carry a downed colonist off the map, release prisoners). Raiders
/// move, fight, pick things up and manage their own gear; visitors do the host's peaceful work and nothing hostile.
/// </summary>
public static class LiveRules
{
    private static readonly HashSet<string> Own = ["Goto", "Wait", "Wait_Combat", "LayDown", "Equip", "Wear", "RemoveApparel", "DropEquipment", "Ingest", "Reload", "UseVerbOnThing", "UseVerbOnThingStatic"];

    private static readonly HashSet<string> RaidAllowed = [.. Own, "AttackMelee", "AttackStatic", "TakeInventory", "UnloadYourInventory", "Flee", "FleeAndCower"];

    private static readonly HashSet<string> VisitAllowed =
    [
        .. Own, "HaulToCell", "HaulToContainer", "TendPatient", "FeedPatient", "Rescue", "Repair", "Sow", "Harvest", "HarvestDesignated", "CutPlant", "Mine",
        "DoBill", "Research", "Clean", "Refuel", "Train", "Tame", "Milk", "Shear", "FinishFrame", "SmoothFloor", "SmoothWall", "BeatFire", "ExtinguishSelf",
        "Meditate", "Pray", "VisitSickPawn", "SocialRelax", "Play_Chess", "Play_Billiards", "Play_Hoopstone", "Play_Horseshoes", "Reading",
    ];

    /// <summary>Null when the job is allowed, else the refusal reason.</summary>
    public static string? Forbidden(LiveKind kind, JobDef job)
    {
        if (kind == LiveKind.LiveRaid) return RaidAllowed.Contains(job.defName) ? null : "raiders may not " + job.defName;
        return VisitAllowed.Contains(job.defName) ? null : "visitors may not " + job.defName;
    }
}
