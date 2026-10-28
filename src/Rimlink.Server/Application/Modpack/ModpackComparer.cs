using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.Modpack;

/// <summary>
/// Compares the client's running mods with the server list by package id only (lower case, "_steam" suffix dropped)
/// and by version string when the server pins one. Folder names, load order and settings are never compared.
/// </summary>
public static class ModpackComparer
{
    public static ModpackDiff? Compare(IReadOnlyList<ModpackEntry> server, IReadOnlyList<ModRef> client)
    {
        if (server.Count == 0) return null;

        var clientById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mod in client) clientById[Normalize(mod.PackageId)] = mod.Version.Trim();

        var diff = new ModpackDiff();
        foreach (var entry in server)
        {
            string id = Normalize(entry.PackageId);
            if (!clientById.TryGetValue(id, out var clientVersion))
            {
                if (entry.Kind == EntryKind.Required) diff.Missing.Add(entry);
                continue;
            }
            string serverVersion = entry.Version.Trim();
            if (serverVersion.Length > 0 && !string.Equals(serverVersion, clientVersion, StringComparison.Ordinal))
                diff.WrongVersion.Add(new VersionMismatch { PackageId = id, ClientVersion = clientVersion, ServerVersion = serverVersion });
        }

        var serverIds = server.Select(e => Normalize(e.PackageId)).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, version) in clientById)
            if (!serverIds.Contains(id)) diff.Extra.Add(new ModRef { PackageId = id, Version = version });

        if (diff.Missing.Count == 0 && diff.WrongVersion.Count == 0 && diff.Extra.Count == 0) return null;

        diff.ServerList.AddRange(server);
        return diff;
    }

    public static string Normalize(string packageId)
    {
        string id = packageId.Trim().ToLowerInvariant();
        return id.EndsWith("_steam", StringComparison.Ordinal) ? id[..^"_steam".Length] : id;
    }
}
