using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Application.Outposts;

public enum OutpostSchemaContext { CurrentRuntime, HistoricalCutV1, HistoricalCutV2, HistoricalCutV3 }
public sealed record OutpostSchemaObservation(string ProfileId, ByteString Digest, bool IsCurrent);

/// <summary>Matches one complete actual catalog to a finite code-owned profile, without schema writes or text normalization.</summary>
public static class OutpostDomainSchema
{
    public const string AdminMigrationId = "20261105014436_OutpostAdminCommands";
    private const int MaximumObjects = 512;
    private const int MaximumMigrations = 256;
    private const int MaximumCatalogBytes = 512 * 1024;
    private static readonly Profile[] Profiles =
    [
        Load("v1-crlf", "de2714285e9601759a7c9bc224c4a8b5a97496476d9e8732a7258c84f22c7baf", false),
        Load("v1-lf", "6c21b7f5385262f44891ef459b2a5bd9f48e2587afd3c13b55bc2516c1e7e679", false),
        Load("v2-crlf-crlf", "73ef95ae47f0bd493ce2ea2d8ab1e1e4b1c8d4fa25ca2b3fa0d746f4732c374a", true),
        Load("v2-lf-lf", "b6bea3816a1adc5963ca0f1e63c833474d5b92081d79a160d1e4b78db6f8cbd2", true),
        Load("v2-crlf-lf", "312b2b46df2ecf0ff8b89e5a831619e6278fe4e51b4ed83cb5a6be44da9aebee", true),
        Load("v2-lf-crlf", "96adc83a1e5c6eb12aeb1aa2e7bef60b912f7066be12235b5b46c96111d414da", true),
        Load("v3-crlf-crlf-crlf", "3b745026dbc1519acc4708ca36ea6929cfb2eba17d63a59ffcc5c73996fc5829", true),
        Load("v3-lf-lf-crlf", "e5ad9fefa9cfa47e6ef1772b81fb1ed2ce4299b41fa329a914e98b0c5047727f", true),
        Load("v3-crlf-lf-crlf", "711372f2fd0a08328211a8392590e7573af03625f6913f89ad176d24ce36346f", true),
        Load("v3-lf-crlf-crlf", "355b9555747a4f5bf74c7a39999c0adb29721964679fcfea113e4e889ef0fd14", true),
        Load("v3-crlf-crlf-lf", "2703d348570c2ddbbadef7be02f89b0d81a0cf35f8e5548de6d2bbda33b00d4b", true),
        Load("v3-lf-lf-lf", "b6328ea84dac462422dd713d27d9e1ca36bc82aeae3b2049b8a358a53efcf7ed", true),
        Load("v3-crlf-lf-lf", "bc94d120ff9a3937bdd63cc0106ee7b190ef1a8c74a3a52b90e566797292285d", true),
        Load("v3-lf-crlf-lf", "0129573dc6d5dcb1e8ef039e7e4d9f158777ed5095ebe57329cf1e6e17ac3073", true)
    ];

    internal static bool HasGuildTables(OutpostSchemaObservation schema) =>
        Profiles.Any(p => p.Observation == schema && p.Observation.ProfileId.StartsWith("rimlink-outposts-schema/v3-", StringComparison.Ordinal));

    // A binding identifies one already qualified descriptor; it never observes/adopts a schema.
    internal static bool IsKnownBinding(OutpostSchemaBinding? binding, bool current) =>
        binding?.Digest is { Length: 32 } && Profiles.Any(p => (!current || p.Observation.IsCurrent)
            && p.Observation.ProfileId == binding.ProfileId && p.Observation.Digest.Span.SequenceEqual(binding.Digest));

    internal static OutpostSchemaBinding KnownBinding(string id)
    {
        var profile = Profiles.SingleOrDefault(p => p.Observation.ProfileId == id)
            ?? throw new InvalidDataException("outpost-schema-binding-unknown");
        return new(id, profile.Observation.Digest.ToByteArray());
    }

    public static Task<OutpostSchemaObservation> ValidateAsync(RimlinkDbContext db, OutpostSchemaContext context, CancellationToken ct)
    {
        if (!Enum.IsDefined(context)) throw new ArgumentOutOfRangeException(nameof(context));
        return ObserveCoreAsync(db, context, ct);
    }

    // Admit the complete actual schema before reading versioned evidence; this does NOT choose a domain format.
    internal static Task<OutpostSchemaObservation> ObserveAsync(RimlinkDbContext db, CancellationToken ct) =>
        ObserveCoreAsync(db, null, ct);

    private static async Task<OutpostSchemaObservation> ObserveCoreAsync(RimlinkDbContext db, OutpostSchemaContext? context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.Database.GetDbConnection() is not SqliteConnection connection)
            throw new InvalidDataException("outpost-schema-sqlite-required");
        bool ownsOpen = connection.State == ConnectionState.Closed;
        SqliteTransaction? ownedTransaction = null;
        try
        {
            if (ownsOpen) await connection.OpenAsync(ct);
            var transaction = (SqliteTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
            // Both catalog and migration lineage must come from one read snapshot; never claim a writer lease.
            transaction ??= ownedTransaction = connection.BeginTransaction(deferred: true);
            var actual = await ReadAsync(connection, transaction, ct);
            var matches = Profiles.Where(p => (context is null or OutpostSchemaContext.HistoricalCutV1 || p.Observation.IsCurrent)
                && (context != OutpostSchemaContext.HistoricalCutV3 || HasGuildTables(p.Observation))
                && Matches(actual, p.Catalog)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("outpost-schema-profile-mismatch");
            var profile = matches[0];
            if (context is OutpostSchemaContext.HistoricalCutV1 or OutpostSchemaContext.HistoricalCutV2 && HasGuildTables(profile.Observation))
                await GuildCapture.GuildDomainDigest.RequireEmptyAsync(db, ct, transaction);
            if (context == OutpostSchemaContext.HistoricalCutV1 && profile.Observation.IsCurrent)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM outpost_admin_commands LIMIT 1)";
                if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0)
                    throw new InvalidDataException("outpost-schema-v1-admin-evidence-conflict");
            }
            return profile.Observation;
        }
        finally
        {
            if (ownedTransaction is not null) await ownedTransaction.DisposeAsync();
            if (ownsOpen) await connection.CloseAsync();
        }
    }

    private static async Task<SchemaCatalog> ReadAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        var objects = new List<string?[]>();
        int bytes = 0;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT CASE WHEN length(CAST(type AS BLOB)) <= 64 THEN type END,
                    CASE WHEN length(CAST(name AS BLOB)) <= 4096 THEN name END,
                    CASE WHEN length(CAST(tbl_name AS BLOB)) <= 4096 THEN tbl_name END,
                    CASE WHEN length(CAST(sql AS BLOB)) <= 65536 THEN sql END, sql IS NULL
                FROM sqlite_schema ORDER BY type COLLATE BINARY,name COLLATE BINARY LIMIT 513
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (objects.Count == MaximumObjects || reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2)
                    || (reader.IsDBNull(3) && !reader.GetBoolean(4)))
                    throw new InvalidDataException("outpost-schema-catalog-bounds");
                string?[] row = [reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)];
                bytes += row.Sum(value => value is null ? 0 : Encoding.UTF8.GetByteCount(value));
                if (bytes > MaximumCatalogBytes) throw new InvalidDataException("outpost-schema-catalog-bounds");
                objects.Add(row);
            }
        }
        // Reject absent/forged migration-table definitions before issuing a query against their columns.
        if (!Profiles.Any(p => ObjectsMatch(objects, p.Catalog.Objects)))
            throw new InvalidDataException("outpost-schema-profile-mismatch");
        var migrations = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT CASE WHEN length(CAST(MigrationId AS BLOB)) <= 256 THEN MigrationId END FROM __EFMigrationsHistory ORDER BY MigrationId COLLATE BINARY LIMIT 257";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (migrations.Count == MaximumMigrations || reader.IsDBNull(0))
                    throw new InvalidDataException("outpost-schema-migration-bounds");
                string value = reader.GetString(0);
                bytes += Encoding.UTF8.GetByteCount(value);
                if (bytes > MaximumCatalogBytes) throw new InvalidDataException("outpost-schema-catalog-bounds");
                migrations.Add(value);
            }
        }
        return new(migrations.ToArray(), objects.ToArray());
    }

    private static bool ObjectsMatch(IReadOnlyList<string?[]> actual, IReadOnlyList<string?[]> expected) =>
        actual.Count == expected.Count && actual.Zip(expected).All(pair => pair.First.SequenceEqual(pair.Second, StringComparer.Ordinal));

    private static bool Matches(SchemaCatalog actual, SchemaCatalog expected) =>
        actual.Migrations.SequenceEqual(expected.Migrations, StringComparer.Ordinal) && ObjectsMatch(actual.Objects, expected.Objects);

    private static Profile Load(string name, string expectedDigest, bool current)
    {
        using var stream = typeof(OutpostDomainSchema).Assembly.GetManifestResourceStream("Rimlink.OutpostSchema." + name + ".json")
            ?? throw new InvalidDataException("outpost-schema-catalog-missing");
        if (stream.Length is <= 0 or > MaximumCatalogBytes) throw new InvalidDataException("outpost-schema-catalog-bounds");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        byte[] digest = SHA256.HashData(bytes);
        if (Convert.ToHexStringLower(digest) != expectedDigest) throw new InvalidDataException("outpost-schema-catalog-digest-mismatch");
        var catalog = JsonSerializer.Deserialize<SchemaCatalog>(bytes, new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true
        }) ?? throw new InvalidDataException("outpost-schema-catalog-invalid");
        if (catalog.Migrations is null || catalog.Objects is null || catalog.Migrations.Length is 0 or > MaximumMigrations
            || catalog.Objects.Length is 0 or > MaximumObjects || catalog.Migrations.Any(string.IsNullOrEmpty)
            || catalog.Migrations.Distinct(StringComparer.Ordinal).Count() != catalog.Migrations.Length
            || catalog.Objects.Any(r => r is null || r.Length != 4 || r.Take(3).Any(string.IsNullOrEmpty))
            || catalog.Objects.Select(r => (r[0], r[1])).Distinct().Count() != catalog.Objects.Length)
            throw new InvalidDataException("outpost-schema-catalog-invalid");
        return new(catalog, new("rimlink-outposts-schema/" + name, ByteString.CopyFrom(digest), current));
    }

    private sealed record SchemaCatalog(string[] Migrations, string?[][] Objects);
    private sealed record Profile(SchemaCatalog Catalog, OutpostSchemaObservation Observation);
}
