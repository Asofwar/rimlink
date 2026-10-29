using HarmonyLib;
using Rimlink.Client.Game.Patches;
using Rimlink.Client.Mods;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>
/// The server mod settings this launch uses (spec §7): read once in Rimlink's constructor, then the settings files of those
/// mod classes are the server's copies until the game exits. The player's own Config files are never touched.
/// </summary>
public static class ServerSettings
{
    private static HashSet<SettingsKey> keys = [];
    private static Dictionary<string, string>? packageByFolder;

    public static ServerSettingsStore Store { get; } = new(Path.Combine(GenFilePaths.SaveDataFolderPath, "Rimlink", "server-settings"));

    /// <summary>The set in effect since the game started; null = the player's own settings.</summary>
    public static ServerSettingsSet? Active { get; private set; }

    public static void Load()
    {
        Active = Store.ReadActive();
        if (Active is null) return;
        keys = Active.Mods.ToHashSet();
        new Harmony(ClientInfo.ModPackageId + ".settings").Patch(
            AccessTools.Method(typeof(LoadedModManager), "GetSettingsFilename"),
            postfix: new HarmonyMethod(typeof(Patch_LoadedModManager_GetSettingsFilename), nameof(Patch_LoadedModManager_GetSettingsFilename.Postfix)));
        Log.Message($"[Rimlink] mod settings of {Active.Display} in effect for {string.Join(", ", Active.Mods.Select(m => $"{m.PackageId} {m.Handle}"))}");
    }

    public static string Redirect(string modIdentifier, string handle, string original)
    {
        if (Active is null) return original;
        packageByFolder ??= ModLister.AllInstalledMods
            .GroupBy(m => m.FolderName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => ModpackActions.Normalize(g.First().PackageId), StringComparer.OrdinalIgnoreCase);
        if (!packageByFolder.TryGetValue(modIdentifier, out var packageId)) return original;
        var key = new SettingsKey(packageId, handle);
        return keys.Contains(key) ? Store.PathFor(Active.Server, key) : original;
    }
}
