using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostNestedRestoreTests
{
    [Theory]
    [InlineData(false, false)][InlineData(false, true)]
    [InlineData(true, false)][InlineData(true, true)]
    public async Task Repeated_restore_keeps_actual_sources_and_accepts_one_fresh_current_capture(bool freshBetween, bool home)
    {
        using var f = await LifecycleFixture.Create();
        if (home) await OutpostLifecycleTests.CloseAndReceive(f);
        var physical = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        byte[] originalBytes = f.H.F.H.H.Files.ReadEvidence(physical);
        await OutpostRestoreBridgeTests.Restore(f);
        var first = await OutpostRestoreAcceptanceTests.FreshMetadata(f, physical);
        string firstRoot = first.RestoreBridge.ActiveAuthorization.TransitionId;
        if (freshBetween)
        {
            var recovery = SaveStore.RecoveryOf(physical);
            var fresh = f.H.F.H.Snapshot(first, removed: recovery.Removed, returned: recovery.Returned);
            await f.H.F.H.Accept(fresh); physical = fresh.Version;
        }
        string archive;
        using (var host = StartHost(f))
        {
            archive = Path.Combine(f.H.F.H.H.Root, Guid.NewGuid().ToString("N") + ".tar.zst");
            using var scope = host.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<OutpostBackupCapture>().CreateAsync(archive, default);
        }
        var procedure = new OutpostRestoreProcedure(f.H.F.Time);
        var request = await OutpostRestoreTests.Request(f, procedure, archive);
        var result = await procedure.ExecuteAsync(request, default);
        Assert.True(result.Outcome == RestoreOutcome.Completed, result.Reason); f.H.Db.ChangeTracker.Clear();
        var actual = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        Assert.Equal(physical.SnapshotId, actual.SnapshotId);
        var original = SaveStore.RecoveryOf(actual); byte[] metadataBefore = original.Outposts.Metadata.ToByteArray();
        OutpostRestoreBridge bridge;
        await using (var lease = await f.H.F.H.H.Authority.AcquireAsync([1], default))
            bridge = Assert.IsType<OutpostRestoreBridge>(await new OutpostRestoreEvidence(f.H.Db, f.H.F.H.H.Files)
                .GetDownloadBridgeHeldAsync(lease, 1, actual, default));
        var active = OutpostSourceGraph.ValidateRoot(bridge);
        Assert.Equal(request.RestoreId, active.TransitionId);
        Assert.Equal(actual.SnapshotId, bridge.CanonicalSnapshot.SnapshotId);
        Assert.Equal(original.Outposts.Metadata.Scope, bridge.CanonicalSnapshot.Scope);
        Assert.Equal(actual.RawSha256, bridge.CanonicalSnapshot.RawSha256.ToByteArray());
        Assert.Equal(2, bridge.SourceProvenance.AuthorizedViews.Count);
        Assert.Contains(bridge.SourceProvenance.AuthorizedViews, v => v.TransitionId == firstRoot);
        var source = Assert.Single(bridge.SourceProvenance.ActualSources, s => s.Snapshot.SnapshotId == actual.SnapshotId);
        Assert.Equal(freshBetween ? firstRoot : null, source.HistoricalAuthorization?.TransitionId);
        Assert.Equal(freshBetween ? 2 : 1, bridge.SourceProvenance.ActualSources.Count);
        var custody = JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().ToListAsync());
        var returns = JsonSerializer.Serialize(await f.H.Db.OutpostParcelLinks.AsNoTracking().ToListAsync());
        var metadata = Apply(original.Outposts.Metadata, bridge);
        for (int i = 0; i < 2; i++)
            await f.H.F.H.Accept(f.H.F.H.Snapshot(metadata.Clone(), removed: original.Removed, returned: original.Returned));
        Assert.Equal(custody, JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().ToListAsync()));
        Assert.Equal(returns, JsonSerializer.Serialize(await f.H.Db.OutpostParcelLinks.AsNoTracking().ToListAsync()));
        Assert.Equal(metadataBefore, original.Outposts.Metadata.ToByteArray());
        Assert.Equal(SHA256.HashData(originalBytes), SHA256.HashData(f.H.F.H.H.Files.ReadEvidence(
            await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.SnapshotId == (freshBetween ? first.RestoreBridge.CanonicalSnapshot.SnapshotId : actual.SnapshotId)))));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData(false, false, false)][InlineData(false, true, false)]
    [InlineData(true, false, false)][InlineData(true, true, false)]
    [InlineData(false, false, true)][InlineData(false, true, true)]
    [InlineData(true, false, true)][InlineData(true, true, true)]
    public async Task Rollback_then_restore_preserves_Q_source_before_or_after_fresh_capture(bool freshBetween, bool home, bool restoreBefore)
    {
        using var f = await LifecycleFixture.Create(); if (home) await OutpostLifecycleTests.CloseAndReceive(f);
        var physical = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        if (restoreBefore) await OutpostRestoreBridgeTests.Restore(f);
        else f.Host.Dispose();
        using (var host = StartHost(f))
        {
            using var scope = host.Services.CreateScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<SaveStore>().Rollback(1, physical.Number));
        }
        f.H.Db.ChangeTracker.Clear();
        var rollback = await f.H.Db.OutpostLifecycleOperations.AsNoTracking().SingleAsync(o => o.Kind == "Rollback");
        if (freshBetween)
        {
            var recovery = SaveStore.RecoveryOf(physical); var echo = recovery.Outposts.Metadata.Clone();
            await using (var held = await f.H.F.H.H.Authority.AcquireAsync([1], default))
            {
                var bridges = await new OutpostRestoreEvidence(f.H.Db, f.H.F.H.H.Files).GetActiveDownloadBridgesHeldAsync(held, 1, physical, default);
                Assert.Null(bridges.Restore); echo.RestoreBridge = null;
                echo.RollbackBridge = bridges.Rollback; echo.RollbackFromViewBridge = bridges.FromView;
                if (bridges.FromView is { } fromView)
                    ApplyPath(echo, fromView.SourceProvenance, OutpostSourceGraph.ValidateRoot(fromView));
                else
                {
                    var bridge = Assert.IsType<OutpostRollbackBridge>(bridges.Rollback);
                    foreach (var entry in bridge.MutableRegistry)
                    {
                        int index = echo.Registry.IndexOf(Assert.Single(echo.Registry, e => e.Equals(entry)));
                        var after = entry.Clone(); after.Scope = bridge.CurrentScope.Clone(); echo.Registry[index] = after;
                    }
                    echo.Scope = bridge.CurrentScope.Clone();
                }
            }
            var fresh = f.H.F.H.Snapshot(echo, removed: recovery.Removed, returned: recovery.Returned);
            await f.H.F.H.Accept(fresh); physical = fresh.Version;
        }
        byte[] originalBytes = f.H.F.H.H.Files.ReadEvidence(physical);
        string archive = Path.Combine(f.H.F.H.H.Root, Guid.NewGuid().ToString("N") + ".tar.zst");
        using (var host = StartHost(f))
        {
            using var scope = host.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<OutpostBackupCapture>().CreateAsync(archive, default);
        }
        var procedure = new OutpostRestoreProcedure(f.H.F.Time);
        var request = await OutpostRestoreTests.Request(f, procedure, archive);
        var result = await procedure.ExecuteAsync(request, default);
        Assert.True(result.Outcome == RestoreOutcome.Completed, result.Reason); f.H.Db.ChangeTracker.Clear();
        var actual = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        Assert.Equal(physical.SnapshotId, actual.SnapshotId); Assert.Equal(originalBytes, f.H.F.H.H.Files.ReadEvidence(actual));
        OutpostRestoreBridge root;
        await using (var held = await f.H.F.H.H.Authority.AcquireAsync([1], default))
            root = Assert.IsType<OutpostRestoreBridge>(await new OutpostRestoreEvidence(f.H.Db, f.H.F.H.H.Files).GetDownloadBridgeHeldAsync(held, 1, actual, default));
        var active = OutpostSourceGraph.ValidateRoot(root); var graph = root.SourceProvenance;
        Assert.Equal(request.RestoreId, active.LifecycleOperationId); Assert.Equal(1, active.EffectiveScope.SaveWriteEpoch);
        Assert.NotEqual(OutpostLifecycleEvidence.Intent(rollback).Boundary, active.EffectiveScope.RestoreBoundary);
        Assert.Equal(restoreBefore ? 3 : 2, graph.AuthorizedViews.Count);
        Assert.Contains(graph.AuthorizedViews, v => v.Kind == OutpostSourceAuthorizationKind.Rollback && v.LifecycleOperationId == rollback.Id);
        var immediate = Assert.Single(graph.ActualSources, s => s.Snapshot.SnapshotId == actual.SnapshotId);
        Assert.Equal(freshBetween ? OutpostLifecycleEvidence.Decode<LifecycleProgress>(rollback.Progress).TransitionId : null, immediate.HistoricalAuthorization?.TransitionId);
        Assert.Equal(freshBetween ? 2 : 1, graph.ActualSources.Count);
        var original = SaveStore.RecoveryOf(actual); var metadata = Apply(original.Outposts.Metadata, root);
        string custody = JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().ToListAsync());
        string parcels = JsonSerializer.Serialize(await f.H.Db.OutpostParcelLinks.AsNoTracking().ToListAsync());
        for (int n = 0; n < 2; n++)
            await f.H.F.H.Accept(f.H.F.H.Snapshot(metadata.Clone(), removed: original.Removed, returned: original.Returned));
        Assert.Equal(custody, JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().ToListAsync()));
        Assert.Equal(parcels, JsonSerializer.Serialize(await f.H.Db.OutpostParcelLinks.AsNoTracking().ToListAsync()));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    internal static void ApplyPath(OutpostSaveMetadata metadata, OutpostSourceProvenance graph, OutpostAuthorizedSnapshotView active)
    {
        var steps = new List<OutpostAuthorizedSnapshotView>();
        for (var step = active; ; step = graph.AuthorizedViews.Single(v => v.TransitionId == step.Source.AuthorizedView.TransitionId))
        { steps.Add(step); if (step.Source.SourceCase == OutpostSourceReference.SourceOneofCase.ActualSnapshotId) break; }
        steps.Reverse();
        foreach (var map in steps.SelectMany(step => step.RegistryMap))
        {
            int index = metadata.Registry.IndexOf(Assert.Single(metadata.Registry, e => e.Equals(map.Before)));
            metadata.Registry[index] = map.After.Clone();
        }
        metadata.Scope = active.EffectiveScope.Clone();
    }

    internal static OutpostSaveMetadata Apply(OutpostSaveMetadata original, OutpostRestoreBridge bridge)
    {
        var metadata = original.Clone(); var graph = bridge.SourceProvenance;
        var active = OutpostSourceGraph.ValidateRoot(bridge); var steps = new List<OutpostAuthorizedSnapshotView>();
        for (var step = active; ; step = graph.AuthorizedViews.Single(v => v.TransitionId == step.Source.AuthorizedView.TransitionId))
        { steps.Add(step); if (step.Source.SourceCase == OutpostSourceReference.SourceOneofCase.ActualSnapshotId) break; }
        steps.Reverse();
        foreach (var step in steps)
            foreach (var map in step.RegistryMap)
            {
                var entry = Assert.Single(metadata.Registry, e => e.Equals(map.Before));
                metadata.Registry[metadata.Registry.IndexOf(entry)] = map.After.Clone();
            }
        metadata.Scope = active.EffectiveScope.Clone(); metadata.RollbackBridge = null; metadata.RollbackFromViewBridge = null;
        metadata.RestoreBridge = bridge.Clone(); return metadata;
    }

    internal static ServerFixture StartHost(LifecycleFixture f)
    {
        var shared = f.H.F.H.H;
        return HygieneFixtures.HygieneServer().WithoutWorld().WithDataDir(shared.Data.Root).KeepDataDir()
            .WithSetting("Outposts:AnchorDirectory", Path.Combine(shared.Root, "external"))
            .WithSetting("Outposts:StorageContract", "sqlite-local-v1")
            .WithServices(s => { s.AddSingleton(shared.Files); s.AddSingleton(shared.Owners); s.AddSingleton(shared.Anchor); s.AddSingleton<TimeProvider>(f.H.F.Time); });
    }
}
