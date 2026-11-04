using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Trades;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OutpostCustodyBridgeTests
{
    [Theory]
    [InlineData("cancel")]
    [InlineData("expire")]
    [InlineData("replace")]
    public async Task Actual_trade_escrow_returns_the_same_tagged_pawn(string path)
    {
        using var h = await CustodyHarness.Returned();
        h.AddRecipient(1); await h.Db.SaveChangesAsync();
        var evidence = await h.Depart();
        using var host = h.StartHost(); using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TradeService>();
        var trade = await service.Open(1, new TradeOpenRequest { PartnerId = 2, ClientRequestId = evidence.ClientRequestId,
            GameTick = 1000, Pawns = evidence.Pawns.Clone() });
        var custody = await h.Row();
        Assert.Equal("Escrow", custody.LocationKind); Assert.Equal("trade:" + trade.Id, custody.LocationId);
        if (path == "cancel") await service.Cancel(2, trade.Id);
        else if (path == "expire")
        {
            await h.Db.Trades.Where(t => t.Id == trade.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTime.UtcNow.AddDays(-1)));
            Assert.Equal(1, await service.ExpireDue(DateTime.UtcNow));
        }
        else await service.SetSide(1, new TradeSideRequest { TradeId = trade.Id, BaseRevision = trade.Revision,
            ClientRequestId = Guid.NewGuid().ToString("N"), ReturnGoods = true });
        custody = await h.Row(); Assert.Equal("ReturnPending", custody.LocationKind); Assert.Equal(1, custody.PlayerId);
        var parcel = await h.Db.Parcels.AsNoTracking().SingleAsync(p => p.ToPlayerId == 1 && p.DeliveredAt == null);
        var payload = OutpostParcelPayload.Parser.ParseFrom((await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(p => p.ParcelId == parcel.Id)).Payload);
        Assert.Equal(SnapshotHarness.Custody, Assert.Single(payload.PawnCustodyIds));
        Assert.Equal(SnapshotHarness.Custody, PawnList.Parser.ParseFrom(parcel.Pawns).Pawns.Single().Registry.PawnCustodyId);
        await using var lease = await h.Authority.AcquireAsync([1, 2], default);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.CheckHeldAsync(lease, default)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_tagged_trade_sides_share_one_commit_and_rollback(bool returnGoods)
    {
        using var h = await CustodyHarness.Returned(); h.AddRecipient(1); await h.Db.SaveChangesAsync();
        var baseline = h.HomeSave.Recovery.Outposts.Metadata.Clone(); baseline.Scope.OwnerPlayerId = 2;
        baseline.Registry.Clear(); baseline.Receipts.Clear(); baseline.Frontier.Clear(); baseline.WorkerCaptures.Clear(); baseline.Caravans.Clear();
        var registry = h.Blob.Registry.Clone(); registry.Scope = baseline.Scope.Clone(); registry.PawnCustodyId = Guid.NewGuid().ToString("D");
        registry.AssignmentId = ""; registry.PawnReferenceToken = "Thing_Other";
        var other = h.Blob.Clone(); other.Registry = registry.Clone(); other.Name = "Other";
        var capture = h.HomeSave.Recovery.Outposts.Metadata.WorkerCaptures.Single().Clone();
        capture.PawnCustodyId = registry.PawnCustodyId; capture.AssignmentId = Guid.NewGuid().ToString("D"); capture.Pawn = other.Clone();
        capture.BlobSha256 = ByteString.CopyFrom(SHA256.HashData(other.ToByteArray()));
        baseline.Registry.Add(registry); baseline.WorkerCaptures.Add(capture);
        await h.F.F.H.Accept(h.F.F.H.Snapshot(baseline));
        var evidence = await h.Depart(); string otherRequestId = Guid.NewGuid().ToString("N");
        var removed = baseline.Clone(); removed.Registry[0].Tombstone = true; removed.Registry[0].PawnReferenceToken = "";
        await h.F.F.H.Accept(h.F.F.H.Snapshot(removed, removed: [otherRequestId]));
        using var host = h.StartHost(); using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TradeService>();
        var trade = await service.Open(1, new TradeOpenRequest { PartnerId = 2, ClientRequestId = evidence.ClientRequestId,
            GameTick = 1000, Pawns = evidence.Pawns.Clone() });
        trade = await service.SetSide(2, new TradeSideRequest { TradeId = trade.Id, BaseRevision = trade.Revision,
            ClientRequestId = otherRequestId, GameTick = 1000, Pawns = new PawnList { Pawns = { other } } });
        var before = await h.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var authority = scope.ServiceProvider.GetRequiredService<Rimlink.Server.Application.Outposts.OutpostAuthority>();
        authority.CommitCheckpoint = point => { if (point == "BeforePending") throw new IOException("two-sided-before-pending"); };
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            if (returnGoods) await service.Cancel(1, trade.Id); else await service.Confirm(1, trade.Id, trade.Revision);
        });
        authority.CommitCheckpoint = null;
        Assert.Equal(before.HeadId, (await h.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
        Assert.Equal(Rimlink.Server.Storage.Entities.TradeStatusDb.Open, (await h.Db.Trades.AsNoTracking().SingleAsync()).Status);
        Assert.All(await h.Db.PawnCustody.AsNoTracking().ToListAsync(), p => Assert.Equal("Escrow", p.LocationKind));
        Assert.Empty(await h.Db.Parcels.AsNoTracking().Where(p => p.DeliveredAt == null).ToListAsync());
        if (returnGoods) await service.Cancel(1, trade.Id); else await service.Confirm(1, trade.Id, trade.Revision);
        var rows = await h.Db.PawnCustody.AsNoTracking().OrderBy(p => p.PlayerId).ToListAsync();
        Assert.Equal(2, rows.Count); Assert.Equal(new long[] { 1, 2 }, rows.Select(p => p.PlayerId));
        Assert.All(rows, p => Assert.Equal(returnGoods ? "ReturnPending" : "TransferPending", p.LocationKind));
        var parcels = await h.Db.OutpostParcelLinks.AsNoTracking().Where(p => p.AcceptedSnapshotId == null).ToListAsync();
        Assert.Equal(2, parcels.Count);
        Assert.All(parcels, p => Assert.DoesNotContain("", OutpostParcelPayload.Parser.ParseFrom(p.Payload).AssignmentIds));
        Assert.Single(rows.Select(p => p.FrontierTransitionId).Distinct());
        await using var lease = await h.Authority.AcquireAsync([1, 2], default);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.CheckHeldAsync(lease, default)).State);
    }

    [Fact]
    public async Task Actual_trade_completion_hands_off_uuid_only_at_receiver_receipt()
    {
        using var h = await CustodyHarness.Returned(); h.AddRecipient(1); await h.Db.SaveChangesAsync();
        var baseline = h.HomeSave.Recovery.Outposts.Metadata.Clone(); baseline.Scope.OwnerPlayerId = 2;
        baseline.Registry.Clear(); baseline.Receipts.Clear(); baseline.Frontier.Clear(); baseline.WorkerCaptures.Clear(); baseline.Caravans.Clear();
        await h.F.F.H.Accept(h.F.F.H.Snapshot(baseline));
        var evidence = await h.Depart();
        using var host = h.StartHost(); using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TradeService>();
        var trade = await service.Open(1, new TradeOpenRequest { PartnerId = 2, ClientRequestId = evidence.ClientRequestId,
            GameTick = 1000, Pawns = evidence.Pawns.Clone() });
        await service.Confirm(1, trade.Id, trade.Revision); await service.Confirm(2, trade.Id, trade.Revision);
        var row = await h.Row(); Assert.Equal(1, row.PlayerId); Assert.Equal("TransferPending", row.LocationKind);
        var parcel = await h.Db.Parcels.AsNoTracking().SingleAsync(p => p.ToPlayerId == 2 && p.DeliveredAt == null);
        var payload = OutpostParcelPayload.Parser.ParseFrom((await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(p => p.ParcelId == parcel.Id)).Payload);
        Assert.Equal(SnapshotHarness.Custody, Assert.Single(payload.PawnCustodyIds));
        Assert.Equal(2, payload.Scope.OwnerPlayerId);
        await using var lease = await h.Authority.AcquireAsync([1, 2], default);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.CheckHeldAsync(lease, default)).State);
    }
}
