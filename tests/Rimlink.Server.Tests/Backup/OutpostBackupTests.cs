using System.Formats.Tar;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Xunit;
using ZstdSharp;

namespace Rimlink.Server.Tests.Backup;

public class OutpostBackupTests
{
    [Fact]
    public async Task Backup_cuts_db_saves_archive_and_stock_at_one_head()
    {
        using var f = await LifecycleFixture.Create();
        f.H.F.Time.Advance(TimeSpan.FromMinutes(31.5));
        string archive = await Create(f);
        string snapshot = Path.Combine(f.H.F.H.H.Root, "backup-cut.db");
        var entries = Read(archive);
        File.WriteAllBytes(snapshot, entries[BackupArchive.DatabaseFile]);
        using var db = new SqliteConnection($"Data Source={snapshot};Mode=ReadOnly;Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Stock, FractionNumerator, FractionDenominator FROM Outposts";
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal(10, row.GetInt32(0));
        Assert.Equal("1", row.GetString(1));
        Assert.Equal("2", row.GetString(2));
        Assert.Equal(10, (await f.H.Row()).Stock);
        Assert.Contains("outpost-manifest.json", entries.Keys);
        Assert.DoesNotContain(entries.Keys, p => p.Contains("authority.db", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Backup_retains_exact_archived_and_retired_snapshot_bytes()
    {
        using var f = await LifecycleFixture.Create();
        await f.Archive();
        var files = Directory.EnumerateFiles(f.H.F.H.H.Data.Archive, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(f.H.F.H.H.Data.Root, p).Replace('\\', '/'), File.ReadAllBytes);
        Assert.NotEmpty(files);
        var entries = Read(await Create(f));
        foreach (var (path, bytes) in files)
        {
            Assert.True(entries.ContainsKey(path), "missing exact archived proof: " + path);
            Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(entries[path]));
        }
    }

    [Fact]
    public async Task Legacy_create_cannot_bypass_protected_authority()
    {
        using var f = await LifecycleFixture.Create();
        string output = Path.Combine(f.H.F.H.H.Root, "unsafe.tar.zst");
        string before = await f.Cut();
        Assert.Throws<InvalidOperationException>(() => BackupArchive.Create(f.H.F.H.H.Data.Root, output));
        Assert.False(File.Exists(output));
        Assert.Equal(before, await f.Cut());
    }

    [Fact]
    public async Task Legacy_restore_refuses_protected_archive_before_replacing_any_bytes()
    {
        using var f = await LifecycleFixture.Create();
        string archive = await Create(f);
        string target = Path.Combine(f.H.F.H.H.Root, "legacy-destination");
        string sentinel = Path.Combine(target, "saves", "sentinel.rws");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        File.WriteAllText(sentinel, "previous usable bytes");
        Assert.Throws<InvalidOperationException>(() => BackupArchive.Restore(archive, target));
        Assert.Equal("previous usable bytes", File.ReadAllText(sentinel));
        Assert.False(File.Exists(Path.Combine(target, BackupArchive.DatabaseFile)));
    }

    [Fact]
    public async Task Manifest_matches_every_packed_byte_and_exact_database_cut()
    {
        using var f = await LifecycleFixture.Create();
        var entries = Read(await Create(f));
        var manifest = System.Text.Json.JsonSerializer.Deserialize<OutpostBackupManifest>(entries[OutpostBackupManifest.FileName], OutpostBackupManifest.Json)!;
        Assert.Equal(1, manifest.Version);
        var head = await f.H.F.H.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        Assert.Equal(head.HeadId, manifest.Authority.HeadId);
        Assert.Equal(head.HeadDigest, manifest.Authority.HeadDigest);
        Assert.Equal(entries.Count - 1, manifest.Files.Length);
        foreach (var entry in manifest.Files)
        {
            Assert.Equal(entries[entry.Path].LongLength, entry.Length);
            Assert.Equal(SHA256.HashData(entries[entry.Path]), entry.Sha256);
        }
        Assert.Contains(manifest.Proofs, p => p.Kind == "snapshot");
        Assert.Contains(manifest.Proofs, p => p.Kind == "geographic-manifest");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backup_output_under_protected_tree_is_refused_before_settling(bool anchor)
    {
        using var f = await LifecycleFixture.Create();
        f.H.F.Time.Advance(TimeSpan.FromMinutes(31.5));
        string root = anchor ? Path.GetDirectoryName(f.H.F.H.H.Anchor.DatabasePath)! : f.H.F.H.H.Data.SavesRoot;
        string output = Path.Combine(root, "not-a-safe-backup.tar.zst");
        string before = await f.Cut();
        using var scope = f.Host.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.ServiceProvider.GetRequiredService<OutpostBackupCapture>().CreateAsync(output, default));
        Assert.Equal(before, await f.Cut());
        Assert.False(File.Exists(output));
    }

    internal static async Task<string> Create(LifecycleFixture f)
    {
        var backup = await f.Host.Services.GetRequiredService<BackupService>().CreateNow();
        return Path.Combine(f.H.F.H.H.Data.Backups, backup.Name);
    }

    internal static Dictionary<string, byte[]> Read(string path)
    {
        using var file = File.OpenRead(path);
        using var zstd = new DecompressionStream(file);
        using var tar = new TarReader(zstd);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            Assert.NotNull(entry.DataStream);
            using var content = new MemoryStream(); entry.DataStream.CopyTo(content);
            Assert.True(result.TryAdd(entry.Name, content.ToArray()), "duplicate archive entry: " + entry.Name);
        }
        return result;
    }
}
