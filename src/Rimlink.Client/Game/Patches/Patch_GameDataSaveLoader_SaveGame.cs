using HarmonyLib;
using RimWorld;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>After the multiplayer save file is written, upload it.</summary>
[HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame))]
public static class Patch_GameDataSaveLoader_SaveGame
{
    public static void Postfix(string fileName) => SaveSync.OnSaved(fileName);
}

/// <summary>
/// Loading any other save while a session is active ends the session first (the colony is saved locally and uploaded at
/// the next login), so a single-player game never runs with multiplayer patches redirecting its autosaves.
/// </summary>
[HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.LoadGame), typeof(string))]
public static class Patch_GameDataSaveLoader_LoadGame
{
    public static void Prefix(string saveFileName)
    {
        if (!RimlinkSession.Active || saveFileName == SaveSync.SaveFileName) return;
        RimlinkLog.Instance.Info($"loading '{saveFileName}' outside the session: ending it");
        SaveSync.SaveLocallyForLater("another save is being loaded");
        RimlinkGameComponent.EndSession();
    }
}

/// <summary>In a multiplayer session autosaves go to the multiplayer file instead of Autosave-N.</summary>
[HarmonyPatch(typeof(Autosaver), nameof(Autosaver.DoAutosave))]
public static class Patch_Autosaver_DoAutosave
{
    public static bool Prefix()
    {
        if (!RimlinkSession.Active) return true;
        SaveSync.TriggerSave("autosave");
        return false;
    }
}
