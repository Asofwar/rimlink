using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;

namespace Rimlink.OutpostAcceptanceHost;

// Explicit stopped, positively empty stand maintenance. Never a startup or lost-anchor fallback.
internal static class AcceptanceProvision
{
    internal static void Execute(AcceptanceStand stand)
    {
        using var owned = AcceptanceComposition.Open(stand);
        RequireEmpty(stand);
        using var server = new FileStream(Path.Combine(stand.DataDirectory, "server.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        RequireEmpty(stand);
        var store = new OutpostAnchorStore(DataDirectory.Existing(stand.DataDirectory), Options.Create(new OutpostOptions
        {
            AnchorDirectory = stand.AnchorDirectory, StorageContract = "sqlite-local-v1",
            QualifiedLinuxFileSystem = stand.QualifiedLinuxFileSystem
        }));
        store.Provision();
        using var read = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = store.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        read.Open();
        if (OutpostAnchorStore.ReadCheckedEnvelope(read) is not null)
            throw new InvalidDataException("acceptance-provision-logical-authority-unexpected");
    }

    private static void RequireEmpty(AcceptanceStand stand)
    {
        foreach (string root in new[] { stand.DataDirectory, stand.AnchorDirectory })
            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative is not ("server.lock" or "save-storage.lock") || new FileInfo(path).Length != 0)
                    throw new InvalidDataException("acceptance-provision-state-not-positively-empty");
            }
        foreach (string root in new[] { stand.HostSaveData, stand.GuestSaveData })
            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                bool setup = relative is "Config/ModsConfig.xml" or "Config/Prefs.xml" or "Config/KeyPrefs.xml"
                    or "Config/Knowledge.xml" or "Config/LastPlayedVersion.txt" or "HardwareProfile/gpu-profile.bin";
                bool shader = relative.StartsWith("ShaderCache/", StringComparison.Ordinal) && relative.EndsWith(".layout-cache", StringComparison.Ordinal);
                bool textures = relative.StartsWith("TextureCache/v", StringComparison.Ordinal)
                    && (relative.EndsWith(".zip", StringComparison.Ordinal) || relative.EndsWith(".zip.lock", StringComparison.Ordinal));
                bool install = relative.StartsWith("TextureCache/installs/", StringComparison.Ordinal)
                    && relative["TextureCache/installs/".Length..] is { Length: 16 } name && name.All(Uri.IsHexDigit);
                // Engine-only smoke creates these known caches; none is a save, outbox or custody namespace.
                if (!setup && !shader && !textures && !install)
                    throw new InvalidDataException("acceptance-provision-save-state-not-positively-empty");
            }
    }
}
