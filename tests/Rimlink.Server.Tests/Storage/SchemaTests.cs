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
    public void RaidsMigrationCreatesTheTables()
    {
        using var db = CreateContext();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        var tables = new List<string>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
        foreach (string table in new[] { "raids", "raid_outcomes", "visits", "shop_items", "pending_removals" }) Assert.Contains(table, tables);

        cmd.CommandText = "SELECT name FROM pragma_table_info('players')";
        var columns = new List<string>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(0));
        foreach (string column in new[] { "Peaceful", "PeacefulOffAt", "VisitsClosed", "FirstSaveAt", "WealthTotal", "WealthItems" }) Assert.Contains(column, columns);

        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND tbl_name = 'shop_items' AND sql LIKE '%UNIQUE%HostPlayerId%ThingId%'";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        Assert.False(db.Players.Select(p => p.Peaceful).FirstOrDefault());
    }

    [Fact]
    public void LiveMigrationCreatesTheTable()
    {
        using var db = CreateContext();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        var tables = new List<string>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
        Assert.Contains("live_sessions", tables);

        cmd.CommandText = "SELECT name FROM pragma_table_info('players')";
        var columns = new List<string>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(0));
        Assert.Contains("AutoAcceptLive", columns);

        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND tbl_name = 'live_sessions' AND sql LIKE '%UNIQUE%GuestPlayerId%ClientRequestId%'";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        Assert.False(db.Players.Select(p => p.AutoAcceptLive).FirstOrDefault());
    }

    [Fact]
    public void AdminActionsHaveActorName()
    {
        using var db = CreateContext();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('admin_actions')";
        var columns = new List<string>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(0));
        Assert.Contains("ActorName", columns);
    }

    [Fact]
    public void ChatMessagesHaveExternalAuthor()
    {
        using var db = CreateContext();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('chat_messages')";
        var columns = new List<string>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(0));
        Assert.Contains("ExternalAuthor", columns);
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

    [Fact]
    public void SafeTradesTablesAndColumnsExist()
    {
        using var db = CreateContext();

        string[] trades = ["Id", "InitiatorPlayerId", "PartnerPlayerId", "Status", "Revision", "CreatedAt", "ExpiresAt", "ClosedAt", "CloseReason", "ClientRequestId",
            "InitiatorItems", "InitiatorPawns", "InitiatorSilver", "InitiatorNote", "InitiatorConfirmed",
            "PartnerItems", "PartnerPawns", "PartnerSilver", "PartnerNote", "PartnerConfirmed"];
        foreach (string column in trades) Assert.Contains(column, Columns("trades"));
        foreach (string column in new[] { "BlockerPlayerId", "BlockedPlayerId", "CreatedAt" }) Assert.Contains(column, Columns("player_blocks"));
        Assert.Contains("Decision", Columns("parcels"));
        Assert.Contains("AcceptGifts", Columns("players"));
        Assert.Contains("TradesClosed", Columns("players"));

        var nova = new Player { Account = new Account { CreatedAt = DateTime.UtcNow }, Name = "Nova", FactionName = "Nova" };
        var bob = new Player { Account = new Account { CreatedAt = DateTime.UtcNow }, Name = "Bob", FactionName = "Bob" };
        db.Players.AddRange(nova, bob);
        db.SaveChanges();
        db.PlayerBlocks.Add(new PlayerBlock { BlockerPlayerId = nova.Id, BlockedPlayerId = bob.Id, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        db.PlayerBlocks.Add(new PlayerBlock { BlockerPlayerId = nova.Id, BlockedPlayerId = bob.Id, CreatedAt = DateTime.UtcNow });

        var ex = Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        Assert.Equal(19, Assert.IsType<SqliteException>(ex.InnerException).SqliteErrorCode);
    }

    private List<string> Columns(string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        var columns = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }

    [Fact]
    public void ModpackFilesAndConfigsMigration()
    {
        using var db = CreateContext();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('modpack_entries')";
        var columns = new List<string>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(0));
        Assert.Contains("FilesHash", columns);

        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND tbl_name = 'modpack_configs' AND sql LIKE '%UNIQUE%PackageId%Handle%'";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        Assert.Equal(0, db.ModpackConfigs.Count());
    }

    [Fact]
    public void IntegrityColumnsExist()
    {
        using var db = CreateContext();

        Assert.Contains("EconomyFrozenAt", Columns("players"));
        Assert.Contains("EconomyFreezeReason", Columns("players"));
        foreach (string column in new[] { "DevMode", "WealthTotal", "WealthItems" }) Assert.Contains(column, Columns("save_versions"));
        Assert.Null(db.Players.Select(p => p.EconomyFrozenAt).FirstOrDefault());
    }

    [Fact]
    public void WalletsAcceptANegativeBalance()
    {
        using var db = CreateContext();

        // Storage fees may leave a debt (economy hardening spec §4).
        db.Wallets.Add(new Wallet { PlayerId = 7, Balance = -5, UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();

        Assert.Equal(-5, db.Wallets.Single(w => w.PlayerId == 7).Balance);
    }

    [Fact]
    public void ForbiddenDefsTableExists()
    {
        using var db = CreateContext();

        db.ForbiddenDefs.Add(new ForbiddenDef { DefName = "Gun_Revolver" });
        db.SaveChanges();
        Assert.Equal("Gun_Revolver", db.ForbiddenDefs.AsNoTracking().Single().DefName);

        using var second = CreateContext();
        second.ForbiddenDefs.Add(new ForbiddenDef { DefName = "Gun_Revolver" });
        Assert.Throws<DbUpdateException>(() => second.SaveChanges());
    }
}
