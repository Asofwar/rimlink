using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Backup;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Backup;
using Rimlink.Server.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Rimlink.Server.Tests.Outposts;

/// <summary>Explicit worker for real legacy authority/cuts transported between qualified executors.</summary>
public sealed class OutpostCrossExecutorSchemaTests(ITestOutputHelper output)
{
    private const string V1Migration = "20261104093958_OutpostOrdinaryReturns";

    [Fact]
    public async Task Owned_cross_executor_schema_worker()
    {
        string? export = Environment.GetEnvironmentVariable("RIMLINK_TEST_SCHEMA_EXPORT");
        string? import = Environment.GetEnvironmentVariable("RIMLINK_TEST_SCHEMA_IMPORT");
        string? validate = Environment.GetEnvironmentVariable("RIMLINK_TEST_SCHEMA_VALIDATE");
        if (export is null && import is null && validate is null) return;
        Assert.Equal(1, new[] { export, import, validate }.Count(p => p is not null));
        string root = Path.Combine(Path.GetTempPath(), "rimlink-schema-cross-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string original;
            string cut;
            if (export is not null)
            {
                (original, cut) = await CreateLegacyCut(root, export);
            }
            else
            {
                original = (import ?? validate)!;
                cut = original + ".cut.tar.zst";
            }
            Assert.True(new FileInfo(original).Length is > 0 and <= 512L * 1024 * 1024);
            Assert.False(File.Exists(original + "-wal"));
            Assert.False(File.Exists(original + "-shm"));
            byte[] originalHash = await Hash(original), cutHash = await Hash(cut);
            var archive = OutpostBackupTests.Read(cut);
            if (validate is null) Assert.Equal(archive["rimlink.db"], await File.ReadAllBytesAsync(original));
            var data = new DataDirectory(Path.Combine(root, "working"));
            File.Copy(original, data.DatabasePath, false);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = data.DatabasePath, Pooling = false, Mode = SqliteOpenMode.ReadWrite
            }.ToString()))
            {
                await connection.OpenAsync();
                await using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
                var files = new SaveFiles(data);
                byte[] prefix = await ProtectedPrefix(db);
                var row = await db.OutpostAuthority.AsNoTracking().SingleAsync();
                var manifest = JsonSerializer.Deserialize<OutpostBackupManifest>(archive[OutpostBackupManifest.FileName], OutpostBackupManifest.Json)!;
                Assert.Equal(manifest.Authority.HeadId, row.HeadId);
                Assert.Equal(manifest.Authority.HeadDigest, row.HeadDigest);
                Assert.Equal("Initialize", (await db.OutpostTransitions.AsNoTracking().SingleAsync()).Kind);
                await OutpostAuthority.ValidateStoredChainAsync(db, files, row, new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)), CancellationToken.None);
                byte[] before = await Catalog(connection);
                var v1 = await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V1, CancellationToken.None);
                Assert.Equal(manifest.DomainDigest, v1.ToByteArray());
                output.WriteLine("SCHEMA-SOURCE-V1:" + Convert.ToHexStringLower(v1.Span));
                output.WriteLine("SCHEMA-LEGACY-PREFIX-SHA256:" + Convert.ToHexStringLower(SHA256.HashData(prefix)));
                output.WriteLine("SCHEMA-RETAINED-CUT-SHA256:" + Convert.ToHexStringLower(cutHash));
                Emit(validate is null ? "pre" : "copied", before);
                if (validate is null)
                {
                    await db.GetService<IMigrator>().MigrateAsync();
                    Assert.Equal(v1, await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V1, CancellationToken.None));
                    Assert.Equal(prefix, await ProtectedPrefix(db));
                    await OutpostAuthority.ValidateStoredChainAsync(db, files, row, new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)), CancellationToken.None);
                    byte[] migrated = await Catalog(connection);
                    AssertBaseObjectsUnchanged(before, migrated);
                    Emit("post", migrated);
                    string migratedExport = Environment.GetEnvironmentVariable("RIMLINK_TEST_SCHEMA_POST_EXPORT") ?? original + ".migrated";
                    string recipe = db.GetService<IMigrator>().GenerateScript(V1Migration, null);
                    byte[] recipeBytes = System.Text.Encoding.UTF8.GetBytes(recipe);
                    await File.WriteAllBytesAsync(migratedExport + ".recipe.sql", recipeBytes);
                    output.WriteLine("SCHEMA-MIGRATION-RECIPE-SHA256:" + Convert.ToHexStringLower(SHA256.HashData(recipeBytes)));
                    using var snapshot = connection.CreateCommand();
                    snapshot.CommandText = "VACUUM INTO $path";
                    snapshot.Parameters.AddWithValue("$path", migratedExport);
                    await snapshot.ExecuteNonQueryAsync();
                    File.Copy(cut, migratedExport + ".cut.tar.zst", false);
                    output.WriteLine("SCHEMA-MIGRATED-DB-SHA256:" + Convert.ToHexStringLower(await Hash(migratedExport)));
                    before = migrated;
                }
                await db.Database.ExecuteSqlRawAsync("PRAGMA query_only=ON");
                var actual = await OutpostDomainSchema.ValidateAsync(db, validate is not null && !(await db.Database.GetAppliedMigrationsAsync()).Contains(OutpostDomainSchema.AdminMigrationId)
                    ? OutpostSchemaContext.HistoricalCutV1 : OutpostSchemaContext.CurrentRuntime, CancellationToken.None);
                Assert.Equal(32, actual.Digest.Length);
                if (Environment.GetEnvironmentVariable("RIMLINK_TEST_SCHEMA_EXPECTED_PROFILE") is { } expected)
                    Assert.Equal(expected, actual.ProfileId);
                Assert.Equal(before, await Catalog(connection));
                Assert.Equal(prefix, await ProtectedPrefix(db));
                output.WriteLine("SCHEMA-QUALIFIED-PROFILE:" + actual.ProfileId + ":" + Convert.ToHexStringLower(actual.Digest.Span));
                output.WriteLine("SCHEMA-PROVIDER:" + typeof(SqliteConnection).Assembly.FullName);
                output.WriteLine("SCHEMA-EF:" + typeof(DbContext).Assembly.FullName);
                output.WriteLine("SCHEMA-SQLITE:" + connection.ServerVersion);
                output.WriteLine("SCHEMA-RUNTIME:" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            }
            Assert.Equal(originalHash, await Hash(original));
            Assert.Equal(cutHash, await Hash(cut));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<(string Database, string Cut)> CreateLegacyCut(string root, string export)
    {
        Assert.True(Path.IsPathFullyQualified(export));
        Assert.True(Directory.Exists(Path.GetDirectoryName(export)));
        string dataRoot, anchorRoot;
        using (var h = new AuthorityHarness(Path.Combine(root, "seed")))
        {
            await h.Db.GetService<IMigrator>().MigrateAsync(V1Migration);
            h.Anchor.Provision();
            h.Db.World.Add(new Rimlink.Server.Storage.Entities.World { InfluenceInstanceId = "world-1" });
            await h.Db.SaveChangesAsync();
            Assert.Equal(OutpostStartupState.Ready, (await h.Authority.InitializeAsync(new("world-1", "cross-executor-fixture", true), CancellationToken.None)).State);
            dataRoot = h.Data.Root;
            anchorRoot = Path.GetDirectoryName(h.Anchor.DatabasePath)!;
            // The owned fixture must remain alive until its coherent backup has completed.
            string cut = export + ".cut.tar.zst";
            await StoppedBackup.CreateAsync(dataRoot, cut, new OutpostOptions { AnchorDirectory = anchorRoot,
                StorageContract = "sqlite-local-v1", QualifiedLinuxFileSystem = LocalTestStorage.LinuxQualification(dataRoot) }, CancellationToken.None);
            var archive = OutpostBackupTests.Read(cut);
            await File.WriteAllBytesAsync(export, archive["rimlink.db"]);
            return (export, cut);
        }
    }

    private static async Task<byte[]> ProtectedPrefix(RimlinkDbContext db) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        Authority = await db.OutpostAuthority.AsNoTracking().ToArrayAsync(),
        Transitions = await db.OutpostTransitions.AsNoTracking().OrderBy(t => t.Id).ToArrayAsync(),
        World = await db.World.AsNoTracking().ToArrayAsync()
    });

    private static void AssertBaseObjectsUnchanged(byte[] before, byte[] after)
    {
        using var old = JsonDocument.Parse(before);
        using var next = JsonDocument.Parse(after);
        var rows = next.RootElement.GetProperty("Objects").EnumerateArray().ToDictionary(r => (r[0].GetString(), r[1].GetString()));
        foreach (var row in old.RootElement.GetProperty("Objects").EnumerateArray())
            Assert.Equal(row.GetRawText(), rows[(row[0].GetString(), row[1].GetString())].GetRawText());
    }

    private void Emit(string kind, byte[] catalog)
    {
        string encoded = Convert.ToBase64String(catalog);
        for (int i = 0; i < encoded.Length; i += 1024)
            output.WriteLine($"SCHEMA-CROSS-CATALOG:{kind}:{i / 1024}:{encoded.Substring(i, Math.Min(1024, encoded.Length - i))}");
    }

    private static async Task<byte[]> Hash(string path)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream);
    }

    private static async Task<byte[]> Catalog(SqliteConnection connection)
    {
        var objects = new List<string?[]>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_schema ORDER BY type COLLATE BINARY,name COLLATE BINARY";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) objects.Add([reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)]);
        }
        var migrations = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId COLLATE BINARY";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) migrations.Add(reader.GetString(0));
        }
        return JsonSerializer.SerializeToUtf8Bytes(new { Migrations = migrations, Objects = objects });
    }
}
