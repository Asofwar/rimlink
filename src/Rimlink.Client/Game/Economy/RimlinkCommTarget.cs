using HarmonyLib;
using RimWorld;
using Rimlink.Client.UI.Economy;
using Verse;

namespace Rimlink.Client.Game.Economy;

/// <summary>The "Rimlink" entry in the comms console: opens the economy window for the console's map.</summary>
public sealed class RimlinkCommTarget : ICommunicable
{
    public string GetCallLabel() => "Rimlink.Economy.CallLabel".Translate();

    public string GetInfoText() => "Rimlink.Economy.CallInfo".Translate(RimlinkSession.Endpoint?.Display ?? "");

    public void TryOpenComms(Pawn negotiator)
    {
        if (!RimlinkSession.Active || negotiator.Map is null) return;
        Find.WindowStack.Add(new Dialog_Economy(negotiator.Map));
    }

    public Faction? GetFaction() => null;

    public FloatMenuOption CommFloatMenuOption(Building_CommsConsole console, Pawn negotiator) =>
        new("Rimlink.Economy.CallOption".Translate(), () => console.GiveUseCommsJob(negotiator, this));
}

/// <summary>Adds the Rimlink target to every comms console while a session is active.</summary>
[HarmonyPatch(typeof(Building_CommsConsole), nameof(Building_CommsConsole.GetCommTargets))]
public static class Patch_CommsConsole_GetCommTargets
{
    private static readonly RimlinkCommTarget Target = new();

    public static void Postfix(ref IEnumerable<ICommunicable> __result)
    {
        if (RimlinkSession.Active) __result = __result.Concat([Target]);
    }
}
