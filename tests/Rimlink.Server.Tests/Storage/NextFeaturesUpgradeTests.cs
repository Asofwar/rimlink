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
        var previous = db.Database.GetMigrations().TakeWhile(x => !x.EndsWith("_NextFeatures", StringComparison.Ordinal)).Last();
        db.GetService<IMigrator>().Migrate(previous);
        var player = new Player { Account = new Account { Login = "old-account", CreatedAt = DateTime.UtcNow }, Name = "Existing", FactionName = "Colony" };
        db.Players.Add(player);
        db.SaveChanges();
        db.Wallets.Add(new Wallet { PlayerId = player.Id, Balance = 1234 });
        byte[] hash = Enumerable.Repeat((byte)42, 32).ToArray();
        db.SaveVersions.Add(new SaveVersion { PlayerId = player.Id, Number = 7, Size = 100, Sha256 = hash, IsCurrent = true, CreatedAt = DateTime.UtcNow, GameTick = 10000 });
        db.SaveChanges();
        db.ChangeTracker.Clear();
        db.Database.Migrate();
        Assert.Equal(1234, db.Wallets.Single().Balance);
        var save = db.SaveVersions.Single();
        Assert.Equal(7, save.Number);
        Assert.Equal(hash, save.Sha256);
        Assert.True(save.IsCurrent);
        Assert.Equal("Existing", db.Players.Single().Name);
        Assert.Empty(db.Set<ColonyMember>());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
