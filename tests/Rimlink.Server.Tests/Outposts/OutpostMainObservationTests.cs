using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostMainObservationTests
{
    [Fact]
    public void Cold_resource_refuses_before_opening_sqlite_or_creating_any_file()
    {
        using var f = new AuthorityHarness(); using var observer = new OutpostMainObservation(f.Data);
        var before = Snapshot(f); Assert.Null(observer.TryPin()); Assert.Equal(before, Snapshot(f));
    }

    [Fact]
    public async Task Prepared_readonly_keeper_survives_writer_close_and_reads_fresh_commits_without_sidecar_effects()
    {
        using var f = new AuthorityHarness(); using var observer = new OutpostMainObservation(f.Data);
        f.Db.World.Add(new() { Seed = "before", InfluenceInstanceId = "world-1" }); await f.Db.SaveChangesAsync();
        await Prepare(f, observer);
        var before = Snapshot(f);
        using (var pin = observer.TryPin())
        {
            Assert.NotNull(pin); await using var child = new SqliteConnection(pin.ReadOnlyConnectionString);
            await child.OpenAsync(); Assert.Equal(1L, await Scalar(child, "SELECT count(*) FROM world;"));
            await using var rejected = child.CreateCommand(); rejected.CommandText = "UPDATE world SET Seed='unauthorized';";
            var error = await Assert.ThrowsAsync<SqliteException>(() => rejected.ExecuteNonQueryAsync());
            Assert.Equal(8, error.SqliteErrorCode);
        }
        Assert.Equal(before, Snapshot(f));
        // The keeper owns an attachment, not a snapshot or transaction. Later genuine commits must be visible.
        await f.Db.Database.ExecuteSqlRawAsync("UPDATE world SET Seed='fresh';");
        var afterWrite = Snapshot(f);
        using (var pin = observer.TryPin())
        {
            Assert.NotNull(pin); await using var child = new SqliteConnection(pin.ReadOnlyConnectionString);
            await child.OpenAsync(); Assert.Equal("fresh", await Scalar(child, "SELECT Seed FROM world;"));
        }
        Assert.Equal(afterWrite, Snapshot(f));
    }

    [Fact]
    public async Task Drain_refuses_new_pins_and_waits_for_existing_observation_before_retiring_the_attachment()
    {
        using var f = new AuthorityHarness(); using var observer = new OutpostMainObservation(f.Data);
        f.Db.World.Add(new() { Seed = "before", InfluenceInstanceId = "world-1" }); await f.Db.SaveChangesAsync();
        await Prepare(f, observer); var pin = observer.TryPin(); Assert.NotNull(pin);
        var draining = observer.DrainAsync(default);
        Assert.False(draining.IsCompleted); Assert.Null(observer.TryPin());
        // The already admitted observation may finish. It does not own keeper shutdown.
        await using (var child = new SqliteConnection(pin.ReadOnlyConnectionString))
        { await child.OpenAsync(); Assert.Equal(1L, await Scalar(child, "SELECT count(*) FROM world;")); }
        pin.Dispose(); await draining; Assert.Null(observer.TryPin());
        var before = Snapshot(f); Assert.Null(observer.TryPin()); Assert.Equal(before, Snapshot(f));
    }

    [Fact]
    public async Task Missing_main_or_sidecar_is_not_recreated_by_a_pin()
    {
        using var f = new AuthorityHarness(); using var observer = new OutpostMainObservation(f.Data);
        await Prepare(f, observer); await observer.DrainAsync(default);
        string main = f.Data.DatabasePath;
        File.Move(main, main + ".retained"); var before = Snapshot(f);
        Assert.Null(observer.TryPin()); Assert.Equal(before, Snapshot(f)); Assert.False(File.Exists(main));
    }

    [Fact]
    public async Task Preparation_requires_the_actual_qualified_writer_not_a_readonly_or_foreign_database()
    {
        using var f = new AuthorityHarness(); using var observer = new OutpostMainObservation(f.Data);
        await using var readOnly = new SqliteConnection($"Data Source={f.Data.DatabasePath};Mode=ReadOnly;Pooling=False");
        await readOnly.OpenAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => observer.PrepareAsync(readOnly, default));
        Assert.Null(observer.TryPin());
    }

    internal static async Task Prepare(AuthorityHarness f, OutpostMainObservation observer)
    {
        await f.Db.Database.OpenConnectionAsync();
        try
        {
            await f.Db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=FULL;");
            await observer.PrepareAsync((SqliteConnection)f.Db.Database.GetDbConnection(), default);
        }
        finally { await f.Db.Database.CloseConnectionAsync(); }
    }

    private static async Task<object?> Scalar(SqliteConnection connection, string sql)
    { await using var c = connection.CreateCommand(); c.CommandText = sql; return await c.ExecuteScalarAsync(); }

    // SHM readmark slots are coordination only (SQLite WAL-index offset100..119). Nothing else is ignored.
    internal static string Snapshot(AuthorityHarness f) => string.Join("\n", Directory.EnumerateFiles(f.Data.Root, "rimlink.db*")
        .Order(StringComparer.Ordinal).Select(path =>
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var bytes = new MemoryStream(); file.CopyTo(bytes); var content = bytes.ToArray();
            if (path.EndsWith("-shm", StringComparison.Ordinal) && content.Length >= 120) Array.Clear(content, 100, 20);
            return Path.GetFileName(path) + ":" + content.Length + ":" + Convert.ToHexString(SHA256.HashData(content));
        }));
}
