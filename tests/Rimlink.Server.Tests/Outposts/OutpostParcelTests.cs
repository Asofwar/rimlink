using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostParcelTests
{
    [Fact]
    public async Task Full_mailbox_does_not_destroy_own_returns()
    {
        using var h = await MutationHarness.Create();
        h.Db.Parcels.Add(new Rimlink.Server.Storage.Entities.Parcel { ToPlayerId = 1, Kind = ParcelKindDb.LotReturn, CreatedAt = DateTime.UtcNow });
        await h.Db.SaveChangesAsync(); h.F.Time.Advance(TimeSpan.FromHours(1));
        var result = await h.Service.MutateAsync(1, h.Request(OutpostMutationKind.Close), default);
        Assert.NotEmpty(result.ParcelIds); Assert.Equal(20, await h.ResourceCount());
        Assert.Equal(1, await h.Db.PawnCustody.CountAsync(p => p.LocationKind == "ReturnPending"));
        foreach (long id in result.ParcelIds)
        {
            var parcel = await h.Mailbox.ToProto(await h.Mailbox.Get(1, id));
            Assert.NotNull(parcel.OutpostPayload); Assert.NotEmpty(parcel.OutpostPayload.PayloadDigest);
            Assert.Equal(0, parcel.Silver); Assert.Empty(parcel.Items.Items);
        }
    }

    [Fact]
    public async Task Legacy_delivery_ack_cannot_materialize_a_protected_parcel()
    {
        using var h = await MutationHarness.Create();
        long id = Assert.Single((await h.Service.MutateAsync(1, h.Request(OutpostMutationKind.Close), default)).ParcelIds);
        await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() => h.Mailbox.Deliver(1, id, 1000));
        Assert.Null((await h.Mailbox.Get(1, id)).DeliveredAt);
        Assert.Empty(await h.Db.OutpostDeliveryAttempts.ToListAsync());
    }

    [Fact]
    public async Task Child_parcel_batching_is_bounded_and_stable_on_replay()
    {
        using var h = await MutationHarness.Create();
        var workers = new List<OutpostWorkerCapture>();
        for (int n = 0; n < 3; n++)
        {
            var worker = h.Workers[0].Clone(); worker.PawnCustodyId = Guid.NewGuid().ToString("D");
            worker.AssignmentId = Guid.NewGuid().ToString("D"); worker.Pawn.Registry.PawnCustodyId = worker.PawnCustodyId;
            worker.Pawn.ScribeXmlZstd = ByteString.CopyFrom(new byte[500000]);
            worker.BlobSha256 = ByteString.CopyFrom(SHA256.HashData(worker.Pawn.ToByteArray())); workers.Add(worker);
        }
        string id = Guid.NewGuid().ToString("D");
        var batch = new OutpostReturnBatch(id, id, h.Scope.Clone(), h.F.Request.OutpostId, h.F.Request.ManifestId,
            h.F.Request.ManifestDigest, 1, workers, [new ResourceParcel { SchemaVersion = 1, DefName = "WoodLog", Count = 1000, ImportValueX100 = 120000 }]);
        await using var lease = await h.F.H.H.Authority.AcquireAsync([1], default);
        await using var tx = await h.Db.Database.BeginTransactionAsync();
        var first = await h.Parcels.StageHeldAsync(lease, batch, default);
        var second = await h.Parcels.StageHeldAsync(lease, batch, default);
        Assert.Equal(first, second); Assert.Equal(3, first.Count);
        var links = await h.Db.OutpostParcelLinks.AsNoTracking().OrderBy(p => p.ParcelId).ToListAsync();
        Assert.Equal(3, links.Count); Assert.All(links, p => Assert.Equal(SHA256.HashData(p.Payload), p.PayloadDigest));
        foreach (long parcelId in first)
        {
            var parcel = await h.Db.Parcels.AsNoTracking().SingleAsync(p => p.Id == parcelId);
            Assert.InRange(parcel.SizeBytes, 1, Limits.EconomyMaxParcelBytes);
            Assert.Single(PawnList.Parser.ParseFrom(parcel.Pawns).Pawns);
        }
        Assert.Equal(1000, await h.ResourceCount()); await tx.RollbackAsync();
    }

    [Fact]
    public async Task Receipt_closes_delivery_attempt_without_ack()
    {
        using var h = await MutationHarness.Create();
        var close = await h.Service.MutateAsync(1, h.Request(OutpostMutationKind.Close), default);
        long parcelId = Assert.Single(close.ParcelIds);
        var link = await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(p => p.ParcelId == parcelId);
        var request = new OutpostDeliveryRequest { Scope = h.Scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("N"),
            ParcelId = parcelId, PayloadDigest = ByteString.CopyFrom(link.PayloadDigest), CanonicalSnapshot = AssignmentHarness.SnapshotProof(h.Post) };
        var started = await h.Parcels.BeginDeliveryAsync(1, request, default);
        Assert.False(started.AckOnly); Assert.Equal(started, await h.Parcels.BeginDeliveryAsync(1, request, default));
        var attempt = await h.Db.OutpostDeliveryAttempts.AsNoTracking().SingleAsync(); Assert.True(attempt.Active);
        var transition = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == attempt.TransitionId);
        var metadata = h.Post.Recovery.Outposts.Metadata.Clone(); metadata.Registry[0].Tombstone = false;
        metadata.Registry[0].PawnReferenceToken = "Thing_HumanReturned";
        metadata.Receipts.Add(new OutpostTransitionReceipt { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "",
            TransitionDigest = ByteString.CopyFrom(transition.Digest), ClientRequestId = request.ClientRequestId,
            IntentDigest = ByteString.CopyFrom(attempt.IntentDigest), Snapshot = request.CanonicalSnapshot.Clone(),
            DeliveryAttemptId = started.AttemptId, ParcelIds = { parcelId },
            PawnCustodyIds = { h.Workers.Select(w => w.PawnCustodyId) }, AssignmentIds = { h.Workers.Select(w => w.AssignmentId) } });
        metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "", TransitionDigest = ByteString.CopyFrom(transition.Digest) });
        await h.F.H.Accept(h.F.H.Snapshot(metadata));
        attempt = await h.Db.OutpostDeliveryAttempts.AsNoTracking().SingleAsync(); Assert.False(attempt.Active); Assert.Equal("Accepted", attempt.State);
        Assert.NotNull((await h.Mailbox.Get(1, parcelId)).DeliveredAt);
        Assert.Empty(await h.Mailbox.Pending(1));
        Assert.True((await h.Parcels.BeginDeliveryAsync(1, request, default)).AckOnly);
        Assert.Equal(OutpostStartupState.Ready, (await h.F.H.H.Authority.OpenAsync(default)).State);
    }
}
