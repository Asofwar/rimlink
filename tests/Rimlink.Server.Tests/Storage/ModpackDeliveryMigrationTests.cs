using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Storage;

public sealed class ModpackDeliveryMigrationTests
{
    [Theory]
    [InlineData("20261102104417_SaveGeneration")]
    [InlineData("20261102173555_GuildInfluence")]
    public void UpgradingTheReleasedSchemaPreservesExistingModpackAndWorld(string previousMigration)
    {
        using var connection = new SqliteConnection("DataSource=:memory:"); connection.Open();
        using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>(); migrator.Migrate(previousMigration);
        db.ModpackEntries.Add(new ModpackEntry { PackageId = "ludeon.rimworld", Version = "1.7", Position = 0 });
        db.SaveChanges();
        db.Database.ExecuteSqlInterpolated($"""
            INSERT INTO world (Id, Seed, Params, NpcTiles, CreatedAt) VALUES (1, 'keep-world', X'010203', '', {DateTime.UtcNow});
            """);
        migrator.Migrate();
        Assert.Equal("ludeon.rimworld", db.ModpackEntries.Single().PackageId); Assert.Equal("keep-world", db.World.Single().Seed);
        Assert.Empty(db.Set<ModpackPackage>()); Assert.Empty(db.Set<ModpackPublication>());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
