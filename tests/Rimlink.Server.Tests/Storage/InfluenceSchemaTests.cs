using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Storage;

public sealed class InfluenceSchemaTests
{
    [Fact]
    public async Task MigrationFrom070PreservesColonyAndArchivesWithoutGuessingSurfaceOrConsent()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261102104417_SaveGeneration");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO accounts (Id, Login, Role, CreatedAt) VALUES (1, 'owner', 0, {DateTime.UtcNow});
            INSERT INTO players (Id, AccountId, Name, FactionName, ColorArgb, WealthTotal, SaveGeneration) VALUES (1, 1, 'Owner', '', 0, 321, 7);
            INSERT INTO world (Id, Seed, Params, NpcTiles, CreatedAt) VALUES (1, 'seed', X'', '', {DateTime.UtcNow});
            INSERT INTO world_objects (Id, Type, OwnerPlayerId, Tile, Name, Payload, CreatedAt) VALUES (5, 'settlement', 1, 42, 'Home', X'', {DateTime.UtcNow});
            INSERT INTO archived_colonies (Id, PlayerId, SaveGeneration, Tile, Name, SettledAt, ArchivedAt, Reason, ArchivedBy, WealthTotal, Dir, State)
              VALUES (9, 1, 7, 43, 'Archive', {DateTime.UtcNow}, {DateTime.UtcNow}, 'admin', 'api', 321, 'archive/test', 0);
            INSERT INTO wallets (PlayerId, Balance, UpdatedAt) VALUES (1, 456, {DateTime.UtcNow});
            """);

        await db.Database.MigrateAsync();

        var world = await db.World.SingleAsync();
        Assert.True(Guid.TryParseExact(world.InfluenceInstanceId, "N", out _));
        Assert.Equal(0, world.InfluenceRevision);
        var home = await db.WorldObjects.SingleAsync();
        Assert.Equal(42, home.Tile);
        Assert.False(home.InfluenceEnabled);
        Assert.Null(home.InfluenceRootSurface);
        var archive = await db.ArchivedColonies.SingleAsync();
        Assert.Equal(7, archive.SaveGeneration);
        Assert.Equal("archive/test", archive.Dir);
        Assert.False(archive.InfluenceEnabled);
        Assert.Null(archive.InfluenceRootSurface);
        Assert.Equal(7, (await db.Players.SingleAsync()).SaveGeneration);
        Assert.Equal(456, (await db.Wallets.SingleAsync()).Balance);
        db.Add(new InfluenceOperation { Actor = "player:1", RequestId = "same", Intent = [1], Result = [2] });
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.Set<InfluenceOperation>().CountAsync());
        var originalId = world.InfluenceInstanceId;
        await db.Database.MigrateAsync();
        Assert.Equal(originalId, (await db.World.AsNoTracking().SingleAsync()).InfluenceInstanceId);
    }
}
