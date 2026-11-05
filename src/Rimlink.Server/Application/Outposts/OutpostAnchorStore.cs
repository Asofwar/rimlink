using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Rimlink.Server.Hosting;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Application.Outposts;

public class OutpostAnchorStore
{
    private readonly string dataRoot;
    private readonly IOptions<OutpostOptions> options;
    private readonly SaveStorageLock gate;

    public OutpostAnchorStore(DataDirectory data, IOptions<OutpostOptions> options) : this(data.Root, options) { }

    // Stopped-server procedures need the real owned lease without creating a destination DataDirectory.
    internal OutpostAnchorStore(string dataRoot, IOptions<OutpostOptions> options)
    {
        this.dataRoot = Path.GetFullPath(dataRoot);
        this.options = options;
        gate = new(options.Value.AnchorDirectory);
    }

    internal Action<string>? RecoveryCheckpoint { get; set; }
    internal OutpostSqliteReport? LastBackendReport { get; private set; }

    internal async Task<OutpostAnchorLease> AcquireAsync(CancellationToken ct)
    {
        ValidatePath();
        return new(this, await gate.HoldAsync(ct));
    }

    internal AnchorEnvelope? ReadEnvelope(OutpostAnchorLease lease) => ReadEnvelopeRecord(lease);

    internal AnchorRecord? Read(OutpostAnchorLease lease)
    {
        lease.Require(this);
        return KnownRecord(ReadEnvelopeRecord(lease));
    }

    internal void WriteDurable(OutpostAnchorLease lease, AnchorRecord record, CancellationToken ct)
        => WriteEnvelopeDurable(lease, new KnownAnchorEnvelope(record), ct);

    internal void WriteEnvelopeDurable(OutpostAnchorLease lease, AnchorEnvelope envelope, CancellationToken ct)
    {
        lease.Require(this);
        ct.ThrowIfCancellationRequested();
        byte[] bytes = AnchorEnvelopeCodec.Encode(envelope);
        if (JournalRefusal() is { } refusal) throw new InvalidDataException(refusal);
        using (var connection = OpenConnection())
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE authority_store SET Record=$record WHERE Id=1;";
            command.Parameters.Add("$record", SqliteType.Blob).Value = bytes;
            if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("anchor-schema-or-integrity-mismatch");
            // A caller disconnect after this point cannot turn a durable decision into business rejection.
            transaction.Commit();
        }
        if (ReadEnvelopeRecord(lease) is not { } readback || !AnchorEnvelopeCodec.Encode(readback).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("anchor-write-readback-mismatch");
    }

    internal sealed class OutpostAnchorLease(OutpostAnchorStore owner, IDisposable held) : IDisposable
    {
        private IDisposable? heldLease = held;
        internal void Require(OutpostAnchorStore expected)
        {
            ObjectDisposedException.ThrowIf(heldLease is null, this);
            if (!ReferenceEquals(owner, expected)) throw new InvalidOperationException("authority-lease-owner-mismatch");
        }
        public void Dispose() => Interlocked.Exchange(ref heldLease, null)?.Dispose();
    }

    // Explicit physical maintenance only. Ordinary startup never invokes this method.
    public void Provision()
    {
        ValidatePath();
        using var lease = SaveStorageLock.Hold(options.Value.AnchorDirectory);
        // Reserve without replacing anything; a failed provisioning remains for manual inspection.
        using (var reserved = new FileStream(DatabasePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            reserved.Flush(flushToDisk: true);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = """
            PRAGMA application_id=1380732737;
            PRAGMA user_version=1;
            CREATE TABLE authority_store (
                Id INTEGER NOT NULL PRIMARY KEY CHECK (Id=1),
                Record BLOB NULL CHECK (Record IS NULL OR length(Record) BETWEEN 1 AND 4096));
            INSERT INTO authority_store(Id,Record) VALUES(1,NULL);
            """;
        create.ExecuteNonQuery();
        transaction.Commit();
        connection.Close();
        if (JournalRefusal() is { } refusal) throw new InvalidDataException(refusal);
    }

    internal bool IsPhysicallyUninitialized(OutpostAnchorLease lease) => ReadEnvelopeRecord(lease) is null;

    private AnchorEnvelope? ReadEnvelopeRecord(OutpostAnchorLease lease)
    {
        lease.Require(this);
        if (JournalRefusal() is { } refusal) throw new InvalidDataException(refusal);
        bool hot;
        using (var journal = File.OpenRead(DatabasePath + "-journal")) hot = journal.ReadByte() != 0;
        AnchorEnvelope? record;
        using (var connection = OpenConnection()) record = ReadCheckedEnvelope(connection);
        if (hot)
        {
            // SQLite, not us, rolls back a hot journal. Its recovery can remove the journal
            // before this connection's PERSIST mode takes effect. Only this fenced, positively
            // observed hot-recovery path may reestablish it; ordinary missing journals still refuse.
            RecoveryCheckpoint?.Invoke("AfterRecoveryRead");
            using (var connection = OpenConnection())
            {
                if (ReadCheckedEnvelope(connection) != record) throw new InvalidDataException("anchor-recovery-identity-mismatch");
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                // An unchanged UPDATE can be optimized away without creating any journal.
                // Force a journaled header write, restoring the verified schema version in the
                // same transaction. No logical record/identity change is ever committed.
                command.CommandText = "PRAGMA user_version=0; PRAGMA user_version=1;";
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            if (JournalRefusal() is { } recoveredRefusal) throw new InvalidDataException(recoveredRefusal);
            using var verified = OpenConnection();
            if (ReadCheckedEnvelope(verified) != record) throw new InvalidDataException("anchor-recovery-identity-mismatch");
        }
        return record;
    }

    internal static AnchorRecord? ReadChecked(SqliteConnection connection) => KnownRecord(ReadCheckedEnvelope(connection));

    private static AnchorRecord? KnownRecord(AnchorEnvelope? envelope) => envelope switch
    {
        null => null, KnownAnchorEnvelope known => known.Record,
        _ => throw new InvalidDataException("anchor-fresh-fence-requires-restore")
    };

    internal static AnchorEnvelope? ReadCheckedEnvelope(SqliteConnection connection)
    {
        if (!Equals(Scalar(connection, "PRAGMA integrity_check;"), "ok") ||
            Convert.ToInt64(Scalar(connection, "PRAGMA application_id;")) != 1380732737 ||
            Convert.ToInt64(Scalar(connection, "PRAGMA user_version;")) != 1)
            throw new InvalidDataException("anchor-schema-or-integrity-mismatch");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,typeof(Record),length(Record),substr(Record,1,4097) FROM authority_store;";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) != 1)
            throw new InvalidDataException("anchor-schema-or-integrity-mismatch");
        AnchorEnvelope? record = null;
        if (!reader.IsDBNull(2))
        {
            if (reader.GetString(1) != "blob" || reader.GetInt64(2) is < 1 or > 4096)
                throw new InvalidDataException("anchor-record-invalid");
            record = AnchorEnvelopeCodec.Decode((byte[])reader.GetValue(3));
        }
        if (reader.Read()) throw new InvalidDataException("anchor-schema-or-integrity-mismatch");
        return record;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            if (!string.Equals(Convert.ToString(Scalar(connection, "PRAGMA journal_mode=PERSIST;")), "persist", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("anchor-journal-mode-mismatch");
            Scalar(connection, "PRAGMA synchronous=EXTRA;");
            if (Convert.ToInt64(Scalar(connection, "PRAGMA synchronous;")) != 3)
                throw new InvalidDataException("anchor-synchronous-mismatch");
            LastBackendReport = OutpostSqliteBackend.Validate(connection, OutpostDatabaseRole.Anchor);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public string DatabasePath => Path.Combine(options.Value.AnchorDirectory, "authority.db");

    // Run before SQLite can recover or create a journal. A missing journal is never guessed cold.
    internal string? JournalRefusal()
    {
        string path = DatabasePath + "-journal";
        if (!File.Exists(path)) return "anchor-journal-missing";
        RejectLinks(DatabasePath);
        RejectLinks(path);
        using var journal = File.OpenRead(path);
        Span<byte> header = stackalloc byte[28];
        if (journal.Length < header.Length || journal.Read(header) != header.Length)
            return "anchor-journal-invalid";
        bool cold = true;
        foreach (byte value in header) cold &= value == 0;
        if (cold) return null;
        ReadOnlySpan<byte> magic = [0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7];
        return journal.Length >= 512 && header[..8].SequenceEqual(magic) ? null : "anchor-journal-invalid";
    }
    public string ValidatePath()
    {
        string configured = options.Value.AnchorDirectory;
        if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured) || configured.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("authority path must be an explicit local absolute directory");
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        string root = Path.TrimEndingDirectorySeparator(dataRoot);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Within(path, root, comparison) || Within(root, path, comparison))
            throw new InvalidDataException("authority path overlaps protected data");
        RejectLinks(path);
        RejectLinks(root);
        if (!Directory.Exists(path))
            throw new InvalidDataException("authority directory is not provisioned");
        OutpostStorageProfile.Validate(dataRoot, path, options.Value.StorageContract, options.Value.QualifiedLinuxFileSystem);
        return path;
    }

    private static bool Within(string candidate, string parent, StringComparison comparison) =>
        candidate.Equals(parent, comparison) || candidate.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, comparison);

    private static void RejectLinks(string path)
    {
        for (string? cursor = path; cursor is not null; cursor = Path.GetDirectoryName(cursor))
        {
            if ((Directory.Exists(cursor) || File.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("authority and data paths must not contain links or reparse points");
        }
    }
}
