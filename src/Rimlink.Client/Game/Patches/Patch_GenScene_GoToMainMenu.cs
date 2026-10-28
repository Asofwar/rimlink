using HarmonyLib;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>
/// Leaving a multiplayer game through the vanilla menu saves the colony, waits for the upload and ends the session
/// before the scene changes. The original is skipped only on that first call: GoToMainMenu clears the long-event queue,
/// so the work runs first and the original is re-entered once the session is over (Active is false by then).
/// A session that is already over (replaced, failed) goes straight through.
/// </summary>
[HarmonyPatch(typeof(GenScene), nameof(GenScene.GoToMainMenu))]
public static class Patch_GenScene_GoToMainMenu
{
    private static bool leaving;

    public static bool Prefix()
    {
        if (!RimlinkSession.Active || leaving) return true;
        leaving = true;
        if (Current.Game != null && Current.ProgramState == ProgramState.Playing)
        {
            try { GameDataSaveLoader.SaveGame(SaveSync.SaveFileName); } // the SaveGame postfix starts the upload
            catch (Exception ex) { Log.Error("[Rimlink] save before leaving failed: " + ex); }
        }
        bool uploaded = false;
        LongEventHandler.QueueLongEvent(() => uploaded = SaveSync.WaitForUpload(TimeSpan.FromSeconds(60)), "Rimlink.Flow.Uploading",
            doAsynchronously: true, null, showExtraUIInfo: false, callback: () =>
            {
                // An upload that did not finish in time is not trusted: the local file is flagged and sent at the next login.
                if (!uploaded) SaveSync.Pending.Set();
                RimlinkGameComponent.EndSession();
                leaving = false;
                GenScene.GoToMainMenu();
            });
        return false;
    }
}
