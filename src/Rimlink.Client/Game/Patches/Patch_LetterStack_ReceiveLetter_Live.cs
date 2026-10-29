using HarmonyLib;
using RimWorld;
using Rimlink.Client.Game.Live;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>
/// A threat letter in the host's game during a live session (live UX spec §4): it goes to the guest's letter stack and pauses the session
/// when either player's threshold asks for it; vanilla's own automatic pause is swallowed meanwhile (<see cref="LiveTempo.InLetter"/>).
/// Every other ReceiveLetter overload ends in this one.
/// </summary>
[HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), typeof(Letter), typeof(string), typeof(int), typeof(bool))]
public static class Patch_LetterStack_ReceiveLetter_Live
{
    public static void Prefix() => LiveTempo.InLetter = LiveHost.Active && LiveHost.GuestHere;

    public static void Finalizer() => LiveTempo.InLetter = false;

    public static void Postfix(Letter let)
    {
        if (!LiveHost.Active || !LiveHost.GuestHere || let?.def is null) return;
        bool major = let.def == LetterDefOf.ThreatBig;
        if (!major && let.def != LetterDefOf.ThreatSmall) return;
        LiveHost.Collector?.Letter(let);
        if (LiveTempo.OnThreatLetter(major, let.Label.ToString())) RimlinkLog.Instance.Info($"live: paused by the letter '{let.Label}'");
    }
}
