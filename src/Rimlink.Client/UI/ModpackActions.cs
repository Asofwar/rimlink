using HarmonyLib;
using Rimlink.Client.Game;
using Rimlink.Client.Mods;
using Rimlink.Client.Net;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Steamworks;
using Verse;
using Verse.Steam;

namespace Rimlink.Client.UI;

/// <summary>What the client reports about its mods at login and does with a modpack diff: subscribe, open links, fix and restart.</summary>
public static class ModpackActions
{
    /// <summary>The running mods' content, scanned in the background from RimlinkMod's constructor.</summary>
    public static readonly ModContentCache Content = new();

    private static readonly Func<string, string, string> SettingsFile =
        AccessTools.MethodDelegate<Func<string, string, string>>(AccessTools.Method(typeof(LoadedModManager), "GetSettingsFilename"));

    /// <summary>packageId as the server keys it: trimmed, lower case, without the "_steam" suffix of a Workshop copy.</summary>
    public static string Normalize(string packageId)
    {
        string id = packageId.Trim().ToLowerInvariant();
        return id.EndsWith("_steam", StringComparison.Ordinal) ? id[..^"_steam".Length] : id;
    }

    public static void StartScan() =>
        Content.Start(LoadedModManager.RunningMods.Select(m => (Normalize(m.PackageId), m.RootDir)));

    /// <summary>The running mods in load order with their content (waits for the scan started at game start).</summary>
    public static async Task<List<ModRef>> RunningMods()
    {
        var content = await Content.Ready;
        return LoadedModManager.RunningMods.Select((m, i) =>
        {
            var mod = new ModRef { PackageId = m.PackageId, Version = m.ModMetaData?.ModVersion ?? "", Position = i };
            if (content.TryGetValue(Normalize(m.PackageId), out var info))
            {
                mod.FilesHash = info.FilesHash;
                mod.HasDefs = info.HasDefs;
                mod.HasPatches = info.HasPatches;
                mod.HasAssemblies = info.HasAssemblies;
            }
            return mod;
        }).ToList();
    }

    /// <summary>The settings file in effect for every Mod class (the server's copy when its settings are active), hashed.</summary>
    public static List<ModSettingsHash> RunningModSettings() =>
        LoadedModManager.ModHandles.Select(mod =>
        {
            string handle = mod.GetType().Name;
            string path = SettingsFile(mod.Content.FolderName, handle);
            return new ModSettingsHash
            {
                PackageId = Normalize(mod.Content.PackageId),
                Handle = handle,
                Hash = File.Exists(path) ? ModContent.SettingsHash(File.ReadAllBytes(path)) : ModContent.NoSettings,
            };
        }).ToList();

    public static int SubscribeMissing(ModpackDiff diff)
    {
        if (!SteamManager.Initialized) return 0;
        int count = 0;
        foreach (var entry in diff.Missing.Where(e => e.WorkshopId != 0))
        {
            SteamUGC.SubscribeItem(new PublishedFileId_t(entry.WorkshopId));
            count++;
        }
        return count;
    }

    /// <summary>Missing Workshop mods Steam has not finished installing (0 without Steam: nothing to wait for).</summary>
    public static int PendingDownloads(ModpackDiff diff)
    {
        if (!SteamManager.Initialized) return 0;
        return diff.Missing.Where(e => e.WorkshopId != 0).Count(e =>
        {
            var state = (EItemState)SteamUGC.GetItemState(new PublishedFileId_t(e.WorkshopId));
            return (state & EItemState.k_EItemStateInstalled) == 0
                || (state & (EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending | EItemState.k_EItemStateNeedsUpdate)) != 0;
        });
    }

    public static int OpenUrls(ModpackDiff diff)
    {
        int count = 0;
        foreach (var entry in diff.Missing.Where(e => e.WorkshopId == 0 && !string.IsNullOrWhiteSpace(e.Url)))
        {
            UnityEngine.Application.OpenURL(entry.Url);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Writes the server's list as the active mod list (backup kept next to ModsConfig.xml), keeping core/DLC first. Running
    /// mods the server accepted without listing them (translations, textures) stay on, after the server's mods.
    /// </summary>
    public static void ApplyServerOrder(ModpackDiff diff)
    {
        string path = GenFilePaths.ModsConfigFilePath;
        if (File.Exists(path)) File.Copy(path, Path.Combine(Path.GetDirectoryName(path)!, "ModsConfig.rimlink-backup.xml"), overwrite: true);

        // Mods Steam installed since the game started are not in the list yet, and SetActiveToList drops unknown ids.
        ModLister.RebuildModList();
        var installed = ModLister.AllInstalledMods
            .GroupBy(m => Normalize(m.PackageIdNonUnique), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var entry in diff.ServerList.OrderBy(e => e.Position))
        {
            if (installed.TryGetValue(Normalize(entry.PackageId), out var mod)) ordered.Add(mod.PackageId);
        }
        var listed = diff.ServerList.Select(e => Normalize(e.PackageId)).ToHashSet(StringComparer.Ordinal);
        var refused = diff.Extra.Select(e => Normalize(e.PackageId)).ToHashSet(StringComparer.Ordinal);
        foreach (var mod in LoadedModManager.RunningMods)
        {
            string id = Normalize(mod.PackageId);
            if (!listed.Contains(id) && !refused.Contains(id)) ordered.Add(mod.PackageId);
        }
        ModsConfig.SetActiveToList(ordered);
        ModsConfig.Save();
    }

    /// <summary>
    /// Everything the player can fix, then a restart that logs in again by itself: the server's mod settings (if they differ),
    /// the server's mod list and order, the join intent. Throws <see cref="IOException"/> when a file cannot be written.
    /// </summary>
    public static void FixAndRestart(ModpackDiff diff, ServerEndpoint endpoint, string? login)
    {
        if (diff.Configs.Count > 0) ServerSettings.Store.Activate(ServerSettingsStore.ServerKey(endpoint.Host, endpoint.Port), endpoint.Display, diff.Configs);
        ApplyServerOrder(diff);
        JoinIntents.Save(new JoinIntent(endpoint.Host, endpoint.Port, endpoint.Name, login, DateTime.UtcNow));
        RimlinkLog.Instance.Info($"fix and restart for {endpoint.Display}: {diff.Configs.Count} settings files, {diff.ServerList.Count} mods in the server order");
        GenCommandLine.Restart();
    }

    public static JoinIntentStore JoinIntents { get; } = new(Path.Combine(GenFilePaths.SaveDataFolderPath, "Rimlink", "join-intent.json"));
}
