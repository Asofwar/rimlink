using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.World;
using Xunit;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Tests.Support;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostWorldBindingTests
{
    [Fact]
    public async Task NoActiveWorld_starts_host_but_blocks_gameplay_and_keeps_original_evidence()
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); await OutpostLifecycleTests.CloseAndReceive(f);
        var before = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var links = await f.H.Db.OutpostSnapshotLinks.AsNoTracking().ToArrayAsync();
        using (var scope = f.Host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AdminOperations>().RecreateWorld(Actor.Api);
        Assert.Empty(await f.H.Db.World.AsNoTracking().ToListAsync());
        Assert.Equal(1, (await f.H.Db.Players.AsNoTracking().SingleAsync()).SaveGeneration);
        var after = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        Assert.Equal(before.InstallationId, after.InstallationId); Assert.Equal(before.WorldId, after.WorldId);
        Assert.Equal(before.RestoreBoundary, after.RestoreBoundary); Assert.NotEqual(before.HeadId, after.HeadId);
        Assert.Equal("NoActiveWorld", (await f.H.F.H.H.Authority.OpenAsync(default)).State.ToString());
        await OutpostFeature.CheckStartupAsync(f.Host.Services, default);
        await using (var lease = await f.H.F.H.H.Authority.AcquireAsync([1], default))
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using var tx = await f.H.F.H.H.Authority.BeginHeldAsync(lease,
                    new(Guid.NewGuid().ToString("D"), after.WorldId, 1, 1, after.RestoreBoundary, "Production", "test",
                        new(after.HeadId, ByteString.CopyFrom(after.HeadDigest)), ByteString.Empty), default);
            });
        await OutpostLifecycleTests.AssertOriginalEvidence(f, links);
        var operation = Assert.Single(await f.H.Db.OutpostLifecycleOperations.AsNoTracking().ToListAsync());
        Assert.Equal("Completed", operation.Phase);
        Assert.Equal("NoActiveWorld", OutpostLifecycleEvidence.Decode<LifecycleResult>(operation.Result).Outcome);
    }

    [Theory]
    [InlineData("Prepared", false)] [InlineData("EvidenceRetained", false)]
    [InlineData("Destructive", false)] [InlineData("SourceCleanup", false)] [InlineData("Completed", true)]
    public async Task Crash_each_recreate_phase_retains_evidence_and_blocks_unfinished_startup(string phase, bool complete)
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); await OutpostLifecycleTests.CloseAndReceive(f);
        var links = await f.H.Db.OutpostSnapshotLinks.AsNoTracking().ToArrayAsync();
        using var scope = f.Host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<OutpostLifecycle>().Checkpoint = at =>
        { if (at == phase) throw new IOException("simulated retirement interruption: " + phase); };
        await Assert.ThrowsAsync<IOException>(() => scope.ServiceProvider.GetRequiredService<AdminOperations>().RecreateWorld(Actor.Api));
        Assert.Equal(complete ? "NoActiveWorld" : "Maintenance", (await f.H.F.H.H.Authority.OpenAsync(default)).State.ToString());
        Assert.Equal(phase is "Destructive" or "SourceCleanup" or "Completed", !await f.H.Db.World.AnyAsync());
        await OutpostLifecycleTests.AssertOriginalEvidence(f, links);
    }

    [Fact]
    public async Task WorldBind_continues_installation_chain_once_without_reinitialization()
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); await OutpostLifecycleTests.CloseAndReceive(f);
        var links = await f.H.Db.OutpostSnapshotLinks.AsNoTracking().ToArrayAsync();
        using var scope = f.Host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AdminOperations>().RecreateWorld(Actor.Api);
        var retired = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        int count = await f.H.Db.OutpostTransitions.CountAsync();
        var parameters = new WorldParams { Seed = "Successor", PlanetCoverage = 0.3f, Rainfall = 1, Temperature = 1, Population = 1 };
        var service = scope.ServiceProvider.GetRequiredService<WorldService>();
        var world = await service.Create(parameters, [42, 7, 42], Admin());
        var bound = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        Assert.Equal(world.InfluenceInstanceId, bound.WorldId); Assert.NotEqual(retired.WorldId, bound.WorldId);
        Assert.Equal(retired.InstallationId, bound.InstallationId); Assert.Equal(retired.RestoreBoundary, bound.RestoreBoundary);
        Assert.NotEqual(retired.HeadId, bound.HeadId); Assert.Equal(count + 1, await f.H.Db.OutpostTransitions.CountAsync());
        var transition = await f.H.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == bound.TransitionId);
        Assert.Equal("WorldBind", transition.Kind); Assert.Equal(retired.WorldId, transition.WorldId);
        Assert.Equal(parameters.ToByteArray(), world.Params); Assert.Equal("7,42", world.NpcTiles);
        await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() => service.Create(parameters, [7, 42], Admin()));
        Assert.Equal(count + 1, await f.H.Db.OutpostTransitions.CountAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
        await OutpostLifecycleTests.AssertOriginalEvidence(f, links);
    }

    [Theory]
    [InlineData("BeforePending", false)] [InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)] [InlineData("AfterExternalCommit", true)]
    public async Task WorldBind_pending_reconciles_only_exact_old_or_new_binding(string phase, bool committed)
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); await OutpostLifecycleTests.CloseAndReceive(f);
        using var scope = f.Host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AdminOperations>().RecreateWorld(Actor.Api);
        var retired = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        int count = await f.H.Db.OutpostTransitions.CountAsync();
        var authority = scope.ServiceProvider.GetRequiredService<OutpostAuthority>();
        authority.CommitCheckpoint = at => { if (at == phase) throw new IOException("simulated WorldBind interruption: " + phase); };
        await Assert.ThrowsAsync<IOException>(() => scope.ServiceProvider.GetRequiredService<WorldService>()
            .Create(new WorldParams { Seed = "Successor" }, [7], Admin()));
        authority.CommitCheckpoint = null;
        Assert.Equal(committed ? 1 : 0, await f.H.Db.World.CountAsync());
        var result = await f.H.F.H.H.Authority.OpenAsync(default);
        Assert.Equal(new OutpostStartupResult(committed ? OutpostStartupState.Ready : OutpostStartupState.NoActiveWorld), result);
        var head = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        Assert.Equal(retired.InstallationId, head.InstallationId); Assert.Equal(retired.RestoreBoundary, head.RestoreBoundary);
        Assert.Equal(count + (committed ? 1 : 0), await f.H.Db.OutpostTransitions.CountAsync());
        using (var held = await f.H.F.H.H.Anchor.AcquireAsync(default))
        {
            var anchor = f.H.F.H.H.Anchor.Read(held)!; Assert.Null(anchor.Pending);
            Assert.Equal(head.WorldId, anchor.WorldId); Assert.Equal(head.HeadId, anchor.Committed.Id);
        }
        if (!committed)
        {
            var next = await scope.ServiceProvider.GetRequiredService<WorldService>().Create(new WorldParams { Seed = "Retry" }, [], Admin());
            Assert.Equal(next.InfluenceInstanceId, (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).WorldId);
        }
    }

    [Theory]
    [InlineData("configuration")] [InlineData("world-id")] [InlineData("authority-world")]
    public async Task WorldBind_pending_does_not_clear_on_actual_world_projection_mismatch(string tamper)
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); await OutpostLifecycleTests.CloseAndReceive(f);
        using var scope = f.Host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AdminOperations>().RecreateWorld(Actor.Api);
        scope.ServiceProvider.GetRequiredService<OutpostAuthority>().CommitCheckpoint = at =>
        { if (at == "AfterDomainCommit") throw new IOException("simulated pending WorldBind"); };
        await Assert.ThrowsAsync<IOException>(() => scope.ServiceProvider.GetRequiredService<WorldService>()
            .Create(new WorldParams { Seed = "Successor" }, [7], Admin()));
        AnchorRecord pending;
        using (var held = await f.H.F.H.H.Anchor.AcquireAsync(default)) pending = f.H.F.H.H.Anchor.Read(held)!;
        Assert.NotNull(pending.Pending);
        if (tamper == "configuration") await f.H.Db.World.ExecuteUpdateAsync(s => s.SetProperty(w => w.Seed, "Other"));
        else if (tamper == "world-id") await f.H.Db.World.ExecuteUpdateAsync(s => s.SetProperty(w => w.InfluenceInstanceId, Guid.NewGuid().ToString("N")));
        else await f.H.Db.OutpostAuthority.ExecuteUpdateAsync(s => s.SetProperty(w => w.WorldId, Guid.NewGuid().ToString("N")));
        Assert.Equal(OutpostStartupState.Maintenance, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
        using var inspection = await f.H.F.H.H.Anchor.AcquireAsync(default);
        Assert.Equal(pending, f.H.F.H.H.Anchor.Read(inspection));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Missing_or_replaced_initial_World_is_not_a_completed_retirement(bool missing)
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true);
        if (missing) await f.H.Db.World.ExecuteDeleteAsync();
        else await f.H.Db.World.ExecuteUpdateAsync(s => s.SetProperty(w => w.InfluenceInstanceId, Guid.NewGuid().ToString("N")));
        Assert.Equal(OutpostStartupState.Maintenance, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_retirement_retains_all_worlds_and_accepts_only_fresh_current_world_saves(bool deltaAndArchive)
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); await OutpostLifecycleTests.CloseAndReceive(f);
        if (deltaAndArchive)
        {
            var save = f.H.F.Pre; var files = f.H.F.H.H.Files;
            byte[] raw = SaveFixtures.Decompress(save.Packed);
            await using (var lease = await f.H.F.H.H.Authority.AcquireAsync([1], default))
            {
                files.WriteAtomically(files.BasePath(1, save.Version.Number), save.Packed);
                files.WriteAtomically(files.DeltaPath(1, save.Version.Number, save.Version.Number), SaveDelta.Compress(raw, raw));
                File.Delete(files.FullPath(1, save.Version.Number));
            }
            long archive = await f.Archive();
            var journal = await f.H.Db.OutpostLifecycleOperations.AsNoTracking().SingleAsync(o => o.Kind == "Archive");
            var retained = OutpostLifecycleEvidence.Intent(journal);
            Assert.Contains(retained.Files, file => file.Name == Path.GetFileName(files.DeltaPath(1, save.Version.Number, save.Version.Number)));
            Assert.Contains(retained.Files, file => file.Name == Path.GetFileName(files.BasePath(1, save.Version.Number)));
            using var restoring = f.Host.Services.CreateScope();
            Assert.IsType<RestoreOutcome.Restored>(await restoring.ServiceProvider.GetRequiredService<ColonyArchive>().Restore(archive, "test"));
        }
        var original = await f.H.Db.OutpostSnapshotLinks.AsNoTracking().ToArrayAsync();
        using var scope = f.Host.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<AdminOperations>();
        var worlds = scope.ServiceProvider.GetRequiredService<WorldService>();
        await operations.RecreateWorld(Actor.Api);
        var first = await worlds.Create(new WorldParams { Seed = "Rimlink" }, [], Admin());
        var metadata = f.H.F.H.Metadata(); metadata.Registry.Clear();
        metadata.Scope.WorldInstanceId = first.InfluenceInstanceId; metadata.Scope.SaveGeneration = 1;
        var fresh = f.H.F.H.Snapshot(metadata);
        fresh.Version.Number = 1; // The new world reuses a filename, never the old world/snapshot proof identity.
        await f.H.F.H.Accept(fresh);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
        await operations.RecreateWorld(Actor.Api);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.NoActiveWorld), await f.H.F.H.H.Authority.OpenAsync(default));
        Assert.Equal(2, (await f.H.Db.Players.AsNoTracking().SingleAsync()).SaveGeneration);
        var second = await worlds.Create(new WorldParams { Seed = "Rimlink" }, [], Admin());
        Assert.NotEqual(first.InfluenceInstanceId, second.InfluenceInstanceId);
        Assert.Equal(1, await f.H.Db.OutpostTransitions.CountAsync(t => t.Kind == "Initialize"));
        Assert.Equal(2, await f.H.Db.OutpostTransitions.CountAsync(t => t.Kind == "WorldBind"));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
        await OutpostLifecycleTests.AssertOriginalEvidence(f, original);
        var freshLink = await f.H.Db.OutpostSnapshotLinks.AsNoTracking().SingleAsync(l => l.SnapshotId == fresh.Version.SnapshotId);
        await OutpostLifecycleTests.AssertOriginalEvidence(f, [freshLink]);
        var old = await new OutpostLifecycleEvidence(f.H.Db, f.H.F.H.H.Files).ResolveSnapshotAsync(original[0], default);
        Assert.NotNull(old.Archive);
        string path = Path.Combine(old.Archive!, Path.GetFileName(f.H.F.H.H.Files.FullPath(old.Version.PlayerId, old.Version.Number)));
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Equal(OutpostStartupState.Maintenance, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Current_manifest_and_owner_state_never_select_an_active_retired_world_catalog()
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); await OutpostLifecycleTests.CloseAndReceive(f);
        var oldManifest = await f.H.Db.OutpostManifests.AsNoTracking().SingleAsync(m => m.Active && m.Confirmed);
        using var scope = f.Host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AdminOperations>().RecreateWorld(Actor.Api);
        var world = await scope.ServiceProvider.GetRequiredService<WorldService>().Create(new WorldParams { Seed = "Rimlink" }, [], Admin());
        var manifests = scope.ServiceProvider.GetRequiredService<OutpostManifestService>();
        var document = OutpostManifestDocument.Parser.ParseFrom(oldManifest.Document);
        document.Binding = await manifests.BindingAsync(Actor.Api, default); document.ValuationVersion = "current-world";
        var staged = await manifests.StageAsync(Actor.Api, document, default);
        var admin = scope.ServiceProvider.GetRequiredService<OutpostAdminService>();
        var preview = await admin.PreviewConfirmManifestAsync(Actor.Api, staged.ManifestId, Convert.FromHexString(staged.Digest),
            Guid.NewGuid().ToString("D"), "Confirm the successor world's exact manifest", default);
        Assert.Empty(preview.Blockers);
        await admin.ConfirmManifestAsync(Actor.Api, preview.Command! with { Confirmed = true }, default);
        await using (var lease = await scope.ServiceProvider.GetRequiredService<OutpostAuthority>().AcquireAsync([1], default))
        {
            var active = await manifests.RequireActiveHeldAsync(lease, "planet", 0, OutpostProfile.Wood, default);
            Assert.Equal(world.InfluenceInstanceId, active.Binding.WorldInstanceId);
            Assert.Equal("current-world", active.ValuationVersion);
        }
        var current = f.H.F.H.Metadata().Scope; current.WorldInstanceId = world.InfluenceInstanceId; current.SaveGeneration = 1;
        var state = await scope.ServiceProvider.GetRequiredService<OutpostService>().StateAsync(1, new() { Scope = current }, default);
        Assert.Empty(state.Outposts); Assert.Empty(state.PendingOperations); Assert.Equal(staged.ManifestId, state.ManifestId);
        Assert.True((await f.H.Db.OutpostManifests.AsNoTracking().SingleAsync(m => m.Id == oldManifest.Id)).Active);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    private static Session Admin() => new() { Token = "fixture", AccountId = 1, PlayerId = 1,
        Role = Rimlink.Server.Storage.Entities.Role.Admin, Ip = "127.0.0.1", PlayerName = "Nova" };
}
