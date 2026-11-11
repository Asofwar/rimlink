using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildSchemaTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Unanchored_guild_rows_are_retained_history_not_a_fresh_installation(bool provisioned)
    {
        using var f = new Rimlink.Server.Tests.Outposts.AuthorityHarness();
        if (provisioned) f.Anchor.Provision();
        f.Db.World.Add(new() { InfluenceInstanceId = "world" });
        f.Db.Add(new GuildOutpostRow { Id = Guid.NewGuid().ToString("D"), WorldId = "world", Resource = GuildResource.Steel, Stock = 1 });
        await f.Db.SaveChangesAsync();
        Assert.Equal(OutpostStartupState.Maintenance, (await f.Authority.OpenAsync(default)).State);
        Assert.Equal(OutpostStartupState.Maintenance, (await f.Authority.InitializeAsync(new("world", "admin", true), default)).State);
        Assert.Equal(1, (await f.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).Stock);
        Assert.Empty(await f.Db.OutpostTransitions.ToArrayAsync());
    }

    [Fact]
    public async Task Historical_v2_on_additive_schema_is_read_only_and_requires_empty_guild_state()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA query_only=ON");
        Assert.True((await OutpostDomainSchema.ValidateAsync(db, OutpostSchemaContext.HistoricalCutV2, default)).IsCurrent);
        await db.Database.ExecuteSqlRawAsync("PRAGMA query_only=OFF");
        db.Add(new GuildOutpostRow { Id = "retained", WorldId = "world", Resource = GuildResource.Steel }); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => OutpostDomainSchema.ValidateAsync(db, OutpostSchemaContext.HistoricalCutV2, default));
    }

    [Fact]
    public async Task Exact_new_catalog_is_admitted_without_weakening_unknown_schema_refusal()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var observed = await OutpostDomainSchema.ValidateAsync(db, OutpostSchemaContext.CurrentRuntime, default);
        Assert.StartsWith("rimlink-outposts-schema/v3-", observed.ProfileId);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE guild_unrecognized(Id TEXT)");
        await Assert.ThrowsAsync<InvalidDataException>(() => OutpostDomainSchema.ValidateAsync(db, OutpostSchemaContext.CurrentRuntime, default));
    }

    [Fact]
    public async Task Upgrade_is_additive_and_preserves_exact_legacy_domain_and_table_definitions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
        var migrations = db.GetService<IMigrator>();
        await migrations.MigrateAsync(OutpostDomainSchema.AdminMigrationId);
        var v1 = await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V1, default);
        var v2 = await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V2, default);
        var originalCatalog = await LegacyCatalog(connection);
        await migrations.MigrateAsync();
        Assert.Equal(originalCatalog, await LegacyCatalog(connection));
        Assert.Equal(v1, await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V1, default));
        Assert.Equal(v2, await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V2, default));
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name GLOB 'guild_*' ORDER BY name";
        await using var reader = await tables.ExecuteReaderAsync();
        var names = new List<string>(); while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        Assert.Equal(13, names.Count);
        Assert.DoesNotContain("guild_battle_commands", names);
        Assert.False(db.Database.HasPendingModelChanges());
        await reader.DisposeAsync();
        foreach (string name in names)
        {
            using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM \"{name}\"";
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1001, 0)]
    [InlineData(1000, 1)]
    [InlineData(0, -1)]
    [InlineData(0, TimeSpan.TicksPerHour)]
    public async Task Actual_sqlite_checks_refuse_corrupt_stock(int stock, long remainder)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var row = new GuildOutpostRow { Id = Guid.NewGuid().ToString("D"), WorldId = "world", Resource = GuildResource.Steel };
        db.Add(row); await db.SaveChangesAsync();
        row.Stock = stock; row.Remainder = remainder;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Actual_sqlite_uniqueness_reserves_a_neutral_tile_too()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        db.Add(new GuildOutpostRow { Id = "first", WorldId = "world", Tile = 123, Resource = GuildResource.Wood });
        await db.SaveChangesAsync();
        db.Add(new GuildOutpostRow { Id = "second", WorldId = "world", Tile = 123, Resource = GuildResource.Steel });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static async Task<string[]> LegacyCatalog(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name,sql FROM sqlite_schema WHERE name GLOB 'outpost*' OR name='pawn_custody' ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>(); while (await reader.ReadAsync()) result.Add(reader.GetString(0) + "\0" + reader.GetString(1));
        return result.ToArray();
    }
}
