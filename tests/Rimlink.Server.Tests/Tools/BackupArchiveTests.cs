using Microsoft.Data.Sqlite;
using Rimlink.Server.Backup;
using Xunit;

namespace Rimlink.Server.Tests.Tools;

public class BackupArchiveTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rimlink-tests", Guid.NewGuid().ToString("N"));

    public BackupArchiveTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string MakeDataDir(string name, string marker)
    {
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, "saves", "7"));
        File.WriteAllText(Path.Combine(dir, "saves", "7", "3.rws.zst"), marker);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "rimlink.db")};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE TABLE t (v TEXT); INSERT INTO t VALUES ('{marker}');";
        cmd.ExecuteNonQuery();
        return dir;
    }

    private static string ReadMarker(string dir)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "rimlink.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT v FROM t";
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void BackupThenRestoreReproducesDatabaseAndSaves()
    {
        string source = MakeDataDir("source", "alpha");
        string archive = Path.Combine(root, "backup.tar.zst");

        BackupArchive.Create(source, archive);
        string target = Path.Combine(root, "restored");
        BackupArchive.Restore(archive, target);

        Assert.Equal("alpha", ReadMarker(target));
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(target, "saves", "7", "3.rws.zst")));
        Assert.Empty(Directory.GetFiles(source, "*.db-*")); // VACUUM INTO temp cleaned up
    }

    private static string SavePath(string dir, string name) => Path.Combine(dir, "saves", "7", name);

    private string RoundTrip(string source, Action<string>? beforeEntry = null)
    {
        string archive = Path.Combine(root, "backup.tar.zst");
        BackupArchive.Create(source, archive, beforeEntry);
        string target = Path.Combine(root, "restored");
        BackupArchive.Restore(archive, target);
        return target;
    }

    [Fact]
    public void BackupCarriesDeltasAndBases()
    {
        string source = MakeDataDir("source", "alpha");
        File.WriteAllText(SavePath(source, "4.d1.zst"), "delta");
        File.WriteAllText(SavePath(source, "base-1.rws.zst"), "base");

        string target = RoundTrip(source);

        Assert.Equal("delta", File.ReadAllText(SavePath(target, "4.d1.zst")));
        Assert.Equal("base", File.ReadAllText(SavePath(target, "base-1.rws.zst")));
    }

    [Fact]
    public void TemporaryFilesAreNotPacked()
    {
        string source = MakeDataDir("source", "alpha");
        File.WriteAllText(SavePath(source, "upload-x.tmp"), "half an upload");
        File.WriteAllText(SavePath(source, "compact-x.tmp"), "half a delta");

        string target = RoundTrip(source);

        Assert.Equal(["3.rws.zst"], Directory.GetFiles(Path.Combine(target, "saves", "7")).Select(Path.GetFileName));
    }

    [Fact]
    public void AFileGoneDuringPackingIsSkipped()
    {
        string source = MakeDataDir("source", "alpha");
        File.WriteAllText(SavePath(source, "4.rws.zst"), "beta");
        bool gone = false;

        string target = RoundTrip(source, entry =>
        {
            if (gone) return;
            gone = true;
            File.Delete(SavePath(source, entry.EndsWith("3.rws.zst", StringComparison.Ordinal) ? "4.rws.zst" : "3.rws.zst"));
        });

        Assert.Single(Directory.GetFiles(Path.Combine(target, "saves", "7")));
    }

    [Fact]
    public void AFileAddedDuringPackingIsCaughtByTheSecondPass()
    {
        string source = MakeDataDir("source", "alpha");
        bool added = false;

        string target = RoundTrip(source, _ =>
        {
            if (added) return;
            added = true;
            File.WriteAllText(SavePath(source, "5.d1.zst"), "delta");
        });

        Assert.Equal("delta", File.ReadAllText(SavePath(target, "5.d1.zst")));
    }

    [Fact]
    public void RestoreRefusesWhileServerHoldsTheLock()
    {
        string source = MakeDataDir("source", "alpha");
        string archive = Path.Combine(root, "backup.tar.zst");
        BackupArchive.Create(source, archive);
        string target = MakeDataDir("target", "beta");
        using var held = new FileStream(Path.Combine(target, "server.lock"), FileMode.Create, FileAccess.Write, FileShare.None);

        Assert.Throws<InvalidOperationException>(() => BackupArchive.Restore(archive, target));
        Assert.Equal("beta", ReadMarker(target));
    }

    [Fact]
    public void RestoreProceedsOverAStaleLockFile()
    {
        string source = MakeDataDir("source", "alpha");
        string archive = Path.Combine(root, "backup.tar.zst");
        BackupArchive.Create(source, archive);
        string target = MakeDataDir("target", "beta");
        File.WriteAllText(Path.Combine(target, "server.lock"), "12345"); // left behind by a killed server

        BackupArchive.Restore(archive, target);

        Assert.Equal("alpha", ReadMarker(target));
        Assert.False(File.Exists(Path.Combine(target, "server.lock")));
    }

    [Fact]
    public void SchedulerKeepsOnlyLatestBackups()
    {
        string backups = Path.Combine(root, "backups");
        Directory.CreateDirectory(backups);
        for (int i = 0; i < 10; i++)
        {
            string path = Path.Combine(backups, $"rimlink-2026102{i}-000000.tar.zst");
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 20 + i, 0, 0, 0, DateTimeKind.Utc));
        }

        BackupScheduler.Prune(backups, keep: 7);

        var left = Directory.GetFiles(backups).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(7, left.Count);
        Assert.Equal("rimlink-20261023-000000.tar.zst", left[0]);
    }
}
