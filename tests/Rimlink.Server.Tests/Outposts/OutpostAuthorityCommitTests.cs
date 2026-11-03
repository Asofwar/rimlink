using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAuthorityCommitTests
{
    [Fact]
    public async Task Protected_stage_commits_one_transition_then_external_head_and_reopens()
    {
        using var h = await Initialized();
        var before = h.Db.OutpostAuthority.Single();
        var intent = Intent(before);
        AuthorityHead result;
        await using (var lease = await h.Authority.AcquireAsync([1], CancellationToken.None))
            result = await h.Authority.CommitHeldAsync(lease, intent, async (db, ct) =>
            {
                db.Outposts.Add(new OutpostRow { Id = intent.OutpostId!, WorldId = "world-1", PlayerId = 1, State = "Active" });
                await Task.CompletedTask;
            }, CancellationToken.None);

        Assert.Equal(2, await h.Db.OutpostTransitions.CountAsync());
        Assert.Single(h.Db.Outposts);
        using (var lease = await h.Anchor.AcquireAsync(CancellationToken.None))
        {
            var record = h.Anchor.Read(lease)!;
            Assert.Equal(result, record.Committed);
            Assert.Null(record.Pending);
        }
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task Failing_stage_rolls_back_domain_without_advancing_anchor()
    {
        using var h = await Initialized();
        var intent = Intent(h.Db.OutpostAuthority.Single());
        await using (var lease = await h.Authority.AcquireAsync([1], CancellationToken.None))
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Authority.CommitHeldAsync(lease, intent, async (db, ct) =>
            {
                db.Outposts.Add(new OutpostRow { Id = intent.OutpostId!, State = "Closed" });
                await db.SaveChangesAsync(ct);
                throw new InvalidOperationException("stage-failure");
            }, CancellationToken.None));
        Assert.Empty(await h.Db.Outposts.AsNoTracking().ToListAsync());
        Assert.Single(await h.Db.OutpostTransitions.AsNoTracking().ToListAsync());
        using var reopened = await h.Anchor.AcquireAsync(CancellationToken.None);
        Assert.Equal(intent.ExpectedHead, h.Anchor.Read(reopened)!.Committed);
        Assert.Null(h.Anchor.Read(reopened)!.Pending);
    }

    [Fact]
    public async Task Stale_expected_head_refuses_before_running_stage()
    {
        using var h = await Initialized();
        var intent = Intent(h.Db.OutpostAuthority.Single()) with
        { ExpectedHead = new(Guid.NewGuid().ToString("D"), ByteString.CopyFrom(new byte[32])) };
        bool staged = false;
        await using var lease = await h.Authority.AcquireAsync([1], CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Authority.CommitHeldAsync(lease, intent, (db, ct) =>
        { staged = true; return Task.CompletedTask; }, CancellationToken.None));
        Assert.False(staged);
        Assert.Single(h.Db.OutpostTransitions);
    }

    [Theory]
    [InlineData("BeforePending", false)]
    [InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)]
    [InlineData("AfterExternalCommit", true)]
    public async Task Interrupted_commit_reopens_only_proven_domain_branch(string point, bool committed)
    {
        using var h = await Initialized();
        var intent = Intent(h.Db.OutpostAuthority.Single());
        h.Authority.CommitCheckpoint = current =>
        { if (current == point) throw new IOException("injected-owned-commit-interruption"); };
        await using (var lease = await h.Authority.AcquireAsync([1], CancellationToken.None))
            await Assert.ThrowsAsync<IOException>(() => h.Authority.CommitHeldAsync(lease, intent, (db, ct) =>
            {
                db.Outposts.Add(new OutpostRow { Id = intent.OutpostId!, PlayerId = 1, WorldId = "world-1", State = "Active" });
                return Task.CompletedTask;
            }, CancellationToken.None));
        if (point != "BeforePending") Assert.Equal(OutpostStartupState.Maintenance, h.Feature.Startup.State);
        h.Authority.CommitCheckpoint = null;

        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(CancellationToken.None)).State);
        Assert.Equal(committed ? 1 : 0, await h.Db.Outposts.CountAsync());
        Assert.Equal(committed ? 2 : 1, await h.Db.OutpostTransitions.CountAsync());
        using var reopened = await h.Anchor.AcquireAsync(CancellationToken.None);
        Assert.Null(h.Anchor.Read(reopened)!.Pending);
    }

    [Fact]
    public async Task Caller_cancellation_after_durable_pending_does_not_cancel_finalization()
    {
        using var h = await Initialized();
        using var cancellation = new CancellationTokenSource();
        var intent = Intent(h.Db.OutpostAuthority.Single());
        h.Authority.CommitCheckpoint = current => { if (current == "AfterPending") cancellation.Cancel(); };
        await using (var lease = await h.Authority.AcquireAsync([1], CancellationToken.None))
            await h.Authority.CommitHeldAsync(lease, intent, (db, ct) => Task.CompletedTask, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(2, await h.Db.OutpostTransitions.CountAsync());
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(CancellationToken.None)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Proof_dependent_stage_is_unavailable_before_pending_or_success(bool hasLink)
    {
        using var h = await Initialized();
        var intent = Intent(h.Db.OutpostAuthority.Single());
        bool published = false;
        await using (var lease = await h.Authority.AcquireAsync([1], CancellationToken.None))
        {
            await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(async () =>
            {
                await h.Authority.CommitHeldAsync(lease, intent, (db, ct) =>
                {
                    if (hasLink) db.OutpostSnapshotLinks.Add(new OutpostSnapshotLink
                    {
                        WorldId = "world-1", PlayerId = 1, SnapshotId = Guid.NewGuid().ToString("N"),
                        RestoreBoundary = intent.RestoreBoundary, MetadataVersion = 1,
                        RawSha256 = new byte[32], FrontierDigest = new byte[32], TransitionId = intent.Id
                    });
                    else db.PawnCustody.Add(new PawnCustodyRecord
                    {
                        Id = Guid.NewGuid().ToString("D"), WorldId = "world-1", PlayerId = 1,
                        RestoreBoundary = intent.RestoreBoundary, LocationKind = "Colony", FrontierTransitionId = intent.Id
                    });
                    return Task.CompletedTask;
                }, CancellationToken.None);
                published = true;
            });
        }
        Assert.False(published);
        Assert.Empty(await h.Db.OutpostSnapshotLinks.AsNoTracking().ToListAsync());
        Assert.Empty(await h.Db.PawnCustody.AsNoTracking().ToListAsync());
        Assert.Single(await h.Db.OutpostTransitions.AsNoTracking().ToListAsync());
        using var reopened = await h.Anchor.AcquireAsync(CancellationToken.None);
        Assert.Equal(intent.ExpectedHead, h.Anchor.Read(reopened)!.Committed);
        Assert.Null(h.Anchor.Read(reopened)!.Pending);
    }

    [Theory]
    [InlineData("BeforePending", false)]
    [InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)]
    [InlineData("AfterExternalCommit", true)]
    public async Task Interrupted_initialization_preserves_the_durable_decision_without_retrying_bootstrap(string point, bool committed)
    {
        using var h = new AuthorityHarness();
        h.Anchor.Provision();
        h.Db.World.Add(new Rimlink.Server.Storage.Entities.World { InfluenceInstanceId = "world-1" });
        await h.Db.SaveChangesAsync();
        h.Authority.CommitCheckpoint = current =>
        { if (current == point) throw new IOException("injected-owned-initialization-interruption"); };

        await Assert.ThrowsAsync<IOException>(() =>
            h.Authority.InitializeAsync(new("world-1", "test-admin", true), CancellationToken.None));
        Assert.Empty(h.Db.ChangeTracker.Entries());
        if (point != "BeforePending") Assert.Equal(OutpostStartupState.Maintenance, h.Feature.Startup.State);
        h.Authority.CommitCheckpoint = null;
        var reopened = await h.Authority.OpenAsync(CancellationToken.None);
        Assert.Equal(committed ? OutpostStartupState.Ready : point == "BeforePending"
            ? OutpostStartupState.RequiresInitialization : OutpostStartupState.Maintenance, reopened.State);
        Assert.Equal(committed ? 1 : 0, await h.Db.OutpostAuthority.CountAsync());
        Assert.Equal(committed ? 1 : 0, await h.Db.OutpostTransitions.CountAsync());
        using var inspection = await h.Anchor.AcquireAsync(CancellationToken.None);
        var record = h.Anchor.Read(inspection);
        if (point == "AfterPending") Assert.NotNull(record!.Pending);
        else if (committed) Assert.Null(record!.Pending);
        else Assert.Null(record);
    }

    [Fact]
    public async Task Caller_cancellation_after_initialization_pending_cannot_cancel_finalization()
    {
        using var h = new AuthorityHarness();
        h.Anchor.Provision();
        h.Db.World.Add(new Rimlink.Server.Storage.Entities.World { InfluenceInstanceId = "world-1" });
        await h.Db.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        h.Authority.CommitCheckpoint = current => { if (current == "AfterPending") cancellation.Cancel(); };

        var initialized = await h.Authority.InitializeAsync(new("world-1", "test-admin", true), cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(OutpostStartupState.Ready, initialized.State);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(CancellationToken.None)).State);
        Assert.Single(h.Db.OutpostTransitions);
    }

    private static TransitionIntent Intent(Rimlink.Server.Storage.Entities.OutpostAuthority row) =>
        new(Guid.NewGuid().ToString("D"), row.WorldId, 1, 0, row.RestoreBoundary, "Production", "test-admin",
            new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)), ByteString.CopyFromUtf8("bounded-intent"),
            OutpostId: Guid.NewGuid().ToString("D"));

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
