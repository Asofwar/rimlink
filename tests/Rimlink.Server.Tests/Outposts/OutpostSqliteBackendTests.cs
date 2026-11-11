using Microsoft.Data.Sqlite;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

// Real local file-backed SQLite, not a power-failure or filesystem qualification test.
public sealed class OutpostSqliteBackendTests
{
    [Fact]
    public void Anchor_persist_extra_reports_the_actual_qualified_backend_without_mutating_it()
    {
        using var db = new BackendDatabase("persist", 3);

        var report = OutpostSqliteBackend.Validate(db.Connection, OutpostDatabaseRole.Anchor);

        Assert.Equal("3.53.3", report.NativeVersion);
        Assert.Equal("2026-06-26 20:14:12 d4c0e51e4aeb96955b99185ab9cde75c339e2c29c3f3f12428d364a10d782c62", report.SourceId);
        Assert.Equal("2.1.13.3146", report.ProviderVersion);
        Assert.Equal(OperatingSystem.IsWindows() ? "win32" : "unix", report.Vfs);
        Assert.Equal(OperatingSystem.IsWindows()
            ? "b7385d722c83fb52142a00477a726723745916d22a555711ee89834c1111fb2e"
            : "b0f5c48026fedcf9f05cf31c6fadf9e1e001ca59cd85aa68b8b6814e52c18a07", report.NativeSha256);
        Assert.Equal("persist", report.JournalMode);
        Assert.Equal(3, report.Synchronous);
        db.AssertUnchanged("persist", 3);
    }

    [Fact]
    public void Repeated_validation_inspects_the_native_module_once_per_process()
    {
        using var first = new BackendDatabase("persist", 3);
        var expected = OutpostSqliteBackend.Validate(first.Connection, OutpostDatabaseRole.Anchor);
        long inspections = OutpostSqliteBackend.NativeModuleInspections;
        Assert.True(inspections >= 1);

        Parallel.For(0, 24, _ =>
        {
            using var db = new BackendDatabase("wal", 2);
            var report = OutpostSqliteBackend.Validate(db.Connection, OutpostDatabaseRole.Domain);
            Assert.Equal(expected.NativeSha256, report.NativeSha256);
            Assert.Equal(expected.Vfs, report.Vfs);
        });

        Assert.Equal(inspections, OutpostSqliteBackend.NativeModuleInspections);
    }

    [Theory]
    [InlineData("persist", 0)]
    [InlineData("persist", 1)]
    [InlineData("persist", 2)]
    [InlineData("memory", 3)]
    [InlineData("delete", 3)]
    [InlineData("wal", 3)]
    public void Anchor_refuses_unqualified_effective_settings_without_repair(string journal, int synchronous)
    {
        using var db = new BackendDatabase(journal, synchronous);

        Assert.Throws<InvalidDataException>(() => OutpostSqliteBackend.Validate(db.Connection, OutpostDatabaseRole.Anchor));

        db.AssertUnchanged(journal, synchronous);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Domain_accepts_effective_wal_full_or_extra(int synchronous)
    {
        using var db = new BackendDatabase("wal", synchronous);

        var report = OutpostSqliteBackend.Validate(db.Connection, OutpostDatabaseRole.Domain);

        Assert.Equal("wal", report.JournalMode);
        Assert.Equal(synchronous, report.Synchronous);
        db.AssertUnchanged("wal", synchronous);
    }

    [Theory]
    [InlineData("wal", 0)]
    [InlineData("wal", 1)]
    [InlineData("persist", 3)]
    [InlineData("delete", 2)]
    public void Domain_refuses_nondurable_or_nonwal_settings_without_repair(string journal, int synchronous)
    {
        using var db = new BackendDatabase(journal, synchronous);

        Assert.Throws<InvalidDataException>(() => OutpostSqliteBackend.Validate(db.Connection, OutpostDatabaseRole.Domain));

        db.AssertUnchanged(journal, synchronous);
    }

    [Fact]
    public void Closed_connection_is_refused_without_opening_it()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");

        Assert.Throws<InvalidDataException>(() => OutpostSqliteBackend.Validate(connection, OutpostDatabaseRole.Anchor));

        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Theory]
    [InlineData(OutpostDatabaseRole.Anchor)]
    [InlineData(OutpostDatabaseRole.Domain)]
    public void In_memory_database_is_refused(OutpostDatabaseRole role)
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();

        Assert.Throws<InvalidDataException>(() => OutpostSqliteBackend.Validate(connection, role));
    }

    [Fact]
    public void Unknown_role_is_refused_without_mutating_settings()
    {
        using var db = new BackendDatabase("persist", 3);

        Assert.Throws<InvalidDataException>(() => OutpostSqliteBackend.Validate(db.Connection, (OutpostDatabaseRole)42));

        db.AssertUnchanged("persist", 3);
    }

    [Theory]
    [InlineData("persist", 3, false)][InlineData("persist", 3, true)]
    [InlineData("wal", 2, false)][InlineData("wal", 2, true)]
    [InlineData("wal", 1, false)]
    public void Readonly_identity_qualifies_actual_backend_without_claiming_or_changing_writer_settings(string journal, int synchronous, bool inTransaction)
    {
        using var db = new BackendDatabase(journal, synchronous);
        using var reader = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = db.Connection.DataSource, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        reader.Open();
        using var tx = inTransaction ? reader.BeginTransaction(deferred: true) : null;
        var identity = OutpostSqliteBackend.ValidateReadOnlyIdentity(reader, tx);
        Assert.Equal("3.53.3", identity.NativeVersion);
        Assert.Equal(OperatingSystem.IsWindows() ? "win32" : "unix", identity.Vfs);
        Assert.Equal("2.1.13.3146", identity.ProviderVersion);
        Assert.Null(identity.GetType().GetProperty("Synchronous"));
        Assert.Null(identity.GetType().GetProperty("JournalMode"));
        tx?.Commit(); db.AssertUnchanged(journal, synchronous);
    }

    [Fact]
    public void Writer_validation_does_not_accept_a_readonly_handle_even_if_its_default_settings_look_durable()
    {
        using var db = new BackendDatabase("wal", 2);
        using var reader = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = db.Connection.DataSource, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        reader.Open();
        Assert.Throws<InvalidDataException>(() => OutpostSqliteBackend.Validate(reader, OutpostDatabaseRole.Domain));
        db.AssertUnchanged("wal", 2);
    }

    [Fact]
    public void Readonly_identity_does_not_accept_a_writer_or_open_a_closed_connection()
    {
        using var db = new BackendDatabase("wal", 2);
        Assert.Throws<InvalidDataException>(() => OutpostSqliteBackend.ValidateReadOnlyIdentity(db.Connection));
        using var closed = new SqliteConnection(db.Connection.ConnectionString);
        Assert.Throws<InvalidDataException>(() => OutpostSqliteBackend.ValidateReadOnlyIdentity(closed));
        Assert.Equal(System.Data.ConnectionState.Closed, closed.State); db.AssertUnchanged("wal", 2);
    }

    private sealed class BackendDatabase : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "rimlink-backend-" + Guid.NewGuid().ToString("N"));
        public SqliteConnection Connection { get; }

        public BackendDatabase(string journal, int synchronous)
        {
            Directory.CreateDirectory(directory);
            Connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "backend.db"),
                Pooling = false
            }.ToString());
            Connection.Open();
            Assert.Equal(journal, Convert.ToString(Scalar($"PRAGMA journal_mode={journal};")));
            Scalar($"PRAGMA synchronous={synchronous};");
            using var command = Connection.CreateCommand();
            command.CommandText = "CREATE TABLE sentinel(value INTEGER NOT NULL); INSERT INTO sentinel VALUES(17);";
            command.ExecuteNonQuery();
        }

        public void AssertUnchanged(string journal, int synchronous)
        {
            Assert.Equal(journal, Convert.ToString(Scalar("PRAGMA journal_mode;")));
            Assert.Equal(synchronous, Convert.ToInt32(Scalar("PRAGMA synchronous;")));
            Assert.Equal(17L, Scalar("SELECT value FROM sentinel;"));
            Assert.Equal(1L, Scalar("SELECT count(*) FROM sentinel;"));
        }

        private object? Scalar(string sql)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        public void Dispose()
        {
            Connection.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
