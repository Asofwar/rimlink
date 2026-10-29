using RimBridgeServer.Sdk;
using RimWorld;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Live;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.BridgeTools;

/// <summary>
/// Live-session details the UI reaches only through the Work and Schedule tabs or by chance (a resync): the same calls those
/// make. In the guest's mirror the live patches turn them into orders; on the host they read what the orders did.
/// </summary>
public sealed class LiveTools
{
    [Tool("rimlink/letter_pause", Description = "Set this player's letter-pause threshold as the mod settings do (never, major, any); during a live session the guest's goes to the host",
        ResultDescription = "success/started; rimlink/status live.letterPause shows it")]
    public Task<object> LetterPauseTool(IRimBridgeContext ctx,
        [ToolParameter(Description = "never, major or any", Required = true)] string mode) => Bridge.OnMain(ctx, () =>
    {
        LetterPause? chosen = mode.ToLowerInvariant() switch { "never" => LetterPause.Never, "major" => LetterPause.Major, "any" => LetterPause.Any, _ => null };
        if (chosen is not { } value) return Bridge.Fail("mode must be never, major or any");
        LiveTempo.ChangeOwnThreshold(value);
        return Targets.Started($"letter pause: {value}");
    });

    [Tool("rimlink/live_resync", Description = "Guest: ask the host for a fresh snapshot, as the mirror does after too many unknown ids",
        ResultDescription = "success/started; the host saves (live-snapshot) and the guest reloads the copy")]
    public Task<object> LiveResync(IRimBridgeContext ctx) => Bridge.OnMain(ctx, () =>
    {
        if (!RimlinkSession.Active || RimlinkSession.Live is not { } live) return Bridge.Fail("no Rimlink session");
        if (!MirrorMode.Active || LiveGuest.Session is null) return Bridge.Fail("not in a live session's mirror");
        live.Send(new LiveClientMsg { ResyncRequest = new Empty() });
        return Targets.Started("resync requested");
    });

    [Tool("rimlink/work_priority", Description = "Set (or read, priority -1) one work priority of one of our pawns, as a cell of the Work tab does",
        ResultDescription = "success/started and the priority the pawn now shows (raw: 1..4 whether or not manual priorities are on)")]
    public Task<object> WorkPriority(IRimBridgeContext ctx,
        [ToolParameter(Description = "Pawn id (rimworld/list_colonists) or name", Required = true)] string pawn,
        [ToolParameter(Description = "Work type defName or label, e.g. Cooking, Hauling", Required = true)] string work,
        [ToolParameter(Description = "0 (off) .. 4, or -1 to only read")] int priority = -1) => Bridge.OnMain(ctx, () =>
    {
        if (Targets.NotPlaying() is { } no) return no;
        if (Targets.Pawns(pawn, out string? error) is not [var p]) return Bridge.Fail(error ?? "give one pawn");
        var def = DefDatabase<WorkTypeDef>.AllDefs.FirstOrDefault(d => string.Equals(d.defName, work, StringComparison.OrdinalIgnoreCase)
                                                                     || string.Equals(d.labelShort, work, StringComparison.OrdinalIgnoreCase));
        if (def is null) return Bridge.Fail($"unknown work type '{work}'");
        if (p.workSettings is null) return Bridge.Fail($"{p.LabelShort} has no work settings");
        if (priority is < -1 or > 4) return Bridge.Fail("priority must be -1..4");
        if (priority >= 0) p.workSettings.SetPriority(def, priority);
        return Targets.Started(priority >= 0 ? $"{p.LabelShort} {def.defName} set to {priority}" : $"{p.LabelShort} {def.defName} read",
            new { pawn = p.LabelShort, work = def.defName, shown = RawPriority(p, def) });
    });

    [Tool("rimlink/schedule", Description = "Set (or read, no assignment) one hour of one of our pawns' schedule, as a cell of the Schedule tab does",
        ResultDescription = "success/started and the assignment the pawn now shows for that hour")]
    public Task<object> Schedule(IRimBridgeContext ctx,
        [ToolParameter(Description = "Pawn id (rimworld/list_colonists) or name", Required = true)] string pawn,
        [ToolParameter(Description = "Hour 0..23", Required = true)] int hour,
        [ToolParameter(Description = "TimeAssignmentDef defName: Anything, Work, Joy, Sleep, Meditate; empty to only read")] string assignment = "") => Bridge.OnMain(ctx, () =>
    {
        if (Targets.NotPlaying() is { } no) return no;
        if (Targets.Pawns(pawn, out string? error) is not [var p]) return Bridge.Fail(error ?? "give one pawn");
        if (p.timetable is null) return Bridge.Fail($"{p.LabelShort} has no schedule");
        if (hour is < 0 or > 23) return Bridge.Fail("hour must be 0..23");
        if (assignment.Length > 0)
        {
            var def = DefDatabase<TimeAssignmentDef>.AllDefs.FirstOrDefault(d => string.Equals(d.defName, assignment, StringComparison.OrdinalIgnoreCase));
            if (def is null) return Bridge.Fail($"unknown assignment '{assignment}'");
            p.timetable.SetAssignment(hour, def);
        }
        return Targets.Started(assignment.Length > 0 ? $"{p.LabelShort} hour {hour} set to {assignment}" : $"{p.LabelShort} hour {hour} read",
            new { pawn = p.LabelShort, hour, shown = p.timetable.GetAssignment(hour).defName });
    });

    [Tool("rimlink/capture", Description = "Host, dev: make a downed pawn of another faction a prisoner of the colony, as the end of the capture job does (no prisoner room needed)",
        ResultDescription = "success and whether the pawn is now a prisoner of the colony")]
    public Task<object> Capture(IRimBridgeContext ctx,
        [ToolParameter(Description = "Pawn load id on the current map (rimlink/map_pawns)", Required = true)] string pawn) => Bridge.OnMain(ctx, () =>
    {
        if (Targets.NotPlaying() is { } no) return no;
        if (Find.CurrentMap.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == pawn) is not { } p) return Bridge.Fail($"no spawned pawn '{pawn}'");
        if (p.Faction == Faction.OfPlayer || p.guest is null) return Bridge.Fail($"{p.LabelShort} cannot be captured");
        if (!p.Downed) return Bridge.Fail($"{p.LabelShort} is not downed");
        p.guest.CapturedBy(Faction.OfPlayer);
        return Targets.Started($"{p.LabelShort} captured", new { prisoner = p.IsPrisonerOfColony });
    });

    /// <summary>GetPriority reports 3 for any non-zero priority while manual priorities are off; the order carries the raw value.</summary>
    private static int RawPriority(Pawn p, WorkTypeDef def)
    {
        var settings = Find.PlaySettings;
        bool manual = settings.useWorkPriorities;
        settings.useWorkPriorities = true;
        try { return p.workSettings.GetPriority(def); }
        finally { settings.useWorkPriorities = manual; }
    }
}
