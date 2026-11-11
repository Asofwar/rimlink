using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildDomainHistoryTests
{
    [Theory]
    [InlineData("1.5")]
    [InlineData("'false'")]
    [InlineData("3")]
    public async Task Governance_hash_refuses_noncanonical_boolean_storage(string sqlValue)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Database(connection); await db.Database.MigrateAsync();
        db.Add(new Player { Id = 1, Name = "owner", Account = new Account { Id = 1, Login = "owner" } });
        db.Add(new AllianceGroup { Id = 1, OwnerId = 1, Name = "guild" }); await db.SaveChangesAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE alliance_groups SET Disbanded=" + sqlValue;
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => GuildDomainDigest.ComputeHeldAsync(db, default));
    }

    [Fact]
    public async Task V3_binds_guild_stock_without_changing_either_legacy_writer()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Database(connection); await db.Database.MigrateAsync();
        var v1 = await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V1, default);
        var v2 = await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V2, default);
        var v3 = await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V3, default);
        Assert.NotEqual(v2, v3);
        db.Add(new GuildOutpostRow { Id = "outpost", WorldId = "world", Resource = GuildResource.Steel }); await db.SaveChangesAsync();
        var after = await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V3, default);
        Assert.NotEqual(v3, after);
        await db.Database.ExecuteSqlRawAsync("UPDATE guild_outposts SET Stock=10");
        Assert.NotEqual(after, await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V3, default));
        Assert.Equal(v1, await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V1, default));
        Assert.Equal(v2, await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V2, default));
    }

    [Fact]
    public async Task Governance_is_bound_but_ordinary_treasury_changes_are_outside_the_projection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Database(connection); await db.Database.MigrateAsync();
        for (int id = 1; id <= 2; id++) db.Add(new Player { Id = id, Name = "player" + id, Account = new Account { Id = id, Login = "player" + id } });
        db.Add(new AllianceGroup { Id = 1, OwnerId = 1, Name = "guild" });
        db.Add(new AllianceMember { PlayerId = 1, AllianceId = 1, JoinedAt = DateTime.UnixEpoch });
        await db.SaveChangesAsync();
        var before = await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V3, default);
        await db.Database.ExecuteSqlRawAsync("UPDATE alliance_groups SET Silver=100");
        Assert.Equal(before, await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V3, default));
        await db.Database.ExecuteSqlRawAsync("UPDATE alliance_groups SET OwnerId=2");
        var ownerChanged = await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V3, default);
        Assert.NotEqual(before, ownerChanged);
        await db.Database.ExecuteSqlRawAsync("UPDATE alliance_members SET JoinedAt='2030-01-01 00:00:00'");
        Assert.NotEqual(ownerChanged, await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V3, default));
    }

    [Fact]
    public async Task Preactivation_requires_all_new_tables_to_be_empty()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Database(connection); await db.Database.MigrateAsync();
        await GuildDomainDigest.RequireEmptyAsync(db, default);
        db.Add(new GuildOutpostRow { Id = "outpost", WorldId = "world", Resource = GuildResource.Steel }); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => GuildDomainDigest.RequireEmptyAsync(db, default));
    }

    private static RimlinkDbContext Database(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
}
