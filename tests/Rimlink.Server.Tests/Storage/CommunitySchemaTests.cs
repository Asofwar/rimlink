using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Storage;

public sealed class CommunitySchemaTests
{
    [Fact]
    public async Task MigrationPreservesOldPlayerAndCreatesCommunityTablesWithUniqueMembership()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
        const string before = "20261030085555_IncidentRequestIdPerBuyer"; // 0.6.2, before the combined next-feature tables
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync(before);
        // Seed only columns present in 0.6.2, independently of the current EF Player model.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO accounts (Id, Login, Role, CreatedAt) VALUES (1, 'alice', 0, {DateTime.UtcNow});
            INSERT INTO players (Id, AccountId, Name, FactionName, ColorArgb, WealthTotal) VALUES (1, 1, 'Alice', '', {0xFF4A90D9L}, 321);
            """);
        Assert.Equal(0L, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_table_info('players') WHERE name = 'SaveGeneration'").SingleAsync());
        await db.Database.MigrateAsync();
        var player = await db.Players.AsNoTracking().SingleAsync();
        Assert.Equal("Alice", player.Name);
        Assert.Equal(321, player.WealthTotal);
        Assert.Equal(0L, player.SaveGeneration);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        var tables = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync()) while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        foreach (string table in new[] { "alliance_groups", "alliance_members", "community_invitations", "friendships", "player_profiles", "community_operations", "alliance_audit", "alliance_messages" }) Assert.Contains(table, tables);
        var group = new AllianceGroup { Name = "First", OwnerId = player.Id }; db.Add(group); await db.SaveChangesAsync();
        db.Add(new AllianceMember { PlayerId = player.Id, AllianceId = group.Id }); await db.SaveChangesAsync();
        cmd.CommandText = "INSERT INTO alliance_members (PlayerId, AllianceId, JoinedAt, MayWithdraw, DailyCap, WithdrawalDay, Withdrawn) SELECT PlayerId, AllianceId, JoinedAt, MayWithdraw, DailyCap, WithdrawalDay, Withdrawn FROM alliance_members";
        await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync());
    }
}
