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
