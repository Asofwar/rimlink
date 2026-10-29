namespace Rimlink.Client.Game.Patches;

/// <summary>
/// The settings file of a mod whose settings the remembered server shares is the server's copy (spec §7). Applied by hand from
/// RimlinkMod's constructor, before the mods below Rimlink read their settings, so it has no [HarmonyPatch] for PatchAll.
/// </summary>
public static class Patch_LoadedModManager_GetSettingsFilename
{
    public static void Postfix(string modIdentifier, string modHandleName, ref string __result) =>
        __result = UI.ServerSettings.Redirect(modIdentifier, modHandleName, __result);
}
