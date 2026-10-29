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
    private static SettingsRedirect? redirect;
    private static Dictionary<string, string>? packageByFolder;

    public static ServerSettingsStore Store { get; } = new(Path.Combine(GenFilePaths.SaveDataFolderPath, "Rimlink", "server-settings"));

    /// <summary>The set in effect since the game started; null = the player's own settings.</summary>
    public static ServerSettingsSet? Active => redirect?.Set;

    public static void Load()
    {
        var set = Store.ReadActive();
        if (set is null) return;
        // Mod classes created before ours (mods above Rimlink) have already read their own settings files: they keep them.
        var created = (Dictionary<Type, Mod>)AccessTools.Field(typeof(LoadedModManager), "runningModClasses").GetValue(null)!;
        var early = created.Values.Where(m => m.Content is not null)
            .Select(m => new SettingsKey(ModpackActions.Normalize(m.Content.PackageId), m.GetType().Name))
            .Where(set.Mods.Contains).ToList();
        redirect = new SettingsRedirect(Store, set, early);
        new Harmony(ClientInfo.ModPackageId + ".settings").Patch(
            AccessTools.Method(typeof(LoadedModManager), "GetSettingsFilename"),
            postfix: new HarmonyMethod(typeof(Patch_LoadedModManager_GetSettingsFilename), nameof(Patch_LoadedModManager_GetSettingsFilename.Postfix)));
        Log.Message($"[Rimlink] mod settings of {set.Display} in effect for {string.Join(", ", set.Mods.Except(early).Select(m => $"{m.PackageId} {m.Handle}"))}"
            + (early.Count > 0 ? $"; not for {string.Join(", ", early.Select(m => m.PackageId))} (loaded before Rimlink)" : ""));
    }

    public static string Redirect(string modIdentifier, string handle, string original)
    {
        if (redirect is null) return original;
        packageByFolder ??= ModLister.AllInstalledMods
            .GroupBy(m => m.FolderName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => ModpackActions.Normalize(g.First().PackageId), StringComparer.OrdinalIgnoreCase);
        if (!packageByFolder.TryGetValue(modIdentifier, out var packageId)) return original;
        return redirect.Resolve(new SettingsKey(packageId, handle), original);
    }
}
