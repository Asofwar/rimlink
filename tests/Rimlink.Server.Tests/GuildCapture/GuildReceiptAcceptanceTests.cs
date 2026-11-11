using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Feed;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.Integrity;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildReceiptAcceptanceTests
{
    [Fact]
    public async Task Physical_receipt_acceptance_switches_held_value_once_in_the_snapshot_commit()
    {
        using var f = await GuildFixture.Create(); var metadata = await Applied(f);
        var wealth = new ServerWealth(f.H.Db, Options.Create(new EconomyOptions { ServerWealthPercent = 100 }));
        Assert.Equal(13, await wealth.Of(1));
        var guard = new OutpostSnapshotGuard(f.H.Db, f.H.Files);
        var accepted = await GuildSnapshotTests.Accept(f, guard, metadata, 3);
        Assert.Equal(0, await wealth.Of(1));
        var link = await f.H.Db.Set<GuildParcelLink>().AsNoTracking().SingleAsync();
        Assert.Equal(accepted.Version.SnapshotId, link.AcceptedSnapshotId);
        var attempt = await f.H.Db.Set<GuildDeliveryAttempt>().AsNoTracking().SingleAsync();
        Assert.False(attempt.Active); Assert.Equal(link.AcceptedSnapshotId, attempt.AcceptedSnapshotId);
        Assert.Equal(metadata.GuildMetadata.Receipts[0].ToByteArray(), attempt.Receipt);
        Assert.NotNull((await f.H.Db.Parcels.AsNoTracking().SingleAsync()).DeliveredAt);
        Assert.Equal("Accepted", (await f.H.Db.Set<GuildResourceClaim>().AsNoTracking().SingleAsync()).State);
        await GuildSnapshotTests.Accept(f, guard, metadata, 4);
        Assert.Equal(accepted.Version.SnapshotId, (await f.H.Db.Set<GuildParcelLink>().AsNoTracking().SingleAsync()).AcceptedSnapshotId);
        Assert.Empty(await f.H.Db.PawnCustody.ToArrayAsync());
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData("attempt")][InlineData("payload")][InlineData("authorization")][InlineData("before")][InlineData("request")]
    public async Task A_changed_physical_receipt_cannot_deliver_or_replace_the_home_save(string fault)
    {
        using var f = await GuildFixture.Create(); var metadata = await Applied(f); var receipt = metadata.GuildMetadata.Receipts[0];
        if (fault == "attempt") receipt.AttemptId = Guid.NewGuid().ToString("D");
        if (fault == "payload") receipt.PayloadDigest = ByteString.CopyFrom(new byte[32]);
        if (fault == "authorization") receipt.AuthorizationTransitionDigest = ByteString.CopyFrom(new byte[32]);
        if (fault == "before") receipt.BeforeSnapshot.RawSha256 = ByteString.CopyFrom(new byte[32]);
        if (fault == "request") receipt.ClientRequestId = Guid.NewGuid().ToString("D");
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => GuildSnapshotTests.Accept(f, new(f.H.Db, f.H.Files), metadata, 3));
        Assert.Equal(2, await f.H.Db.SaveVersions.Where(v => v.IsCurrent).Select(v => v.Number).SingleAsync());
        Assert.Null((await f.H.Db.Parcels.AsNoTracking().SingleAsync()).DeliveredAt);
    }

    [Fact]
    public async Task Manifest_valued_import_is_allowed_but_an_unexplained_extra_unit_still_freezes()
    {
        using var f = await GuildFixture.Create(); var metadata = await Applied(f);
        var accepted = await GuildSnapshotTests.Accept(f, new(f.H.Db, f.H.Files), metadata, 3);
        var before = await f.H.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.Number == 2);
        before.WealthTotal = 100; accepted.Version.WealthTotal = 113;
        var events = new EventBus(new SessionRegistry(), NullLogger<EventBus>.Instance);
        var feed = new AdminFeed(Options.Create(new AdminOptions()), f.Time, NullLogger<AdminFeed>.Instance);
        var integrity = new IntegrityService(f.H.Db, new PlayerFreeze(f.H.Db, events), feed,
            Options.Create(new IntegrityOptions { WealthWarnPerDay = 1, WealthFreezePerDay = 1 }), NullLogger<IntegrityService>.Instance);
        await integrity.OnStored(1, before, accepted.Version);
        Assert.Null((await f.H.Db.Players.AsNoTracking().SingleAsync(p => p.Id == 1)).EconomyFrozenAt);
        accepted.Version.WealthTotal = 114;
        await integrity.OnStored(1, before, accepted.Version);
        Assert.NotNull((await f.H.Db.Players.AsNoTracking().SingleAsync(p => p.Id == 1)).EconomyFrozenAt);
    }

    [Fact]
    public async Task A_later_snapshot_cannot_erase_an_accepted_guild_receipt()
    {
        using var f = await GuildFixture.Create(); var metadata = await Applied(f); var guard = new OutpostSnapshotGuard(f.H.Db, f.H.Files);
        await GuildSnapshotTests.Accept(f, guard, metadata, 3);
        metadata.GuildMetadata.Receipts.Clear();
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => GuildSnapshotTests.Accept(f, guard, metadata, 4));
        Assert.Equal(3, await f.H.Db.SaveVersions.Where(v => v.IsCurrent).Select(v => v.Number).SingleAsync());
    }

    [Theory]
    [InlineData("BeforePending", false)][InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)][InlineData("AfterExternalCommit", true)]
    public async Task Snapshot_acceptance_interruption_reconciles_parcel_and_home_together(string checkpoint, bool committed)
    {
        using var f = await GuildFixture.Create(); var metadata = await Applied(f); var guard = new OutpostSnapshotGuard(f.H.Db, f.H.Files);
        f.H.Authority.CommitCheckpoint = p => { if (p == checkpoint) throw new Interrupted(); };
        await Assert.ThrowsAsync<Interrupted>(() => GuildSnapshotTests.Accept(f, guard, metadata, 3));
        f.H.Authority.CommitCheckpoint = null; f.H.Db.ChangeTracker.Clear();
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
        Assert.Equal(committed, (await f.H.Db.Parcels.AsNoTracking().SingleAsync()).DeliveredAt is not null);
        Assert.Equal(committed ? 3 : 2, await f.H.Db.SaveVersions.Where(v => v.IsCurrent).Select(v => v.Number).SingleAsync());
        string? first = await f.H.Db.Set<GuildParcelLink>().Select(p => p.AcceptedSnapshotId).SingleAsync();
        await GuildSnapshotTests.Accept(f, guard, metadata, committed ? 4 : 3);
        var after = await f.H.Db.Set<GuildParcelLink>().AsNoTracking().SingleAsync();
        Assert.NotNull(after.AcceptedSnapshotId);
        if (committed) Assert.Equal(first, after.AcceptedSnapshotId);
        Assert.Equal(3, (await f.H.Db.Set<GuildOutpostRow>().AsNoTracking().SingleAsync()).Stock);
    }

    // Server integration fixture: the native physical materialization is qualified separately in the game slice.
    internal static async Task<OutpostSaveMetadata> Applied(GuildFixture f)
    {
        var request = await GuildDeliveryStartTests.Prepared(f);
        var authorization = await f.Service.BeginDeliveryAsync(1, request, default);
        var claim = await f.H.Db.Set<GuildResourceClaim>().AsNoTracking().SingleAsync();
        var metadata = GuildSnapshotTests.Metadata(f);
        metadata.GuildMetadata.Receipts.Add(new GuildDeliveryReceipt { SchemaVersion = 1, AttemptId = authorization.AttemptId,
            ClaimId = claim.Id, ParcelId = request.ParcelId, ClientRequestId = request.ClientRequestId, PayloadDigest = request.PayloadDigest,
            AuthorizationTransitionId = authorization.TransitionId, AuthorizationTransitionDigest = authorization.TransitionDigest,
            AuthorizationIntentDigest = authorization.IntentDigest, BeforeSnapshot = request.BeforeSnapshot.Clone() });
        return metadata;
    }
    private sealed class Interrupted : Exception;
}
