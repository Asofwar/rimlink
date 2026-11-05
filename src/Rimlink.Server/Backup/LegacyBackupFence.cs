using System.Formats.Tar;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace Rimlink.Server.Backup;

/// <summary>Old entrypoints must prove the absence of protected history, even with the player capability disabled.</summary>
internal static class LegacyBackupFence
{
    internal static void RequireEmptyHistory(string database)
    {
        if (!File.Exists(database)) return;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND (name='outposts' OR name='pawn_custody' OR name LIKE 'outpost_%')";
        var tables = new List<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
        foreach (string table in tables)
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM \"" + table.Replace("\"", "\"\"") + "\" LIMIT 1)";
            if (Convert.ToInt64(command.ExecuteScalar()) != 0)
                throw new InvalidOperationException("protected outpost history requires the explicit authority-aware backup/restore procedure");
        }
    }

    internal static void RequireLegacyArchive(string path)
    {
        string root = Path.Combine(Path.GetTempPath(), "rimlink-legacy-inspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var file = File.OpenRead(path);
            using var zstd = new DecompressionStream(file);
            using var tar = new TarReader(zstd);
            bool databaseFound = false; int count = 0; long total = 0;
            while (tar.GetNextEntry() is { } entry)
            {
                if (++count > OutpostBackupManifest.MaximumFiles || entry.Length < 0 || entry.Length > 128L * 1024 * 1024 * 1024 - total)
                    throw new InvalidDataException("backup-archive-limit");
                total += entry.Length;
                if (entry.Name == OutpostBackupManifest.FileName)
                    throw new InvalidOperationException("protected outpost archive requires the explicit authority-aware restore procedure");
                if (entry.Name != BackupArchive.DatabaseFile) continue;
                if (databaseFound || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                    || entry.Length > 512L * 1024 * 1024 || entry.DataStream is null) throw new InvalidDataException("backup-database-invalid");
                databaseFound = true;
                string database = Path.Combine(root, BackupArchive.DatabaseFile);
                using (var output = new FileStream(database, FileMode.CreateNew, FileAccess.Write, FileShare.None)) entry.DataStream.CopyTo(output);
                RequireEmptyHistory(database);
            }
            if (!databaseFound) throw new InvalidDataException("backup-database-missing");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
