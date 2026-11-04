using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Rimlink.Client.UI;
using Verse;

namespace Rimlink.Client.Game.Outposts;

/// <summary>Developer menu delegates to the same guarded production UI and original-operation cancellation.</summary>
public static class OutpostDebugActions
{
    [DebugAction("Rimlink", "Outposts: staff arrived caravan", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void Staffing()
    {
        var choices = Find.WorldObjects.Caravans.Where(c => c.IsPlayerControlled).Select(c =>
            new FloatMenuOption(c.Label, () => Find.WindowStack.Add(new Dialog_OutpostStaffing(c)))).ToList();
        if (choices.Count == 0) { Messages.Message("Rimlink.Outposts.ArrivalRequired".Translate(), MessageTypeDefOf.RejectInput, false); return; }
        Find.WindowStack.Add(new FloatMenu(choices));
    }

    [DebugAction("Rimlink", "Outposts: geography review", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void Geography()
    {
        if (!RimlinkSession.Active || !RimlinkSession.IsAdmin)
        { Messages.Message("Rimlink.Outposts.ManifestFailed".Translate(), MessageTypeDefOf.RejectInput, false); return; }
        Find.WindowStack.Add(new Dialog_OutpostManifest());
    }

    [DebugAction("Rimlink", "Outposts: safely cancel staffing", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void Cancel()
    {
        var choices = OutpostOutgoing.PendingAdmissionIds.Select(id => new FloatMenuOption(id, () => OutpostOutgoing.Cancel(id))).ToList();
        if (choices.Count == 0) return;
        Find.WindowStack.Add(new FloatMenu(choices));
    }
}
