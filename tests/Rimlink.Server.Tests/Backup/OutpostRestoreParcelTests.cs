using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Backup;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostRestoreParcelTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Restore_authorizes_the_original_pending_parcel_once_without_changing_its_payload(bool workers)
    {
        using var f = await LifecycleFixture.Create();
        long id = await Pending(f, workers);
        var original = await f.H.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(p => p.ParcelId == id);
        var originalParcel = await f.H.Db.Parcels.AsNoTracking().SingleAsync(p => p.Id == id);
        var canonical = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        await OutpostRestoreBridgeTests.Restore(f);
        var root = await f.H.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Kind == "BulkRestore");
        var authorization = await f.H.Db.OutpostRestoreAuthorizations.AsNoTracking().SingleOrDefaultAsync();
        Assert.NotNull(authorization);
        Assert.Equal(id, authorization.ParcelId); Assert.Equal(root.RestoreBoundary, authorization.RestoreBoundary);
        Assert.Equal(root.Id, authorization.TransitionId); Assert.Equal(original.PayloadDigest, authorization.PayloadDigest);
        Assert.Equal(canonical.SnapshotId, authorization.CanonicalSnapshotId); Assert.Equal(canonical.RawSha256, authorization.CanonicalSnapshotHash);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await f.H.Db.OutpostParcelLinks.AsNoTracking().SingleAsync()));
        Assert.Equal(JsonSerializer.Serialize(originalParcel), JsonSerializer.Serialize(await f.H.Db.Parcels.AsNoTracking().SingleAsync(p => p.Id == id)));
        Assert.Empty(await f.H.Db.OutpostDeliveryAttempts.ToListAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
        var plan = OutpostRestoreEvidence.Plan(root);
        var repeated = await new OutpostRestoreProcedure().ResumeAsync(f.H.F.H.H.Data.Root,
            Path.GetDirectoryName(f.H.F.H.H.Anchor.DatabasePath)!, root.Id, default);
        Assert.Equal(RestoreOutcome.Completed, repeated.Outcome);
        Assert.Equal(authorization.Id, (await f.H.Db.OutpostRestoreAuthorizations.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(root.Intent, (await f.H.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == root.Id)).Intent);
        Assert.Equal(plan.Journal.Selection.TargetBoundary, repeated.Boundary);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Repeated_restore_adds_one_current_authorization_and_keeps_every_original_link(bool freshBetween)
    {
        using var f = await LifecycleFixture.Create(); long id = await Pending(f, workers: true);
        var original = await f.H.Db.OutpostParcelLinks.AsNoTracking().SingleAsync();
        var target = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        await OutpostRestoreBridgeTests.Restore(f);
        var first = await f.H.Db.OutpostRestoreAuthorizations.AsNoTracking().SingleOrDefaultAsync(); Assert.NotNull(first);
        if (freshBetween)
        {
            var metadata = await OutpostRestoreAcceptanceTests.FreshMetadata(f, target);
            var recovery = SaveStore.RecoveryOf(target);
            await f.H.F.H.Accept(f.H.F.H.Snapshot(metadata, removed: recovery.Removed, returned: recovery.Returned));
        }
        var canonical = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        string archive;
        using (var host = OutpostNestedRestoreTests.StartHost(f))
        {
            archive = Path.Combine(f.H.F.H.H.Root, Guid.NewGuid().ToString("N") + ".tar.zst");
            using var scope = host.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<OutpostBackupCapture>().CreateAsync(archive, default);
        }
        var procedure = new OutpostRestoreProcedure(f.H.F.Time);
        var restore = await OutpostRestoreTests.Request(f, procedure, archive);
        var result = await procedure.ExecuteAsync(restore, default);
        Assert.True(result.Outcome == RestoreOutcome.Completed, result.Reason); f.H.Db.ChangeTracker.Clear();
        var head = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var rows = await f.H.Db.OutpostRestoreAuthorizations.AsNoTracking().ToListAsync(); Assert.Equal(2, rows.Count);
        var second = Assert.Single(rows, a => a.RestoreBoundary == head.RestoreBoundary);
        Assert.NotEqual(first.Id, second.Id); Assert.Equal(id, second.ParcelId); Assert.Equal(original.PayloadDigest, second.PayloadDigest);
        Assert.Equal(canonical.SnapshotId, second.CanonicalSnapshotId); Assert.Equal(canonical.RawSha256, second.CanonicalSnapshotHash);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(Assert.Single(rows, a => a.Id == first.Id)));
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await f.H.Db.OutpostParcelLinks.AsNoTracking().SingleAsync()));
        Assert.Empty(await f.H.Db.OutpostDeliveryAttempts.ToListAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    [Fact]
    public async Task Accepted_original_receipt_is_not_converted_to_a_new_pending_authorization()
    {
        using var f = await LifecycleFixture.Create(); await OutpostLifecycleTests.CloseAndReceive(f);
        string links = JsonSerializer.Serialize(await f.H.Db.OutpostParcelLinks.AsNoTracking().ToListAsync());
        string attempts = JsonSerializer.Serialize(await f.H.Db.OutpostDeliveryAttempts.AsNoTracking().ToListAsync());
        await OutpostRestoreBridgeTests.Restore(f);
        Assert.Empty(await f.H.Db.OutpostRestoreAuthorizations.ToListAsync());
        Assert.Equal(links, JsonSerializer.Serialize(await f.H.Db.OutpostParcelLinks.AsNoTracking().ToListAsync()));
        Assert.Equal(attempts, JsonSerializer.Serialize(await f.H.Db.OutpostDeliveryAttempts.AsNoTracking().ToListAsync()));
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    [Fact]
    public async Task Started_original_attempt_is_retained_for_recovery_not_reauthorized_by_restore()
    {
        using var f = await LifecycleFixture.Create(); long id = await Pending(f, workers: true);
        var link = await f.H.Db.OutpostParcelLinks.AsNoTracking().SingleAsync();
        var request = new OutpostDeliveryRequest { Scope = f.H.Scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("N"),
            ParcelId = id, PayloadDigest = ByteString.CopyFrom(link.PayloadDigest), CanonicalSnapshot = AssignmentHarness.SnapshotProof(f.H.Post) };
        await f.H.Parcels.BeginDeliveryAsync(1, request, default);
        string attempts = JsonSerializer.Serialize(await f.H.Db.OutpostDeliveryAttempts.AsNoTracking().ToListAsync());
        await OutpostRestoreBridgeTests.Restore(f);
        Assert.Empty(await f.H.Db.OutpostRestoreAuthorizations.ToListAsync());
        Assert.Equal(attempts, JsonSerializer.Serialize(await f.H.Db.OutpostDeliveryAttempts.AsNoTracking().ToListAsync()));
        Assert.Equal(link.Payload, (await f.H.Db.OutpostParcelLinks.AsNoTracking().SingleAsync()).Payload);
        var scope = f.H.Scope.Clone();
        scope.RestoreBoundary = (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).RestoreBoundary;
        var payload = OutpostParcelPayload.Parser.ParseFrom(link.Payload);
        var error = await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() => f.H.Service.ReadManifestAsync(1,
            new() { Scope = scope, ParcelId = id, ManifestId = payload.ManifestId, ExpectedDigest = Convert.ToHexStringLower(payload.ManifestDigest.Span) }, default));
        Assert.Equal("manifest-parcel-mismatch", error.Status.Detail);
    }

    internal static async Task<long> Pending(LifecycleFixture f, bool workers)
    {
        f.H.F.Time.Advance(TimeSpan.FromHours(1));
        return Assert.Single((await f.H.Service.MutateAsync(1, f.H.Request(workers ? OutpostMutationKind.Close : OutpostMutationKind.Claim), default)).ParcelIds);
    }
}
