using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Rimlink.Server.Storage;
using Xunit;

namespace Rimlink.Server.Tests.Storage;

public sealed class OutpostMigrationTests
{
    [Fact]
    public void Migration_from_081_preserves_recovery_rows()
    {
        using var connection = Open();
        using var db = Context(connection);
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate("20261103083849_RecoveryCorrectness");
        db.Database.ExecuteSqlRaw("""
            INSERT INTO wallets (PlayerId,Balance,UpdatedAt) VALUES (1,1234,'2026-11-03');
            INSERT INTO save_versions (PlayerId,Number,GameTick,Size,Sha256,CreatedAt,IsCurrent,DevMode,WealthTotal,WealthItems,SaveWriteEpoch,RecoveryVersion,SnapshotId,RecoveryJson)
            VALUES (1,1,5,99,X'12','2026-11-03',1,0,0,0,7,1,'known-snapshot','{{"version":1}}');
            """);
        migrator.Migrate();
        Assert.Equal(1234, db.Wallets.Single().Balance);
        var save = db.SaveVersions.Single();
        Assert.Equal("known-snapshot", save.SnapshotId);
        Assert.Equal("{\"version\":1}", save.RecoveryJson);
        Assert.Equal(7, save.SaveWriteEpoch);
        Assert.Equal(new byte[] { 0x12 }, save.Sha256);
        Assert.Equal(13L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('outposts','outpost_assignments','pawn_custody','outpost_transitions','outpost_operations','outpost_reservations','outpost_parcel_links','outpost_snapshot_links','outpost_manifests','outpost_authority','outpost_restore_authorizations','outpost_delivery_attempts','outpost_lifecycle_operations')"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM outposts"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM outpost_authority"));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Unique_active_custody_and_reserved_tile_are_enforced()
    {
        using var connection = Open();
        using var db = Context(connection);
        db.Database.Migrate();
        db.Database.ExecuteSqlRaw("""
            INSERT INTO outpost_assignments (Id,OutpostId,PawnCustodyId,Active,Blob,BlobDigest,FactsJson,PreparedOperationId,CreatedAt)
            VALUES ('assignment-a','outpost-a','pawn-a',1,X'01',X'02','{{}}','prepare-a','2026-11-03');
            """);
        AssertConstraint(db, """
            INSERT INTO outpost_assignments (Id,OutpostId,PawnCustodyId,Active,Blob,BlobDigest,FactsJson,PreparedOperationId,CreatedAt)
            VALUES ('assignment-b','outpost-b','pawn-a',1,X'01',X'02','{{}}','prepare-b','2026-11-03');
            """);
        db.Database.ExecuteSqlRaw("""
            INSERT INTO outpost_reservations (Id,WorldId,PlayerId,SaveGeneration,Tile,Active,OperationId) VALUES ('a','world',1,2,42,1,'op-a');
            """);
        AssertConstraint(db, """
            INSERT INTO outpost_reservations (Id,WorldId,PlayerId,SaveGeneration,Tile,Active,OperationId) VALUES ('b','world',2,2,42,1,'op-b');
            """);
        AssertConstraint(db, """
            INSERT INTO outpost_reservations (Id,WorldId,PlayerId,SaveGeneration,Tile,Active,OperationId) VALUES ('c','world',1,2,43,1,'op-c');
            """);
        db.Database.ExecuteSqlRaw("UPDATE outpost_assignments SET Active=0; UPDATE outpost_reservations SET Active=0;");
        db.Database.ExecuteSqlRaw("""
            INSERT INTO outpost_assignments (Id,OutpostId,PawnCustodyId,Active,Blob,BlobDigest,FactsJson,PreparedOperationId,CreatedAt)
            VALUES ('assignment-b','outpost-b','pawn-a',1,X'01',X'02','{{}}','prepare-b','2026-11-03');
            INSERT INTO outpost_reservations (Id,WorldId,PlayerId,SaveGeneration,Tile,Active,OperationId) VALUES ('b','world',2,2,42,1,'op-b');
            """);
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM outpost_assignments"));
    }

    [Fact]
    public void Custody_transition_evidence_cannot_be_rewritten_or_deleted()
    {
        using var connection = Open();
        using var db = Context(connection);
        db.Database.Migrate();
        db.OutpostTransitions.Add(new Rimlink.Server.Storage.Entities.OutpostTransition
        {
            Id = "transition-a", NextHeadId = "head-a", CreatedAt = new DateTime(2026, 11, 3)
        });
        db.SaveChanges();
        AssertConstraint(db, "UPDATE outpost_transitions SET Kind='rewritten' WHERE Id='transition-a'");
        AssertConstraint(db, "DELETE FROM outpost_transitions WHERE Id='transition-a'");
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM outpost_transitions WHERE Kind=''"));
    }

    [Theory]
    [InlineData("FractionNumerator", "'00'")]
    [InlineData("FractionNumerator", "'-1'")]
    [InlineData("FractionNumerator", "'1.5'")]
    [InlineData("FractionDenominator", "'0'")]
    [InlineData("FractionDenominator", "'01'")]
    [InlineData("FractionDenominator", "''")]
    [InlineData("FractionDenominator", "'abc'")]
    [InlineData("Stock", "-1")]
    [InlineData("Stock", "1001")]
    public void Invalid_production_projection_is_rejected(string column, string literal)
    {
        using var connection = Open();
        using var db = Context(connection);
        db.Database.Migrate();
        db.Outposts.Add(new Rimlink.Server.Storage.Entities.OutpostRow { Id = "outpost-a" });
        db.SaveChanges();
        AssertConstraint(db, $"UPDATE outposts SET {column}={literal} WHERE Id='outpost-a'");
        Assert.Equal(0L, Scalar(connection, "SELECT Stock FROM outposts"));
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static RimlinkDbContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static void AssertConstraint(RimlinkDbContext db, string sql) =>
        Assert.Equal(19, Assert.Throws<SqliteException>(() => db.Database.ExecuteSqlRaw(sql)).SqliteErrorCode);
}
