using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Live;

/// <summary>What a guest may not order on the host's map (spec §5): raiders neither build nor manage; visitors do no harm.</summary>
public static class LiveRules
{
    private static readonly HashSet<string> RaidForbidden =
    [
        "FinishFrame", "PlaceNoCostFrame", "BuildRoof", "RemoveRoof", "SmoothFloor", "SmoothWall", "RemoveFloor", "Deconstruct", "Uninstall", "Install",
        "DoBill", "Research", "Repair", "Maintain", "Train", "Tame", "Slaughter", "Milk", "Shear", "Harvest", "Sow", "CutPlant", "HarvestDesignated", "Mine",
        "HaulToCell", "HaulToContainer", "Refuel", "RearmTurret", "Flick", "ManTurret", "TendPatient", "FeedPatient", "Rescue", "Capture", "Arrest", "Strip", "Open", "Ignite", "ExtinguishSelf", "BeatFire",
    ];

    private static readonly HashSet<string> VisitForbidden = ["Deconstruct", "Uninstall", "Ignite", "Slaughter", "Arrest", "Capture", "Strip", "Open", "PrisonerExecution", "Kidnap", "Steal", "ManTurret"];

    /// <summary>Null when the job is allowed, else the refusal reason.</summary>
    public static string? Forbidden(LiveKind kind, JobDef job)
    {
        if (kind == LiveKind.LiveRaid && RaidForbidden.Contains(job.defName)) return "raiders cannot " + job.defName;
        if (kind == LiveKind.LiveVisit && VisitForbidden.Contains(job.defName)) return "visitors cannot " + job.defName;
        return null;
    }
}
