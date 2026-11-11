using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

public sealed class GuildDeliveryStartTests
{
    [Theory]
    [InlineData("valid")][InlineData("recipient")][InlineData("digest")]
    public async Task Resource_manifest_is_pinned_to_the_fixed_recipient_and_original_payload(string change)
    {
        using var f = await GuildFixture.Create(); var delivery = await Prepared(f);
        var link = await f.H.Db.Set<GuildParcelLink>().AsNoTracking().SingleAsync();
        var payload = GuildResourceParcelPayload.Parser.ParseFrom(link.Payload);
        var request = new GuildManifestReadRequest { Scope = delivery.Scope.Clone(), ParcelId = delivery.ParcelId,
            ManifestId = payload.ManifestId, ManifestDigest = payload.ManifestDigest };
        if (change == "recipient") request.Scope.PlayerId = 2;
        if (change == "digest") request.ManifestDigest = ByteString.CopyFrom(new byte[32]);
        if (change != "valid")
            await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.ReadDeliveryManifestAsync(1, request, default));
        else
        {
            var page = await f.Service.ReadDeliveryManifestAsync(1, request, default);
            var document = OutpostManifestDocument.Parser.ParseFrom(page.Data);
            Assert.Equal(payload.UnitValueX100, document.Resources.Single(r => r.DefName == payload.DefName).UnitValueX100);
            Assert.Equal(Convert.ToHexStringLower(payload.ManifestDigest.Span), page.Digest);
        }
        Assert.Empty(await f.H.Db.Set<GuildDeliveryAttempt>().ToArrayAsync());
    }
    [Fact]
    public async Task Begin_binds_the_real_protected_before_snapshot_and_does_not_deliver_the_parcel()
    {
        using var f = await GuildFixture.Create(); var request = await Prepared(f);
        var first = await f.Service.BeginDeliveryAsync(1, request, default);
        Assert.Equal(first, await f.Service.BeginDeliveryAsync(1, request, default));
        Assert.Equal(request, first.Request); Assert.Equal(32, first.TransitionDigest.Length); Assert.False(first.AckOnly);
        Assert.Null((await f.H.Db.Parcels.AsNoTracking().SingleAsync()).DeliveredAt);
        Assert.Null((await f.H.Db.Set<GuildParcelLink>().AsNoTracking().SingleAsync()).AcceptedSnapshotId);
        Assert.True((await f.H.Db.Set<GuildDeliveryAttempt>().AsNoTracking().SingleAsync()).Active);
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData("before-hash")][InlineData("snapshot")][InlineData("generation")][InlineData("epoch")][InlineData("recipient")][InlineData("payload")]
    public async Task A_forged_or_stale_before_proof_creates_no_attempt(string fault)
    {
        using var f = await GuildFixture.Create(); var request = await Prepared(f);
        if (fault == "before-hash") request.BeforeSnapshot.RawSha256 = ByteString.CopyFrom(new byte[32]);
        if (fault == "snapshot") request.BeforeSnapshot.SnapshotId = Guid.NewGuid().ToString("N");
        if (fault == "generation") { request.Scope.SaveGeneration++; request.BeforeSnapshot.Scope.SaveGeneration++; }
        if (fault == "epoch") { request.Scope.SaveWriteEpoch++; request.BeforeSnapshot.Scope.SaveWriteEpoch++; }
        if (fault == "recipient") { request.Scope.PlayerId = 2; request.BeforeSnapshot.Scope.PlayerId = 2; }
        if (fault == "payload") request.PayloadDigest = ByteString.CopyFrom(new byte[32]);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => f.Service.BeginDeliveryAsync(1, request, default));
        Assert.Empty(await f.H.Db.Set<GuildDeliveryAttempt>().ToArrayAsync());
        Assert.Null((await f.H.Db.Parcels.AsNoTracking().SingleAsync()).DeliveredAt);
    }

    [Theory]
    [InlineData("BeforePending", false)][InlineData("AfterPending", false)]
    [InlineData("AfterDomainCommit", true)][InlineData("AfterExternalCommit", true)]
    public async Task Begin_crash_and_retry_preserve_one_authorization(string checkpoint, bool committed)
    {
        using var f = await GuildFixture.Create(); var request = await Prepared(f);
        f.H.Authority.CommitCheckpoint = p => { if (p == checkpoint) throw new Interrupted(); };
        await Assert.ThrowsAsync<Interrupted>(() => f.Service.BeginDeliveryAsync(1, request, default));
        f.H.Authority.CommitCheckpoint = null; f.H.Db.ChangeTracker.Clear();
        Assert.Equal(OutpostStartupState.Ready, (await f.H.Authority.OpenAsync(default)).State);
        Assert.Equal(committed ? 1 : 0, await f.H.Db.Set<GuildDeliveryAttempt>().CountAsync());
        await f.Service.BeginDeliveryAsync(1, request, default);
        Assert.Single(await f.H.Db.Set<GuildDeliveryAttempt>().ToArrayAsync());
    }

    internal static async Task<GuildDeliveryRequest> Prepared(GuildFixture f)
    {
        var source = await f.Place(0);
        var consent = await f.Service.PreviewConsentAsync(1, 50, f.Scope, source.OutpostId, 4, default);
        await f.Service.AcceptConsentAsync(1, 50, f.Scope, Guid.NewGuid().ToString("D"), source.OutpostId, 4, consent.Digest, default);
        f.Time.Advance(TimeSpan.FromHours(1));
        var claim = await f.Service.ClaimResourcesAsync(1, new(f.Scope, Guid.NewGuid().ToString("D"), 50, "Outpost", source.OutpostId, 1, 1, 0, 0, 7), default);
        var metadata = GuildSnapshotTests.Metadata(f);
        var save = await GuildSnapshotTests.Accept(f, new OutpostSnapshotGuard(f.H.Db, f.H.Files), metadata, 2);
        return new() { Scope = metadata.GuildMetadata.Scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("D"), ParcelId = claim.ParcelId,
            PayloadDigest = ByteString.CopyFrom(Convert.FromHexString(claim.PayloadDigest)), BeforeSnapshot = new() { Scope = metadata.GuildMetadata.Scope.Clone(),
                SnapshotId = save.Version.SnapshotId, RawSha256 = ByteString.CopyFrom(save.Version.RawSha256!) } };
    }

    private sealed class Interrupted : Exception;
}
