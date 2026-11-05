using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Tests.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Backup;

public sealed class OutpostRestoreDeliveryTests
{
    [Theory]
    [InlineData("missing")][InlineData("changed")][InlineData("old-proof")][InlineData("relabelled-proof")]
    [InlineData("payload")][InlineData("owner")][InlineData("unaccepted")]
    public async Task Restore_never_uses_old_or_unaccepted_proofs_to_start_a_current_delivery(string fault)
    {
        using var f = await LifecycleFixture.Create(); long id = await OutpostRestoreParcelTests.Pending(f, workers: true);
        var original = await f.H.Db.OutpostParcelLinks.AsNoTracking().SingleAsync();
        var target = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        await OutpostRestoreBridgeTests.Restore(f);
        var auth = await f.H.Db.OutpostRestoreAuthorizations.AsNoTracking().SingleAsync();
        var metadata = await OutpostRestoreAcceptanceTests.FreshMetadata(f, target); var recovery = SaveStore.RecoveryOf(target);
        var fresh = f.H.F.H.Snapshot(metadata, removed: recovery.Removed, returned: recovery.Returned);
        if (fault != "unaccepted") await f.H.F.H.Accept(fresh);
        var request = new OutpostDeliveryRequest { Scope = metadata.Scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("N"),
            ParcelId = id, PayloadDigest = ByteString.CopyFrom(original.PayloadDigest), CanonicalSnapshot = AssignmentHarness.SnapshotProof(fresh),
            RestoreAuthorizationId = auth.Id };
        if (fault == "missing") request.RestoreAuthorizationId = "";
        if (fault == "changed") request.RestoreAuthorizationId = Guid.NewGuid().ToString("D");
        if (fault is "old-proof" or "relabelled-proof") request.CanonicalSnapshot = new() { Scope = recovery.Outposts.Metadata.Scope.Clone(),
            SnapshotId = target.SnapshotId, RawSha256 = ByteString.CopyFrom(target.RawSha256!) };
        if (fault == "relabelled-proof") request.CanonicalSnapshot.Scope = request.Scope.Clone();
        if (fault == "payload") request.PayloadDigest = ByteString.CopyFrom(new byte[32]);
        if (fault == "owner") request.Scope.OwnerPlayerId++;
        string before = await f.Cut();
        var error = await Record.ExceptionAsync(() => f.H.Parcels.BeginDeliveryAsync(1, request, default));
        Assert.True(error is global::Grpc.Core.RpcException or OutpostEvidenceUnavailableException, error?.ToString() ?? "delivery unexpectedly started");
        Assert.Equal(before, await f.Cut()); Assert.Empty(await f.H.Db.OutpostDeliveryAttempts.ToListAsync());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Original_pending_payload_uses_one_current_attempt_and_one_exact_acceptance(bool workers)
    {
        using var f = await LifecycleFixture.Create(); long id = await OutpostRestoreParcelTests.Pending(f, workers);
        var original = await f.H.Db.OutpostParcelLinks.AsNoTracking().SingleAsync();
        var target = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        await OutpostRestoreBridgeTests.Restore(f);
        var parcel = await f.H.Mailbox.ToProto(await f.H.Mailbox.Get(1, id)); Assert.NotNull(parcel.OutpostRestoreAuthorization);
        var auth = await f.H.Db.OutpostRestoreAuthorizations.AsNoTracking().SingleAsync();
        Assert.Equal(auth.Id, parcel.OutpostRestoreAuthorization.AuthorizationId);
        Assert.Equal(original.RestoreBoundary, parcel.OutpostPayload.Scope.RestoreBoundary);
        var metadata = await OutpostRestoreAcceptanceTests.FreshMetadata(f, target); var recovery = SaveStore.RecoveryOf(target);
        var fresh = f.H.F.H.Snapshot(metadata, removed: recovery.Removed, returned: recovery.Returned); await f.H.F.H.Accept(fresh);
        var request = new OutpostDeliveryRequest { Scope = metadata.Scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("N"),
            ParcelId = id, PayloadDigest = ByteString.CopyFrom(original.PayloadDigest), CanonicalSnapshot = AssignmentHarness.SnapshotProof(fresh),
            RestoreAuthorizationId = auth.Id };
        var reply = await f.H.Parcels.BeginDeliveryAsync(1, request, default);
        Assert.False(reply.AckOnly); Assert.Equal(parcel.OutpostRestoreAuthorization, reply.RestoreAuthorization);
        Assert.Equal(reply, await f.H.Parcels.BeginDeliveryAsync(1, request.Clone(), default));
        Assert.Single(await f.H.Db.OutpostDeliveryAttempts.ToListAsync());
        if (workers) { var envelope = Assert.Single(metadata.Registry); envelope.Scope = metadata.Scope.Clone(); envelope.Tombstone = false; envelope.PawnReferenceToken = "Thing_RestoreReturned"; }
        metadata.Receipts.Add(new OutpostTransitionReceipt { TransitionId = reply.TransitionId, PredecessorId = reply.PredecessorId, TransitionDigest = reply.TransitionDigest,
            ClientRequestId = request.ClientRequestId, IntentDigest = ByteString.CopyFrom(SHA256.HashData(request.ToByteArray())),
            Snapshot = request.CanonicalSnapshot.Clone(), DeliveryAttemptId = reply.AttemptId, ParcelIds = { id },
            PawnCustodyIds = { parcel.OutpostPayload.PawnCustodyIds }, AssignmentIds = { parcel.OutpostPayload.AssignmentIds } });
        metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = reply.TransitionId, PredecessorId = reply.PredecessorId, TransitionDigest = reply.TransitionDigest });
        var accepted = f.H.F.H.Snapshot(metadata, removed: recovery.Removed, returned: recovery.Returned); await f.H.F.H.Accept(accepted);
        var imported = JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().ToListAsync());
        long balance = await f.H.Db.Wallets.Where(w => w.PlayerId == 1).Select(w => w.Balance).SingleAsync();
        await f.H.F.H.Accept(f.H.F.H.Snapshot(metadata.Clone(), removed: recovery.Removed, returned: recovery.Returned));
        Assert.True((await f.H.Parcels.BeginDeliveryAsync(1, request, default)).AckOnly);
        Assert.Equal(imported, JsonSerializer.Serialize(await f.H.Db.PawnCustody.AsNoTracking().ToListAsync()));
        Assert.Equal(balance, await f.H.Db.Wallets.Where(w => w.PlayerId == 1).Select(w => w.Balance).SingleAsync());
        var delivered = await f.H.Db.OutpostParcelLinks.AsNoTracking().SingleAsync();
        Assert.Equal(original.Payload, delivered.Payload); Assert.Equal(original.PayloadDigest, delivered.PayloadDigest);
        Assert.Equal(accepted.Version.SnapshotId, delivered.AcceptedSnapshotId);
        Assert.Equal("Accepted", (await f.H.Db.OutpostDeliveryAttempts.AsNoTracking().SingleAsync()).State);
        if (workers) Assert.Equal("Home", (await f.H.Db.PawnCustody.AsNoTracking().SingleAsync()).LocationKind);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }
}
