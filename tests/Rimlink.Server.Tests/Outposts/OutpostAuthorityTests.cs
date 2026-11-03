using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Application.World;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Xunit;
using AuthorityService = Rimlink.Server.Application.Outposts.OutpostAuthority;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAuthorityTests
{
    [Fact]
    public async Task Ready_primitives_report_effective_backend_without_enabling_feature()
    {
        using var h = new AuthorityHarness();
        h.Anchor.Provision();
        h.Db.World.Add(new Rimlink.Server.Storage.Entities.World { InfluenceInstanceId = "world-1" });
        await h.Db.SaveChangesAsync();
        Assert.Equal(OutpostStartupState.Ready,
            (await h.Authority.InitializeAsync(new("world-1", "test-admin", true), CancellationToken.None)).State);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(CancellationToken.None)).State);

        Assert.NotNull(h.Feature.LastAnchorBackend);
        Assert.NotNull(h.Feature.LastDomainBackend);
        Assert.Equal("persist", h.Feature.LastAnchorBackend.JournalMode);
        Assert.Equal(3, h.Feature.LastAnchorBackend.Synchronous);
        Assert.Equal("wal", h.Feature.LastDomainBackend.JournalMode);
        Assert.Equal(2, h.Feature.LastDomainBackend.Synchronous);
        Assert.Equal("3.53.3", h.Feature.LastDomainBackend.NativeVersion);
        Assert.Equal(OperatingSystem.IsWindows() ? "win32" : "unix", h.Feature.LastDomainBackend.Vfs);
        Assert.False(h.Feature.IsAvailable);
    }

    [Fact]
    public async Task Main_database_outside_supported_durability_profile_stops_without_touching_anchor()
    {
        using var h = new AuthorityHarness();
        h.Anchor.Provision();
        h.Db.World.Add(new Rimlink.Server.Storage.Entities.World { InfluenceInstanceId = "world-1" });
        await h.Db.SaveChangesAsync();
        await h.Authority.InitializeAsync(new("world-1", "test-admin", true), CancellationToken.None);
        byte[] before = File.ReadAllBytes(h.Anchor.DatabasePath);
        await h.Db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE;");

        Assert.Equal(OutpostStartupState.Maintenance, (await h.Authority.OpenAsync(CancellationToken.None)).State);
        Assert.Equal(before, File.ReadAllBytes(h.Anchor.DatabasePath));
        Assert.False(h.Feature.IsAvailable);
    }

    [Fact]
    public async Task Missing_anchor_with_history_stops()
    {
        using var h = new AuthorityHarness();
        h.Db.Outposts.Add(new OutpostRow { Id = "retained-history", State = "Closed" });
        await h.Db.SaveChangesAsync();

        var result = await h.Authority.OpenAsync(CancellationToken.None);

        Assert.Equal(OutpostStartupState.Maintenance, result.State);
        Assert.Equal("anchor-missing-with-history", result.Reason);
        Assert.False(File.Exists(h.Anchor.DatabasePath));
        Assert.Equal("Closed", h.Db.Outposts.Single().State);
        Assert.Empty(h.Db.OutpostAuthority);
    }

    [Fact]
    public async Task Empty_history_requires_explicit_initialization_without_creating_anchor()
    {
        using var h = new AuthorityHarness();

        var result = await h.Authority.OpenAsync(CancellationToken.None);

        Assert.Equal(OutpostStartupState.RequiresInitialization, result.State);
        Assert.False(File.Exists(h.Anchor.DatabasePath));
        Assert.Empty(h.Db.OutpostAuthority);
    }

    [Fact]
    public async Task Provisioned_store_is_not_logical_history_bootstrap()
    {
        using var h = new AuthorityHarness();

        h.Anchor.Provision();
        Assert.True(File.Exists(h.Anchor.DatabasePath));
        Assert.True(File.Exists(h.Anchor.DatabasePath + "-journal"));
        var result = await h.Authority.OpenAsync(CancellationToken.None);

        Assert.Equal(OutpostStartupState.RequiresInitialization, result.State);
        Assert.Empty(h.Db.OutpostAuthority);
    }

    [Fact]
    public void Provisioning_refuses_overwrite_and_preserves_existing_bytes()
    {
        using var h = new AuthorityHarness();
        byte[] original = [7, 8, 9];
        File.WriteAllBytes(h.Anchor.DatabasePath, original);

        Assert.Throws<IOException>(() => h.Anchor.Provision());

        Assert.Equal(original, File.ReadAllBytes(h.Anchor.DatabasePath));
        Assert.False(File.Exists(h.Anchor.DatabasePath + "-journal"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_or_corrupt_journal_before_open_stops(bool missing)
    {
        using var h = new AuthorityHarness();
        using (var connection = new SqliteConnection($"Data Source={h.Anchor.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE provisioning_probe (id INTEGER PRIMARY KEY)";
            create.ExecuteNonQuery();
        }
        byte[] original = File.ReadAllBytes(h.Anchor.DatabasePath);
        if (!missing) File.WriteAllBytes(h.Anchor.DatabasePath + "-journal", [1, 2, 3]);

        var result = await h.Authority.OpenAsync(CancellationToken.None);

        Assert.Equal(OutpostStartupState.Maintenance, result.State);
        Assert.Equal(missing ? "anchor-journal-missing" : "anchor-journal-invalid", result.Reason);
        Assert.Equal(original, File.ReadAllBytes(h.Anchor.DatabasePath));
        if (missing) Assert.False(File.Exists(h.Anchor.DatabasePath + "-journal"));
        else Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(h.Anchor.DatabasePath + "-journal"));
        Assert.Empty(h.Db.OutpostAuthority);
    }

    [Fact]
    public async Task Explicit_initialization_of_empty_history_persists_matching_heads()
    {
        using var h = new AuthorityHarness();
        h.Anchor.Provision();
        h.Db.World.Add(new Rimlink.Server.Storage.Entities.World { InfluenceInstanceId = "world-1" });
        await h.Db.SaveChangesAsync();

        var result = await h.Authority.InitializeAsync(new("world-1", "test-admin", true), CancellationToken.None);

        Assert.Equal(OutpostStartupState.Ready, result.State);
        var row = Assert.Single(h.Db.OutpostAuthority);
        using (var lease = await h.Anchor.AcquireAsync(CancellationToken.None))
        {
            var record = Assert.IsType<AnchorRecord>(h.Anchor.Read(lease));
            Assert.Equal(row.InstallationId, record.InstallationId);
            Assert.Equal(row.HeadId, record.Committed.Id);
            Assert.Equal(row.HeadDigest, record.Committed.Digest.ToByteArray());
            Assert.Null(record.Pending);
        }
        Assert.Single(h.Db.OutpostTransitions);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(CancellationToken.None)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_anchor_reconciles_only_verified_database_head(bool domainCommitted)
    {
        using var h = new AuthorityHarness();
        h.Anchor.Provision();
        h.Db.World.Add(new Rimlink.Server.Storage.Entities.World { InfluenceInstanceId = "world-1" });
        await h.Db.SaveChangesAsync();
        Assert.Equal(OutpostStartupState.Ready,
            (await h.Authority.InitializeAsync(new("world-1", "test-admin", true), CancellationToken.None)).State);
        AnchorRecord original;
        using (var lease = await h.Anchor.AcquireAsync(CancellationToken.None))
        {
            original = h.Anchor.Read(lease)!;
            var previous = domainCommitted
                ? new AuthorityHead(Guid.Empty.ToString("D"), Google.Protobuf.ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData("rimlink-outpost-genesis-v1"u8)))
                : original.Committed;
            var next = domainCommitted ? original.Committed : new AuthorityHead(Guid.NewGuid().ToString("D"), Google.Protobuf.ByteString.CopyFrom(new byte[32]));
            string transition = domainCommitted ? h.Db.OutpostTransitions.Single().Id : Guid.NewGuid().ToString("D");
            h.Anchor.WriteDurable(lease, original with { Committed = previous, Pending = new(transition, next) }, CancellationToken.None);
        }

        var result = await h.Authority.OpenAsync(CancellationToken.None);

        Assert.Equal(OutpostStartupState.Ready, result.State);
        using var reopened = await h.Anchor.AcquireAsync(CancellationToken.None);
        Assert.Equal(original, h.Anchor.Read(reopened));
        Assert.Single(h.Db.OutpostTransitions);
    }

    [Fact]
    public async Task Pending_anchor_preserves_evidence_when_database_effects_do_not_match()
    {
        using var h = new AuthorityHarness();
        h.Anchor.Provision();
        h.Db.World.Add(new Rimlink.Server.Storage.Entities.World { InfluenceInstanceId = "world-1" });
        await h.Db.SaveChangesAsync();
        await h.Authority.InitializeAsync(new("world-1", "test-admin", true), CancellationToken.None);
        AnchorRecord pending;
        using (var lease = await h.Anchor.AcquireAsync(CancellationToken.None))
        {
            var current = h.Anchor.Read(lease)!;
            pending = current with { Pending = new(Guid.NewGuid().ToString("D"), new AuthorityHead(Guid.NewGuid().ToString("D"), Google.Protobuf.ByteString.CopyFrom(new byte[32]))) };
            h.Anchor.WriteDurable(lease, pending, CancellationToken.None);
        }
        h.Db.Outposts.Add(new OutpostRow { Id = "unattested-mutation", State = "Closed" });
        await h.Db.SaveChangesAsync();

        var result = await h.Authority.OpenAsync(CancellationToken.None);

        Assert.Equal(OutpostStartupState.Maintenance, result.State);
        Assert.Equal("authority-protected-state-mismatch", result.Reason);
        using var reopened = await h.Anchor.AcquireAsync(CancellationToken.None);
        Assert.Equal(pending, h.Anchor.Read(reopened));
    }

    [Theory]
    [InlineData("SnapshotLink", "snapshot-byte-verification-required")]
    [InlineData("CustodyWithoutLink", "required-snapshot-evidence-unavailable")]
    [InlineData("ReceiptWithoutLink", "required-snapshot-evidence-unavailable")]
    public async Task Pending_next_with_unverified_or_missing_snapshot_proof_is_preserved(string proof, string reason)
    {
        using var h = new AuthorityHarness();
        h.Anchor.Provision();
        h.Db.World.Add(new Rimlink.Server.Storage.Entities.World { InfluenceInstanceId = "world-1" });
        await h.Db.SaveChangesAsync();
        await h.Authority.InitializeAsync(new("world-1", "test-admin", true), CancellationToken.None);
        AnchorRecord pending;
        using (var lease = await h.Anchor.AcquireAsync(CancellationToken.None))
        {
            var original = h.Anchor.Read(lease)!;
            var row = h.Db.OutpostAuthority.Single();
            var transition = new OutpostTransition
            {
                Id = Guid.NewGuid().ToString("D"), WorldId = row.WorldId, RestoreBoundary = row.RestoreBoundary,
                Kind = "SnapshotAccepted", Actor = "fixture", PredecessorId = row.TransitionId,
                OldHeadId = row.HeadId, NextHeadId = Guid.NewGuid().ToString("D"), CreatedAt = DateTime.UtcNow
            };
            if (proof == "SnapshotLink") h.Db.OutpostSnapshotLinks.Add(new OutpostSnapshotLink
            {
                WorldId = row.WorldId, PlayerId = 1, SnapshotId = Guid.NewGuid().ToString("N"),
                RestoreBoundary = row.RestoreBoundary, MetadataVersion = 1, RawSha256 = new byte[32],
                FrontierDigest = new byte[32], TransitionId = transition.Id
            });
            if (proof == "CustodyWithoutLink") h.Db.PawnCustody.Add(new PawnCustodyRecord
            {
                Id = Guid.NewGuid().ToString("D"), WorldId = row.WorldId, PlayerId = 1,
                RestoreBoundary = row.RestoreBoundary, FrontierTransitionId = transition.Id, LocationKind = "Colony"
            });
            await h.Db.SaveChangesAsync();
            transition.Effects = (await OutpostDomainDigest.ComputeAsync(h.Db, CancellationToken.None)).ToByteArray();
            transition.Digest = OutpostTransitionDigest.Compute(original.Committed, transition).ToByteArray();
            h.Db.OutpostTransitions.Add(transition);
            row.HeadId = transition.NextHeadId;
            row.HeadDigest = transition.Digest;
            row.TransitionId = transition.Id;
            await h.Db.SaveChangesAsync();
            pending = original with { Pending = new(transition.Id, new(row.HeadId, Google.Protobuf.ByteString.CopyFrom(row.HeadDigest))) };
            h.Anchor.WriteDurable(lease, pending, CancellationToken.None);
        }

        var result = await h.Authority.OpenAsync(CancellationToken.None);

        Assert.Equal(OutpostStartupState.Maintenance, result.State);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(2, h.Db.OutpostTransitions.Count());
        using var reopened = await h.Anchor.AcquireAsync(CancellationToken.None);
        Assert.Equal(pending, h.Anchor.Read(reopened));
    }

    [Fact]
    public async Task Unconfirmed_initialization_does_not_mutate_provisioned_anchor()
    {
        using var h = new AuthorityHarness();
        h.Anchor.Provision();

        var result = await h.Authority.InitializeAsync(new("world-1", "test-admin", false), CancellationToken.None);

        Assert.Equal(OutpostStartupState.RequiresInitialization, result.State);
        Assert.Equal("initialization-confirmation-required", result.Reason);
        using var inspect = await h.Anchor.AcquireAsync(CancellationToken.None);
        Assert.Null(h.Anchor.Read(inspect));
        Assert.Empty(h.Db.OutpostAuthority);
    }

    [Fact]
    public async Task Explicit_initialization_never_bootstraps_retained_history()
    {
        using var h = new AuthorityHarness();
        h.Anchor.Provision();
        h.Db.Outposts.Add(new OutpostRow { Id = "closed-history", State = "Closed" });
        await h.Db.SaveChangesAsync();

        var result = await h.Authority.InitializeAsync(new("world-1", "test-admin", true), CancellationToken.None);

        Assert.Equal(OutpostStartupState.Maintenance, result.State);
        Assert.Equal("initialization-history-not-empty", result.Reason);
        using var inspect = await h.Anchor.AcquireAsync(CancellationToken.None);
        Assert.Null(h.Anchor.Read(inspect));
        Assert.Empty(h.Db.OutpostAuthority);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Anchor_record_roundtrips_after_connection_reopen(bool pending)
    {
        using var h = new AuthorityHarness();
        h.Anchor.Provision();
        var head = new AuthorityHead(Guid.NewGuid().ToString("D"), Google.Protobuf.ByteString.CopyFrom(new byte[32]));
        var record = new AnchorRecord(1, Guid.NewGuid().ToString("D"), "world-1", Guid.NewGuid().ToString("D"), head,
            pending ? new AnchorPending(Guid.NewGuid().ToString("D"), head with { Id = Guid.NewGuid().ToString("D") }) : null);
        using (var lease = await h.Anchor.AcquireAsync(CancellationToken.None))
            h.Anchor.WriteDurable(lease, record, CancellationToken.None);
        using var reopened = await h.Anchor.AcquireAsync(CancellationToken.None);

        Assert.Equal(record, h.Anchor.Read(reopened));
        Assert.True(File.Exists(h.Anchor.DatabasePath + "-journal"));
    }

    [Fact]
    public async Task Disposed_authority_lease_cannot_write()
    {
        using var h = new AuthorityHarness();
        h.Anchor.Provision();
        var lease = await h.Anchor.AcquireAsync(CancellationToken.None);
        lease.Dispose();
        var head = new AuthorityHead(Guid.NewGuid().ToString("D"), Google.Protobuf.ByteString.CopyFrom(new byte[32]));
        var record = new AnchorRecord(1, Guid.NewGuid().ToString("D"), "world-1", Guid.NewGuid().ToString("D"), head);

        Assert.Throws<ObjectDisposedException>(() => h.Anchor.WriteDurable(lease, record, CancellationToken.None));
        using var inspect = await h.Anchor.AcquireAsync(CancellationToken.None);
        Assert.Null(h.Anchor.Read(inspect));
    }

    [Fact]
    public void Invalid_path_overlap_is_rejected()
    {
        using var h = new AuthorityHarness();
        var nested = new OutpostAnchorStore(h.Data, Options.Create(new OutpostOptions
        { AnchorDirectory = Path.Combine(h.Data.Backups, "authority") }));

        Assert.Throws<InvalidDataException>(() => nested.ValidatePath());
        Assert.False(Directory.Exists(Path.Combine(h.Data.Backups, "authority")));
    }
}

internal sealed class AuthorityHarness : IDisposable
{
    public string Root { get; }
    public DataDirectory Data { get; }
    public RimlinkDbContext Db { get; }
    public OutpostAnchorStore Anchor { get; }
    public AuthorityService Authority { get; }
    public ColonyLocks Owners { get; } = new();
    public SaveFiles Files { get; }
    public OutpostFeature Feature { get; } = new();

    public AuthorityHarness(string? root = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "rimlink-outpost-authority", Guid.NewGuid().ToString("N"));
        Data = new DataDirectory(Path.Combine(Root, "main"));
        string external = Path.Combine(Root, "external");
        Directory.CreateDirectory(external);
        Db = new RimlinkDbContext(new DbContextOptionsBuilder<RimlinkDbContext>()
            .UseSqlite($"Data Source={Data.DatabasePath};Pooling=False").Options);
        Db.Database.Migrate();
        Anchor = new OutpostAnchorStore(Data, Options.Create(new OutpostOptions { AnchorDirectory = external, StorageContract = "sqlite-local-v1" }));
        Files = new SaveFiles(Data);
        Authority = new AuthorityService(Db, Anchor, Files, Owners, Feature);
    }

    public void Dispose()
    {
        Db.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}
