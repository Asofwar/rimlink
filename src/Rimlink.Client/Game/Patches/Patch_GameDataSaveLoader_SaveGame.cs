using HarmonyLib;
using Rimlink.Protocol;
using RimWorld;
using System.Xml;
using Verse;

namespace Rimlink.Client.Game.Patches;

/// <summary>After the multiplayer save file is written, upload it.</summary>
[HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame))]
public static class Patch_GameDataSaveLoader_SaveGame
{
    public static bool Prefix(string fileName) => SaveSync.BeforeSave(fileName);
    public static void Postfix(string fileName, bool __runOriginal)
    {
        if (__runOriginal) SaveSync.OnSaved(fileName); // a refused prefix must not upload an older file
    }
}

/// <summary>
/// Loading any other save while a session is active ends the session first (the colony is saved locally and uploaded at
/// the next login), so a single-player game never runs with multiplayer patches redirecting its autosaves. The player is
/// told so once the loaded game runs.
/// </summary>
[HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.LoadGame), typeof(string))]
public static class Patch_GameDataSaveLoader_LoadGame
{
    public static bool Prefix(string saveFileName)
    {
        if (GuildCapture.BattleGameContext.Active) return GuildCapture.BattleGameContext.MayLoad(saveFileName);
        if (GuildCapture.BattleExecutorStartup.Selected) return false;
        if (!CanLoadPersonalSave(saveFileName)) return false;
        if (!RimlinkSession.Active || saveFileName == SaveSync.SaveFileName || Visits.VisitSession.IsVisitSave(saveFileName)) return true;
        RimlinkLog.Instance.Info($"loading '{saveFileName}' outside the session: ending it");
        bool saved = SaveSync.SaveLocallyForLater("another save is being loaded");
        RimlinkGameComponent.EndSession();
        RimlinkGameComponent.NoticeAfterLoad(saved ? "Rimlink.Save.EndedByLoad" : "Rimlink.Save.EndedByLoadUnsaved");
        return true;
    }

    private static bool CanLoadPersonalSave(string saveFileName)
    {
        string name = Path.GetFileName(saveFileName), message;
        try
        {
            if (!name.StartsWith("GuildView_", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("GuildBattle_", StringComparison.OrdinalIgnoreCase))
            {
                using var file = File.OpenRead(GenFilePaths.FilePathForSavedGame(saveFileName));
                if (!SaveRecoveryData.HasBattleScope(file)) return true;
            }
            message = "Rimlink.GuildBattle.OpenFromCampaign".Translate();
        }
        catch (FileNotFoundException) { return true; } // Preserve the native missing-file error.
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
        {
            message = "Rimlink.Refusal.Saves.Unreadable".Translate(name);
        }
        Find.WindowStack.Add(new Dialog_MessageBox(message));
        return false;
    }
}

/// <summary>In a multiplayer session autosaves go to the multiplayer file instead of Autosave-N.</summary>
[HarmonyPatch(typeof(Autosaver), nameof(Autosaver.DoAutosave))]
public static class Patch_Autosaver_DoAutosave
{
    public static bool Prefix()
    {
        if (GuildCapture.BattleGameContext.Active) return false;
        if (!RimlinkSession.Active) return true;
        if (!Visits.VisitSession.InVisit) SaveSync.TriggerSave("autosave");
        return false;
    }
}
