using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.Modpack;

/// <summary>
/// Compares the client's running mods with the server list by package id (lower case, "_steam" suffix dropped) and by version
/// string when the server pins one. A newer client (one that reports file hashes) is also checked for its mod files, the
/// relative order of the required mods and the shared mod settings, and its extra mods without defs, patches or code
/// (translations, textures) are allowed by themselves. Folder names are never compared.
/// </summary>
public static class ModpackComparer
{
    public static ModpackDiff? Compare(IReadOnlyList<ModpackEntry> server, IReadOnlyList<ModRef> client, IReadOnlyList<ModConfig>? configs = null, IReadOnlyList<ModSettingsHash>? settings = null)
    {
        if (server.Count == 0) return null;

        bool newClient = client.Any(m => m.FilesHash.Length > 0);
        var clientById = new Dictionary<string, ModRef>(StringComparer.Ordinal);
        foreach (var mod in client) clientById[Normalize(mod.PackageId)] = mod;

        var diff = new ModpackDiff();
        foreach (var entry in server)
        {
            string id = Normalize(entry.PackageId);
            if (!clientById.TryGetValue(id, out var mod))
            {
                if (entry.Kind == EntryKind.Required) diff.Missing.Add(entry);
                continue;
            }
            string clientVersion = mod.Version.Trim();
            string serverVersion = entry.Version.Trim();
            if (serverVersion.Length > 0 && !string.Equals(serverVersion, clientVersion, StringComparison.Ordinal))
            {
                diff.WrongVersion.Add(new VersionMismatch
                {
                    PackageId = id,
                    ClientVersion = clientVersion,
                    ServerVersion = serverVersion,
                    Side = IsNewer(clientVersion, serverVersion) ? FixSide.Server : FixSide.Player,
                });
                continue;
            }
            if (newClient && entry.FilesHash.Length > 0 && mod.FilesHash.Length > 0 && !string.Equals(entry.FilesHash, mod.FilesHash, StringComparison.OrdinalIgnoreCase))
                diff.Files.Add(new FileMismatch { PackageId = id, Version = clientVersion });
        }

        var serverIds = server.Select(e => Normalize(e.PackageId)).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, mod) in clientById)
        {
            if (serverIds.Contains(id)) continue;
            bool contentFree = newClient && mod.FilesHash.Length > 0 && !mod.HasDefs && !mod.HasPatches && !mod.HasAssemblies;
            if (!contentFree) diff.Extra.Add(new ModRef { PackageId = id, Version = mod.Version.Trim() });
        }

        if (newClient) diff.OrderDiffers = OrderDiffers(server, clientById);
        if (configs is { Count: > 0 } && settings is { Count: > 0 }) AddConfigs(diff, configs, settings);

        if (diff.Missing.Count == 0 && diff.WrongVersion.Count == 0 && diff.Extra.Count == 0 && diff.Files.Count == 0
            && !diff.OrderDiffers && diff.Configs.Count == 0) return null;

        diff.ServerList.AddRange(server);
        return diff;
    }

    public static string Normalize(string packageId)
    {
        string id = packageId.Trim().ToLowerInvariant();
        return id.EndsWith("_steam", StringComparison.Ordinal) ? id[..^"_steam".Length] : id;
    }

    /// <summary>The required mods the client has must come in the server's relative order; other mods do not count.</summary>
    private static bool OrderDiffers(IReadOnlyList<ModpackEntry> server, Dictionary<string, ModRef> clientById)
    {
        int last = int.MinValue;
        foreach (var entry in server.Where(e => e.Kind == EntryKind.Required).OrderBy(e => e.Position))
        {
            if (!clientById.TryGetValue(Normalize(entry.PackageId), out var mod)) continue;
            if (mod.Position <= last) return true;
            last = mod.Position;
        }
        return false;
    }

    /// <summary>
    /// When any shared settings file differs, the client gets every one of its mods' shared files: "Fix and restart" replaces
    /// the whole active set, so a partial set would drop the files that happened to match this time.
    /// </summary>
    private static void AddConfigs(ModpackDiff diff, IReadOnlyList<ModConfig> configs, IReadOnlyList<ModSettingsHash> settings)
    {
        var clientHashes = new Dictionary<(string, string), string>();
        foreach (var s in settings) clientHashes[(Normalize(s.PackageId), s.Handle)] = s.Hash;
        var theirs = configs.Where(c => clientHashes.ContainsKey((Normalize(c.PackageId), c.Handle))).ToList();
        if (theirs.Any(c => !string.Equals(clientHashes[(Normalize(c.PackageId), c.Handle)], c.Hash, StringComparison.OrdinalIgnoreCase)))
            diff.Configs.AddRange(theirs);
    }

    /// <summary>True when both versions are dot-separated numbers and the client's is the higher one.</summary>
    private static bool IsNewer(string client, string server)
    {
        if (!TryParts(client, out var c) || !TryParts(server, out var s)) return false;
        for (int i = 0; i < Math.Max(c.Length, s.Length); i++)
        {
            int a = i < c.Length ? c[i] : 0, b = i < s.Length ? s[i] : 0;
            if (a != b) return a > b;
        }
        return false;
    }

    private static bool TryParts(string version, out int[] parts)
    {
        string[] raw = version.Split('.');
        parts = new int[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            if (!int.TryParse(raw[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out parts[i])) return false;
        return raw.Length > 0;
    }
}
