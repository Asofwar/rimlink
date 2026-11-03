using System.Data;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostDomainDigestTests
{
    [Fact]
    public async Task Empty_migrated_domain_has_deterministic_sha256()
    {
        using var first = new DigestDatabase();
        using var second = new DigestDatabase();
        var digest = await Compute(first.Db);
        Assert.Equal(32, digest.Length);
        Assert.Equal(digest, await Compute(first.Db));
        Assert.Equal(digest, await Compute(second.Db));
    }

    [Theory]
    [InlineData("outposts", "UPDATE outposts SET Stock=1")]
    [InlineData("outpost_assignments", "UPDATE outpost_assignments SET Blob=X'010203'")]
    [InlineData("pawn_custody", "UPDATE pawn_custody SET Tombstone=1")]
    [InlineData("outpost_operations", "UPDATE outpost_operations SET Result=X'010203'")]
    [InlineData("outpost_reservations", "UPDATE outpost_reservations SET Tile=19")]
    [InlineData("outpost_parcel_links", "UPDATE outpost_parcel_links SET Payload=X'010203'")]
    [InlineData("outpost_snapshot_links", "UPDATE outpost_snapshot_links SET RawSha256=X'010203'")]
    [InlineData("outpost_manifests", "UPDATE outpost_manifests SET Document=X'010203'")]
    [InlineData("outpost_restore_authorizations", "UPDATE outpost_restore_authorizations SET CanonicalSnapshotHash=X'010203'")]
    [InlineData("outpost_delivery_attempts", "UPDATE outpost_delivery_attempts SET State='Accepted'")]
    [InlineData("outpost_lifecycle_operations", "UPDATE outpost_lifecycle_operations SET Progress=X'010203'")]
    public async Task Every_protected_table_insert_update_delete_changes_digest(string table, string mutation)
    {
        using var h = new DigestDatabase();
        var empty = await Compute(h.Db);
        object row = Row(table);
        h.Db.Add(row);
        await h.Db.SaveChangesAsync();
        var inserted = await Compute(h.Db);
        Assert.NotEqual(empty, inserted);
        await h.Db.Database.ExecuteSqlRawAsync(mutation);
        Assert.NotEqual(inserted, await Compute(h.Db));
        h.Db.Remove(row);
        await h.Db.SaveChangesAsync();
        Assert.Equal(empty, await Compute(h.Db));
    }

    [Fact]
    public async Task Authority_projection_and_separately_verified_transition_chain_are_excluded()
    {
        using var h = new DigestDatabase();
        var initial = await Compute(h.Db);
        h.Db.OutpostAuthority.Add(new Rimlink.Server.Storage.Entities.OutpostAuthority
        { Id = 1, HeadId = "head-a", HeadDigest = [1] });
        h.Db.OutpostTransitions.Add(new OutpostTransition { Id = "transition-a", NextHeadId = "head-b" });
        await h.Db.SaveChangesAsync();
        Assert.Equal(initial, await Compute(h.Db));
        await h.Db.Database.ExecuteSqlRawAsync("UPDATE outpost_authority SET HeadId='head-c', HeadDigest=X'0203'");
        Assert.Equal(initial, await Compute(h.Db));
    }

    [Fact]
    public async Task Legacy_wallet_changes_are_excluded()
    {
        using var h = new DigestDatabase();
        var initial = await Compute(h.Db);
        await h.Db.Database.ExecuteSqlRawAsync("INSERT INTO wallets (PlayerId,Balance,UpdatedAt) VALUES (1,123,'2026-11-03')");
        Assert.Equal(initial, await Compute(h.Db));
        await h.Db.Database.ExecuteSqlRawAsync("UPDATE wallets SET Balance=999");
        Assert.Equal(initial, await Compute(h.Db));
    }

    [Fact]
    public async Task Caller_transaction_sees_saved_uncommitted_changes_and_rollback_restores_digest()
    {
        using var h = new DigestDatabase();
        var initial = await Compute(h.Db);
        await using (var transaction = await h.Db.Database.BeginTransactionAsync())
        {
            h.Db.Outposts.Add(new OutpostRow { Id = "uncommitted" });
            await h.Db.SaveChangesAsync();
            var pending = await Compute(h.Db);
            Assert.NotEqual(initial, pending);
            Assert.Same(transaction, h.Db.Database.CurrentTransaction);
            Assert.Equal(pending, await Compute(h.Db));
            await transaction.RollbackAsync();
        }
        Assert.Equal(initial, await Compute(h.Db));
    }

    [Fact]
    public async Task Digest_reads_persisted_values_not_unsaved_change_tracker()
    {
        using var h = new DigestDatabase();
        var initial = await Compute(h.Db);
        h.Db.Outposts.Add(new OutpostRow { Id = "not-saved" });
        Assert.Equal(initial, await Compute(h.Db));
        Assert.Equal(EntityState.Added, h.Db.Entry(h.Db.Outposts.Local.Single()).State);
    }

    [Fact]
    public async Task Primary_key_order_including_full_snapshot_composite_key_is_deterministic()
    {
        using var first = new DigestDatabase();
        using var second = new DigestDatabase();
        foreach (int n in new[] { 3, 1, 2 }) AddOrderedRows(first.Db, n);
        foreach (int n in new[] { 1, 2, 3 }) AddOrderedRows(second.Db, n);
        await first.Db.SaveChangesAsync();
        await second.Db.SaveChangesAsync();
        Assert.Equal(await Compute(first.Db), await Compute(second.Db));
    }

    [Fact]
    public async Task Existing_open_connection_remains_open_and_owned_connection_is_closed()
    {
        using var h = new DigestDatabase();
        var connection = h.Db.Database.GetDbConnection();
        Assert.Equal(ConnectionState.Closed, connection.State);
        await Compute(h.Db);
        Assert.Equal(ConnectionState.Closed, connection.State);
        await h.Db.Database.OpenConnectionAsync();
        await Compute(h.Db);
        Assert.Equal(ConnectionState.Open, connection.State);
        await h.Db.Database.CloseConnectionAsync();
    }

    [Fact]
    public async Task Schema_column_names_and_declared_types_are_bound_even_without_rows()
    {
        using var first = new DigestDatabase();
        using var second = new DigestDatabase();
        var initial = await Compute(first.Db);
        await first.Db.Database.ExecuteSqlRawAsync("ALTER TABLE outposts ADD COLUMN Extra TEXT");
        await second.Db.Database.ExecuteSqlRawAsync("ALTER TABLE outposts ADD COLUMN Extra BLOB");
        var textColumn = await Compute(first.Db);
        Assert.NotEqual(initial, textColumn);
        Assert.NotEqual(textColumn, await Compute(second.Db));
        await first.Db.Database.ExecuteSqlRawAsync("ALTER TABLE outposts RENAME COLUMN Extra TO Other");
        Assert.NotEqual(textColumn, await Compute(first.Db));
    }

    [Fact]
    public async Task Null_empty_text_empty_blob_integer_and_real_have_distinct_type_tags()
    {
        using var h = new DigestDatabase();
        h.Db.Outposts.Add(new OutpostRow { Id = "row" });
        await h.Db.SaveChangesAsync();
        await h.Db.Database.ExecuteSqlRawAsync("ALTER TABLE outposts ADD COLUMN Extra");
        var digests = new HashSet<ByteString>();
        foreach (string literal in new[] { "NULL", "''", "X''", "0", "0.0", "9223372036854775807", "-9223372036854775808" })
        {
            await h.Db.Database.ExecuteSqlRawAsync("UPDATE outposts SET Extra=" + literal);
            Assert.True(digests.Add(await Compute(h.Db)), "Different SQLite storage values must not collapse.");
        }
    }

    [Fact]
    public async Task Adjacent_text_fields_and_embedded_nulls_cannot_collide()
    {
        using var h = new DigestDatabase();
        h.Db.PawnCustody.Add(new PawnCustodyRecord { Id = "pawn", LocationKind = "ab", LocationId = "c" });
        await h.Db.SaveChangesAsync();
        var before = await Compute(h.Db);
        await h.Db.Database.ExecuteSqlRawAsync("UPDATE pawn_custody SET LocationKind='a',LocationId='bc'");
        Assert.NotEqual(before, await Compute(h.Db));
        await h.Db.Database.ExecuteSqlRawAsync("UPDATE pawn_custody SET LocationKind=char(97,0,98),LocationId='c'");
        var embeddedNull = await Compute(h.Db);
        await h.Db.Database.ExecuteSqlRawAsync("UPDATE pawn_custody SET LocationKind=char(97,0),LocationId='bc'");
        Assert.NotEqual(embeddedNull, await Compute(h.Db));
    }

    [Theory]
    [InlineData("UPDATE outpost_assignments SET Blob=zeroblob(33554433)")]
    [InlineData("UPDATE outpost_assignments SET FactsJson=CAST(zeroblob(33554433) AS TEXT)")]
    [InlineData("UPDATE outpost_assignments SET FactsJson=CAST(X'80' AS TEXT)")]
    [InlineData("UPDATE outpost_assignments SET Active=1e999")]
    public async Task Oversized_invalid_utf8_or_nonfinite_values_fail_closed(string mutation)
    {
        using var h = new DigestDatabase();
        h.Db.OutpostAssignments.Add(new OutpostAssignment { Id = "assignment" });
        await h.Db.SaveChangesAsync();
        await h.Db.Database.ExecuteSqlRawAsync(mutation);
        await Assert.ThrowsAsync<InvalidDataException>(() => Compute(h.Db));
        Assert.Equal(ConnectionState.Closed, h.Db.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task Cancelled_request_returns_no_digest_and_preserves_connection_ownership()
    {
        using var h = new DigestDatabase();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OutpostDomainDigest.ComputeAsync(h.Db, cancellation.Token));
        Assert.Equal(ConnectionState.Closed, h.Db.Database.GetDbConnection().State);
    }

    private static Task<ByteString> Compute(RimlinkDbContext db) => OutpostDomainDigest.ComputeAsync(db, CancellationToken.None);

    private static object Row(string table) => table switch
    {
        "outposts" => new OutpostRow { Id = "outpost" },
        "outpost_assignments" => new OutpostAssignment { Id = "assignment" },
        "pawn_custody" => new PawnCustodyRecord { Id = "pawn" },
        "outpost_operations" => new OutpostOperation { Id = "operation" },
        "outpost_reservations" => new OutpostReservation { Id = "reservation" },
        "outpost_parcel_links" => new OutpostParcelLink { ParcelId = 1 },
        "outpost_snapshot_links" => new OutpostSnapshotLink { SnapshotId = "snapshot" },
        "outpost_manifests" => new OutpostManifest { Id = "manifest" },
        "outpost_restore_authorizations" => new OutpostRestoreAuthorization { Id = "authorization" },
        "outpost_delivery_attempts" => new OutpostDeliveryAttempt { Id = "attempt" },
        "outpost_lifecycle_operations" => new OutpostLifecycleOperation { Id = "lifecycle" },
        _ => throw new ArgumentOutOfRangeException(nameof(table))
    };

    private static void AddOrderedRows(RimlinkDbContext db, int n)
    {
        db.Outposts.Add(new OutpostRow { Id = "outpost-" + n, Stock = n });
        db.OutpostSnapshotLinks.Add(new OutpostSnapshotLink
        { WorldId = "world", PlayerId = n, SaveGeneration = 1, SnapshotId = "shared", RawSha256 = [(byte)n] });
        db.OutpostParcelLinks.Add(new OutpostParcelLink { ParcelId = n, BatchId = "batch-" + n, Payload = [(byte)n] });
    }

    private sealed class DigestDatabase : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "rimlink-domain-digest", Guid.NewGuid().ToString("N"));
        public RimlinkDbContext Db { get; }

        public DigestDatabase()
        {
            Directory.CreateDirectory(directory);
            Db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "domain.db")};Pooling=False").Options);
            Db.Database.Migrate();
        }

        public void Dispose()
        {
            Db.Dispose();
            Directory.Delete(directory, true);
        }
    }
}
