using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Economy;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OutpostCustodyBridgeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admin_gift_refund_preserves_uuid_and_cannot_reverse_started_delivery(bool started)
    {
        using var h = await CustodyHarness.Returned(); h.AddRecipient(1); await h.Db.SaveChangesAsync();
        var baseline = new OutpostSaveMetadata { SchemaVersion = 1, Scope = h.F.Scope.Clone() }; baseline.Scope.OwnerPlayerId = 2;
        var targetSave = h.F.F.H.Snapshot(baseline); await h.F.F.H.Accept(targetSave);
        var outgoing = new TransferRequest { ToPlayerId = 2, Items = new(), ClientRequestId = Guid.NewGuid().ToString("N"),
            GameTick = 1000, Pawns = new() { Pawns = { h.Blob.Clone() } } };
        await h.Depart(outgoing);
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var parcel = await scope.ServiceProvider.GetRequiredService<TransferService>().Send(1, outgoing);
        await scope.ServiceProvider.GetRequiredService<GiftService>().Answer(2, parcel.Id, true);
        if (started)
        {
            var link = await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(p => p.ParcelId == parcel.Id);
            await h.F.Parcels.BeginDeliveryAsync(2, new OutpostDeliveryRequest { Scope = baseline.Scope.Clone(), ParcelId = parcel.Id,
                ClientRequestId = Guid.NewGuid().ToString("N"), PayloadDigest = ByteString.CopyFrom(link.PayloadDigest),
                CanonicalSnapshot = AssignmentHarness.SnapshotProof(targetSave) }, default);
            await Assert.ThrowsAsync<RpcException>(() => scope.ServiceProvider.GetRequiredService<AdminOperations>().RefundParcel(Actor.Api, parcel.Id));
            Assert.Null((await h.Db.Parcels.AsNoTracking().SingleAsync(p => p.Id == parcel.Id)).DeliveredAt);
            Assert.Equal("TransferPending", (await h.Row()).LocationKind);
            Assert.Empty(await h.Db.Parcels.AsNoTracking().Where(p => p.ToPlayerId == 1 && p.DeliveredAt == null).ToListAsync());
        }
        else
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<AdminOperations>().RefundParcel(Actor.Api, parcel.Id));
            var row = await h.Row(); Assert.Equal("ReturnPending", row.LocationKind); Assert.Equal(1, row.PlayerId);
            var back = await h.Db.Parcels.AsNoTracking().SingleAsync(p => p.ToPlayerId == 1 && p.DeliveredAt == null);
            var link = await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(p => p.ParcelId == back.Id);
            Assert.Equal(SnapshotHarness.Custody, Assert.Single(OutpostParcelPayload.Parser.ParseFrom(link.Payload).PawnCustodyIds));
        }
    }
}
