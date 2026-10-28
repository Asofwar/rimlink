using Rimlink.Protocol.V1;
using Steamworks;
using Verse;
using Verse.Steam;

namespace Rimlink.Client.UI;

/// <summary>What the client does with a modpack diff: subscribe, open links, apply the server's load order, restart.</summary>
public static class ModpackActions
{
    public static IEnumerable<ModRef> RunningMods() =>
        LoadedModManager.RunningMods.Select(m => new ModRef { PackageId = m.PackageId, Version = m.ModMetaData?.ModVersion ?? "" });

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

    /// <summary>Writes the server's list as the active mod list (backup kept next to ModsConfig.xml), keeping core/DLC first.</summary>
    public static void ApplyServerOrder(ModpackDiff diff)
    {
        string path = GenFilePaths.ModsConfigFilePath;
        if (File.Exists(path)) File.Copy(path, Path.Combine(Path.GetDirectoryName(path)!, "ModsConfig.rimlink-backup.xml"), overwrite: true);

        var installed = ModLister.AllInstalledMods.ToDictionary(m => m.PackageIdNonUnique.ToLowerInvariant(), m => m, StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var entry in diff.ServerList.OrderBy(e => e.Position))
        {
            if (installed.TryGetValue(entry.PackageId, out var mod)) ordered.Add(mod.PackageId);
        }
        ModsConfig.SetActiveToList(ordered);
        ModsConfig.Save();
    }

    public static void Restart() => GenCommandLine.Restart();
}
