using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostRollbackBridgeTests
{
    [Fact]
    public async Task Normal_current_snapshot_has_no_epoch_bridge()
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true);
        using var services = f.Host.Services.CreateScope(); var provider = services.ServiceProvider;
        await using var lease = await provider.GetRequiredService<Rimlink.Server.Application.Outposts.OutpostAuthority>().AcquireAsync([1], default);
        var current = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        Assert.Null(await new OutpostLifecycleEvidence(provider.GetRequiredService<RimlinkDbContext>(), provider.GetRequiredService<SaveFiles>()).GetRollbackBridgeHeldAsync(lease, 1, current, default));
    }

    [Fact]
    public async Task Bridge_comes_from_committed_exact_target_and_authorizes_only_original_live_own_registry()
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); await OutpostLifecycleTests.CloseAndReceive(f);
        var target = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        var original = JsonSerializer.Deserialize<SaveRecoveryData>(target.RecoveryJson!)!;
        var newer = f.H.F.H.Snapshot(original.Outposts.Metadata.Clone(), tick: target.GameTick + 100); await f.H.F.H.Accept(newer);
        using var services = f.Host.Services.CreateScope(); var provider = services.ServiceProvider;
        Assert.True(await provider.GetRequiredService<SaveStore>().Rollback(1, target.Number));
        await using var lease = await provider.GetRequiredService<Rimlink.Server.Application.Outposts.OutpostAuthority>().AcquireAsync([1], default);
        var bridge = Assert.IsType<OutpostRollbackBridge>(await new OutpostLifecycleEvidence(provider.GetRequiredService<RimlinkDbContext>(), provider.GetRequiredService<SaveFiles>()).GetRollbackBridgeHeldAsync(lease, 1, target, default));
        var operation = Assert.Single(await f.H.Db.OutpostLifecycleOperations.AsNoTracking().ToListAsync());
        var transition = Assert.Single(await f.H.Db.OutpostTransitions.AsNoTracking().Where(t => t.Kind == "Rollback").ToListAsync());
        Assert.Equal(operation.Id, bridge.LifecycleOperationId); Assert.Equal(transition.Id, bridge.TransitionId);
        Assert.Equal(transition.Digest, bridge.TransitionDigest.ToByteArray());
        Assert.Equal(original.Outposts.Metadata.Scope, bridge.SourceScope); Assert.Equal(f.H.Scope.SaveWriteEpoch + 1, bridge.CurrentScope.SaveWriteEpoch);
        var expectedCurrent = f.H.Scope.Clone(); expectedCurrent.SaveWriteEpoch++;
        Assert.Equal(expectedCurrent, bridge.CurrentScope);
        Assert.Equal(target.SnapshotId, bridge.CanonicalSnapshot.SnapshotId); Assert.Equal(target.RawSha256, bridge.CanonicalSnapshot.RawSha256.ToByteArray());
        Assert.Equal(bridge.SourceScope, bridge.CanonicalSnapshot.Scope);
        var link = await f.H.Db.OutpostSnapshotLinks.AsNoTracking().SingleAsync(l => l.SnapshotId == target.SnapshotId);
        Assert.Equal(link.FrontierDigest, bridge.LocalFrontierDigest.ToByteArray());
        Assert.Equal(Assert.Single(original.Outposts.Metadata.Registry), Assert.Single(bridge.MutableRegistry));
        Assert.Equal(bridge, await new OutpostLifecycleEvidence(provider.GetRequiredService<RimlinkDbContext>(), provider.GetRequiredService<SaveFiles>()).GetRollbackBridgeHeldAsync(lease, 1, target, default));
        Assert.Equal(original.Outposts.Metadata, OutpostSaveMetadata.Parser.ParseFrom(link.Metadata));
    }

    [Fact]
    public async Task OpenCurrent_returns_old_exact_bytes_and_current_epoch_bridge_from_one_protected_cut()
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); await OutpostLifecycleTests.CloseAndReceive(f);
        var target = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        var original = JsonSerializer.Deserialize<SaveRecoveryData>(target.RecoveryJson!)!;
        await f.H.F.H.Accept(f.H.F.H.Snapshot(original.Outposts.Metadata.Clone(), tick: target.GameTick + 100));
        using var services = f.Host.Services.CreateScope(); var provider = services.ServiceProvider;
        var store = provider.GetRequiredService<SaveStore>(); Assert.True(await store.Rollback(1, target.Number));
        var opened = (await store.OpenCurrent(1))!.Value;
        await using (opened.Content)
        {
            var bridge = Assert.IsType<OutpostRollbackBridge>(opened.RollbackBridge);
            Assert.Equal(target.SnapshotId, opened.Version.SnapshotId); Assert.Equal(0, opened.Version.SaveWriteEpoch);
            Assert.Equal(1, opened.WriteEpoch); Assert.Equal(opened.Generation, bridge.CurrentScope.SaveGeneration);
            Assert.Equal(opened.WriteEpoch, bridge.CurrentScope.SaveWriteEpoch);
            using var raw = new ZstdSharp.DecompressionStream(opened.Content);
            var actual = SaveRecoveryData.ReadXml(raw, out byte[] hash);
            Assert.Equal(original.SnapshotId, actual.SnapshotId); Assert.Equal(original.Outposts.Metadata, actual.Outposts.Metadata); Assert.Equal(target.RawSha256, hash);
            Assert.Equal(actual.Outposts.Metadata.Scope, bridge.SourceScope);
        }
        Assert.Equal(1, await f.H.Db.OutpostLifecycleOperations.CountAsync(r => r.Kind == "Rollback"));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Fresh_current_epoch_acceptance_preserves_historical_receipts_without_importing_or_crediting_again(bool received)
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); if (received) await OutpostLifecycleTests.CloseAndReceive(f);
        var target = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        var original = SaveStore.RecoveryOf(target); var metadata = original.Outposts.Metadata.Clone();
        await f.H.F.H.Accept(f.H.F.H.Snapshot(metadata.Clone(), tick: target.GameTick + 100));
        using var services = f.Host.Services.CreateScope(); var provider = services.ServiceProvider;
        Assert.True(await provider.GetRequiredService<SaveStore>().Rollback(1, target.Number));
        var before = JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().ToListAsync());
        long balance = await f.H.Db.Wallets.Where(w => w.PlayerId == 1).Select(w => w.Balance).SingleAsync();
        await using (var lease = await provider.GetRequiredService<Rimlink.Server.Application.Outposts.OutpostAuthority>().AcquireAsync([1], default))
        {
            var bridge = (await new OutpostLifecycleEvidence(provider.GetRequiredService<RimlinkDbContext>(), provider.GetRequiredService<SaveFiles>())
                .GetRollbackBridgeHeldAsync(lease, 1, target, default))!;
            metadata.Scope = bridge.CurrentScope.Clone(); metadata.RollbackBridge = bridge.Clone();
            foreach (var authorized in bridge.MutableRegistry) metadata.Registry.Single(r => r.PawnCustodyId == authorized.PawnCustodyId).Scope = metadata.Scope.Clone();
        }
        var fresh = f.H.F.H.Snapshot(metadata, removed: original.Removed, returned: original.Returned);
        await f.H.F.H.Accept(fresh);
        Assert.NotEqual(target.SnapshotId, fresh.Version.SnapshotId); Assert.Equal(1, fresh.Version.SaveWriteEpoch);
        Assert.Equal(original.Outposts.Metadata.Receipts, metadata.Receipts);
        Assert.Equal(before, JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().ToListAsync()));
        Assert.Equal(balance, await f.H.Db.Wallets.Where(w => w.PlayerId == 1).Select(w => w.Balance).SingleAsync());
        Assert.Equal(OutpostStartupState.Ready, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Rollback_requires_fresh_accepted_current_epoch_bytes_before_another_mutation()
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); var target = f.H.Post;
        await f.H.F.H.Accept(f.H.F.H.Snapshot(target.Recovery.Outposts.Metadata.Clone(), tick: 2000));
        f.H.F.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(20, Assert.Single((await f.H.Service.StateAsync(1, new() { Scope = f.H.Scope.Clone() }, default)).Outposts).Stock);
        using var services = f.Host.Services.CreateScope(); var provider = services.ServiceProvider;
        Assert.True(await provider.GetRequiredService<SaveStore>().Rollback(1, target.Version.Number));
        OutpostRollbackBridge bridge;
        await using (var lease = await provider.GetRequiredService<Rimlink.Server.Application.Outposts.OutpostAuthority>().AcquireAsync([1], default))
            bridge = (await new OutpostLifecycleEvidence(provider.GetRequiredService<RimlinkDbContext>(), provider.GetRequiredService<SaveFiles>())
                .GetRollbackBridgeHeldAsync(lease, 1, target.Version, default))!;
        var stale = f.H.Request(OutpostMutationKind.Claim); stale.Scope = bridge.CurrentScope.Clone();
        stale.CanonicalSnapshot.Scope = stale.Scope.Clone(); // Relabeling old bytes is not a new accepted snapshot.
        MutationHarness.Digest(stale);
        string before = await f.Cut();
        var refusal = await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() => f.H.Service.MutateAsync(1, stale, default));
        Assert.Equal("exact-snapshot-mismatch", refusal.Status.Detail);
        Assert.Equal(before, await f.Cut());
        var metadata = target.Recovery.Outposts.Metadata.Clone(); metadata.Scope = bridge.CurrentScope.Clone(); metadata.RollbackBridge = bridge;
        var fresh = f.H.F.H.Snapshot(metadata); await f.H.F.H.Accept(fresh);
        var request = f.H.Request(OutpostMutationKind.Claim); request.Scope = bridge.CurrentScope.Clone();
        request.CanonicalSnapshot = AssignmentHarness.SnapshotProof(fresh); MutationHarness.Digest(request);
        var accepted = await f.H.Service.MutateAsync(1, request, default);
        Assert.Single(accepted.ParcelIds); Assert.Equal(20, await f.H.ResourceCount()); Assert.Equal(0, (await f.H.Row()).Stock);
        Assert.Equal(1, await f.H.Db.Players.Where(p => p.Id == 1).Select(p => p.SaveWriteEpoch).SingleAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData("missing-bridge")][InlineData("transition")][InlineData("frontier")][InlineData("capture")][InlineData("receipt")]
    public async Task Changed_bridge_or_historical_bytes_never_grants_fresh_epoch_authority(string fault)
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); var target = f.H.Post;
        await f.H.F.H.Accept(f.H.F.H.Snapshot(target.Recovery.Outposts.Metadata.Clone(), tick: 2000));
        using var services = f.Host.Services.CreateScope(); var provider = services.ServiceProvider;
        Assert.True(await provider.GetRequiredService<SaveStore>().Rollback(1, target.Version.Number));
        var metadata = target.Recovery.Outposts.Metadata.Clone();
        await using (var lease = await provider.GetRequiredService<Rimlink.Server.Application.Outposts.OutpostAuthority>().AcquireAsync([1], default))
        {
            var bridge = (await new OutpostLifecycleEvidence(provider.GetRequiredService<RimlinkDbContext>(), provider.GetRequiredService<SaveFiles>())
                .GetRollbackBridgeHeldAsync(lease, 1, target.Version, default))!;
            metadata.Scope = bridge.CurrentScope.Clone(); metadata.RollbackBridge = bridge;
        }
        switch (fault)
        {
            case "missing-bridge": metadata.RollbackBridge = null; metadata.WorkerCaptures.Clear(); metadata.Caravans.Clear(); break;
            case "transition": metadata.RollbackBridge.TransitionDigest = ByteString.CopyFrom(new byte[32]); break;
            case "frontier": metadata.RollbackBridge.LocalFrontierDigest = ByteString.CopyFrom(new byte[32]); break;
            case "capture": metadata.WorkerCaptures[0].Plants++; break;
            case "receipt": metadata.Receipts[0].IntentDigest = ByteString.CopyFrom(new byte[32]); break;
        }
        var head = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var custody = JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().ToListAsync());
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.H.F.H.Accept(f.H.F.H.Snapshot(metadata)));
        Assert.Equal(head.HeadId, (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
        Assert.Equal(custody, JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().ToListAsync()));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData("not-current")][InlineData("wrong-owner")][InlineData("wrong-hash")]
    public async Task Bridge_never_covers_a_different_download_cut(string fault)
    {
        using var f = await LifecycleFixture.Create(runtimeQualified: true); var target = f.H.Post;
        var newer = f.H.F.H.Snapshot(target.Recovery.Outposts.Metadata.Clone(), tick: 2000); await f.H.F.H.Accept(newer);
        using var services = f.Host.Services.CreateScope(); var provider = services.ServiceProvider;
        Assert.True(await provider.GetRequiredService<SaveStore>().Rollback(1, target.Version.Number));
        await using var lease = await provider.GetRequiredService<Rimlink.Server.Application.Outposts.OutpostAuthority>().AcquireAsync([1], default);
        var selected = fault == "not-current" ? newer.Version : target.Version;
        if (fault == "wrong-hash") selected.RawSha256 = Enumerable.Repeat((byte)9, 32).ToArray();
        if (fault == "wrong-owner")
            await Assert.ThrowsAsync<InvalidOperationException>(() => new OutpostLifecycleEvidence(provider.GetRequiredService<RimlinkDbContext>(), provider.GetRequiredService<SaveFiles>()).GetRollbackBridgeHeldAsync(lease, 2, selected, default));
        else
            await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => new OutpostLifecycleEvidence(provider.GetRequiredService<RimlinkDbContext>(), provider.GetRequiredService<SaveFiles>()).GetRollbackBridgeHeldAsync(lease, 1, selected, default));
    }
}
