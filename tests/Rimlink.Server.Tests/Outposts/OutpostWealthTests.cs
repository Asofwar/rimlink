using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Feed;
using Rimlink.Server.Application.Integrity;
using Rimlink.Server.Hosting;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostWealthTests
{
    [Theory]
    [InlineData(100, 300, 324)]
    [InlineData(50, 150, 162)]
    public async Task Assigned_workers_and_integer_stock_count_once_with_one_percentage(int percent, long initial, long produced)
    {
        using var h = await MutationHarness.Create();
        var wealth = new ServerWealth(h.Db, Options.Create(new EconomyOptions { ServerWealthPercent = percent }));
        Assert.Equal(initial, await wealth.Of(1)); // 200 wallet + 100 worker, not its already-on-map tombstone.
        h.F.Time.Advance(TimeSpan.FromMinutes(1)); await h.Service.StateAsync(1, new() { Scope = h.Scope.Clone() }, default);
        Assert.Equal(initial, await wealth.Of(1)); // A fraction is not physical stock.
        h.F.Time.Advance(TimeSpan.FromMinutes(59)); await h.Service.StateAsync(1, new() { Scope = h.Scope.Clone() }, default);
        Assert.Equal(produced, await wealth.Of(1)); // Twenty wood, pinned 1.20 each.
        await h.Service.MutateAsync(1, h.Request(OutpostMutationKind.Close), default);
        Assert.Equal(produced, await wealth.Of(1)); // Moving workers/stock into own mail conserves the same value.
    }

    [Fact]
    public async Task Resource_receipt_without_ack_gives_one_import_credit_and_removes_server_value()
    {
        using var h = await MutationHarness.Create(); h.F.Time.Advance(TimeSpan.FromHours(1));
        var claim = await h.Service.MutateAsync(1, h.Request(OutpostMutationKind.Claim), default);
        long id = Assert.Single(claim.ParcelIds);
        var link = await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(p => p.ParcelId == id);
        var request = new OutpostDeliveryRequest { Scope = h.Scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("N"),
            ParcelId = id, PayloadDigest = ByteString.CopyFrom(link.PayloadDigest), CanonicalSnapshot = AssignmentHarness.SnapshotProof(h.Post) };
        var started = await h.Parcels.BeginDeliveryAsync(1, request, default);
        var attempt = await h.Db.OutpostDeliveryAttempts.AsNoTracking().SingleAsync();
        var transition = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == attempt.TransitionId);
        var metadata = h.Post.Recovery.Outposts.Metadata.Clone();
        metadata.Receipts.Add(new OutpostTransitionReceipt { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "",
            TransitionDigest = ByteString.CopyFrom(transition.Digest), ClientRequestId = request.ClientRequestId,
            IntentDigest = ByteString.CopyFrom(attempt.IntentDigest), Snapshot = request.CanonicalSnapshot.Clone(),
            DeliveryAttemptId = started.AttemptId, ParcelIds = { id } });
        metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "", TransitionDigest = ByteString.CopyFrom(transition.Digest) });
        var received = h.F.H.Snapshot(metadata, tick: 61000); received.Version.WealthTotal = 1024;
        await h.F.H.Accept(received);
        var wealth = new ServerWealth(h.Db, Options.Create(new EconomyOptions { ServerWealthPercent = 100 }));
        Assert.Equal(300, await wealth.Of(1)); // Wallet and assigned worker remain; imported resources do not.
        var events = new EventBus(new SessionRegistry(), NullLogger<EventBus>.Instance);
        var feed = new AdminFeed(Options.Create(new AdminOptions()), h.F.Time, NullLogger<AdminFeed>.Instance);
        var integrity = new IntegrityService(h.Db, new PlayerFreeze(h.Db, events), feed,
            Options.Create(new IntegrityOptions { WealthWarnPerDay = 1, WealthFreezePerDay = 2 }), NullLogger<IntegrityService>.Instance);
        var before = await h.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.SnapshotId == h.Post.Version.SnapshotId); before.WealthTotal = 1000;
        await integrity.OnStored(1, before, received.Version);
        Assert.Null((await h.Db.Players.AsNoTracking().SingleAsync()).EconomyFrozenAt);
        var repeated = h.F.H.Snapshot(metadata, tick: 121000); repeated.Version.WealthTotal = 1048; await h.F.H.Accept(repeated);
        await integrity.OnStored(1, received.Version, repeated.Version);
        Assert.NotNull((await h.Db.Players.AsNoTracking().SingleAsync()).EconomyFrozenAt); // No second credit for the retained receipt.
    }
}
