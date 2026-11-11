using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Application.GuildCapture;

public static class GuildDomainDigest
{
    public const string MigrationId = "20261108103250_GuildCaptureDomain";
    private static readonly (string Name, string OrderBy)[] Tables =
    [
        ("guild_outposts", "Id"), ("guild_capture_consents", "Id"), ("guild_resource_obligations", "Id"),
        ("guild_resource_claims", "Id"), ("guild_parcel_links", "ParcelId"), ("guild_delivery_attempts", "Id"),
        ("guild_campaigns", "Id"), ("guild_acquisition_reservations", "Id"), ("guild_battle_slots", "Id"),
        ("guild_battles", "Id"), ("guild_battle_artifacts", "Id"),
        ("guild_battle_control_grants", "Id"), ("guild_capture_operations", "Id")
    ];

    public static async Task<ByteString> ComputeAuthorityAsync(RimlinkDbContext db, CancellationToken ct)
    {
        var legacy = await OutpostDomainDigest.ComputeAsync(db, OutpostDomainFormat.V2, ct);
        var guild = await ComputeHeldAsync(db, ct);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("Rimlink.Outposts.DomainDigest/v3\0"u8);
        hash.AppendData(legacy.Span); hash.AppendData(guild.Span);
        return ByteString.CopyFrom(hash.GetHashAndReset());
    }

    public static async Task<ByteString> ComputeHeldAsync(RimlinkDbContext db, CancellationToken ct)
    {
        var tables = await OutpostDomainDigest.ComputeTablesAsync(db, "Rimlink.GuildCapture.Domain/v1", Tables, ct);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("Rimlink.GuildCapture.Governance/v1\0"u8); hash.AppendData(tables.Span);
        bool opened = db.Database.GetDbConnection().State == ConnectionState.Closed;
        try
        {
            if (opened) await db.Database.OpenConnectionAsync(ct);
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            foreach (bool members in new[] { false, true })
            {
                using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandText = members
                    ? "SELECT CASE WHEN typeof(AllianceId)='integer' AND AllianceId>0 THEN AllianceId END, " +
                        "CASE WHEN typeof(PlayerId)='integer' AND PlayerId>0 THEN PlayerId END, " +
                        "CASE WHEN typeof(JoinedAt)='text' AND length(CAST(JoinedAt AS BLOB)) BETWEEN 1 AND 64 THEN JoinedAt END " +
                        "FROM alliance_members ORDER BY AllianceId, PlayerId"
                    : "SELECT CASE WHEN typeof(Id)='integer' AND Id>0 THEN Id END, " +
                        "CASE WHEN typeof(OwnerId)='integer' AND OwnerId>0 THEN OwnerId END, " +
                        "CASE WHEN typeof(Disbanded)='integer' AND Disbanded IN (0,1) THEN Disbanded END FROM alliance_groups ORDER BY Id";
                hash.AppendData(members ? [2] : [1]);
                await using var reader = await command.ExecuteReaderAsync(ct);
                byte[] row = new byte[25];
                row[0] = 1;
                while (await reader.ReadAsync(ct))
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
                        throw new InvalidDataException("guild-governance-storage-invalid");
                    BinaryPrimitives.WriteInt64LittleEndian(row.AsSpan(1), reader.GetInt64(0));
                    BinaryPrimitives.WriteInt64LittleEndian(row.AsSpan(9), reader.GetInt64(1));
                    long final;
                    try { final = members ? reader.GetDateTime(2).Ticks : reader.GetInt64(2); }
                    catch (Exception error) when (error is FormatException or InvalidCastException or ArgumentOutOfRangeException)
                    { throw new InvalidDataException("guild-governance-storage-invalid", error); }
                    BinaryPrimitives.WriteInt64LittleEndian(row.AsSpan(17), final);
                    hash.AppendData(row);
                }
                hash.AppendData([0]);
            }
            return ByteString.CopyFrom(hash.GetHashAndReset());
        }
        finally { if (opened) await db.Database.CloseConnectionAsync(); }
    }

    public static async Task RequireEmptyAsync(RimlinkDbContext db, CancellationToken ct, SqliteTransaction? transaction = null)
    {
        if (await HasRowsAsync(db, ct, transaction)) throw new InvalidDataException("guild-domain-nonempty-before-activation");
    }

    internal static async Task<bool> HasRowsAsync(RimlinkDbContext db, CancellationToken ct, SqliteTransaction? transaction = null)
    {
        bool opened = db.Database.GetDbConnection().State == ConnectionState.Closed;
        try
        {
            if (opened) await db.Database.OpenConnectionAsync(ct);
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            foreach (var table in Tables)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction ?? (SqliteTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandText = $"SELECT EXISTS(SELECT 1 FROM \"{table.Name}\" LIMIT 1)";
                if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0)
                    return true;
            }
            return false;
        }
        finally { if (opened) await db.Database.CloseConnectionAsync(); }
    }
}
