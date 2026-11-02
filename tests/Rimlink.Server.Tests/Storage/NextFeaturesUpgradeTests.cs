using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Storage;

public sealed class NextFeaturesUpgradeTests
{
    [Fact]
    public void UpgradeKeepsExistingPlayerSaveMetadataAndWalletBalance()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
        const string previous = "20261030085555_IncidentRequestIdPerBuyer"; // 0.6.2
        db.GetService<IMigrator>().Migrate(previous);
        byte[] hash = Enumerable.Repeat((byte)42, 32).ToArray();
        // Keep the source data tied to the old schema rather than today's EF entity columns.
        db.Database.ExecuteSqlInterpolated($"""
            INSERT INTO accounts (Id, Login, Role, CreatedAt) VALUES (1, 'old-account', 0, {DateTime.UtcNow});
            INSERT INTO players (Id, AccountId, Name, FactionName, ColorArgb) VALUES (1, 1, 'Existing', 'Colony', {0xFF4A90D9L});
            INSERT INTO wallets (PlayerId, Balance, UpdatedAt) VALUES (1, 1234, {DateTime.UtcNow});
            INSERT INTO save_versions (PlayerId, Number, Size, Sha256, IsCurrent, CreatedAt, GameTick)
                VALUES (1, 7, 100, {hash}, 1, {DateTime.UtcNow}, 10000);
            """);
        Assert.Equal(0L, db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_table_info('players') WHERE name = 'SaveGeneration'").Single());
        db.Database.Migrate();
        Assert.Equal(1234, db.Wallets.Single().Balance);
        var save = db.SaveVersions.Single();
        Assert.Equal(7, save.Number);
        Assert.Equal(hash, save.Sha256);
        Assert.True(save.IsCurrent);
        var player = db.Players.Single();
        Assert.Equal("Existing", player.Name);
        Assert.Equal("Colony", player.FactionName);
        Assert.Equal(0L, player.SaveGeneration);
        Assert.Empty(db.Set<ColonyMember>());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
