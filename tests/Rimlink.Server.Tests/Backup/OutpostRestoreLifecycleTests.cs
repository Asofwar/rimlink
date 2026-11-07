using System.Text.Json;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.World;
using Rimlink.Server.Backup;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Outposts;
using Xunit;
using BulkOutcome = Rimlink.Server.Backup.RestoreOutcome;

namespace Rimlink.Server.Tests.Backup;

public class OutpostRestoreLifecycleTests
{
    [Theory]
    [InlineData(true, false, null)]
    [InlineData(false, false, null)]
    [InlineData(true, true, null)]
    [InlineData(true, false, "after")]
    [InlineData(true, false, "before")]
    [InlineData(true, false, "before-corrupt")]
    public async Task Restored_legacy_owner_can_upload_a_fresh_save_and_remain_ready(bool saveAtCut, bool firstProtected, string? archivePhase)
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true);
        var shared = f.H.F.H.H;
        f.H.Db.Players.Add(new Rimlink.Server.Storage.Entities.Player
            { Id = 2, Name = "Legacy", Account = new() { Login = "legacy-after-bulk-restore" } });
        if (archivePhase is not null)
            f.H.Db.WorldObjects.Add(new Rimlink.Server.Storage.Entities.WorldObject
                { OwnerPlayerId = 2, Type = WorldObjectTypes.Settlement, Name = "Legacy Home", Tile = 34, CreatedAt = f.H.F.Time.GetUtcNow().UtcDateTime });
        await f.H.Db.SaveChangesAsync();
        var seed = await f.H.Db.World.Select(w => w.Seed).SingleAsync();
        async Task<Rimlink.Server.Storage.Entities.SaveVersion> Upload(ServerFixture host, int tick, OutpostRecoveryData? outposts = null)
        {
            var xml = XDocument.Parse(Encoding.UTF8.GetString(SaveFixtures.Decompress(RecoveryFixtures.Save(tick, outposts: outposts))));
            xml.Root!.Element("game")!.Element("world")!.Element("info")!.Element("seedString")!.Value = seed;
            byte[] packed = SaveFixtures.Compress(Encoding.UTF8.GetBytes(xml.ToString()));
            async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks() { yield return packed; await Task.CompletedTask; }
            using var services = host.Services.CreateScope();
            return await services.ServiceProvider.GetRequiredService<SaveStore>().Store(2, RecoveryFixtures.Header(packed, tick), Chunks(), default);
        }

        var cutSave = saveAtCut ? await Upload(f.Host, 1000) : null;
        long? cutArchive = null;
        if (archivePhase is "before" or "before-corrupt")
        {
            using var services = f.Host.Services.CreateScope();
            var ops = services.ServiceProvider.GetRequiredService<AdminOperations>();
            cutArchive = Assert.IsType<ArchiveOutcome.Archived>(await ops.ArchiveColony(Actor.Api, 2)).ArchiveId;
            Assert.False(await f.H.Db.OutpostLifecycleOperations.AnyAsync(o => o.Kind == "Archive"));
        }
        string archive = await OutpostBackupTests.Create(f);
        var procedure = new OutpostRestoreProcedure(f.H.F.Time);
        var request = await OutpostRestoreTests.Request(f, procedure, archive);
        await f.Host.DisposeAsync();
        var restored = await procedure.ExecuteAsync(request, default);
        Assert.Equal((BulkOutcome.Completed, ""), (restored.Outcome, restored.Reason));

        using var host = HygieneFixtures.HygieneServer().WithoutWorld().WithDataDir(shared.Data.Root).KeepDataDir()
            .WithSetting("Outposts:AnchorDirectory", Path.GetDirectoryName(shared.Anchor.DatabasePath)!)
            .WithSetting("Outposts:StorageContract", "sqlite-local-v1").WithOutpostUnitQualification()
            .WithServices(s => { s.AddSingleton(shared.Files); s.AddSingleton(shared.Owners);
                s.AddSingleton(shared.Anchor); s.AddSingleton<TimeProvider>(f.H.F.Time); });
        if (cutArchive is { } archivedAtCut)
        {
            Assert.Empty(await f.H.Db.SaveVersions.Where(v => v.PlayerId == 2).ToListAsync());
            Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await shared.Authority.OpenAsync(default));
            using var services = host.Services.CreateScope();
            var ops = services.ServiceProvider.GetRequiredService<AdminOperations>();
            if (archivePhase == "before-corrupt")
            {
                var original = await f.H.Db.ArchivedColonies.AsNoTracking().SingleAsync(a => a.Id == archivedAtCut);
                await File.AppendAllTextAsync(Path.Combine(shared.Data.Root, original.Dir, SaveArchive.ManifestFile), " ");
                var refusal = await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => ops.RestoreColony(Actor.Api, archivedAtCut));
                Assert.Equal("lifecycle-restore-archive-bytes-mismatch", refusal.Message);
                Assert.Equal(restored.HeadId, await f.H.Db.OutpostAuthority.Select(a => a.HeadId).SingleAsync());
                Assert.Empty(await f.H.Db.SaveVersions.Where(v => v.PlayerId == 2).ToListAsync());
                Assert.False(await f.H.Db.OutpostLifecycleOperations.AnyAsync(o => o.Kind == "Restore"));
                return;
            }
            Assert.IsType<Rimlink.Server.Application.World.RestoreOutcome.Restored>(await ops.RestoreColony(Actor.Api, archivedAtCut));
            var reloaded = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 2 && v.IsCurrent);
            Assert.Equal(cutSave!.SnapshotId, reloaded.SnapshotId);
            Assert.Equal(cutSave.RawSha256, reloaded.RawSha256);
            Assert.Equal(2, (await Upload(host, 2000)).Number);
            Assert.False(await f.H.Db.OutpostSnapshotLinks.AnyAsync(l => l.PlayerId == 2));
            Assert.False(await f.H.Db.OutpostLifecycleOperations.AnyAsync(o => o.Kind == "Archive"));
            Assert.Equal(1, await f.H.Db.OutpostLifecycleOperations.CountAsync(o => o.Kind == "Restore" && o.Phase == "Completed"));
            Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await shared.Authority.OpenAsync(default));
            return;
        }
        var current = await Upload(host, 2000);
        Assert.Equal(saveAtCut ? 2 : 1, current.Number);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await shared.Authority.OpenAsync(default));
        Assert.Equal(restored.HeadId, await f.H.Db.OutpostAuthority.Select(a => a.HeadId).SingleAsync());
        Assert.False(await f.H.Db.OutpostSnapshotLinks.AnyAsync(l => l.PlayerId == 2));
        using (var services = host.Services.CreateScope())
        {
            var download = await services.ServiceProvider.GetRequiredService<SaveStore>().OpenCurrent(2);
            Assert.NotNull(download);
            var downloaded = download.Value;
            await using (downloaded.Content)
            {
                Assert.Equal(current.SnapshotId, downloaded.Version.SnapshotId);
                Assert.Null(downloaded.RestoreBridge);
            }
        }
        f.H.F.Time.Advance(TimeSpan.FromSeconds(1));
        var backup = await host.Services.GetRequiredService<BackupService>().CreateNow();
        Assert.True(File.Exists(Path.Combine(shared.Data.Backups, backup.Name)));

        if (archivePhase == "after")
        {
            using var services = host.Services.CreateScope();
            var ops = services.ServiceProvider.GetRequiredService<AdminOperations>();
            var archived = Assert.IsType<ArchiveOutcome.Archived>(await ops.ArchiveColony(Actor.Api, 2));
            Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await shared.Authority.OpenAsync(default));
            Assert.Empty(await f.H.Db.SaveVersions.Where(v => v.PlayerId == 2).ToListAsync());
            f.H.F.Time.Advance(TimeSpan.FromSeconds(1));
            var archivedBackup = await host.Services.GetRequiredService<BackupService>().CreateNow();
            Assert.True(File.Exists(Path.Combine(shared.Data.Backups, archivedBackup.Name)));
            Assert.IsType<Rimlink.Server.Application.World.RestoreOutcome.Restored>(await ops.RestoreColony(Actor.Api, archived.ArchiveId));
            var reloaded = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 2 && v.IsCurrent);
            Assert.Equal(current.SnapshotId, reloaded.SnapshotId);
            Assert.Equal(current.RawSha256, reloaded.RawSha256);
            var successor = await Upload(host, 3000);
            Assert.Equal(current.Number + 1, successor.Number);
            Assert.False(await f.H.Db.OutpostSnapshotLinks.AnyAsync(l => l.PlayerId == 2));
            Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await shared.Authority.OpenAsync(default));
            Assert.Equal(1, await f.H.Db.OutpostLifecycleOperations.CountAsync(x => x.Kind == "Archive" && x.Phase == "Completed"));
            Assert.Equal(1, await f.H.Db.OutpostLifecycleOperations.CountAsync(x => x.Kind == "Restore" && x.Phase == "Completed"));
            return;
        }

        if (firstProtected)
        {
            var metadata = new OutpostSaveMetadata { SchemaVersion = OutpostRecoveryData.CurrentVersion, Scope = new()
                { WorldInstanceId = request.Inspection.Manifest.Authority.WorldId, OwnerPlayerId = 2, RestoreBoundary = request.TargetBoundary } };
            var protectedSave = await Upload(host, 3000, new() { Metadata = metadata });
            Assert.Equal(protectedSave.SnapshotId, (await f.H.Db.OutpostSnapshotLinks.SingleAsync(l => l.PlayerId == 2)).SnapshotId);
            Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await shared.Authority.OpenAsync(default));
            return;
        }

        await f.H.Db.SaveVersions.Where(v => v.Id == current.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.SnapshotId, Guid.NewGuid().ToString("N")));
        Assert.Equal(OutpostStartupState.Maintenance, (await shared.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Archived_cut_stays_paused_through_repeated_completions()
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true);
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
        using var f = await LifecycleFixture.Create(runtimeQualified: true);
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
        using var f = await LifecycleFixture.Create(runtimeQualified: true);
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
            .WithOutpostUnitQualification()
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
        using var f = await LifecycleFixture.Create(runtimeQualified: true);
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
