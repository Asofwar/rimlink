using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Application.Outposts;

/// <summary>Hashes persisted outpost state. The caller owns the authority/storage fence for a coherent view.</summary>
public static class OutpostDomainDigest
{
    private const int MaximumFieldBytes = 32 * 1024 * 1024;
    private const int ChunkBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // Fixed schema ownership: authority is self-referential; the transition chain is verified separately.
    private static readonly (string Name, string OrderBy)[] Tables =
    [
        ("outposts", "Id"),
        ("outpost_assignments", "Id"),
        ("pawn_custody", "Id"),
        ("outpost_operations", "Id"),
        ("outpost_reservations", "Id"),
        ("outpost_parcel_links", "ParcelId"),
        ("outpost_snapshot_links", "WorldId, PlayerId, SaveGeneration, SnapshotId"),
        ("outpost_manifests", "Id"),
        ("outpost_restore_authorizations", "Id"),
        ("outpost_delivery_attempts", "Id"),
        ("outpost_lifecycle_operations", "Id"),
        ("outpost_ordinary_returns", "Id")
    ];

    public static async Task<ByteString> ComputeAsync(RimlinkDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();
        if (db.Database.GetDbConnection() is not SqliteConnection connection)
            throw new InvalidOperationException("Outpost domain digests require SQLite.");
        bool ownsOpen = connection.State == ConnectionState.Closed;
        if (!ownsOpen && connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The SQLite connection is not available.");

        try
        {
            if (ownsOpen) await connection.OpenAsync(ct);
            var transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var writer = new DigestWriter(hash);
            writer.Text("Rimlink.Outposts.DomainDigest/v1");
            writer.Integer(Tables.Length);
            foreach (var table in Tables)
            {
                ct.ThrowIfCancellationRequested();
                writer.Marker(0x10);
                writer.Text(table.Name);
                await AppendDefinitionAsync(connection, transaction, table.Name, writer, ct);
                var columns = await ReadColumnsAsync(connection, transaction, table.Name, table.OrderBy, ct);
                writer.Integer(columns.Length);
                foreach (var column in columns)
                {
                    writer.Text(column.Name);
                    writer.Text(column.Type);
                }

                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction?)transaction;
                command.CommandText = DataQuery(table.Name, table.OrderBy, columns);
                command.Parameters.AddWithValue("$max", MaximumFieldBytes);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    ct.ThrowIfCancellationRequested();
                    writer.Marker(0x20);
                    for (int i = 0; i < columns.Length; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        // Never materialize an unchecked original TEXT/BLOB column.
                        int extra = columns.Length + i * 3;
                        switch (reader.GetString(extra))
                        {
                            case "null":
                                writer.Null();
                                break;
                            case "integer":
                                writer.Integer(reader.GetInt64(i));
                                break;
                            case "real":
                                writer.Real(reader.GetDouble(i));
                                break;
                            case "text":
                                writer.Bytes(reader, extra + 2, reader.GetInt64(extra + 1), text: true, ct);
                                break;
                            case "blob":
                                writer.Bytes(reader, i, reader.GetInt64(extra + 1), text: false, ct);
                                break;
                            default:
                                throw new InvalidDataException("Unknown SQLite storage class in protected outpost state.");
                        }
                    }
                    writer.Marker(0x21);
                }
                writer.Marker(0x11);
            }
            ct.ThrowIfCancellationRequested();
            return ByteString.CopyFrom(hash.GetHashAndReset());
        }
        finally
        {
            if (ownsOpen) await connection.CloseAsync();
        }
    }

    private static async Task AppendDefinitionAsync(SqliteConnection connection, DbTransaction? transaction,
        string table, DigestWriter writer, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        // GetDataTypeName omits declared type sizes. Bind exact DDL as well, including keys/constraints.
        command.CommandText = "SELECT length(CAST(sql AS BLOB)), " +
            "CASE WHEN length(CAST(sql AS BLOB)) <= $max THEN CAST(sql AS BLOB) END " +
            "FROM sqlite_schema WHERE type = 'table' AND name = $table";
        command.Parameters.AddWithValue("$max", MaximumFieldBytes);
        command.Parameters.AddWithValue("$table", table);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0))
            throw new InvalidDataException("Missing protected outpost table definition.");
        writer.Bytes(reader, 1, reader.GetInt64(0), text: true, ct);
        if (await reader.ReadAsync(ct))
            throw new InvalidDataException("Ambiguous protected outpost table definition.");
    }

    private static async Task<Column[]> ReadColumnsAsync(SqliteConnection connection, DbTransaction? transaction,
        string table, string orderBy, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        // No row is stepped: untyped columns must not acquire the first row's dynamic storage class.
        command.CommandText = $"SELECT * FROM {Quote(table)} ORDER BY {orderBy} LIMIT 0";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var columns = new Column[reader.FieldCount];
        for (int i = 0; i < columns.Length; i++)
            columns[i] = new Column(reader.GetName(i), reader.GetDataTypeName(i));
        return columns;
    }

    private static string DataQuery(string table, string orderBy, Column[] columns)
    {
        var sql = new StringBuilder("SELECT *");
        foreach (var column in columns)
        {
            string name = Quote(column.Name);
            sql.Append(", typeof(").Append(name).Append("), CASE WHEN typeof(").Append(name)
                .Append(") IN ('text','blob') THEN length(CAST(").Append(name).Append(" AS BLOB)) END, ")
                .Append("CASE WHEN typeof(").Append(name).Append(") = 'text' AND length(CAST(")
                .Append(name).Append(" AS BLOB)) <= $max THEN CAST(").Append(name).Append(" AS BLOB) END");
        }
        return sql.Append(" FROM ").Append(Quote(table)).Append(" ORDER BY ").Append(orderBy).ToString();
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    private sealed record Column(string Name, string Type);

    private sealed class DigestWriter(IncrementalHash hash)
    {
        private readonly char[] chars = new char[StrictUtf8.GetMaxCharCount(ChunkBytes)];

        public void Marker(byte marker)
        {
            Span<byte> value = stackalloc byte[1];
            value[0] = marker;
            hash.AppendData(value);
        }

        private void Header(byte tag, long length)
        {
            Span<byte> header = stackalloc byte[9];
            header[0] = tag;
            BinaryPrimitives.WriteInt64LittleEndian(header[1..], length);
            hash.AppendData(header);
        }

        public void Null() => Header(0, 0);

        public void Integer(long number)
        {
            Header(1, 8);
            Span<byte> value = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(value, number);
            hash.AppendData(value);
        }

        public void Real(double number)
        {
            if (!double.IsFinite(number))
                throw new InvalidDataException("Nonfinite SQLite real in protected outpost state.");
            Header(2, 8);
            Span<byte> value = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(value, BitConverter.DoubleToInt64Bits(number));
            hash.AppendData(value);
        }

        public void Text(string text)
        {
            try
            {
                int length = StrictUtf8.GetByteCount(text);
                CheckLength(length);
                Header(3, length);
                hash.AppendData(StrictUtf8.GetBytes(text));
            }
            catch (EncoderFallbackException ex)
            {
                throw new InvalidDataException("Invalid Unicode in protected outpost schema.", ex);
            }
        }

        public void Bytes(SqliteDataReader reader, int ordinal, long length, bool text, CancellationToken ct)
        {
            CheckLength(length);
            ct.ThrowIfCancellationRequested();
            // Bounded field allocation, not GetBytes/GetStream: those probe native column metadata
            // even for computed expressions and can crash the SQLite provider.
            byte[] value = reader.GetFieldValue<byte[]>(ordinal);
            if (value.LongLength != length)
                throw new InvalidDataException("SQLite field length differs from its protected projection.");
            ct.ThrowIfCancellationRequested();
            Header(text ? (byte)3 : (byte)4, length);
            var decoder = text ? StrictUtf8.GetDecoder() : null;
            int position = 0;
            try
            {
                while (position < value.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    int count = Math.Min(ChunkBytes, value.Length - position);
                    var chunk = value.AsSpan(position, count);
                    position += count;
                    decoder?.GetChars(chunk, chars, flush: position == value.Length);
                    hash.AppendData(chunk);
                }
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Invalid UTF-8 in protected outpost state.", ex);
            }
        }

        private static void CheckLength(long length)
        {
            if (length < 0 || length > MaximumFieldBytes)
                throw new InvalidDataException("Protected outpost field exceeds the 32 MiB digest limit.");
        }
    }
}
