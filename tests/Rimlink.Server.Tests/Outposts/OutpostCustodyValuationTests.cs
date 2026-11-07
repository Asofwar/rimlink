using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Feed;
using Rimlink.Server.Application.Integrity;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.Trades;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostCustodyValuationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tagged_gift_declared_values_do_not_inflate_recipient_wealth_before_or_after_decision(bool accepted)
    {
        using var h = await CustodyHarness.Returned(); h.AddRecipient(1); await h.Db.SaveChangesAsync();
        var pawn = h.Blob.Clone(); pawn.MarketValueX100 = 1_000_000_000_000_000;
        var outgoing = new TransferRequest { ToPlayerId = 2, Silver = 25, GameTick = 1000,
            ClientRequestId = Guid.NewGuid().ToString("N"), Pawns = new() { Pawns = { pawn } },
            Items = new() { Items = { EconomyFixtures.Item("Steel", 2, marketValueX100: 1_000_000_000_000_000) } } };
        await h.Depart(outgoing);
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var parcel = await scope.ServiceProvider.GetRequiredService<TransferService>().Send(1, outgoing);
        if (accepted) await scope.ServiceProvider.GetRequiredService<GiftService>().Answer(2, parcel.Id, true);
        Assert.Equal(accepted ? ParcelDecisionDb.Accepted : ParcelDecisionDb.Awaiting,
            (await h.Db.Parcels.AsNoTracking().SingleAsync(p => p.Id == parcel.Id)).Decision);
        Assert.Equal(25, await scope.ServiceProvider.GetRequiredService<ServerWealth>().Of(2));
        Assert.Equal(1, (await h.Row()).PlayerId);
        Assert.Equal("TransferPending", (await h.Row()).LocationKind);
    }

    [Fact]
    public async Task Mixed_trade_return_records_and_counts_the_full_payload()
    {
        using var h = await CustodyHarness.Returned();
        long id = await ReturnMixedTrade(h);
        var link = await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(p => p.ParcelId == id);
        Assert.Equal(20_010_000, link.ValueX100); // Two item stacks and the same 100-silver pawn.
        var wealth = new ServerWealth(h.Db, Options.Create(new EconomyOptions()));
        Assert.Equal(200_300, await wealth.Of(1)); // Refunded wallet silver is separate from physical goods.
    }

    [Fact]
    public async Task Legacy_partial_link_does_not_hide_mixed_trade_return_held_wealth()
    {
        using var h = await CustodyHarness.Returned();
        long id = await ReturnMixedTrade(h);
        await h.Db.OutpostParcelLinks.Where(p => p.ParcelId == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ValueX100, h.Blob.MarketValueX100));
        var wealth = new ServerWealth(h.Db, Options.Create(new EconomyOptions()));
        Assert.Equal(200_300, await wealth.Of(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mixed_trade_return_receipt_credits_all_physical_goods_once_including_legacy_partial_links(bool legacyPartialValue)
    {
        using var h = await CustodyHarness.Returned();
        long id = await ReturnMixedTrade(h);
        var link = await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(p => p.ParcelId == id);
        var before = await h.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        var metadata = SaveStore.RecoveryOf(before).Outposts.Metadata.Clone();
        var request = new OutpostDeliveryRequest { Scope = metadata.Scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("N"),
            ParcelId = id, PayloadDigest = ByteString.CopyFrom(link.PayloadDigest),
            CanonicalSnapshot = new() { Scope = metadata.Scope.Clone(), SnapshotId = before.SnapshotId,
                RawSha256 = ByteString.CopyFrom(before.RawSha256!) } };
        var started = await h.F.Parcels.BeginDeliveryAsync(1, request, default);
        var transition = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == started.TransitionId);
        metadata.Registry.Single(r => r.PawnCustodyId == h.Blob.Registry.PawnCustodyId).Tombstone = false;
        metadata.Receipts.Add(new OutpostTransitionReceipt { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "",
            TransitionDigest = ByteString.CopyFrom(transition.Digest), ClientRequestId = request.ClientRequestId,
            IntentDigest = ByteString.CopyFrom(SHA256.HashData(request.ToByteArray())), Snapshot = request.CanonicalSnapshot.Clone(),
            DeliveryAttemptId = started.AttemptId, ParcelIds = { id }, PawnCustodyIds = { h.Blob.Registry.PawnCustodyId },
            AssignmentIds = { h.Blob.Registry.AssignmentId } });
        metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = transition.Id,
            PredecessorId = transition.PredecessorId ?? "", TransitionDigest = ByteString.CopyFrom(transition.Digest) });
        var received = h.F.F.H.Snapshot(metadata, tick: 61_000); received.Version.WealthTotal = 201_100;
        await h.F.F.H.Accept(received);
        var wealth = new ServerWealth(h.Db, Options.Create(new EconomyOptions()));
        Assert.Equal(200, await wealth.Of(1));
        var events = new EventBus(new SessionRegistry(), NullLogger<EventBus>.Instance);
        var feed = new AdminFeed(Options.Create(new AdminOptions()), h.F.F.Time, NullLogger<AdminFeed>.Instance);
        var integrity = new IntegrityService(h.Db, new PlayerFreeze(h.Db, events), feed,
            Options.Create(new IntegrityOptions { WealthWarnPerDay = 1, WealthFreezePerDay = 2 }), NullLogger<IntegrityService>.Instance);
        // Exercise the historical value at the read boundary, without forging delivery's authority hash.
        if (legacyPartialValue)
            await h.Db.OutpostParcelLinks.Where(p => p.ParcelId == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ValueX100, h.Blob.MarketValueX100));
        before.WealthTotal = 1000;
        await integrity.OnStored(1, before, received.Version);
        Assert.Null((await h.Db.Players.AsNoTracking().SingleAsync(p => p.Id == 1)).EconomyFrozenAt);
        if (legacyPartialValue)
            await h.Db.OutpostParcelLinks.Where(p => p.ParcelId == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ValueX100, link.ValueX100));
        var repeated = h.F.F.H.Snapshot(metadata, tick: 121_000); repeated.Version.WealthTotal = 401_200;
        await h.F.F.H.Accept(repeated);
        await integrity.OnStored(1, received.Version, repeated.Version);
        Assert.NotNull((await h.Db.Players.AsNoTracking().SingleAsync(p => p.Id == 1)).EconomyFrozenAt);
    }

    private static async Task<long> ReturnMixedTrade(CustodyHarness h)
    {
        h.AddRecipient(1); await h.Db.SaveChangesAsync();
        var outgoing = new TradeOpenRequest { PartnerId = 2, Silver = 25, GameTick = 1000,
            ClientRequestId = Guid.NewGuid().ToString("N"), Pawns = new() { Pawns = { h.Blob.Clone() } },
            Items = new() { Items = { EconomyFixtures.Item("Steel", 2, marketValueX100: 10_000_000) } } };
        await h.Depart(outgoing);
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TradeService>();
        var trade = await service.Open(1, outgoing);
        await service.Cancel(2, trade.Id);
        return (await h.Db.Parcels.AsNoTracking().SingleAsync(p => p.ToPlayerId == 1 && p.DeliveredAt == null)).Id;
    }
}
