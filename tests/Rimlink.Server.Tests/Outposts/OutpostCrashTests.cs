using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostCrashTests
{
    [Theory]
    [InlineData("BeforePending", false)]
    [InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)]
    [InlineData("AfterExternalCommit", true)]
    public async Task Killed_writer_reopens_only_durably_committed_branch(string point, bool committed)
    {
        using var h = await Initialized();
        var termination = await OutpostFaults.KillAtAsync(h.Root, point);
        var reopened = await h.Authority.OpenAsync(CancellationToken.None);
        if (reopened != new OutpostStartupResult(OutpostStartupState.Ready)) h.KeepForDiagnostics = true;
        Assert.True(reopened == new OutpostStartupResult(OutpostStartupState.Ready),
            $"Expected Ready without refusal; got {reopened}; termination: {termination}; " +
            $"sqlite extended code: {(h.Authority.LastOpenFailure as SqliteException)?.SqliteExtendedErrorCode}; " +
            $"native OS error: {h.SqliteFailure.LastFailure}; retained fixture: {h.Root}; storage exception: {h.Authority.LastOpenFailure}");
        Assert.Equal(committed ? 1 : 0, await h.Db.Outposts.CountAsync());
        Assert.Equal(committed ? 2 : 1, await h.Db.OutpostTransitions.CountAsync());
        using var lease = await h.Anchor.AcquireAsync(CancellationToken.None);
        Assert.Null(h.Anchor.Read(lease)!.Pending);
    }

    [Fact]
    public async Task Hot_journal_recovery_keeps_identity_and_reestablishes_the_qualified_pair()
    {
        using var h = await Initialized();
        AnchorRecord original;
        using (var lease = await h.Anchor.AcquireAsync(CancellationToken.None)) original = h.Anchor.Read(lease)!;
        await OutpostFaults.KillAtAsync(h.Root, "HotJournal");
        AssertHot(h.Anchor.DatabasePath);

        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(CancellationToken.None)).State);

        using var reopened = await h.Anchor.AcquireAsync(CancellationToken.None);
        Assert.Equal(original, h.Anchor.Read(reopened));
        Assert.Null(h.Anchor.JournalRefusal());
        Assert.Single(h.Db.OutpostTransitions);
    }

    [Fact]
    public async Task Independent_processes_share_authority_and_legacy_storage_barriers()
    {
        using var h = await Initialized();
        await OutpostFaults.KillAtAsync(h.Root, "HeldBarrier", async () =>
        {
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                { await using var rejected = await h.Authority.AcquireAsync([], cancellation.Token); });
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                { using var rejected = await h.Files.PerStoreLock.HoldAsync(cancellation.Token); });
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var available = await h.Authority.AcquireAsync([1], deadline.Token);
        Assert.True(available.CoversOwner(1));
    }

    [Fact]
    public async Task Interrupted_journal_reestablishment_stays_maintenance_without_creating_missing_journal()
    {
        using var h = await Initialized();
        await OutpostFaults.KillAtAsync(h.Root, "HotJournal");
        AssertHot(h.Anchor.DatabasePath);
        await OutpostFaults.KillAtAsync(h.Root, "RecoveryAfterRead");
        Assert.False(File.Exists(h.Anchor.DatabasePath + "-journal"));
        byte[] recovered = File.ReadAllBytes(h.Anchor.DatabasePath);

        var result = await h.Authority.OpenAsync(CancellationToken.None);

        Assert.Equal(OutpostStartupState.Maintenance, result.State);
        Assert.Equal("anchor-journal-missing", result.Reason);
        Assert.False(File.Exists(h.Anchor.DatabasePath + "-journal"));
        Assert.Equal(recovered, File.ReadAllBytes(h.Anchor.DatabasePath));
        Assert.Single(h.Db.OutpostTransitions);
    }

    [Fact]
    public async Task Recovery_does_not_overwrite_a_conflicting_logical_identity()
    {
        using var h = await Initialized();
        AnchorRecord original;
        using (var lease = await h.Anchor.AcquireAsync(CancellationToken.None)) original = h.Anchor.Read(lease)!;
        await OutpostFaults.KillAtAsync(h.Root, "HotJournal");
        var conflicting = original with { InstallationId = Guid.NewGuid().ToString("D") };
        h.Anchor.RecoveryCheckpoint = _ =>
        {
            using var connection = new SqliteConnection($"Data Source={h.Anchor.DatabasePath};Pooling=False;Mode=ReadWrite");
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText = "PRAGMA journal_mode=PERSIST; PRAGMA synchronous=EXTRA; UPDATE authority_store SET Record=$record WHERE Id=1;";
            update.Parameters.Add("$record", SqliteType.Blob).Value = AnchorRecordCodec.Encode(conflicting);
            update.ExecuteNonQuery();
        };

        Assert.Equal(OutpostStartupState.Maintenance, (await h.Authority.OpenAsync(CancellationToken.None)).State);
        h.Anchor.RecoveryCheckpoint = null;
        using var inspection = await h.Anchor.AcquireAsync(CancellationToken.None);
        Assert.Equal(conflicting, h.Anchor.Read(inspection));
        Assert.Single(h.Db.OutpostTransitions);
    }

    // Executed in a separate vstest process only by OutpostFaults; normal suite has no mutation here.
    [Fact]
    public async Task Owned_crash_worker()
    {
        string? root = Environment.GetEnvironmentVariable("RIMLINK_OUTPOST_FAULT_ROOT");
        if (root is null) return;
        string point = Environment.GetEnvironmentVariable("RIMLINK_OUTPOST_FAULT_POINT")!;
        string ready = Environment.GetEnvironmentVariable("RIMLINK_OUTPOST_FAULT_READY")!;
        using var h = new AuthorityHarness(root);
        if (point == "HeldBarrier")
        {
            await using var lease = await h.Authority.AcquireAsync([1], CancellationToken.None);
            StopAtCheckpoint(ready);
        }
        else if (point == "RecoveryAfterRead")
        {
            h.Anchor.RecoveryCheckpoint = _ => StopAtCheckpoint(ready);
            await h.Authority.OpenAsync(CancellationToken.None);
        }
        else if (point == "HotJournal")
        {
            await using var lease = await h.Authority.AcquireAsync([], CancellationToken.None);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = h.Anchor.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            using (var settings = connection.CreateCommand())
            {
                settings.CommandText = "PRAGMA journal_mode=PERSIST; PRAGMA synchronous=EXTRA; PRAGMA cache_size=1; PRAGMA cache_spill=ON;";
                settings.ExecuteNonQuery();
            }
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE authority_store SET Record=NULL WHERE Id=1; CREATE TABLE crash_spill(value BLOB); INSERT INTO crash_spill VALUES(zeroblob(131072));";
                command.ExecuteNonQuery();
            }
            StopAtCheckpoint(ready);
        }
        else
        {
            var row = h.Db.OutpostAuthority.Single();
            h.Authority.CommitCheckpoint = current => { if (current == point) StopAtCheckpoint(ready); };
            var intent = new TransitionIntent(Guid.NewGuid().ToString("D"), row.WorldId, 1, 0,
                row.RestoreBoundary, "Production", "crash-fixture", new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)),
                ByteString.CopyFromUtf8("fixture"), OutpostId: Guid.NewGuid().ToString("D"));
            await using var lease = await h.Authority.AcquireAsync([1], CancellationToken.None);
            await h.Authority.CommitHeldAsync(lease, intent, (db, ct) =>
            {
                db.Outposts.Add(new OutpostRow { Id = intent.OutpostId!, PlayerId = 1, WorldId = row.WorldId, State = "Active" });
                return Task.CompletedTask;
            }, CancellationToken.None);
        }
        throw new InvalidOperationException("crash-worker-missed-checkpoint");
    }

    private static void StopAtCheckpoint(string ready)
    {
        using var worker = System.Diagnostics.Process.GetCurrentProcess();
        File.WriteAllText(ready + ".pending", $"{worker.Id}|{OutpostFaults.StartIdentity(worker)}");
        File.Move(ready + ".pending", ready);
        Thread.Sleep(Timeout.Infinite);
    }

    private static void AssertHot(string database)
    {
        using var journal = File.OpenRead(database + "-journal");
        byte[] magic = new byte[8];
        journal.ReadExactly(magic);
        Assert.Equal(new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }, magic);
    }

    private static async Task<AuthorityHarness> Initialized()
    {
        var h = new AuthorityHarness();
        h.Anchor.Provision();
        h.Db.World.Add(new Rimlink.Server.Storage.Entities.World { InfluenceInstanceId = "world-1" });
        await h.Db.SaveChangesAsync();
        Assert.Equal(OutpostStartupState.Ready,
            (await h.Authority.InitializeAsync(new("world-1", "test-admin", true), CancellationToken.None)).State);
        return h;
    }
}
