using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public class OutpostRestoreClockTests
{
    [Theory]
    [InlineData("")]
    [InlineData("AfterMainCommitted")]
    [InlineData("AfterCompletionCommitted")]
    public async Task Backup_to_restore_interval_produces_zero_including_interrupted_completion(string stop)
    {
        using var f = await LifecycleFixture.Create();
        f.H.F.Time.Advance(TimeSpan.FromMinutes(31.5));
        string archive = await OutpostBackupTests.Create(f);
        var cut = await f.H.Row();
        Assert.Equal((10, "1", "2"), (cut.Stock, cut.FractionNumerator, cut.FractionDenominator));
        f.H.F.Time.Advance(TimeSpan.FromDays(5));
        var procedure = new OutpostRestoreProcedure(f.H.F.Time)
        { Checkpoint = point => { if (point == stop) throw new StopRestore(); } };
        var request = await OutpostRestoreTests.Request(f, procedure, archive);
        f.Host.Dispose();
        RestoreResult outcome;
        string? immutableRoot = null;
        if (stop.Length != 0)
        {
            await Assert.ThrowsAsync<StopRestore>(() => procedure.ExecuteAsync(request, default));
            immutableRoot = JsonSerializer.Serialize(await f.H.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Kind == "BulkRestore"));
            Assert.Equal(OutpostStartupState.Maintenance, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
            f.H.F.Time.Advance(TimeSpan.FromDays(7));
            outcome = await new OutpostRestoreProcedure(f.H.F.Time).ResumeAsync(request.Inspection.DataDirectory, request.Inspection.AnchorDirectory, request.RestoreId, default);
        }
        else outcome = await procedure.ExecuteAsync(request, default);
        Assert.Equal((RestoreOutcome.Completed, ""), (outcome.Outcome, outcome.Reason));
        var restored = await f.H.Row();
        Assert.Equal((10, "1", "2"), (restored.Stock, restored.FractionNumerator, restored.FractionDenominator));
        Assert.Equal(f.H.F.Time.Now.UtcDateTime, restored.CheckpointUtc);
        Assert.Equal(1, await f.H.Db.OutpostTransitions.CountAsync(t => t.Kind == "BulkRestore"));
        if (immutableRoot is not null)
            Assert.Equal(immutableRoot, JsonSerializer.Serialize(await f.H.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Kind == "BulkRestore")));
        var current = f.H.Scope.Clone(); current.RestoreBoundary = request.TargetBoundary;
        f.H.F.Time.Advance(TimeSpan.FromHours(1));
        await f.H.Service.StateAsync(1, new OutpostStateQuery { Scope = current }, default);
        var afterRestart = await f.H.Row();
        Assert.Equal((30, "1", "2"), (afterRestart.Stock, afterRestart.FractionNumerator, afterRestart.FractionDenominator));
    }

    [Fact]
    public async Task Published_completion_is_not_rebased_when_operator_resumes_later()
    {
        using var f = await LifecycleFixture.Create();
        string archive = await OutpostBackupTests.Create(f);
        f.H.F.Time.Advance(TimeSpan.FromDays(5));
        var procedure = new OutpostRestoreProcedure(f.H.F.Time)
        { Checkpoint = point => { if (point == "AfterRestorePublication") throw new StopRestore(); } };
        var request = await OutpostRestoreTests.Request(f, procedure, archive);
        f.Host.Dispose();
        await Assert.ThrowsAsync<StopRestore>(() => procedure.ExecuteAsync(request, default));
        var published = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var checkpoint = await f.H.Row();
        f.H.F.Time.Advance(TimeSpan.FromHours(1));
        var outcome = await new OutpostRestoreProcedure(f.H.F.Time).ResumeAsync(request.Inspection.DataDirectory, request.Inspection.AnchorDirectory, request.RestoreId, default);
        Assert.Equal(RestoreOutcome.Completed, outcome.Outcome);
        Assert.Equal(published.HeadId, outcome.HeadId);
        Assert.Equal(checkpoint.CheckpointUtc, (await f.H.Row()).CheckpointUtc);
        var current = f.H.Scope.Clone(); current.RestoreBoundary = request.TargetBoundary;
        await f.H.Service.StateAsync(1, new OutpostStateQuery { Scope = current }, default);
        Assert.Equal(20, (await f.H.Row()).Stock);
        var later = await new OutpostRestoreProcedure(f.H.F.Time).ResumeAsync(request.Inspection.DataDirectory, request.Inspection.AnchorDirectory, request.RestoreId, default);
        Assert.Equal(RestoreOutcome.Completed, later.Outcome);
        Assert.Equal(outcome.HeadId, later.HeadId);
        Assert.Equal(outcome.HeadDigest, later.HeadDigest);
        Assert.Equal(20, (await f.H.Row()).Stock);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backward_clock_never_lowers_the_persisted_checkpoint(bool afterCompletion)
    {
        using var f = await LifecycleFixture.Create();
        f.H.F.Time.Advance(TimeSpan.FromMinutes(31.5));
        string archive = await OutpostBackupTests.Create(f);
        var cut = await f.H.Row();
        var initial = new OutpostRestoreProcedure(f.H.F.Time)
        { Checkpoint = point => { if (afterCompletion && point == "AfterCompletionCommitted") throw new StopRestore(); } };
        var request = await OutpostRestoreTests.Request(f, initial, archive);
        f.Host.Dispose();
        if (afterCompletion)
        {
            f.H.F.Time.Advance(TimeSpan.FromDays(5));
            await Assert.ThrowsAsync<StopRestore>(() => initial.ExecuteAsync(request, default));
            cut = await f.H.Row();
        }
        f.H.F.Time.Now = f.H.F.Time.Now.AddDays(-7);
        var procedure = new OutpostRestoreProcedure(f.H.F.Time);
        var outcome = afterCompletion
            ? await procedure.ResumeAsync(request.Inspection.DataDirectory, request.Inspection.AnchorDirectory, request.RestoreId, default)
            : await procedure.ExecuteAsync(request, default);
        Assert.Equal((RestoreOutcome.Completed, ""), (outcome.Outcome, outcome.Reason));
        var restored = await f.H.Row();
        Assert.Equal((10, "1", "2"), (restored.Stock, restored.FractionNumerator, restored.FractionDenominator));
        Assert.Equal((cut.CheckpointUtc, cut.ClockProcessId, cut.ClockTimestamp, cut.ClockFrequency),
            (restored.CheckpointUtc, restored.ClockProcessId, restored.ClockTimestamp, restored.ClockFrequency));
    }

    [Fact]
    public async Task Repeated_unpublished_completions_keep_all_prior_steps_immutable_and_exclude_every_gap()
    {
        using var f = await LifecycleFixture.Create();
        f.H.F.Time.Advance(TimeSpan.FromMinutes(31.5));
        string archive = await OutpostBackupTests.Create(f);
        f.H.F.Time.Advance(TimeSpan.FromDays(5));
        var procedure = new OutpostRestoreProcedure(f.H.F.Time)
        { Checkpoint = point => { if (point == "AfterCompletionCommitted") throw new StopRestore(); } };
        var request = await OutpostRestoreTests.Request(f, procedure, archive);
        f.Host.Dispose();
        await Assert.ThrowsAsync<StopRestore>(() => procedure.ExecuteAsync(request, default));
        var retained = await f.H.Db.OutpostTransitions.AsNoTracking().Where(t => t.Kind == "BulkRestore" || t.Kind == "RestoreCompletion").OrderBy(t => t.Id).ToArrayAsync();
        f.H.F.Time.Advance(TimeSpan.FromDays(7));
        await Assert.ThrowsAsync<StopRestore>(() => procedure.ResumeAsync(request.Inspection.DataDirectory, request.Inspection.AnchorDirectory, request.RestoreId, default));
        Assert.Equal(2, await f.H.Db.OutpostTransitions.CountAsync(t => t.Kind == "RestoreCompletion"));
        foreach (var original in retained)
            Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await f.H.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == original.Id)));
        Assert.Equal(OutpostStartupState.Maintenance, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
        f.H.F.Time.Advance(TimeSpan.FromDays(11));
        var outcome = await new OutpostRestoreProcedure(f.H.F.Time).ResumeAsync(request.Inspection.DataDirectory, request.Inspection.AnchorDirectory, request.RestoreId, default);
        Assert.Equal((RestoreOutcome.Completed, ""), (outcome.Outcome, outcome.Reason));
        Assert.Equal(1, await f.H.Db.OutpostTransitions.CountAsync(t => t.Kind == "BulkRestore"));
        Assert.Equal(3, await f.H.Db.OutpostTransitions.CountAsync(t => t.Kind == "RestoreCompletion"));
        var restored = await f.H.Row();
        Assert.Equal(f.H.F.Time.Now.UtcDateTime, restored.CheckpointUtc);
        Assert.Equal((10, "1", "2"), (restored.Stock, restored.FractionNumerator, restored.FractionDenominator));
        var current = f.H.Scope.Clone(); current.RestoreBoundary = request.TargetBoundary;
        f.H.F.Time.Advance(TimeSpan.FromHours(1));
        await f.H.Service.StateAsync(1, new OutpostStateQuery { Scope = current }, default);
        Assert.Equal((30, "1", "2"), ((await f.H.Row()).Stock, (await f.H.Row()).FractionNumerator, (await f.H.Row()).FractionDenominator));
    }

    private sealed class StopRestore : Exception;
}
