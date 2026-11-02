using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Storage;

public sealed class AuctionSchemaTests
{
    [Fact]
    public async Task UpgradeFrom062KeepsPlayerAndWalletAndAddsDurableOperationIdentity()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261030085555_IncidentRequestIdPerBuyer");
        const long playerId = 1;
        // Seed the 0.6.2 schema directly: the current Player model already requires SaveGeneration.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO accounts (Id, Role, CreatedAt) VALUES (1, 0, {DateTime.UtcNow});
            INSERT INTO players (Id, AccountId, Name, FactionName, ColorArgb) VALUES (1, 1, 'Schema', 'Schema', {0xFF4A90D9L});
            INSERT INTO wallets (PlayerId, Balance, UpdatedAt) VALUES (1, 123, {DateTime.UtcNow});
            """);
        Assert.Equal(0L, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_table_info('players') WHERE name = 'SaveGeneration'").SingleAsync());
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        var player = await db.Players.AsNoTracking().SingleAsync();
        Assert.Equal(playerId, player.Id);
        Assert.Equal("Schema", player.Name);
        Assert.Equal(0L, player.SaveGeneration);
        Assert.Equal(123, await db.Wallets.Where(w => w.PlayerId == playerId).Select(w => w.Balance).SingleAsync());
        Assert.Empty(await db.Set<AuctionRow>().ToListAsync());
        db.Set<AuctionOperation>().Add(new AuctionOperation { PlayerId = player.Id, RequestId = "same", IntentHash = "hash", Response = [1] });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.Set<AuctionOperation>().Add(new AuctionOperation { PlayerId = player.Id, RequestId = "same", IntentHash = "other", Response = [2] });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
