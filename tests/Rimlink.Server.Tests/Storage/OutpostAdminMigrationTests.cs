using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Xunit;
using Xunit.Abstractions;

namespace Rimlink.Server.Tests.Storage;

public sealed class OutpostAdminMigrationTests(ITestOutputHelper output)
{
    private const string V1Migration = "20261104093958_OutpostOrdinaryReturns";
    private const string V1Tables = "'outposts','outpost_assignments','pawn_custody','outpost_operations'," +
        "'outpost_reservations','outpost_parcel_links','outpost_snapshot_links','outpost_manifests'," +
        "'outpost_restore_authorizations','outpost_delivery_attempts','outpost_lifecycle_operations','outpost_ordinary_returns'";

    [Fact]
    public async Task Admin_migration_preserves_every_v1_definition_and_digest()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(V1Migration);
        var before = await OutpostDomainDigest.ComputeAsync(db, CancellationToken.None);
        string[] definitions = await Definitions(connection);
        output.WriteLine("Frozen empty v1: " + Convert.ToHexStringLower(before.Span));
        Assert.Equal(Rimlink.Server.Tests.Support.OutpostDomainVectors.EmptyV1ForLocalMigrations, Convert.ToHexStringLower(before.Span));
        Assert.NotEmpty(definitions);

        await migrator.MigrateAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='outpost_admin_commands'";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        Assert.Equal(definitions, await Definitions(connection));
        Assert.Equal(before, await OutpostDomainDigest.ComputeAsync(db, CancellationToken.None));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("UPDATE outpost_admin_commands SET Reason='changed'")]
    [InlineData("DELETE FROM outpost_admin_commands")]
    public async Task Committed_admin_receipts_are_append_only(string mutation)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var receipt = new Rimlink.Server.Storage.Entities.OutpostAdminCommandRecord
        {
            Id = Guid.NewGuid().ToString("D"), ActorKey = new string('a', 64), Action = "ActivateDomainFormat",
            RequestId = Guid.NewGuid().ToString("D"), Reason = "explicit upgrade", Command = [1], CommandDigest = new byte[32],
            TransitionId = Guid.NewGuid().ToString("D"), NextHeadId = Guid.NewGuid().ToString("D"), Result = [1],
            AuditId = 1, AuditProjection = [1], CreatedAt = DateTime.UtcNow
        };
        db.OutpostAdminCommands.Add(receipt);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(mutation));

        Assert.Equal(19, error.SqliteErrorCode);
        Assert.Contains("outpost admin command evidence is immutable", error.Message);
        db.ChangeTracker.Clear();
        Assert.Equal("explicit upgrade", (await db.OutpostAdminCommands.SingleAsync()).Reason);
    }

    private static async Task<string[]> Definitions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type || ':' || name || ':' || IFNULL(sql,'<implicit>') FROM sqlite_schema " +
            "WHERE tbl_name IN (" + V1Tables + ") ORDER BY type,name";
        var definitions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) definitions.Add(reader.GetString(0));
        return definitions.ToArray();
    }
}
