using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Storage;

public class SchemaTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public SchemaTests() => connection.Open();

    public void Dispose() => connection.Dispose();

    private RimlinkDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite(connection).Options;
        var db = new RimlinkDbContext(options);
        db.Database.Migrate();
        return db;
    }

    [Fact]
    public void MigrationsCreateAllTables()
    {
        using var db = CreateContext();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        var tables = new List<string>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));

        string[] expected = ["accounts", "sessions", "bans", "players", "world", "world_objects", "chat_messages", "modpack_entries", "save_versions", "admin_actions"];
        foreach (string table in expected) Assert.Contains(table, tables);
    }

    [Fact]
    public void EconomyTablesExist()
    {
        using var db = CreateContext();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        var tables = new List<string>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));

        foreach (string table in new[] { "wallets", "wallet_entries", "parcels", "lots", "transfers", "modpack_defs" }) Assert.Contains(table, tables);
        Assert.Equal(0L, db.Players.Select(p => p.LastOutgoingTick).FirstOrDefault());
    }

    [Fact]
    public void NegativeWalletBalanceIsRejected()
    {
        using var db = CreateContext();
        db.Wallets.Add(new Wallet { PlayerId = 1, Balance = -1, UpdatedAt = DateTime.UtcNow });

        var ex = Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        Assert.Equal(19, Assert.IsType<SqliteException>(ex.InnerException).SqliteErrorCode);
    }

    [Fact]
    public void SecondSettlementOnSameTileIsRejected()
    {
        using var db = CreateContext();
        var account = new Account { Role = Role.Player, CreatedAt = DateTime.UtcNow };
        var player = new Player { Account = account, Name = "Nova", FactionName = "Nova" };
        db.WorldObjects.Add(new WorldObject { Type = WorldObjectTypes.Settlement, Owner = player, Tile = 100, Name = "A", CreatedAt = DateTime.UtcNow });
        db.SaveChanges();

        db.WorldObjects.Add(new WorldObject { Type = WorldObjectTypes.Settlement, Owner = player, Tile = 100, Name = "B", CreatedAt = DateTime.UtcNow });

        var ex = Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        Assert.Equal(19, Assert.IsType<SqliteException>(ex.InnerException).SqliteErrorCode);
    }

    [Fact]
    public void NonSettlementObjectsMayShareATile()
    {
        using var db = CreateContext();
        var player = new Player { Account = new Account { CreatedAt = DateTime.UtcNow }, Name = "Nova", FactionName = "Nova" };
        db.WorldObjects.Add(new WorldObject { Type = WorldObjectTypes.Settlement, Owner = player, Tile = 5, Name = "Home", CreatedAt = DateTime.UtcNow });
        db.WorldObjects.Add(new WorldObject { Type = "caravan", Owner = player, Tile = 5, Name = "Trip", CreatedAt = DateTime.UtcNow });

        db.SaveChanges();

        Assert.Equal(2, db.WorldObjects.Count());
    }
}
