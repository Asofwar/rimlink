using HarmonyLib;
using RimWorld;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>Turning dev mode on (the Options checkbox, the debug button) is refused while connected to a server that does not allow it.</summary>
[HarmonyPatch(typeof(Prefs), nameof(Prefs.DevMode), MethodType.Setter)]
public static class Patch_Prefs_DevMode
{
    public static bool Prefix(bool value)
    {
        if (!value || !DevModeGuard.Blocked) return true;
        Messages.Message("Rimlink.DevMode.Refused".Translate(), MessageTypeDefOf.RejectInput, historical: false);
        return false;
    }
}
