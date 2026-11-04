using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Storage;

public sealed class OrdinaryReturnSchemaTests
{
    [Theory]
    [InlineData("original")][InlineData("local")][InlineData("derived")]
    public void Retained_return_identity_cannot_be_reused_in_a_new_scope(string collision)
    {
        using var connection = new SqliteConnection("DataSource=:memory:"); connection.Open();
        using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
        var first = Row(); db.OutpostOrdinaryReturns.Add(first); db.SaveChanges(); db.ChangeTracker.Clear();
        var second = Row(); second.SaveGeneration = 10; second.SaveWriteEpoch = 20; second.RestoreBoundary = "other-boundary";
        switch (collision)
        {
            case "original": second.RequestId = first.RequestId; break;
            case "local": second.LocalOperationId = first.LocalOperationId; break;
            case "derived": second.DerivedRequestId = first.DerivedRequestId; break;
        }
        db.OutpostOrdinaryReturns.Add(second);
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void Ordinary_return_migration_preserves_existing_custody_and_allows_unrelated_identities()
    {
        using var connection = new SqliteConnection("DataSource=:memory:"); connection.Open();
        using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
        const string migration = "20261104093958_OutpostOrdinaryReturns";
        string previous = db.Database.GetMigrations().TakeWhile(m => m != migration).Last();
        db.GetService<IMigrator>().Migrate(previous);
        string id = Guid.NewGuid().ToString("D");
        db.PawnCustody.Add(new PawnCustodyRecord { Id = id, WorldId = "world-before-upgrade", PlayerId = 7,
            SaveGeneration = 3, RestoreBoundary = "retained", LocationKind = "Home", LocationId = "Thing_Human77",
            FrontierTransitionId = Guid.NewGuid().ToString("D"), Tombstone = true, Revision = 5 });
        db.SaveChanges(); db.ChangeTracker.Clear();
        db.Database.Migrate();
        var retained = db.PawnCustody.Single();
        Assert.Equal(id, retained.Id); Assert.Equal("world-before-upgrade", retained.WorldId); Assert.Equal(3, retained.SaveGeneration);
        Assert.Equal("Thing_Human77", retained.LocationId); Assert.True(retained.Tombstone); Assert.Equal(5, retained.Revision);
        var first = Row(); first.DerivedRequestKind = 0; first.DerivedRequestId = "";
        var second = Row(); second.DerivedRequestKind = 0; second.DerivedRequestId = "";
        var otherActor = Row(); otherActor.PlayerId = 2; otherActor.RequestId = first.RequestId; otherActor.LocalOperationId = first.LocalOperationId;
        db.OutpostOrdinaryReturns.AddRange(first, second, otherActor); db.SaveChanges();
        Assert.Equal(3, db.OutpostOrdinaryReturns.Count());
    }

    private static OutpostOrdinaryReturn Row() => new() { Id = Guid.NewGuid().ToString("D"), WorldId = "world",
        PlayerId = 1, RestoreBoundary = "boundary", RequestKind = 6, RequestId = Guid.NewGuid().ToString("N"),
        LocalOperationId = Guid.NewGuid().ToString("N"), DerivedRequestKind = 5, DerivedRequestId = Guid.NewGuid().ToString("N"),
        Intent = [1], Payload = [2], PayloadDigest = new byte[32] };
}
