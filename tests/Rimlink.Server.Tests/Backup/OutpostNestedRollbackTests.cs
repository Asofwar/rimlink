using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostNestedRollbackTests
{
    [Theory]
    [InlineData(false, false)][InlineData(false, true)]
    [InlineData(true, false)][InlineData(true, true)]
    public async Task Restore_then_rollback_preserves_physical_proof_and_advances_only_current_epoch(bool freshBetween, bool home)
    {
        using var f = await LifecycleFixture.Create(); if (home) await OutpostLifecycleTests.CloseAndReceive(f);
        var physical = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        byte[] originalBytes = f.H.F.H.H.Files.ReadEvidence(physical);
        await OutpostRestoreBridgeTests.Restore(f);
        var metadata = await OutpostRestoreAcceptanceTests.FreshMetadata(f, physical);
        if (freshBetween)
        {
            var original = SaveStore.RecoveryOf(physical);
            var fresh = f.H.F.H.Snapshot(metadata, removed: original.Removed, returned: original.Returned);
            await f.H.F.H.Accept(fresh); physical = fresh.Version;
        }
        var actualScope = SaveStore.RecoveryOf(physical).Outposts.Metadata.Scope.Clone();
        string custody = JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().ToListAsync());
        using (var host = OutpostNestedRestoreTests.StartHost(f))
        {
            using var scope = host.Services.CreateScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<SaveStore>().Rollback(1, physical.Number));
        }
        f.H.Db.ChangeTracker.Clear();
        var row = await f.H.Db.OutpostLifecycleOperations.AsNoTracking().SingleAsync(o => o.Kind == "Rollback");
        var intent = OutpostLifecycleEvidence.Intent(row);
        var retained = OutpostSnapshotProof.Parser.ParseFrom(intent.Rollback!.Target);
        Assert.Equal(physical.SnapshotId, retained.SnapshotId); Assert.Equal(physical.RawSha256, retained.RawSha256.ToByteArray());
        Assert.Equal(actualScope, retained.Scope);
        Assert.Equal(metadata.Scope, OutpostScopeMessage.Parser.ParseFrom(intent.Scopes[0].Scope));
        var current = OutpostScopeMessage.Parser.ParseFrom(intent.ResultScopes![0].Scope);
        Assert.Equal(metadata.Scope.RestoreBoundary, current.RestoreBoundary); Assert.Equal(1, current.SaveWriteEpoch);
        Assert.Equal(1, await f.H.Db.Players.Where(p => p.Id == 1).Select(p => p.SaveWriteEpoch).SingleAsync());
        Assert.Equal(custody, JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().ToListAsync()));
        var canonical = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        Assert.Equal(physical.SnapshotId, canonical.SnapshotId); Assert.Equal(actualScope, SaveStore.RecoveryOf(canonical).Outposts.Metadata.Scope);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
        if (!freshBetween) Assert.Equal(originalBytes, f.H.F.H.H.Files.ReadEvidence(canonical));
        OutpostSaveMetadata echo = SaveStore.RecoveryOf(canonical).Outposts.Metadata.Clone();
        OutpostSourceProvenance graph; OutpostAuthorizedSnapshotView active;
        await using (var held = await f.H.F.H.H.Authority.AcquireAsync([1], default))
        {
            echo.RestoreBridge = null; echo.RollbackBridge = null; echo.RollbackFromViewBridge = null;
            if (freshBetween)
            {
                var bridge = await new OutpostLifecycleEvidence(f.H.Db, f.H.F.H.H.Files).GetRollbackBridgeHeldAsync(held, 1, canonical, default);
                Assert.NotNull(bridge); echo.RollbackBridge = bridge;
                active = OutpostSourceGraph.ValidateRoot(bridge); graph = bridge.SourceProvenance;
                Assert.Equal(physical.SnapshotId, active.Source.ActualSnapshotId);
                var actualSource = Assert.Single(graph.ActualSources, s => s.Snapshot.SnapshotId == physical.SnapshotId);
                Assert.NotNull(actualSource.HistoricalAuthorization); Assert.Equal(2, graph.ActualSources.Count);
            }
            else
            {
                var bridge = await new OutpostRestoreEvidence(f.H.Db, f.H.F.H.H.Files).GetRollbackFromViewBridgeHeldAsync(held, 1, canonical, default);
                Assert.NotNull(bridge); echo.RollbackFromViewBridge = bridge;
                active = OutpostSourceGraph.ValidateRoot(bridge); graph = bridge.SourceProvenance;
                Assert.Equal(OutpostSourceReference.SourceOneofCase.AuthorizedView, active.Source.SourceCase);
                Assert.Single(graph.ActualSources);
            }
        }
        Assert.Equal(OutpostSourceAuthorizationKind.Rollback, active.Kind); Assert.Equal(current, active.EffectiveScope);
        Assert.Equal(2, graph.AuthorizedViews.Count); Assert.Equal(metadata.Scope, active.PreviousCurrentScope);
        var path = new List<OutpostAuthorizedSnapshotView>(); var step = active;
        while (true)
        {
            path.Add(step); if (step.Source.SourceCase == OutpostSourceReference.SourceOneofCase.ActualSnapshotId) break;
            step = graph.AuthorizedViews.Single(v => v.TransitionId == step.Source.AuthorizedView.TransitionId);
        }
        path.Reverse();
        foreach (var projection in path.SelectMany(v => v.RegistryMap))
        {
            var before = Assert.Single(echo.Registry, e => e.Equals(projection.Before));
            echo.Registry[echo.Registry.IndexOf(before)] = projection.After.Clone();
        }
        echo.Scope = current.Clone(); var recovery = SaveStore.RecoveryOf(canonical);
        for (int n = 0; n < 2; n++)
        {
            var fresh = f.H.F.H.Snapshot(echo, removed: recovery.Removed, returned: recovery.Returned);
            await f.H.F.H.Accept(fresh);
        }
        Assert.Equal(custody, JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().ToListAsync()));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }
}
