using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Xunit;
using BulkOutcome = Rimlink.Server.Backup.RestoreOutcome;

namespace Rimlink.Server.Tests.Backup;

public class OutpostRestoreLifecycleTests
{
    [Fact]
    public async Task Archived_cut_stays_paused_through_repeated_completions()
    {
        using var f = await LifecycleFixture.Create();
        f.H.F.Time.Advance(TimeSpan.FromMinutes(31.5));
        await f.Archive();
        var archived = await f.H.Row();
        string backup = await OutpostBackupTests.Create(f);
        f.H.F.Time.Advance(TimeSpan.FromDays(5));
        var procedure = new OutpostRestoreProcedure(f.H.F.Time)
        { Checkpoint = point => { if (point == "AfterCompletionCommitted") throw new StopRestore(); } };
        var request = await OutpostRestoreTests.Request(f, procedure, backup);
        f.Host.Dispose();
        await Assert.ThrowsAsync<StopRestore>(() => procedure.ExecuteAsync(request, default));
        f.H.F.Time.Advance(TimeSpan.FromDays(7));
        var result = await new OutpostRestoreProcedure(f.H.F.Time).ResumeAsync(request.Inspection.DataDirectory,
            request.Inspection.AnchorDirectory, request.RestoreId, default);
        Assert.Equal((BulkOutcome.Completed, ""), (result.Outcome, result.Reason));
        var restored = await f.H.Row();
        Assert.Equal(("Archived", 10, "1", "2", true),
            (restored.State, restored.Stock, restored.FractionNumerator, restored.FractionDenominator, restored.OccupiesSlot));
        Assert.Equal((archived.CheckpointUtc, archived.ClockProcessId, archived.ClockTimestamp, archived.ClockFrequency),
            (restored.CheckpointUtc, restored.ClockProcessId, restored.ClockTimestamp, restored.ClockFrequency));
        Assert.Equal((archived.Id, archived.Tile, archived.SaveGeneration), (restored.Id, restored.Tile, restored.SaveGeneration));
        Assert.Equal(request.TargetBoundary, restored.RestoreBoundary);
        Assert.Empty(await f.H.Db.SaveVersions.AsNoTracking().ToArrayAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    [Fact]
    public async Task NoActiveWorld_cut_remains_admin_only_and_retains_all_retired_evidence()
    {
        using var f = await LifecycleFixture.Create();
        await OutpostLifecycleTests.CloseAndReceive(f);
        using (var scope = f.Host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AdminOperations>().RecreateWorld(Actor.Api);
        var custody = await f.H.Db.PawnCustody.AsNoTracking().OrderBy(c => c.Id).ToArrayAsync();
        var links = await f.H.Db.OutpostSnapshotLinks.AsNoTracking().OrderBy(l => l.SnapshotId).ToArrayAsync();
        var owners = await f.H.Db.Players.AsNoTracking().Select(p => new { p.Id, p.SaveGeneration, p.SaveWriteEpoch }).ToArrayAsync();
        string backup = await OutpostBackupTests.Create(f);
        var procedure = new OutpostRestoreProcedure(f.H.F.Time);
        var request = await OutpostRestoreTests.Request(f, procedure, backup);
        f.Host.Dispose();
        var result = await procedure.ExecuteAsync(request, default);
        Assert.Equal((BulkOutcome.Completed, ""), (result.Outcome, result.Reason));
        Assert.Empty(await f.H.Db.World.AsNoTracking().ToArrayAsync());
        Assert.Equal(JsonSerializer.Serialize(owners), JsonSerializer.Serialize(await f.H.Db.Players.AsNoTracking().Select(p => new { p.Id, p.SaveGeneration, p.SaveWriteEpoch }).ToArrayAsync()));
        Assert.Equal(JsonSerializer.Serialize(custody), JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().OrderBy(c => c.Id).ToArrayAsync()));
        Assert.Equal(JsonSerializer.Serialize(links), JsonSerializer.Serialize(await f.H.Db.OutpostSnapshotLinks.AsNoTracking().OrderBy(l => l.SnapshotId).ToArrayAsync()));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.NoActiveWorld), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    [Fact]
    public async Task Restored_NoActiveWorld_accepts_one_typed_successor_without_reinitializing_history()
    {
        using var f = await LifecycleFixture.Create();
        await OutpostLifecycleTests.CloseAndReceive(f);
        using (var scope = f.Host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AdminOperations>().RecreateWorld(Actor.Api);
        var links = await f.H.Db.OutpostSnapshotLinks.AsNoTracking().OrderBy(l => l.SnapshotId).ToArrayAsync();
        string backup = await OutpostBackupTests.Create(f);
        var procedure = new OutpostRestoreProcedure(f.H.F.Time);
        var request = await OutpostRestoreTests.Request(f, procedure, backup);
        f.Host.Dispose();
        Assert.Equal(BulkOutcome.Completed, (await procedure.ExecuteAsync(request, default)).Outcome);
        var restored = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var shared = f.H.F.H.H;
        using var host = Rimlink.Server.Tests.Support.HygieneFixtures.HygieneServer().WithoutWorld()
            .WithDataDir(shared.Data.Root).KeepDataDir()
            .WithSetting("Outposts:AnchorDirectory", Path.Combine(shared.Root, "external"))
            .WithSetting("Outposts:StorageContract", "sqlite-local-v1")
            .WithServices(s => { s.AddSingleton(shared.Files); s.AddSingleton(shared.Owners);
                s.AddSingleton(shared.Anchor); s.AddSingleton<TimeProvider>(f.H.F.Time); });
        using var next = host.Services.CreateScope();
        var world = await next.ServiceProvider.GetRequiredService<Rimlink.Server.Application.World.WorldService>()
            .Create(new Rimlink.Protocol.V1.WorldParams { Seed = "Restored successor" }, [7, 42], new Session
            { Token = "fixture", AccountId = 1, PlayerId = 1, Role = Rimlink.Server.Storage.Entities.Role.Admin,
                Ip = "127.0.0.1", PlayerName = "Nova" });
        var bound = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        Assert.Equal(world.InfluenceInstanceId, bound.WorldId);
        Assert.NotEqual(restored.WorldId, bound.WorldId);
        Assert.Equal(restored.InstallationId, bound.InstallationId);
        Assert.Equal(request.TargetBoundary, bound.RestoreBoundary);
        Assert.Equal(1, await f.H.Db.OutpostTransitions.CountAsync(t => t.Kind == "Initialize"));
        Assert.Equal(1, await f.H.Db.OutpostTransitions.CountAsync(t => t.Kind == "BulkRestore"));
        Assert.Equal(1, await f.H.Db.OutpostTransitions.CountAsync(t => t.Kind == "WorldBind"));
        Assert.Equal(JsonSerializer.Serialize(links), JsonSerializer.Serialize(
            await f.H.Db.OutpostSnapshotLinks.AsNoTracking().OrderBy(l => l.SnapshotId).ToArrayAsync()));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await shared.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("epoch")]
    [InlineData("canonical")]
    [InlineData("canonical-ambiguous")]
    [InlineData("world-seed")]
    [InlineData("world-params")]
    public async Task Current_owner_projection_cannot_be_changed_without_a_typed_descendant(string component)
    {
        using var f = await LifecycleFixture.Create();
        string backup = await OutpostBackupTests.Create(f);
        var procedure = new OutpostRestoreProcedure(f.H.F.Time);
        var request = await OutpostRestoreTests.Request(f, procedure, backup);
        f.Host.Dispose();
        Assert.Equal(BulkOutcome.Completed, (await procedure.ExecuteAsync(request, default)).Outcome);
        if (component == "generation")
            await f.H.Db.Players.ExecuteUpdateAsync(s => s.SetProperty(p => p.SaveGeneration, p => p.SaveGeneration + 1));
        else if (component == "epoch")
            await f.H.Db.Players.ExecuteUpdateAsync(s => s.SetProperty(p => p.SaveWriteEpoch, p => p.SaveWriteEpoch + 1));
        else if (component == "canonical-ambiguous")
        {
            Assert.True(await f.H.Db.SaveVersions.CountAsync() > 1);
            await f.H.Db.SaveVersions.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsCurrent, true));
        }
        else if (component == "world-seed")
            await f.H.Db.World.ExecuteUpdateAsync(s => s.SetProperty(p => p.Seed, "changed-after-restore"));
        else if (component == "world-params")
            await f.H.Db.World.ExecuteUpdateAsync(s => s.SetProperty(p => p.Params, new byte[] { 1, 2, 3 }));
        else
            await f.H.Db.SaveVersions.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsCurrent, false));
        string cut = await f.Cut();
        var outcome = await f.H.F.H.H.Authority.OpenAsync(default);
        Assert.Equal(OutpostStartupState.Maintenance, outcome.State);
        Assert.Equal(cut, await f.Cut());
    }

    private sealed class StopRestore : Exception;
}
