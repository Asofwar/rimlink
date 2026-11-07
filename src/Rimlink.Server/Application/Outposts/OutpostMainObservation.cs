using System.Data;
using Microsoft.Data.Sqlite;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Application.Outposts;

/// <summary>Lifecycle-owned WAL attachment; a request may only pin an already prepared resource.</summary>
public sealed class OutpostMainObservation(DataDirectory data) : IDisposable, IAsyncDisposable
{
    private readonly string path = Path.GetFullPath(data.DatabasePath);
    private readonly object sync = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private Attachment? current;
    private bool disposed;

    // Only an existing authorized writer phase calls this. Preparation is never a read-request fallback.
    public async Task PrepareAsync(SqliteConnection writer, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        SqliteConnection? keeper = null;
        try
        {
            var source = new SqliteConnectionStringBuilder(writer.ConnectionString);
            if (writer.State != ConnectionState.Open || source.Mode == SqliteOpenMode.ReadOnly ||
                !SamePath(writer.DataSource, path)) throw new InvalidDataException("main-observation-qualified-writer-required");
            OutpostSqliteBackend.Validate(writer, OutpostDatabaseRole.Domain);
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (current is { } attached)
                {
                    if (!attached.Accepting || attached.Connection.State != ConnectionState.Open)
                        throw new InvalidDataException("main-observation-drain-required");
                    return;
                }
            }
            keeper = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, Cache = SqliteCacheMode.Private }.ToString());
            await keeper.OpenAsync(ct);
            // Complete actual WAL/index attachment while the admitted writer is still attached. No read transaction is retained.
            using (var setup = keeper.BeginTransaction(deferred: true))
            {
                await using var command = keeper.CreateCommand(); command.Transaction = setup;
                command.CommandText = "SELECT name FROM sqlite_schema ORDER BY name LIMIT 1;";
                await command.ExecuteScalarAsync(ct); setup.Commit();
            }
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                current = new(keeper); keeper = null;
            }
        }
        finally
        {
            if (keeper is not null) await keeper.DisposeAsync();
            lifecycle.Release();
        }
    }

    internal Pin? TryPin() => TryPin(path);

    internal Pin? TryPin(string expectedDataPath)
    {
        if (!SamePath(expectedDataPath, path)) return null;
        lock (sync)
        {
            if (disposed || current is not { Accepting: true } attached || attached.Connection.State != ConnectionState.Open)
                return null;
            // The attachment, not file existence alone, proves preparation. Missing resources never cause SQLite to be opened.
            if (!Regular(path) || !Regular(path + "-wal") || !Regular(path + "-shm") || new FileInfo(path + "-shm").Length < 32768)
                return null;
            attached.Pins = checked(attached.Pins + 1);
            return new(this, attached, attached.Connection.ConnectionString);
        }
    }

    public Task DrainAsync(CancellationToken ct) => RetireAsync(false, ct);
    private async Task RetireAsync(bool permanently, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            Attachment? attached; Task wait;
            lock (sync)
            {
                if (permanently) disposed = true;
                attached = current;
                if (attached is null) return;
                attached.Accepting = false;
                wait = attached.Pins == 0 ? Task.CompletedTask : attached.Empty.Task;
            }
            await wait.WaitAsync(ct);
            lock (sync) current = null;
            await attached.Connection.DisposeAsync();
        }
        finally { lifecycle.Release(); }
    }

    private void Unpin(Attachment attached)
    {
        lock (sync)
        {
            if (--attached.Pins == 0 && !attached.Accepting) attached.Empty.TrySetResult();
        }
    }
    private static bool Regular(string file) => File.Exists(file) &&
        (File.GetAttributes(file) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    private static bool SamePath(string first, string second) => string.Equals(Path.GetFullPath(first), second,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync() => await RetireAsync(true, CancellationToken.None);

    internal sealed class Attachment(SqliteConnection connection)
    {
        internal SqliteConnection Connection { get; } = connection;
        internal bool Accepting = true;
        internal int Pins;
        internal TaskCompletionSource Empty { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal sealed class Pin(OutpostMainObservation owner, Attachment attachment, string connectionString) : IDisposable
    {
        private int released;
        internal string ReadOnlyConnectionString => Volatile.Read(ref released) == 0
            ? connectionString : throw new ObjectDisposedException(nameof(Pin));
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) owner.Unpin(attachment); }
    }
}
