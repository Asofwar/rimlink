using Microsoft.Data.Sqlite;

namespace Rimlink.Server.Application.Outposts;

public partial class OutpostAnchorStore
{
    internal async Task<OutpostAnchorLease> AcquireReadOnlyAsync(CancellationToken ct)
    {
        ValidatePath();
        return new(this, await gate.HoldExistingAsync(ct));
    }

    // No OpenConnection: that qualified writer path may recover a hot journal and reestablish PERSIST.
    internal AnchorEnvelope? ReadReadOnlyEnvelope(OutpostAnchorLease lease) => ReadReadOnlyObservation(lease).Envelope;

    internal (AnchorEnvelope? Envelope, OutpostSqliteIdentity Backend) ReadReadOnlyObservation(OutpostAnchorLease lease)
    {
        lease.Require(this); ValidatePath(); RejectLinks(DatabasePath); RejectLinks(DatabasePath + "-journal");
        if (!File.Exists(DatabasePath)) throw new InvalidDataException("anchor-missing");
        if (File.Exists(DatabasePath + "-wal") || File.Exists(DatabasePath + "-shm"))
            throw new InvalidDataException("anchor-sidecar-unexpected");
        using (var journal = File.OpenRead(DatabasePath + "-journal"))
        {
            Span<byte> header = stackalloc byte[28];
            if (journal.Read(header) != header.Length || header.ContainsAnyExcept((byte)0))
                throw new InvalidDataException("anchor-journal-not-proven-cold");
        }
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        connection.Open();
        var backend = OutpostSqliteBackend.ValidateReadOnlyIdentity(connection);
        return (ReadCheckedEnvelope(connection), backend);
    }
}
