using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostRestoreAcceptanceTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Fresh_restore_capture_keeps_original_history_and_never_imports_or_credits_it_twice(bool home)
    {
        using var f = await LifecycleFixture.Create(); if (home) await OutpostLifecycleTests.CloseAndReceive(f);
        var target = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        var original = SaveStore.RecoveryOf(target); byte[] oldMetadata = original.Outposts.Metadata.ToByteArray();
        await OutpostRestoreBridgeTests.Restore(f);
        var metadata = await FreshMetadata(f, target);
        var custody = JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().ToListAsync());
        var parcels = JsonSerializer.Serialize(await f.H.Db.OutpostParcelLinks.AsNoTracking().ToListAsync());
        long balance = await f.H.Db.Wallets.Where(w => w.PlayerId == 1).Select(w => w.Balance).SingleAsync();
        var fresh = f.H.F.H.Snapshot(metadata, removed: original.Removed, returned: original.Returned);
        await f.H.F.H.Accept(fresh);
        await f.H.F.H.Accept(f.H.F.H.Snapshot(metadata.Clone(), removed: original.Removed, returned: original.Returned));
        Assert.NotEqual(target.SnapshotId, fresh.Version.SnapshotId); Assert.Equal(0, fresh.Version.SaveWriteEpoch);
        Assert.Equal(original.Outposts.Metadata.Receipts, metadata.Receipts);
        Assert.Equal(oldMetadata, original.Outposts.Metadata.ToByteArray());
        Assert.Equal(custody, JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().ToListAsync()));
        Assert.Equal(parcels, JsonSerializer.Serialize(await f.H.Db.OutpostParcelLinks.AsNoTracking().ToListAsync()));
        Assert.Equal(balance, await f.H.Db.Wallets.Where(w => w.PlayerId == 1).Select(w => w.Balance).SingleAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
        await using (var lease = await f.H.F.H.H.Authority.AcquireAsync([1], default))
        {
            var actual = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
            Assert.Null(await new OutpostRestoreEvidence(f.H.Db, f.H.F.H.H.Files).GetDownloadBridgeHeldAsync(lease, 1, actual, default));
        }
        if (home)
        {
            var dead = metadata.Clone(); var entry = Assert.Single(dead.Registry); entry.Tombstone = true; entry.PawnReferenceToken = "";
            long revision = await f.H.Db.PawnCustody.Select(p => p.Revision).SingleAsync();
            await f.H.F.H.Accept(f.H.F.H.Snapshot(dead, removed: original.Removed, returned: original.Returned));
            await f.H.F.H.Accept(f.H.F.H.Snapshot(dead.Clone(), removed: original.Removed, returned: original.Returned));
            var actual = await f.H.Db.PawnCustody.AsNoTracking().SingleAsync(); Assert.True(actual.Tombstone); Assert.Equal(revision + 1, actual.Revision);
            Assert.Equal(balance, await f.H.Db.Wallets.Where(w => w.PlayerId == 1).Select(w => w.Balance).SingleAsync());
            Assert.Equal(parcels, JsonSerializer.Serialize(await f.H.Db.OutpostParcelLinks.AsNoTracking().ToListAsync()));
            Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
        }
    }

    [Fact]
    public async Task Authorized_restore_view_is_not_an_actual_fresh_mutation_proof()
    {
        using var f = await LifecycleFixture.Create(); var original = f.H.Post.Recovery;
        await OutpostRestoreBridgeTests.Restore(f); var metadata = await FreshMetadata(f, f.H.Post.Version);
        f.H.F.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(20, Assert.Single((await f.H.Service.StateAsync(1, new() { Scope = metadata.Scope.Clone() }, default)).Outposts).Stock);
        var stale = f.H.Request(OutpostMutationKind.Claim); stale.Scope = metadata.Scope.Clone();
        stale.CanonicalSnapshot.Scope = stale.Scope.Clone(); MutationHarness.Digest(stale);
        string before = await f.Cut();
        var refusal = await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() => f.H.Service.MutateAsync(1, stale, default));
        Assert.Equal("exact-snapshot-mismatch", refusal.Status.Detail); Assert.Equal(before, await f.Cut());
        var fresh = f.H.F.H.Snapshot(metadata, removed: original.Removed, returned: original.Returned); await f.H.F.H.Accept(fresh);
        var request = f.H.Request(OutpostMutationKind.Claim); request.Scope = metadata.Scope.Clone();
        request.CanonicalSnapshot = AssignmentHarness.SnapshotProof(fresh); MutationHarness.Digest(request);
        Assert.Single((await f.H.Service.MutateAsync(1, request, default)).ParcelIds);
        Assert.Equal(20, await f.H.ResourceCount()); Assert.Equal(0, (await f.H.Row()).Stock);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData("missing-bridge")][InlineData("cut")][InlineData("transition")][InlineData("capture")][InlineData("added-capture")][InlineData("receipt")]
    public async Task Changed_echo_or_carried_capture_never_acquires_restored_authority(string damage)
    {
        using var f = await LifecycleFixture.Create(); await OutpostRestoreBridgeTests.Restore(f);
        var metadata = await FreshMetadata(f, f.H.Post.Version);
        switch (damage)
        {
            case "missing-bridge": metadata.RestoreBridge = null; metadata.WorkerCaptures.Clear(); metadata.Caravans.Clear(); break;
            case "cut": metadata.RestoreBridge.SourceProvenance.AuthorizedViews.Last().SelectedManifestDigest = ByteString.CopyFrom(new byte[32]); break;
            case "transition": metadata.RestoreBridge.ActiveAuthorization.TransitionDigest = ByteString.CopyFrom(new byte[32]); break;
            case "capture": metadata.WorkerCaptures[0].Plants++; break;
            case "added-capture": var added = metadata.WorkerCaptures[0].Clone(); added.AssignmentId = Guid.NewGuid().ToString("D"); metadata.WorkerCaptures.Add(added); break;
            case "receipt": metadata.Receipts[0].IntentDigest = ByteString.CopyFrom(new byte[32]); break;
        }
        string before = await f.Cut();
        if (damage is "cut" or "transition" or "added-capture")
            Assert.Throws<InvalidDataException>(() => f.H.F.H.Snapshot(metadata));
        else await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.H.F.H.Accept(f.H.F.H.Snapshot(metadata)));
        Assert.Equal(before, await f.Cut());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    internal static async Task<OutpostSaveMetadata> FreshMetadata(LifecycleFixture f, Rimlink.Server.Storage.Entities.SaveVersion target)
    {
        var expectedId = target.Id;
        target = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        Assert.Equal(expectedId, target.Id);
        var metadata = SaveStore.RecoveryOf(target).Outposts.Metadata.Clone();
        await using var lease = await f.H.F.H.H.Authority.AcquireAsync([1], default);
        var bridge = (await new OutpostRestoreEvidence(f.H.Db, f.H.F.H.H.Files).GetDownloadBridgeHeldAsync(lease, 1, target, default))!;
        var view = OutpostSourceGraph.ValidateRoot(bridge);
        metadata.Scope = view.EffectiveScope.Clone(); metadata.RollbackBridge = null; metadata.RollbackFromViewBridge = null;
        metadata.RestoreBridge = bridge;
        foreach (var map in view.RegistryMap)
        {
            int index = metadata.Registry.IndexOf(metadata.Registry.Single(e => e.Equals(map.Before)));
            metadata.Registry[index] = map.After.Clone();
        }
        return metadata;
    }
}
