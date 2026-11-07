using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostRestoreAdditiveFreshTests
{
    [Theory]
    [InlineData("AfterMainPlanBound")]
    [InlineData("AfterMainPrepared")]
    [InlineData("AfterSchemaMigration")]
    [InlineData("AfterMainDatabaseCommit")]
    [InlineData("AfterCompletionCommitted")]
    public async Task Fresh_additive_backout_retains_bound_fresh_fence_and_exact_verified_empty_state(string stop)
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostRestoreSchemaMigrationTests.PreAdminCut(f); var target = OutpostRestoreModeTests.Target(f);
        var procedure = new OutpostRestoreProcedure(); var request = await OutpostRestoreModeTests.Request(procedure, archive, target.Data, target.Anchor);
        procedure.Checkpoint = p => { if (p == stop) throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => procedure.ExecuteAsync(request, default));
        FreshAnchorFence before; using (var held = await target.Store.AcquireAsync(default)) before = Assert.IsType<FreshAnchorFence>(target.Store.ReadEnvelope(held));
        var selection = procedure.InspectBackout(target.Data, target.Anchor, request.RestoreId);
        selection = selection with { ConfirmationDigest = OutpostRestoreJournalCodec.BackoutDigest(selection) };
        var result = await new OutpostRestoreProcedure().BackoutAsync(selection, default); Assert.True(result.Outcome == RestoreOutcome.BackedOut, result.Reason);
        Assert.False(File.Exists(Path.Combine(target.Data, "rimlink.db")));
        using (var held = await target.Store.AcquireAsync(default)) Assert.Equal(before with { State = FreshFenceState.BackedOutEmpty }, target.Store.ReadEnvelope(held));
        var repeated = await new OutpostRestoreProcedure().BackoutAsync(selection, default); Assert.Equal(RestoreOutcome.BackedOut, repeated.Outcome);
    }

    [Theory]
    [InlineData(false, "AfterBackoutEffect:rimlink.db")]
    [InlineData(false, "AfterBackoutPublication")]
    [InlineData(true, "AfterBackoutEffect:rimlink.db")]
    [InlineData(true, "AfterBackoutPublication")]
    public async Task Partial_bound_backout_only_finishes_the_same_confirmed_action(bool fresh, string stop)
    {
        using var f = await LifecycleFixture.Create(); var h = f.H.F.H.H;
        string archive = await OutpostRestoreSchemaMigrationTests.PreAdminCut(f); var procedure = new OutpostRestoreProcedure();
        var request = fresh ? await RequestFresh() : await OutpostRestoreTests.Request(f, procedure, archive);
        if (!fresh) f.Host.Dispose();
        procedure.Checkpoint = p => { if (p == "AfterSchemaMigration") throw new Stop(); };
        await Assert.ThrowsAsync<Stop>(() => procedure.ExecuteAsync(request, default));
        string data = request.Inspection.DataDirectory, external = request.Inspection.AnchorDirectory;
        var selected = procedure.InspectBackout(data, external, request.RestoreId);
        selected = selected with { ConfirmationDigest = OutpostRestoreJournalCodec.BackoutDigest(selected) };
        var backout = new OutpostRestoreProcedure { Checkpoint = p => { if (p == stop) throw new Stop(); } };
        await Assert.ThrowsAsync<Stop>(() => backout.BackoutAsync(selected, default));
        Assert.NotEqual(RestoreOutcome.Completed, (await new OutpostRestoreProcedure().ResumeAsync(data, external, request.RestoreId, default)).Outcome);
        var result = await new OutpostRestoreProcedure().BackoutAsync(selected, default); Assert.True(result.Outcome == RestoreOutcome.BackedOut, result.Reason);
        if (fresh) Assert.False(File.Exists(Path.Combine(data, "rimlink.db")));
        else
        {
            h.Db.ChangeTracker.Clear(); Assert.Empty(await h.Db.OutpostTransitions.Where(t => t.Id == request.RestoreId).ToArrayAsync());
            Assert.Equal(request.Inspection.Destination.External!.Committed.Id, result.HeadId);
        }
        async Task<RestoreRequest> RequestFresh()
        { var target = OutpostRestoreModeTests.Target(f); return await OutpostRestoreModeTests.Request(procedure, archive, target.Data, target.Anchor); }
    }

    private sealed class Stop : Exception;
}
