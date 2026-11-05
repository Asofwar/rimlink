using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Backup;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostRestoreBridgeTests
{
    [Fact]
    public async Task Current_actual_without_restore_has_no_restore_grant()
    {
        using var f = await LifecycleFixture.Create(); var shared = f.H.F.H.H;
        var target = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        await using var lease = await shared.Authority.AcquireAsync([1], default);
        Assert.Null(await new OutpostRestoreEvidence(f.H.Db, shared.Files).GetDownloadBridgeHeldAsync(lease, 1, target, default));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Restore_grant_joins_actual_C_canonical_and_exact_committed_scope_only_map(bool home)
    {
        using var f = await LifecycleFixture.Create(); if (home) await OutpostLifecycleTests.CloseAndReceive(f);
        var target = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        var original = Rimlink.Server.Application.Saves.SaveStore.RecoveryOf(target); byte[] encoded = original.Outposts.Metadata.ToByteArray();
        var (request, result) = await Restore(f);
        var root = await f.H.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Kind == "BulkRestore");
        var shared = f.H.F.H.H; string before = await f.Cut();
        await using var lease = await shared.Authority.AcquireAsync([1], default);
        var evidence = new OutpostRestoreEvidence(f.H.Db, shared.Files);
        var bridge = Assert.IsType<OutpostRestoreBridge>(await evidence.GetDownloadBridgeHeldAsync(lease, 1, target, default));
        var view = OutpostSourceGraph.ValidateRoot(bridge);
        Assert.Equal(target.SnapshotId, bridge.CanonicalSnapshot.SnapshotId); Assert.Equal(target.RawSha256, bridge.CanonicalSnapshot.RawSha256.ToByteArray());
        Assert.Equal(original.Outposts.Metadata.Scope, bridge.CanonicalSnapshot.Scope);
        Assert.Equal(request.RestoreId, view.LifecycleOperationId); Assert.Equal(root.Id, view.TransitionId); Assert.Equal(root.Digest, view.TransitionDigest.ToByteArray());
        Assert.Equal(request.Inspection.Manifest.Authority.HeadDigest, view.SelectedCutHeadDigest.ToByteArray());
        Assert.Equal(request.Inspection.ManifestDigest, view.SelectedManifestDigest.ToByteArray());
        Assert.Equal(original.Outposts.Metadata.Scope, view.SourceScope); Assert.Equal(request.TargetBoundary, view.EffectiveScope.RestoreBoundary);
        Assert.Equal(0, view.EffectiveScope.SaveWriteEpoch); Assert.Equal(home ? 1 : 0, view.RegistryMap.Count);
        if (home)
        {
            var map = Assert.Single(view.RegistryMap); Assert.Equal(Assert.Single(original.Outposts.Metadata.Registry), map.Before);
            var expected = map.Before.Clone(); expected.Scope = view.EffectiveScope.Clone(); Assert.Equal(expected, map.After);
        }
        Assert.Equal(bridge, await evidence.GetDownloadBridgeHeldAsync(lease, 1, target, default));
        Assert.Equal(before, await f.Cut()); Assert.Equal(encoded, original.Outposts.Metadata.ToByteArray());
        var unchanged = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.Id == target.Id);
        Assert.Equal(JsonSerializer.Serialize(target), JsonSerializer.Serialize(unchanged));
        Assert.NotEqual(bridge.ActiveAuthorization.TransitionId, result.HeadId);
    }

    [Theory]
    [InlineData("id")][InlineData("hash")][InlineData("generation")][InlineData("epoch")]
    public async Task Changed_canonical_cut_never_produces_a_relabelled_or_partial_grant(string damage)
    {
        using var f = await LifecycleFixture.Create(); await Restore(f);
        var target = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        switch (damage)
        {
            case "id": target.SnapshotId = "99999999999999999999999999999999"; break;
            case "hash": target.RawSha256 = new byte[32]; break;
            case "generation": target.SaveGeneration++; break;
            case "epoch": target.SaveWriteEpoch++; break;
        }
        string before = await f.Cut(); var shared = f.H.F.H.H;
        await using var lease = await shared.Authority.AcquireAsync([1], default);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => new OutpostRestoreEvidence(f.H.Db, shared.Files)
            .GetDownloadBridgeHeldAsync(lease, 1, target, default));
        Assert.Equal(before, await f.Cut());
    }

    internal static async Task<(RestoreRequest Request, RestoreResult Result)> Restore(LifecycleFixture f)
    {
        string backup = await OutpostBackupTests.Create(f); var procedure = new OutpostRestoreProcedure(f.H.F.Time);
        var request = await OutpostRestoreTests.Request(f, procedure, backup); f.Host.Dispose();
        var result = await procedure.ExecuteAsync(request, default); Assert.True(result.Outcome == RestoreOutcome.Completed, result.Reason);
        f.H.Db.ChangeTracker.Clear(); return (request, result);
    }
}
