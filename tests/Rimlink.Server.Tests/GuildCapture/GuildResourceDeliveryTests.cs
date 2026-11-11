using System.Security.Cryptography;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildResourceDeliveryTests
{
    [Fact]
    public async Task Claim_debits_stock_once_and_creates_a_typed_parcel_in_the_same_authority_commit()
    {
        using var f = await GuildFixture.Create(); var source = await Owned(f);
        var request = Request(f, source, 7);
        var account = await f.H.Db.Accounts.AsNoTracking().SingleAsync(a => a.Id == 1);
        var player = await f.H.Db.Players.AsNoTracking().SingleAsync(p => p.Id == 1);
        var recipient = f.Sessions.Start(account, player, "127.0.0.1", out _);
        f.H.Authority.CommitCheckpoint = _ => Assert.Equal(0, recipient.Events.Count);
        var result = await f.Service.ClaimResourcesAsync(1, request, default);
        Assert.Equal(1, recipient.Events.Count);
        await using var events = recipient.Events.ReadAll(default).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        var announced = events.Current.ParcelArrived.Parcel;
        Assert.Equal(result.ParcelId, announced.Id);
        Assert.Equal(result.ClaimId, announced.GuildResourcePayload.ClaimId);
        Assert.Equal(result, await f.Service.ClaimResourcesAsync(1, request, default));
        Assert.Equal(0, recipient.Events.Count);
        Assert.Equal(3, (await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).Stock);
        var link = await f.H.Db.Set<GuildParcelLink>().AsNoTracking().SingleAsync();
        var parcel = await f.H.Db.Parcels.AsNoTracking().SingleAsync();
        var payload = GuildResourceParcelPayload.Parser.ParseFrom(link.Payload);
        Assert.Equal(7, payload.Count); Assert.Equal("Steel", payload.DefName); Assert.Equal(1, payload.Recipient.PlayerId);
        Assert.Equal(1330, GuildResourceContracts.Validate(payload)); Assert.Equal(1330, link.ValueX100);
        Assert.Equal(SHA256.HashData(link.Payload), link.PayloadDigest);
        Assert.Equal(result.ClaimId, payload.ClaimId); Assert.Equal(result.TransitionId, payload.TransitionId);
        Assert.Equal(0, parcel.Silver); Assert.Empty(parcel.Items); Assert.Empty(parcel.Pawns);
        Assert.Null(link.AcceptedSnapshotId); Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task A_full_mailbox_rolls_back_the_stock_and_claim_together()
    {
        using var f = await GuildFixture.Create(); var source = await Owned(f);
        for (int n = 0; n < new EconomyOptions().MaxPendingParcels; n++)
            f.H.Db.Parcels.Add(new Rimlink.Server.Storage.Entities.Parcel { ToPlayerId = 1, Kind = ParcelKindDb.Admin });
        await f.H.Db.SaveChangesAsync(); f.H.Db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<RpcException>(() => f.Service.ClaimResourcesAsync(1, Request(f, source, 7), default));
        Assert.Empty(await f.H.Db.Set<GuildResourceClaim>().ToArrayAsync());
        Assert.Empty(await f.H.Db.Set<GuildParcelLink>().ToArrayAsync());
        Assert.Equal(10, (await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).Stock);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(513)]
    public async Task Claim_capacity_matches_the_restore_preflight_across_recipients(int count)
    {
        using var f = await GuildFixture.Create();
        var source = await Owned(f);
        var db = f.H.Db;
        var first = await f.Service.ClaimResourcesAsync(1, Request(f, source, 1), default);
        for (long id = 100; id <= 110; id++)
        {
            db.Add(new Account { Id = id, Login = $"recipient-{id}" });
            db.Add(new Player { Id = id, AccountId = id, Name = $"recipient-{id}" });
        }
        var pending = Enumerable.Range(0, count - 1).Select(i =>
        {
            long recipient = 100 + i % 11;
            var claim = new GuildResourceClaim { Id = Guid.NewGuid().ToString("D"), RequestId = Guid.NewGuid().ToString("D"),
                ActorId = 1, AllianceId = 50, OriginKind = "Outpost", OriginId = source,
                WorldId = f.Scope.WorldId, Boundary = f.Scope.Boundary, RecipientId = recipient, Resource = GuildResource.Steel,
                Count = 1, TransitionId = first.TransitionId };
            var parcel = new Rimlink.Server.Storage.Entities.Parcel { ToPlayerId = recipient, Kind = ParcelKindDb.GuildResources };
            db.Add(claim);
            db.Add(parcel);
            return (claim, parcel);
        }).ToArray();
        await db.SaveChangesAsync();
        foreach (var (claim, parcel) in pending)
            db.Add(new GuildParcelLink { ParcelId = parcel.Id, ClaimId = claim.Id, PayloadDigest = new byte[32] });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.True(await GuildRestore.PendingDeliverySlotsFullAsync(db, default));
        if (count == 513)
        {
            var error = await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => GuildRestore.RequireSupportedSourceAsync(db, default));
            Assert.Equal("guild-delivery-restore-cut-limit", error.Message);
        }
        else
        {
            var plan = await GuildRestore.CreateAsync(db, f.Scope.WorldId, Guid.NewGuid().ToString("D"), f.Time.Now.UtcDateTime, default);
            Assert.Equal(count, plan.PendingDeliveries!.Length);
            await db.Set<GuildParcelLink>().Where(l => l.ParcelId == first.ParcelId)
                .ExecuteUpdateAsync(update => update.SetProperty(l => l.AcceptedSnapshotId, "fixture-accepted"));
            Assert.False(await GuildRestore.PendingDeliverySlotsFullAsync(db, default));
        }
    }

    [Fact]
    public async Task A_committed_claim_replays_through_a_freeze_but_never_accepts_changed_contents()
    {
        using var f = await GuildFixture.Create(); var source = await Owned(f); var request = Request(f, source, 7);
        var first = await f.Service.ClaimResourcesAsync(1, request, default);
        var player = await f.H.Db.Players.SingleAsync(p => p.Id == 1); player.EconomyFrozenAt = f.Time.Now.UtcDateTime;
        await f.H.Db.SaveChangesAsync(); f.H.Db.ChangeTracker.Clear();
        Assert.Equal(first, await f.Service.ClaimResourcesAsync(1, request, default));
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.ClaimResourcesAsync(1, request with { Count = 1 }, default));
        Assert.Single(await f.H.Db.Parcels.ToArrayAsync());
        Assert.Equal(3, (await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).Stock);
    }

    [Fact]
    public async Task Generic_mailbox_ack_cannot_retire_a_resource_and_value_stays_with_the_fixed_recipient()
    {
        using var f = await GuildFixture.Create(); var source = await Owned(f);
        var result = await f.Service.ClaimResourcesAsync(1, Request(f, source, 7), default);
        var events = new EventBus(new SessionRegistry(), NullLogger<EventBus>.Instance);
        var options = Options.Create(new EconomyOptions { ServerWealthPercent = 100 });
        var mailbox = new MailboxService(f.H.Db, events, new WalletService(f.H.Db, events), options, f.H.Files);
        await Assert.ThrowsAsync<RpcException>(() => mailbox.Deliver(1, result.ParcelId, 123));
        Assert.Null((await f.H.Db.Parcels.AsNoTracking().SingleAsync()).DeliveredAt);
        Assert.Equal(13, await new ServerWealth(f.H.Db, options).Of(1));
        var proto = await mailbox.ToProto(await mailbox.Get(1, result.ParcelId));
        Assert.Equal(7, proto.GuildResourcePayload.Count); Assert.Null(proto.OutpostPayload);
    }

    [Theory]
    [InlineData("count")][InlineData("revision")][InlineData("generation")][InlineData("recipient")][InlineData("boundary")][InlineData("origin")]
    public async Task Changed_claim_is_refused_without_consuming_any_stock(string change)
    {
        using var f = await GuildFixture.Create(); var source = await Owned(f);
        var request = Request(f, source, 7);
        request = change switch
        {
            "count" => request with { Count = 11 }, "revision" => request with { OwnerRevision = 2 },
            "generation" => request with { RecipientGeneration = 1 }, "recipient" => request with { RecipientId = 2 },
            "boundary" => request with { Scope = f.Scope with { Boundary = Guid.NewGuid().ToString("D") } },
            "origin" => request with { OriginKind = "Stock" }, _ => request
        };
        var head = (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
        if (change == "boundary")
            await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.ClaimResourcesAsync(1, request, default));
        else
        {
            var error = await Assert.ThrowsAsync<RpcException>(() => f.Service.ClaimResourcesAsync(1, request, default));
            var detail = ErrorDetail.Parser.ParseFrom(error.Trailers.GetValueBytes(RpcErrors.DetailTrailer)!);
            Assert.Equal(ErrorCode.OutpostConflict, detail.Code);
            Assert.Equal("Guild.Claim", detail.Key);
        }
        Assert.Equal(head, (await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
        Assert.Equal(10, (await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).Stock);
        Assert.Empty(await f.H.Db.Set<GuildResourceClaim>().ToArrayAsync());
        Assert.Empty(await f.H.Db.Parcels.ToArrayAsync());
    }

    [Theory]
    [InlineData("BeforePending", false)][InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)][InlineData("AfterExternalCommit", true)]
    public async Task Claim_crash_and_retry_preserve_one_parcel_and_one_debit(string checkpoint, bool committed)
    {
        using var f = await GuildFixture.Create(); var source = await Owned(f); var request = Request(f, source, 7);
        f.H.Authority.CommitCheckpoint = p => { if (p == checkpoint) throw new Interrupted(); };
        await Assert.ThrowsAsync<Interrupted>(() => f.Service.ClaimResourcesAsync(1, request, default));
        f.H.Authority.CommitCheckpoint = null; f.H.Db.ChangeTracker.Clear();
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
        Assert.Equal(committed ? 1 : 0, await f.H.Db.Parcels.CountAsync());
        await f.Service.ClaimResourcesAsync(1, request, default);
        Assert.Single(await f.H.Db.Parcels.ToArrayAsync());
        Assert.Equal(3, (await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).Stock);
    }

    private static GuildResourceClaimRequest Request(GuildFixture f, string id, int count) =>
        new(f.Scope, Guid.NewGuid().ToString("D"), 50, "Outpost", id, 1, 1, 0, 0, count);
    private static async Task<string> Owned(GuildFixture f)
    {
        var placed = await f.Place(0);
        var preview = await f.Service.PreviewConsentAsync(1, 50, f.Scope, placed.OutpostId, 4, default);
        await f.Service.AcceptConsentAsync(1, 50, f.Scope, Guid.NewGuid().ToString("D"), placed.OutpostId, 4, preview.Digest, default);
        f.Time.Advance(TimeSpan.FromHours(1));
        await f.Service.SettleAsync(f.Scope, Guid.NewGuid().ToString("D"), placed.OutpostId, default);
        return placed.OutpostId;
    }
    private sealed class Interrupted : Exception;
}
