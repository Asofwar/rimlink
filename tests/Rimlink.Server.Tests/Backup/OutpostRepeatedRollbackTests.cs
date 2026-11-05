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

public sealed class OutpostRepeatedRollbackTests
{
    [Theory]
    [InlineData(false, false)][InlineData(false, true)]
    [InlineData(true, false)][InlineData(true, true)]
    public async Task Repeated_Q_keeps_actual_canonical_and_full_historical_closure(bool freshBetween, bool home)
    {
        using var f = await LifecycleFixture.Create(); if (home) await OutpostLifecycleTests.CloseAndReceive(f);
        var physical = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        await OutpostRestoreBridgeTests.Restore(f);
        string custody = JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().ToListAsync());
        string parcels = JsonSerializer.Serialize(await f.H.Db.OutpostParcelLinks.AsNoTracking().ToListAsync());
        string? firstTransition = null;
        for (int epoch = 1; epoch <= 2; epoch++)
        {
            string before = await f.Cut();
            using (var host = OutpostNestedRestoreTests.StartHost(f))
            {
                using var scope = host.Services.CreateScope();
                Assert.True(await scope.ServiceProvider.GetRequiredService<SaveStore>().Rollback(1, physical.Number));
            }
            f.H.Db.ChangeTracker.Clear();
            if (epoch == 2 && !freshBetween) Assert.Equal(before, await f.Cut());
            var canonical = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
            Assert.Equal(physical.SnapshotId, canonical.SnapshotId); Assert.Equal(physical.RawSha256, canonical.RawSha256);
            var original = SaveStore.RecoveryOf(canonical); var metadata = original.Outposts.Metadata.Clone();
            metadata.RestoreBridge = null; metadata.RollbackBridge = null; metadata.RollbackFromViewBridge = null;
            OutpostSourceProvenance graph; OutpostAuthorizedSnapshotView active;
            await using (var held = await f.H.F.H.H.Authority.AcquireAsync([1], default))
            {
                var bridges = await new OutpostRestoreEvidence(f.H.Db, f.H.F.H.H.Files).GetActiveDownloadBridgesHeldAsync(held, 1, canonical, default);
                Assert.Null(bridges.Restore); metadata.RollbackBridge = bridges.Rollback; metadata.RollbackFromViewBridge = bridges.FromView;
                if (epoch == 2 && freshBetween)
                {
                    var bridge = Assert.IsType<OutpostRollbackBridge>(bridges.Rollback); Assert.Null(bridges.FromView);
                    Assert.Equal(physical.SnapshotId, bridge.CanonicalSnapshot.SnapshotId);
                    graph = bridge.SourceProvenance; active = OutpostSourceGraph.ValidateRoot(bridge);
                }
                else
                {
                    var bridge = Assert.IsType<OutpostRollbackFromViewBridge>(bridges.FromView); Assert.Null(bridges.Rollback);
                    Assert.Equal(physical.SnapshotId, bridge.CanonicalSnapshot.SnapshotId);
                    graph = bridge.SourceProvenance; active = OutpostSourceGraph.ValidateRoot(bridge);
                }
            }
            int expectedEpoch = freshBetween ? epoch : 1;
            Assert.Equal(expectedEpoch, active.EffectiveScope.SaveWriteEpoch); Assert.Equal(expectedEpoch - 1, active.PreviousCurrentScope.SaveWriteEpoch);
            Assert.Equal(expectedEpoch + 1, graph.AuthorizedViews.Count);
            if (epoch == 1) firstTransition = active.TransitionId;
            else if (!freshBetween) Assert.Equal(firstTransition, active.TransitionId);
            Assert.Equal(epoch == 2 && freshBetween ? 2 : 1, graph.ActualSources.Count);
            OutpostNestedRestoreTests.ApplyPath(metadata, graph, active);
            if (epoch == 1 && !freshBetween) continue;
            int count = epoch == 1 ? 1 : 2;
            for (int n = 0; n < count; n++)
            {
                var fresh = f.H.F.H.Snapshot(metadata.Clone(), removed: original.Removed, returned: original.Returned);
                await f.H.F.H.Accept(fresh); physical = fresh.Version;
            }
        }
        Assert.Equal(custody, JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().ToListAsync()));
        Assert.Equal(parcels, JsonSerializer.Serialize(await f.H.Db.OutpostParcelLinks.AsNoTracking().ToListAsync()));
        Assert.Equal(freshBetween ? 2 : 1, await f.H.Db.OutpostLifecycleOperations.CountAsync(o => o.Kind == "Rollback"));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }
}
