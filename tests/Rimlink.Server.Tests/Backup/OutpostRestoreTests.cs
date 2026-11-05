using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public class OutpostRestoreTests
{
    [Fact]
    public async Task Bulk_restore_separates_cut_chain_from_external_fence()
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        var prefix = await f.H.Db.OutpostTransitions.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
        var links = await f.H.Db.OutpostSnapshotLinks.AsNoTracking().OrderBy(l => l.SnapshotId).ToListAsync();
        f.H.F.Time.Advance(TimeSpan.FromHours(1));
        await f.H.Service.StateAsync(1, new OutpostStateQuery { Scope = f.H.Scope.Clone() }, default);
        Assert.Equal(20, (await f.H.Row()).Stock);
        var procedure = new OutpostRestoreProcedure();
        var request = await Request(f, procedure, archive);
        Assert.NotEqual(request.Inspection.Manifest.Authority.HeadId, request.Inspection.Destination.External!.Committed.Id);
        f.Host.Dispose();
        var result = await procedure.ExecuteAsync(request, default);
        Assert.Equal((RestoreOutcome.Completed, ""), (result.Outcome, result.Reason));
        f.H.Db.ChangeTracker.Clear();
        var head = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        Assert.Equal(request.TargetBoundary, head.RestoreBoundary);
        Assert.Equal(request.TargetInstallationId, head.InstallationId);
        Assert.Equal(request.Inspection.Manifest.Authority.WorldId, head.WorldId);
        Assert.Equal(result.HeadId, head.HeadId);
        Assert.Equal(result.HeadDigest, head.HeadDigest);
        Assert.Equal(0, (await f.H.Row()).Stock); // Selected C, not post-cut E's twenty units.
        var root = await f.H.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Kind == "BulkRestore");
        Assert.Equal($"archive/outpost-restores/{root.Id}/cut", OutpostRestoreEvidence.Decode<RestoreMainPlan>(root.Intent).SourceDirectory);
        Assert.Equal(request.Inspection.Manifest.Authority.HeadId, root.OldHeadId);
        Assert.Equal(request.Inspection.Manifest.Authority.TransitionId, root.PredecessorId);
        Assert.NotEqual(request.Inspection.Destination.External.Committed.Id, root.OldHeadId);
        var restored = await f.H.Db.OutpostTransitions.AsNoTracking().Where(t => t.Kind != "BulkRestore" && t.Kind != "RestoreCompletion").OrderBy(t => t.Id).ToListAsync();
        Assert.Equal(JsonSerializer.Serialize(prefix), JsonSerializer.Serialize(restored));
        Assert.Equal(JsonSerializer.Serialize(links), JsonSerializer.Serialize(await f.H.Db.OutpostSnapshotLinks.AsNoTracking().OrderBy(l => l.SnapshotId).ToListAsync()));
        var pawn = await f.H.Db.PawnCustody.AsNoTracking().SingleAsync();
        Assert.Equal(request.TargetBoundary, pawn.RestoreBoundary);
        Assert.Equal(root.Id, pawn.FrontierTransitionId);
        Assert.Equal("Assigned", pawn.LocationKind);
        Assert.Equal(f.H.Workers[0].AssignmentId, pawn.AssignmentId);
        Assert.Equal(f.H.Workers[0].PawnCustodyId, pawn.Id);
        Assert.Equal(f.H.F.Request.OutpostId, pawn.LocationId);
        var status = await f.H.F.H.H.Authority.OpenAsync(default);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), status);
        var resumed = await procedure.ResumeAsync(request.Inspection.DataDirectory, request.Inspection.AnchorDirectory, request.RestoreId, default);
        Assert.Equal(result, resumed with { HeadDigest = result.HeadDigest });
        Assert.Equal(1, await f.H.Db.OutpostTransitions.CountAsync(t => t.Kind == "BulkRestore"));
    }

    [Theory]
    [InlineData("AfterMainCommitted")]
    [InlineData("AfterRestorePublication")]
    public async Task Interrupted_main_commit_is_not_normal_startup_repair(string stop)
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        var procedure = new OutpostRestoreProcedure { Checkpoint = point => { if (point == stop) throw new StopRestore(); } };
        var request = await Request(f, procedure, archive);
        f.Host.Dispose();
        await Assert.ThrowsAsync<StopRestore>(() => procedure.ExecuteAsync(request, default));
        f.H.Db.ChangeTracker.Clear();
        var root = await f.H.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Kind == "BulkRestore");
        string original = JsonSerializer.Serialize(root);
        if (stop == "AfterMainCommitted")
            Assert.Equal(OutpostStartupState.Maintenance, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
        var outcome = await new OutpostRestoreProcedure().ResumeAsync(request.Inspection.DataDirectory, request.Inspection.AnchorDirectory, request.RestoreId, default);
        Assert.Equal(RestoreOutcome.Completed, outcome.Outcome);
        Assert.Equal(original, JsonSerializer.Serialize(await f.H.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == root.Id)));
        Assert.Equal(1, await f.H.Db.OutpostTransitions.CountAsync(t => t.Kind == "BulkRestore"));
        Assert.Equal(OutpostStartupState.Ready, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
        Assert.Equal(0, (await f.H.Row()).Stock);
    }

    [Theory]
    [InlineData("AfterMainPrepared")]
    [InlineData("AfterMainDatabaseCommit")]
    public async Task Main_prepared_resume_preserves_the_declared_root_and_commits_once(string stop)
    {
        var (f, request, anchor) = await OutpostRestoreResumeTests.Interrupt(stop);
        using (f)
        {
            string main = Path.Combine(anchor, "restores", request.RestoreId, "main.json");
            byte[] original = File.ReadAllBytes(main);
            Assert.Equal(OutpostStartupState.Maintenance, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
            var result = await new OutpostRestoreProcedure().ResumeAsync(request.Inspection.DataDirectory, anchor, request.RestoreId, default);
            Assert.True(result.Outcome == RestoreOutcome.Completed, result.Reason);
            Assert.Empty(result.Reason);
            Assert.Equal(original, File.ReadAllBytes(main));
            f.H.Db.ChangeTracker.Clear();
            var root = await f.H.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Kind == "BulkRestore");
            Assert.Equal(request.RestoreId, root.Id);
            Assert.Equal(original, root.Intent);
            Assert.Equal(0, (await f.H.Row()).Stock);
            Assert.Equal(OutpostStartupState.Ready, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
            var repeated = await new OutpostRestoreProcedure().ResumeAsync(request.Inspection.DataDirectory, anchor, request.RestoreId, default);
            Assert.Equal(result.HeadId, repeated.HeadId);
            Assert.Equal(result.HeadDigest, repeated.HeadDigest);
            Assert.Equal(1, await f.H.Db.OutpostTransitions.CountAsync(t => t.Kind == "BulkRestore"));
        }
    }

    [Theory]
    [InlineData("boundary")]
    [InlineData("installation")]
    public async Task Restore_chain_identity_cannot_be_relabelled_with_the_same_committed_head(string component)
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        var procedure = new OutpostRestoreProcedure();
        var request = await Request(f, procedure, archive);
        f.Host.Dispose();
        Assert.Equal(RestoreOutcome.Completed, (await procedure.ExecuteAsync(request, default)).Outcome);
        var shared = f.H.F.H.H;
        string replacement = Guid.NewGuid().ToString("D");
        if (component == "boundary")
            await f.H.Db.OutpostAuthority.ExecuteUpdateAsync(s => s.SetProperty(a => a.RestoreBoundary, replacement));
        else
            await f.H.Db.OutpostAuthority.ExecuteUpdateAsync(s => s.SetProperty(a => a.InstallationId, replacement));
        using (var held = await shared.Anchor.AcquireAsync(default))
        {
            var original = shared.Anchor.Read(held)!;
            shared.Anchor.WriteDurable(held, component == "boundary" ? original with { RestoreBoundary = replacement }
                : original with { InstallationId = replacement }, default);
        }
        string before = await f.Cut();
        var status = await shared.Authority.OpenAsync(default);
        Assert.Equal(OutpostStartupState.Maintenance, status.State);
        Assert.Equal(before, await f.Cut());
    }

    [Fact]
    public async Task Changed_main_commit_marker_cannot_add_a_completion_or_change_protected_checkpoints()
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        var procedure = new OutpostRestoreProcedure { Checkpoint = point => { if (point == "AfterMainCommitted") throw new StopRestore(); } };
        var request = await Request(f, procedure, archive);
        f.Host.Dispose();
        await Assert.ThrowsAsync<StopRestore>(() => procedure.ExecuteAsync(request, default));
        var shared = f.H.F.H.H;
        using (var held = await shared.Anchor.AcquireAsync(default))
        {
            var original = shared.Anchor.Read(held)!;
            shared.Anchor.WriteDurable(held, original with { Restore = original.Restore! with { NewBoundary = Guid.NewGuid().ToString("D") } }, default);
        }
        string before = await f.Cut();
        var result = await new OutpostRestoreProcedure().ResumeAsync(request.Inspection.DataDirectory, request.Inspection.AnchorDirectory, request.RestoreId, default);
        Assert.Equal(RestoreOutcome.Maintenance, result.Outcome);
        Assert.Equal(before, await f.Cut());
        Assert.Equal(0, await f.H.Db.OutpostTransitions.CountAsync(t => t.Kind == "RestoreCompletion"));
    }

    internal static async Task<RestoreRequest> Request(LifecycleFixture f, OutpostRestoreProcedure procedure, string archive)
    {
        var shared = f.H.F.H.H;
        var inspected = await procedure.InspectAsync(archive, shared.Data.Root, Path.GetDirectoryName(shared.Anchor.DatabasePath)!, default);
        var request = new RestoreRequest(Guid.NewGuid().ToString("D"), RestoreMode.ExistingInstallationRestore, inspected,
            inspected.Manifest.Authority.InstallationId, Guid.NewGuid().ToString("D"), [], "sqlite-local-v1", LocalTestStorage.LinuxQualification(shared.Data.Root));
        return request with { ConfirmationDigest = OutpostRestoreJournalCodec.SelectionDigest(request) };
    }

    private sealed class StopRestore : Exception;
}
