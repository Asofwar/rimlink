using System.Formats.Tar;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace Rimlink.Server.Backup;

/// <summary>
/// One-file backup of a data directory: a consistent SQLite snapshot (VACUUM INTO) plus the saves tree, as tar + zstd.
/// Restore refuses to touch a directory whose server is running (server.lock present).
/// </summary>
public static class BackupArchive
{
    public const string DatabaseFile = "rimlink.db";
    public const string SavesDir = "saves";
    public const string LockFile = "server.lock";

    public static void Create(string dataDir, string outFile)
    {
        dataDir = Path.GetFullPath(dataDir);
        string snapshot = Path.Combine(dataDir, $"{DatabaseFile}-backup-{Guid.NewGuid():N}");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(dataDir, DatabaseFile)};Pooling=False"))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "VACUUM INTO @path";
                cmd.Parameters.AddWithValue("@path", snapshot);
                cmd.ExecuteNonQuery();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            string temp = outFile + ".tmp";
            using (var file = File.Create(temp))
            using (var zstd = new CompressionStream(file, level: 3))
            using (var tar = new TarWriter(zstd, TarEntryFormat.Pax, leaveOpen: true))
            {
                tar.WriteEntry(snapshot, DatabaseFile);
                string saves = Path.Combine(dataDir, SavesDir);
                if (Directory.Exists(saves))
                {
                    foreach (string path in Directory.EnumerateFiles(saves, "*", SearchOption.AllDirectories))
                    {
                        tar.WriteEntry(path, Path.GetRelativePath(dataDir, path).Replace('\\', '/'));
                    }
                }
            }
            File.Move(temp, outFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(snapshot)) File.Delete(snapshot);
        }
    }

    public static void Restore(string archiveFile, string dataDir)
    {
        dataDir = Path.GetFullPath(dataDir);
        if (File.Exists(Path.Combine(dataDir, LockFile)))
            throw new InvalidOperationException($"'{dataDir}' has a {LockFile}: stop the server before restoring");

        Directory.CreateDirectory(dataDir);
        SqliteConnection.ClearAllPools(); // pooled handles of a stopped server in this process would block the delete
        string saves = Path.Combine(dataDir, SavesDir);
        if (Directory.Exists(saves)) Directory.Delete(saves, recursive: true);
        foreach (string stale in Directory.GetFiles(dataDir, DatabaseFile + "*")) File.Delete(stale);

        using var file = File.OpenRead(archiveFile);
        using var zstd = new DecompressionStream(file);
        using var tar = new TarReader(zstd);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            string target = Path.GetFullPath(Path.Combine(dataDir, entry.Name));
            if (!target.StartsWith(dataDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException($"archive entry escapes the data directory: {entry.Name}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }
}
