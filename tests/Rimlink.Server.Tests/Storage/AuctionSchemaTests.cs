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
        var player = new Player { Account = new Account { CreatedAt = DateTime.UtcNow, Role = Role.Player }, Name = "Schema", FactionName = "Schema" };
        db.Players.Add(player);
        await db.SaveChangesAsync();
        db.Wallets.Add(new Wallet { PlayerId = player.Id, Balance = 123, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(123, await db.Wallets.Where(w => w.PlayerId == player.Id).Select(w => w.Balance).SingleAsync());
        Assert.Empty(await db.Set<AuctionRow>().ToListAsync());
        db.Set<AuctionOperation>().Add(new AuctionOperation { PlayerId = player.Id, RequestId = "same", IntentHash = "hash", Response = [1] });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.Set<AuctionOperation>().Add(new AuctionOperation { PlayerId = player.Id, RequestId = "same", IntentHash = "other", Response = [2] });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
