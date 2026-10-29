using HarmonyLib;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>
/// Leaving a multiplayer game through the vanilla menu saves the colony, waits for the upload and ends the session
/// before the scene changes (<see cref="SaveSync.SaveUploadAndEnd"/>). The original is skipped only on that first call:
/// GoToMainMenu clears the long-event queue, so the work runs first and the original is re-entered once the session is over
/// (Active is false by then). A session that is already over (replaced, failed) goes straight through.
/// </summary>
[HarmonyPatch(typeof(GenScene), nameof(GenScene.GoToMainMenu))]
public static class Patch_GenScene_GoToMainMenu
{
    private static bool leaving;

    public static bool Prefix()
    {
        if (!RimlinkSession.Active || leaving) return true;
        leaving = true;
        SaveSync.SaveUploadAndEnd(() =>
        {
            leaving = false;
            GenScene.GoToMainMenu();
        });
        return false;
    }
}
