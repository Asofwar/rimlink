using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Migrations;
using Xunit;
using Xunit.Abstractions;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostDomainFormatTests(ITestOutputHelper output)
{
    private const string V1Migration = "20261104093958_OutpostOrdinaryReturns";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Trigger_migration_SQL_preserves_exact_qualified_CRLF_bytes(bool admin)
    {
        Migration migration = admin ? new OutpostAdminCommands() : new PassiveOutposts();
        string table = admin ? "outpost_admin_commands" : "outpost_transitions";
        string evidence = admin ? "outpost admin command" : "outpost transition";
        string expected = $"CREATE TRIGGER {table}_no_update BEFORE UPDATE ON {table}\r\n" +
            $"BEGIN SELECT RAISE(ABORT, '{evidence} evidence is immutable'); END;\r\n" +
            $"CREATE TRIGGER {table}_no_delete BEFORE DELETE ON {table}\r\n" +
            $"BEGIN SELECT RAISE(ABORT, '{evidence} evidence is immutable'); END;";

        Assert.Equal(expected, Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql);
    }

    [Fact]
    public async Task Additive_migration_recipe_preserves_the_qualified_digest()
    {
        await using var f = await SchemaDatabase.Create(true);
        string recipe = f.Db.GetService<IMigrator>().GenerateScript(V1Migration, OutpostDomainSchema.AdminMigrationId);
        string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(recipe)));
        output.WriteLine("SCHEMA-MIGRATION-RECIPE-SHA256:" + digest);
        Assert.Equal(OperatingSystem.IsWindows()
            ? "64358bbb34d8080afaaeeb745041b9fdeccadc28af8ce84666734ce3b8324525"
            : "a5ee5d52c1d25f13d4f1d75e74f3cd02763b0667d81f3a74f23d62e7cea8b3f4", digest);
    }

    [Fact]
    public async Task Admitted_profiles_include_exact_cross_executor_qualification_evidence()
    {
        var assembly = typeof(OutpostDomainSchema).Assembly;
        using var stream = assembly.GetManifestResourceStream("Rimlink.OutpostSchema.qualification.json");
        Assert.NotNull(stream);
        Assert.InRange(stream.Length, 1, 65536);
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        Assert.Equal("5137aaaabb6394d206fa2a46e5ae980f618a787331a103bb5dee7662bd486caa",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        using var evidence = JsonDocument.Parse(bytes);
        var profiles = evidence.RootElement.GetProperty("profiles").EnumerateArray().ToArray();
        Assert.Equal(6, profiles.Length);
        Assert.Equal(4, evidence.RootElement.GetProperty("additiveEdges").GetArrayLength());
        foreach (var profile in profiles)
        {
            using var descriptor = assembly.GetManifestResourceStream("Rimlink.OutpostSchema." + profile.GetProperty("descriptor").GetString());
            Assert.NotNull(descriptor);
            Assert.Equal(profile.GetProperty("descriptorSha256").GetString(),
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(descriptor)));
            var validators = profile.GetProperty("validators").EnumerateArray().ToArray();
            Assert.Equal(new[] { "linux", "windows" }, validators.Select(v => v.GetProperty("executor").GetString()).Order().ToArray());
            Assert.All(validators, v => Assert.Equal(1, v.GetProperty("testsPassed").GetInt32()));
        }
        await using var database = await SchemaDatabase.Create(false);
        var current = await OutpostDomainSchema.ValidateAsync(database.Db, OutpostSchemaContext.CurrentRuntime, CancellationToken.None);
        var qualified = Assert.Single(profiles, p => p.GetProperty("id").GetString() == current.ProfileId);
        Assert.Equal(Convert.ToHexStringLower(current.Digest.Span), qualified.GetProperty("descriptorSha256").GetString());
    }

    [Fact]
    public async Task Explicit_v2_hash_does_not_change_the_frozen_v1_hash()
    {
        await using var f = await SchemaDatabase.Create(false);
        var v1 = await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V1, CancellationToken.None);
        Assert.Equal(Rimlink.Server.Tests.Support.OutpostDomainVectors.EmptyV1ForLocalMigrations, Convert.ToHexStringLower(v1.Span));
        var v2 = await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V2, CancellationToken.None);
        Assert.Equal(32, v2.Length);
        Assert.NotEqual(v1, v2);
        Assert.Equal(v1, await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V1, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Historical_v1_cut_is_recognized_without_migration_or_writes(bool preAdmin)
    {
        await using var f = await SchemaDatabase.Create(preAdmin);
        byte[] before = await f.Catalog();
        string encoded = Convert.ToBase64String(before);
        for (int i = 0; i < encoded.Length; i += 1024)
            output.WriteLine($"SCHEMA-CATALOG:{(preAdmin ? "v1" : "v2")}:{i / 1024}:{encoded.Substring(i, Math.Min(1024, encoded.Length - i))}");
        await f.Db.Database.ExecuteSqlRawAsync("PRAGMA query_only=ON");

        var result = await OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.HistoricalCutV1, CancellationToken.None);

        Assert.Equal("rimlink-outposts-schema/" + (OperatingSystem.IsWindows()
            ? (preAdmin ? "v1-crlf" : "v2-crlf-crlf") : (preAdmin ? "v1-lf" : "v2-lf-lf")), result.ProfileId);
        Assert.Equal(!preAdmin, result.IsCurrent);
        Assert.Equal(32, result.Digest.Length);
        Assert.Equal(before, await f.Catalog());
    }

    [Fact]
    public async Task Current_runtime_requires_the_actual_additive_schema()
    {
        await using var f = await SchemaDatabase.Create(true);
        byte[] before = await f.Catalog();
        await Assert.ThrowsAsync<InvalidDataException>(() => OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.CurrentRuntime, CancellationToken.None));
        Assert.Equal(before, await f.Catalog());
    }

    [Theory]
    [InlineData("CREATE TABLE outpost_admin_shadow(Id TEXT)")]
    [InlineData("CREATE TABLE sqlitex_shadow(Id TEXT)")]
    [InlineData("CREATE VIEW sqlitex_shadow_view AS SELECT Id FROM outposts")]
    [InlineData("CREATE VIEW forged_projection AS SELECT Id FROM outposts")]
    [InlineData("CREATE INDEX unrecognized_outpost_index ON outposts(Stock)")]
    [InlineData("DROP TRIGGER outpost_admin_commands_no_update")]
    [InlineData("CREATE TRIGGER extra_outpost_trigger AFTER INSERT ON outposts BEGIN SELECT 1; END")]
    [InlineData("ALTER TABLE outpost_admin_commands ADD COLUMN Unreviewed TEXT")]
    [InlineData("DELETE FROM __EFMigrationsHistory WHERE MigrationId='20261105014436_OutpostAdminCommands'")]
    public async Task Unknown_actual_schema_or_migration_lineage_is_not_accepted(string change)
    {
        await using var f = await SchemaDatabase.Create(false);
        await f.Db.Database.ExecuteSqlRawAsync(change);
        byte[] changed = await f.Catalog();
        await f.Db.Database.ExecuteSqlRawAsync("PRAGMA query_only=ON");

        await Assert.ThrowsAsync<InvalidDataException>(() => OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.CurrentRuntime, CancellationToken.None));

        Assert.Equal(changed, await f.Catalog());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task An_unqualified_LF_trigger_is_refused_without_rewriting_the_catalog(bool admin, bool update)
    {
        await using var f = await SchemaDatabase.Create(false);
        string table = admin ? "outpost_admin_commands" : "outpost_transitions";
        string evidence = admin ? "outpost admin command" : "outpost transition";
        string action = update ? "UPDATE" : "DELETE";
        string trigger = table + (update ? "_no_update" : "_no_delete");
        await f.ExecuteFixtureSql($"DROP TRIGGER {trigger}; CREATE TRIGGER {trigger} BEFORE {action} ON {table}\n" +
            $"BEGIN SELECT RAISE(ABORT, '{evidence} evidence is immutable'); END;");
        byte[] changed = await f.Catalog();
        await f.Db.Database.ExecuteSqlRawAsync("PRAGMA query_only=ON");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => OutpostDomainSchema.ValidateAsync(f.Db,
            OutpostSchemaContext.CurrentRuntime, CancellationToken.None));

        Assert.Equal("outpost-schema-profile-mismatch", error.Message);
        Assert.Equal(changed, await f.Catalog());
    }

    [Fact]
    public async Task Missing_internal_autoindex_is_not_hidden_by_reserved_name_filtering()
    {
        await using var f = await SchemaDatabase.Create(false);
        await f.ExecuteFixtureSql("PRAGMA writable_schema=ON; DELETE FROM sqlite_schema WHERE name=(SELECT name FROM sqlite_schema WHERE type='index' AND name GLOB 'sqlite_autoindex_*' ORDER BY name LIMIT 1); PRAGMA writable_schema=OFF;");
        byte[] changed = await f.Catalog();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.CurrentRuntime, CancellationToken.None));
        Assert.Equal("outpost-schema-profile-mismatch", error.Message);
        Assert.Equal(changed, await f.Catalog());
    }

    [Fact]
    public async Task Missing_migration_table_is_a_typed_schema_refusal_without_repair()
    {
        await using var f = await SchemaDatabase.Create(false);
        await f.ExecuteFixtureSql("DROP TABLE __EFMigrationsHistory");
        await f.Db.Database.ExecuteSqlRawAsync("PRAGMA query_only=ON");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.CurrentRuntime, CancellationToken.None));

        Assert.Equal("outpost-schema-profile-mismatch", error.Message);
        await Assert.ThrowsAsync<SqliteException>(() => f.Catalog());
    }

    [Fact]
    public async Task Missing_admin_table_is_never_a_v2_historical_downgrade()
    {
        await using var f = await SchemaDatabase.Create(true);
        await Assert.ThrowsAsync<InvalidDataException>(() => OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.HistoricalCutV2, CancellationToken.None));
    }

    [Fact]
    public async Task V2_covers_receipts_while_v1_bytes_remain_frozen()
    {
        await using var f = await SchemaDatabase.Create(false);
        var v1 = await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V1, CancellationToken.None);
        var v2 = await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V2, CancellationToken.None);
        f.Db.OutpostAdminCommands.Add(Receipt());
        await f.Db.SaveChangesAsync();

        Assert.Equal(v1, await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V1, CancellationToken.None));
        Assert.NotEqual(v2, await OutpostDomainDigest.ComputeAsync(f.Db, OutpostDomainFormat.V2, CancellationToken.None));
    }

    [Fact]
    public async Task Historical_v1_cannot_hide_nonempty_admin_receipts_in_the_migrated_table()
    {
        await using var f = await SchemaDatabase.Create(false);
        f.Db.OutpostAdminCommands.Add(Receipt());
        await f.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.HistoricalCutV1, CancellationToken.None));
        Assert.Equal("outpost-schema-v1-admin-evidence-conflict", error.Message);
        Assert.Single(await f.Db.OutpostAdminCommands.ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Actual_schema_catalog_size_limits_refuse_without_partial_acceptance(bool objectCount)
    {
        await using var f = await SchemaDatabase.Create(false);
        if (objectCount)
        {
            for (int i = 0; i < 513; i++)
                await f.ExecuteFixtureSql("CREATE TABLE excess_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "(Id INTEGER)");
        }
        else await f.ExecuteFixtureSql("CREATE VIEW excessive_sql AS SELECT '" + new string('x', 65537) + "' AS value");
        byte[] before = await f.Catalog();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => OutpostDomainSchema.ValidateAsync(f.Db, OutpostSchemaContext.CurrentRuntime, CancellationToken.None));
        Assert.Equal("outpost-schema-catalog-bounds", error.Message);
        Assert.Equal(before, await f.Catalog());
    }

    private static Rimlink.Server.Storage.Entities.OutpostAdminCommandRecord Receipt() => new()
    {
        Id = "11111111-1111-4111-8111-111111111111", ActorKey = new string('a', 64), Action = "ActivateDomainFormat",
        RequestId = "22222222-2222-4222-8222-222222222222", Reason = "explicit schema activation", Command = [1], CommandDigest = new byte[32],
        TransitionId = "33333333-3333-4333-8333-333333333333", NextHeadId = "44444444-4444-4444-8444-444444444444",
        Result = [1], AuditId = 1, AuditProjection = [1], CreatedAt = DateTime.UnixEpoch
    };

    private sealed class SchemaDatabase(SqliteConnection connection, RimlinkDbContext db) : IAsyncDisposable
    {
        public RimlinkDbContext Db => db;
        public static async Task<SchemaDatabase> Create(bool preAdmin)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
            await db.GetService<IMigrator>().MigrateAsync(preAdmin ? V1Migration : null);
            return new(connection, db);
        }
        public async Task ExecuteFixtureSql(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql; // Only bounded fixture-owned names and literals are supplied above.
            await command.ExecuteNonQueryAsync();
        }
        public async Task<byte[]> Catalog()
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
                command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) migrations.Add(reader.GetString(0));
            }
            return JsonSerializer.SerializeToUtf8Bytes(new { Migrations = migrations, Objects = objects });
        }
        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
