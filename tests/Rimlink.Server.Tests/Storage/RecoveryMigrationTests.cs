using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Rimlink.Server.Storage;
using Xunit;

namespace Rimlink.Server.Tests.Storage;

public sealed class RecoveryMigrationTests
{
    [Fact]
    public void UpgradeKeepsLegacyPayloadsAndBindsOnlyKnownRequestOwners()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate("20261103070836_ModpackDelivery");
        db.Database.ExecuteSqlRaw("""
            INSERT INTO wallets (PlayerId, Balance, UpdatedAt) VALUES (1, 1234, '2026-11-03');
            INSERT INTO parcels (Id,ToPlayerId,FromPlayerId,Kind,Items,Pawns,Silver,SizeBytes,CreatedAt,ClientRequestId,Decision)
            VALUES (1,2,1,0,X'CAFE',X'',0,2,'2026-11-03','send-old',0),
                   (2,2,NULL,3,X'',X'',15,0,'2026-11-03','withdraw-old',0),
                   (3,2,1,1,X'',X'',0,0,'2026-11-03','shop-old',0),
                   (4,2,NULL,4,X'',X'',0,0,'2026-11-03','unknown-old',0);
            INSERT INTO save_versions (PlayerId,Number,GameTick,Size,Sha256,CreatedAt,IsCurrent,DevMode,WealthTotal,WealthItems)
            VALUES (1,1,5,99,X'12','2026-11-03',1,0,0,0);
            """);

        migrator.Migrate();

        var parcels = db.Parcels.OrderBy(p => p.Id).ToArray();
        Assert.Equal(4, parcels.Length);
        Assert.Equal(new byte[] { 0xca, 0xfe }, parcels[0].Items);
        Assert.Equal(1, parcels[0].RequestActorId); Assert.Equal("transfer.send", parcels[0].RequestKind);
        Assert.Equal(2, parcels[1].RequestActorId); Assert.Equal("wallet.withdraw", parcels[1].RequestKind);
        Assert.Equal(2, parcels[2].RequestActorId); Assert.Equal("shop.buy", parcels[2].RequestKind);
        Assert.Null(parcels[3].RequestActorId); Assert.Null(parcels[3].RequestKind);
        Assert.All(parcels, p => Assert.Null(p.RequestIntent));
        Assert.Equal("unknown-old", parcels[3].ClientRequestId);
        Assert.Equal(1234, db.Wallets.Single().Balance);
        var save = db.SaveVersions.Single();
        Assert.Null(save.SnapshotId); Assert.Equal(0u, save.RecoveryVersion); Assert.Null(save.RecoveryJson);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
