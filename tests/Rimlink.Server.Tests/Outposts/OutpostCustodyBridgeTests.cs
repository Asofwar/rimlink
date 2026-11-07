using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Economy;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Tests.Support;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage.Entities;
using Xunit;
using Lot = Rimlink.Server.Storage.Entities.Lot;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OutpostCustodyBridgeTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("uuid")]
    [InlineData("owner")]
    [InlineData("mirror")]
    [InlineData("epoch")]
    [InlineData("hash")]
    [InlineData("not-removed")]
    [InlineData("blob")]
    public async Task Forged_or_missing_envelope_for_known_pawn_rejected(string fault)
    {
        using var h = await CustodyHarness.Returned();
        var evidence = await h.Depart();
        switch (fault)
        {
            case "missing": evidence.Pawns.Pawns[0].Registry = null; break;
            case "uuid": evidence.Pawns.Pawns[0].Registry.PawnCustodyId = Guid.NewGuid().ToString("D"); break;
            case "owner": evidence.Pawns.Pawns[0].Registry.Scope.OwnerPlayerId = 2; break;
            case "mirror": evidence.Pawns.Pawns[0].Registry.NonAuthoritativeMirror = true; break;
            case "epoch": evidence.RemovalSnapshot.Scope.SaveWriteEpoch++; break;
            case "hash": evidence.RemovalSnapshot.RawSha256 = ByteString.CopyFrom(new byte[32]); break;
            case "not-removed": evidence.ClientRequestId = Guid.NewGuid().ToString("N"); break;
            case "blob": evidence.Pawns.Pawns[0].ScribeXmlZstd = ByteString.CopyFromUtf8("changed-after-capture"); break;
        }
        await using var lease = await h.Authority.AcquireAsync([1], default);
        await Assert.ThrowsAsync<RpcException>(() => h.Bridge.ValidateOutgoingHeldAsync(lease, 1, evidence.Pawns, evidence, default));
        var row = await h.Row(); Assert.Equal("Home", row.LocationKind); Assert.Equal(1, row.PlayerId);
        Assert.Empty(await h.Db.Lots.ToListAsync());
    }

    [Fact]
    public async Task Returned_then_posted_market_uses_exact_removal_and_retains_uuid()
    {
        using var h = await CustodyHarness.Returned();
        var evidence = await h.Depart();
        await h.Post(evidence);
        var row = await h.Row();
        Assert.Equal(SnapshotHarness.Custody, row.Id);
        Assert.Equal(1, row.PlayerId); Assert.Equal("Escrow", row.LocationKind);
        Assert.Equal("market:77", row.LocationId); Assert.True(row.Tombstone);
        var lot = await h.Db.Lots.AsNoTracking().SingleAsync();
        Assert.Equal(SnapshotHarness.Custody, PawnBlob.Parser.ParseFrom(lot.Pawn!).Registry.PawnCustodyId);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Actual_market_post_stages_the_same_uuid_inside_its_authority_commit()
    {
        using var h = await CustodyHarness.Returned(); var evidence = await h.Depart();
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var market = scope.ServiceProvider.GetRequiredService<MarketService>();
        var request = new PostRequest { Pawn = h.Blob.Clone(), Price = 150, ClientRequestId = evidence.ClientRequestId, GameTick = 1000 };
        var lot = await market.Post(1, request);
        var row = await h.Row();
        Assert.Equal("Escrow", row.LocationKind); Assert.Equal("market:" + lot.Id, row.LocationId);
        Assert.Equal(SnapshotHarness.Custody, row.Id); Assert.Equal(1, row.PlayerId);
        Assert.Equal(lot.Id, (await market.Post(1, request)).Id);
        Assert.Single(await h.Db.OutpostTransitions.AsNoTracking().Where(t => t.Kind == "CustodyTransfer").ToListAsync());
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Cross_owner_market_wire_is_parseable_without_replacing_original_registry_scope()
    {
        using var h = await CustodyHarness.Returned(); h.AddRecipient(1);
        h.Db.Wallets.Add(new Rimlink.Server.Storage.Entities.Wallet { PlayerId = 2, Balance = 1000 }); await h.Db.SaveChangesAsync();
        var target = new OutpostSaveMetadata { SchemaVersion = 1, Scope = h.F.Scope.Clone() }; target.Scope.OwnerPlayerId = 2;
        await h.F.F.H.Accept(h.F.F.H.Snapshot(target)); var evidence = await h.Depart();
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var market = scope.ServiceProvider.GetRequiredService<MarketService>();
        var lot = await market.Post(1, new PostRequest { Pawn = h.Blob.Clone(), Price = 150, ClientRequestId = evidence.ClientRequestId, GameTick = 1000 });
        var parcel = await scope.ServiceProvider.GetRequiredService<Rimlink.Server.Application.Economy.MailboxService>().ToProto(await market.Buy(2, lot.Id));
        Assert.Equal(2, parcel.OutpostPayload.Scope.OwnerPlayerId); Assert.Equal(1, parcel.Pawns.Pawns[0].Registry.Scope.OwnerPlayerId);
        Assert.Null(Rimlink.Protocol.OutpostPayloadCompatibility.Refusal(parcel, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Returned_then_sold_keeps_uuid_and_changes_owner_only_with_receipts(bool recipientHasProtectedSnapshot)
    {
        using var h = await CustodyHarness.Returned(); h.AddRecipient(1);
        h.Db.Wallets.Add(new Rimlink.Server.Storage.Entities.Wallet { PlayerId = 2, Balance = 1000 }); await h.Db.SaveChangesAsync();
        var target = new OutpostSaveMetadata { SchemaVersion = 1, Scope = h.F.Scope.Clone() }; target.Scope.OwnerPlayerId = 2;
        var baseline = h.F.F.H.Snapshot(target);
        if (recipientHasProtectedSnapshot) await h.F.F.H.Accept(baseline);
        var evidence = await h.Depart(); using var host = await h.StartHostAsync();
        long parcelId;
        using (var scope = host.Services.CreateScope())
        {
            var market = scope.ServiceProvider.GetRequiredService<MarketService>();
            var lot = await market.Post(1, new PostRequest { Pawn = h.Blob.Clone(), Price = 150, ClientRequestId = evidence.ClientRequestId, GameTick = 1000 });
            parcelId = (await market.Buy(2, lot.Id)).Id;
        }
        Assert.Equal(1, (await h.Row()).PlayerId); Assert.Equal("TransferPending", (await h.Row()).LocationKind);
        if (!recipientHasProtectedSnapshot)
        {
            Assert.False(await h.Db.OutpostSnapshotLinks.AnyAsync(l => l.PlayerId == 2));
            Assert.False(await h.Db.OutpostDeliveryAttempts.AnyAsync(a => a.ParcelId == parcelId));
            Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
            await h.F.F.H.Accept(baseline);
        }
        var link = await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(p => p.ParcelId == parcelId);
        var request = new OutpostDeliveryRequest { Scope = target.Scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("N"),
            ParcelId = parcelId, PayloadDigest = ByteString.CopyFrom(link.PayloadDigest), CanonicalSnapshot = AssignmentHarness.SnapshotProof(baseline) };
        var start = await h.F.Parcels.BeginDeliveryAsync(2, request, default);
        Assert.False(start.AckOnly); Assert.Equal(1, (await h.Row()).PlayerId);
        var attempt = await h.Db.OutpostDeliveryAttempts.AsNoTracking().SingleAsync(a => a.Id == start.AttemptId);
        var transition = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == attempt.TransitionId);
        var registry = h.Blob.Registry.Clone(); registry.Scope = target.Scope.Clone(); registry.PawnReferenceToken = "Thing_ReceivedHuman";
        target.Registry.Add(registry);
        target.Receipts.Add(new OutpostTransitionReceipt { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "",
            TransitionDigest = ByteString.CopyFrom(transition.Digest), ClientRequestId = request.ClientRequestId,
            IntentDigest = ByteString.CopyFrom(attempt.IntentDigest), Snapshot = request.CanonicalSnapshot.Clone(), DeliveryAttemptId = start.AttemptId,
            ParcelIds = { parcelId }, PawnCustodyIds = { SnapshotHarness.Custody }, AssignmentIds = { SnapshotHarness.Assignment } });
        target.Frontier.Add(new OutpostFrontierEntry { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "", TransitionDigest = ByteString.CopyFrom(transition.Digest) });
        await h.F.F.H.Accept(h.F.F.H.Snapshot(target));
        var received = await h.Row(); Assert.Equal(2, received.PlayerId); Assert.Equal("Home", received.LocationKind);
        Assert.Equal("Thing_ReceivedHuman", received.LocationId); Assert.Equal(SnapshotHarness.Custody, received.Id);
        Assert.True((await h.F.Parcels.BeginDeliveryAsync(2, request, default)).AckOnly);
        // The sender's retained tombstone is history, not a fresh bootstrap or a second live pawn.
        var source = await h.Db.OutpostSnapshotLinks.AsNoTracking().SingleAsync(l => l.PlayerId == 1 && l.SnapshotId == evidence.RemovalSnapshot.SnapshotId);
        await h.F.F.H.Accept(h.F.F.H.Snapshot(OutpostSaveMetadata.Parser.ParseFrom(source.Metadata)));
        Assert.Equal(2, (await h.Row()).PlayerId); Assert.False((await h.Row()).Tombstone);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tagged_gift_silver_is_credited_once_when_exact_receipt_is_accepted(bool aged)
    {
        using var h = await CustodyHarness.Returned(); h.AddRecipient(1); await h.Db.SaveChangesAsync();
        var target = new OutpostSaveMetadata { SchemaVersion = 1, Scope = h.F.Scope.Clone() }; target.Scope.OwnerPlayerId = 2;
        var baseline = h.F.F.H.Snapshot(target); await h.F.F.H.Accept(baseline);
        var outgoing = new TransferRequest { ToPlayerId = 2, Silver = 25, Items = new(), Pawns = new() { Pawns = { h.Blob.Clone() } },
            ClientRequestId = Guid.NewGuid().ToString("N"), GameTick = 1000 };
        await h.Depart(outgoing); using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var send = scope.ServiceProvider.GetRequiredService<TransferService>();
        var parcel = await send.Send(1, outgoing);
        await scope.ServiceProvider.GetRequiredService<GiftService>().Answer(2, parcel.Id, true);
        if (aged) await h.Db.Parcels.Where(p => p.Id == parcel.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedAt, DateTime.UtcNow.AddDays(-10)));
        var link = await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(l => l.ParcelId == parcel.Id);
        var request = new OutpostDeliveryRequest { Scope = target.Scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("N"),
            ParcelId = parcel.Id, PayloadDigest = ByteString.CopyFrom(link.PayloadDigest), CanonicalSnapshot = AssignmentHarness.SnapshotProof(baseline) };
        var reply = await h.F.Parcels.BeginDeliveryAsync(2, request, default);
        var transition = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == reply.TransitionId);
        var registry = h.Blob.Registry.Clone(); registry.Scope = target.Scope.Clone(); registry.PawnReferenceToken = "Thing_GiftReceived";
        target.Registry.Add(registry);
        target.Receipts.Add(new OutpostTransitionReceipt { TransitionId = reply.TransitionId, PredecessorId = reply.PredecessorId,
            TransitionDigest = reply.TransitionDigest, ClientRequestId = request.ClientRequestId,
            IntentDigest = ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData(request.ToByteArray())),
            Snapshot = request.CanonicalSnapshot.Clone(), DeliveryAttemptId = reply.AttemptId, ParcelIds = { parcel.Id },
            PawnCustodyIds = { SnapshotHarness.Custody }, AssignmentIds = { SnapshotHarness.Assignment } });
        target.Frontier.Add(new OutpostFrontierEntry { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "", TransitionDigest = reply.TransitionDigest });
        await h.F.F.H.Accept(h.F.F.H.Snapshot(target));
        Assert.Equal(aged ? 22 : 25, await h.Db.Wallets.Where(w => w.PlayerId == 2).Select(w => w.Balance).SingleOrDefaultAsync());
        Assert.Single(await h.Db.Transfers.Where(t => t.FromPlayerId == 2 && t.Kind == "deliver").ToListAsync());
        Assert.Equal(aged ? 1 : 0, await h.Db.WalletEntries.CountAsync(w => w.PlayerId == 2 && w.Kind == "storage"));
        Assert.Single(await h.Db.WalletEntries.Where(w => w.PlayerId == 2 && w.Kind == "receive").ToListAsync());
        await h.F.F.H.Accept(h.F.F.H.Snapshot(target));
        Assert.True((await h.F.Parcels.BeginDeliveryAsync(2, request, default)).AckOnly);
        Assert.Equal(aged ? 22 : 25, await h.Db.Wallets.Where(w => w.PlayerId == 2).Select(w => w.Balance).SingleAsync());
        Assert.Single(await h.Db.Transfers.Where(t => t.FromPlayerId == 2 && t.Kind == "deliver").ToListAsync());
        Assert.Single(await h.Db.WalletEntries.Where(w => w.PlayerId == 2 && w.Kind == "receive").ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Market_withdraw_and_expiry_return_tagged_uuid_through_one_own_parcel(bool expired)
    {
        using var h = await CustodyHarness.Returned(); var evidence = await h.Depart();
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var market = scope.ServiceProvider.GetRequiredService<MarketService>();
        var lot = await market.Post(1, new PostRequest { Pawn = h.Blob.Clone(), Price = 150, ClientRequestId = evidence.ClientRequestId, GameTick = 1000 });
        if (expired) Assert.Equal(1, await market.ExpireDue(DateTime.UtcNow.AddYears(1)));
        else await market.Withdraw(1, lot.Id);
        var row = await h.Row(); Assert.Equal("ReturnPending", row.LocationKind); Assert.Equal(1, row.PlayerId);
        long parcelId = long.Parse(row.LocationId, System.Globalization.CultureInfo.InvariantCulture);
        var link = await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(l => l.ParcelId == parcelId);
        Assert.Equal(1, link.PlayerId); Assert.Equal(SnapshotHarness.Custody, OutpostParcelPayload.Parser.ParseFrom(link.Payload).PawnCustodyIds.Single());
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Gift_refusal_returns_same_uuid_and_does_not_offer_closed_original_delivery()
    {
        using var h = await CustodyHarness.Returned(); h.AddRecipient(1); await h.Db.SaveChangesAsync();
        var target = new OutpostSaveMetadata { SchemaVersion = 1, Scope = h.F.Scope.Clone() }; target.Scope.OwnerPlayerId = 2;
        var baseline = h.F.F.H.Snapshot(target); await h.F.F.H.Accept(baseline);
        var outgoing = new TransferRequest { ToPlayerId = 2, Items = new(), Pawns = new() { Pawns = { h.Blob.Clone() } },
            ClientRequestId = Guid.NewGuid().ToString("N"), GameTick = 1000 };
        await h.Depart(outgoing); using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var send = scope.ServiceProvider.GetRequiredService<TransferService>();
        var parcel = await send.Send(1, outgoing);
        var link = await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(l => l.ParcelId == parcel.Id);
        var delivery = new OutpostDeliveryRequest { Scope = target.Scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("N"),
            ParcelId = parcel.Id, PayloadDigest = ByteString.CopyFrom(link.PayloadDigest), CanonicalSnapshot = AssignmentHarness.SnapshotProof(baseline) };
        await Assert.ThrowsAsync<RpcException>(() => h.F.Parcels.BeginDeliveryAsync(2, delivery, default)); // Still awaiting a gift decision.
        await scope.ServiceProvider.GetRequiredService<GiftService>().Answer(2, parcel.Id, false);
        var row = await h.Row(); Assert.Equal("ReturnPending", row.LocationKind); Assert.Equal(1, row.PlayerId);
        Assert.NotEqual(parcel.Id.ToString(), row.LocationId);
        await Assert.ThrowsAsync<RpcException>(() => h.F.Parcels.BeginDeliveryAsync(2, delivery, default));
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Actual_send_rejects_legacy_recipient_before_creating_mail_or_spending_silver()
    {
        using var h = await CustodyHarness.Returned(); h.AddRecipient(0); await h.Db.SaveChangesAsync();
        var outgoing = new TransferRequest { ToPlayerId = 2, Silver = 10, Items = new(), Pawns = new() { Pawns = { h.Blob.Clone() } },
            ClientRequestId = Guid.NewGuid().ToString("N"), GameTick = 1000 };
        await h.Depart(outgoing); using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TransferService>();
        long before = await h.Db.Wallets.Where(w => w.PlayerId == 1).Select(w => w.Balance).SingleAsync();
        var error = await Assert.ThrowsAsync<RpcException>(() => service.Send(1, outgoing));
        Assert.Equal(ErrorCode.OutpostClientRequired, GrpcTestExtensions.ErrorDetail(error)?.Code);
        Assert.Empty(await h.Db.Parcels.Where(p => p.ToPlayerId == 2).ToListAsync());
        Assert.Equal(before, await h.Db.Wallets.Where(w => w.PlayerId == 1).Select(w => w.Balance).SingleAsync());
        Assert.Equal("Home", (await h.Row()).LocationKind);
    }

    [Fact]
    public async Task Stage_requires_caller_transaction_and_cannot_assign_a_free_form_owner()
    {
        using var h = await CustodyHarness.Returned(); var evidence = await h.Depart();
        await using var lease = await h.Authority.AcquireAsync([1, 2], default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Bridge.StageTransferHeldAsync(lease, evidence, default));
        await using var tx = await h.Db.Database.BeginTransactionAsync();
        evidence.Kind = CustodyTransferKind.CustodyTransferMarketBuy; evidence.SourceId = 987; evidence.RecipientPlayerId = 2;
        await Assert.ThrowsAsync<RpcException>(() => h.Bridge.StageTransferHeldAsync(lease, evidence, default));
        Assert.Equal(1, (await h.Row()).PlayerId);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Legacy_recipient_cannot_receive_tagged_pawn()
    {
        using var h = await CustodyHarness.Returned(); var evidence = await h.Depart();
        h.AddRecipient(0); await h.Db.SaveChangesAsync(); evidence.Kind = CustodyTransferKind.CustodyTransferSend; evidence.RecipientPlayerId = 2;
        await using var lease = await h.Authority.AcquireAsync([1, 2], default);
        await Assert.ThrowsAsync<RpcException>(() => h.Bridge.ValidateOutgoingHeldAsync(lease, 1, evidence.Pawns, evidence, default));
        Assert.Empty(await h.Db.Parcels.Where(p => p.ToPlayerId == 2).ToListAsync());
        Assert.Equal(1, (await h.Row()).PlayerId);
    }

    [Fact]
    public async Task Snapshot_raid_or_visit_mirror_never_becomes_second_authority()
    {
        using var h = await CustodyHarness.Returned(); h.AddRecipient(1); await h.Db.SaveChangesAsync();
        var evidence = h.Evidence(h.HomeSave); evidence.Kind = CustodyTransferKind.CustodyTransferRaidLaunch;
        evidence.RecipientPlayerId = 2; evidence.NonAuthoritativeMirror = true;
        evidence.Pawns.Pawns[0].Registry.NonAuthoritativeMirror = true;
        await using var lease = await h.Authority.AcquireAsync([1, 2], default);
        await h.Bridge.ValidateOutgoingHeldAsync(lease, 1, evidence.Pawns, evidence, default);
        var row = await h.Row(); Assert.False(row.Tombstone); Assert.Equal("Home", row.LocationKind);
        Assert.Equal(1, row.PlayerId); Assert.Single(await h.Db.PawnCustody.ToListAsync());
    }

    [Fact]
    public async Task Untagged_legacy_pawn_needs_no_outpost_snapshot_or_owner_handoff()
    {
        using var h = new AuthorityHarness(); var bridge = new PawnCustodyBridge(h.Db, h.Authority, new SessionRegistry(), h.Files, h.Owners);
        var pawns = new PawnList { Pawns = { new PawnBlob { KindDef = "Colonist", ScribeXmlZstd = ByteString.CopyFromUtf8("legacy") } } };
        await using var lease = await h.Authority.AcquireAsync([1], default);
        await bridge.ValidateOutgoingHeldAsync(lease, 1, pawns, new CustodyTransferEvidence(), default);
        Assert.Empty(await h.Db.PawnCustody.ToListAsync());
    }
}

internal sealed class CustodyHarness : IDisposable
{
    public MutationHarness F { get; }
    public Rimlink.Server.Storage.RimlinkDbContext Db => F.Db;
    public Rimlink.Server.Application.Outposts.OutpostAuthority Authority => F.F.H.H.Authority;
    public SessionRegistry Sessions { get; } = new();
    public PawnCustodyBridge Bridge { get; }
    public EvidenceSave HomeSave { get; private set; } = null!;
    public PawnBlob Blob { get; private set; } = null!;
    private CustodyHarness(MutationHarness f) { F = f; Bridge = new(Db, Authority, Sessions, f.F.H.H.Files, f.F.H.H.Owners); }

    public static async Task<CustodyHarness> Returned()
    {
        var h = new CustodyHarness(await MutationHarness.Create());
        long parcel = Assert.Single((await h.F.Service.MutateAsync(1, h.F.Request(OutpostMutationKind.Close), default)).ParcelIds);
        var link = await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(p => p.ParcelId == parcel);
        var request = new OutpostDeliveryRequest { Scope = h.F.Scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("N"),
            ParcelId = parcel, PayloadDigest = ByteString.CopyFrom(link.PayloadDigest), CanonicalSnapshot = AssignmentHarness.SnapshotProof(h.F.Post) };
        var reply = await h.F.Parcels.BeginDeliveryAsync(1, request, default);
        var attempt = await h.Db.OutpostDeliveryAttempts.AsNoTracking().SingleAsync();
        var transition = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == attempt.TransitionId);
        var metadata = h.F.Post.Recovery.Outposts.Metadata.Clone();
        var registry = metadata.Registry[0]; registry.Tombstone = false; registry.PawnReferenceToken = "Thing_HumanReturned";
        metadata.Receipts.Add(new OutpostTransitionReceipt { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "",
            TransitionDigest = ByteString.CopyFrom(transition.Digest), ClientRequestId = request.ClientRequestId, IntentDigest = ByteString.CopyFrom(attempt.IntentDigest),
            Snapshot = request.CanonicalSnapshot.Clone(), DeliveryAttemptId = reply.AttemptId, ParcelIds = { parcel },
            PawnCustodyIds = { SnapshotHarness.Custody }, AssignmentIds = { SnapshotHarness.Assignment } });
        metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "", TransitionDigest = ByteString.CopyFrom(transition.Digest) });
        h.Blob = h.F.Workers[0].Pawn.Clone(); h.Blob.Registry = registry.Clone();
        var capture = h.F.Workers[0].Clone(); capture.Pawn = h.Blob.Clone(); capture.BlobSha256 = ByteString.CopyFrom(SHA256.HashData(capture.Pawn.ToByteArray()));
        metadata.WorkerCaptures.Clear(); metadata.WorkerCaptures.Add(capture);
        h.HomeSave = h.F.F.H.Snapshot(metadata); await h.F.F.H.Accept(h.HomeSave); return h;
    }

    public CustodyTransferEvidence Evidence(EvidenceSave save) => new()
    {
        TransitionId = Guid.NewGuid().ToString("D"), Kind = CustodyTransferKind.CustodyTransferMarketPost, ActorPlayerId = 1,
        ClientRequestId = Guid.NewGuid().ToString("N"), RemovalSnapshot = AssignmentHarness.SnapshotProof(save),
        Pawns = new PawnList { Pawns = { Blob.Clone() } }, SourceRequestIntent = ByteString.CopyFrom(SHA256.HashData("source-intent"u8))
    };
    public async Task<CustodyTransferEvidence> Depart(IMessage? request = null)
    {
        request ??= new PostRequest { Pawn = Blob.Clone(), Price = 150, GameTick = 1000, ClientRequestId = Guid.NewGuid().ToString("N") };
        var (kind, requestId, pawns) = Rimlink.Protocol.OrdinaryCapture.Identity(request);
        var metadata = HomeSave.Recovery.Outposts.Metadata.Clone();
        var capture = Rimlink.Protocol.OrdinaryCapture.Create(request, Guid.NewGuid().ToString("N"), metadata.Scope);
        metadata.OrdinaryOutgoing.Add(capture.Clone());
        foreach (var pawn in capture.Pawns)
            metadata.Registry.Single(r => r.PawnCustodyId == pawn.Pawn.Registry.PawnCustodyId).Tombstone = true;
        var post = F.F.H.Snapshot(metadata, removed: [capture.LocalOperationId]); await F.F.H.Accept(post);
        var evidence = Evidence(post); evidence.ClientRequestId = requestId; evidence.Pawns = pawns.Clone();
        evidence.SourceRequestIntent = ByteString.CopyFrom(SHA256.HashData(request.ToByteArray()));
        evidence.OrdinaryRequestKind = kind; evidence.OrdinaryCapture = capture.Clone();
        evidence.Kind = kind switch
        {
            OrdinaryRequestKind.TransferSend => CustodyTransferKind.CustodyTransferSend,
            OrdinaryRequestKind.MarketPost => CustodyTransferKind.CustodyTransferMarketPost,
            OrdinaryRequestKind.TradeOpen or OrdinaryRequestKind.TradeSide => CustodyTransferKind.CustodyTransferTradeSide,
            OrdinaryRequestKind.RaidLaunch => CustodyTransferKind.CustodyTransferRaidLaunch,
            OrdinaryRequestKind.LivePropose => CustodyTransferKind.CustodyTransferLiveLaunch,
            OrdinaryRequestKind.RaidReport => CustodyTransferKind.CustodyTransferRaidReport,
            OrdinaryRequestKind.LiveReport => CustodyTransferKind.CustodyTransferLiveReturn,
            _ => throw new InvalidOperationException("unsupported fixture route")
        };
        return evidence;
    }
    public void AddRecipient(uint version)
    {
        var account = new Account { Id = 2 }; var player = new Player { Id = 2, Name = "Receiver" };
        account.Player = player; Db.Accounts.Add(account); var session = Sessions.Start(account, player, "test", out _);
        session.OutpostVersion = version;
    }
    public async Task Post(CustodyTransferEvidence evidence)
    {
        await using var lease = await Authority.AcquireAsync([1], default);
        await Bridge.ValidateOutgoingHeldAsync(lease, 1, evidence.Pawns, evidence, default);
        var head = await Db.OutpostAuthority.AsNoTracking().SingleAsync(); evidence.SourceId = 77;
        await Authority.CommitHeldAsync(lease, new(evidence.TransitionId, "world-1", 1, 0, head.RestoreBoundary,
            "CustodyTransfer", "fixture", new(head.HeadId, ByteString.CopyFrom(head.HeadDigest)), ByteString.CopyFrom(evidence.ToByteArray())), async (db, ct) =>
        {
            db.Lots.Add(new Lot { Id = 77, SellerPlayerId = 1, Pawn = evidence.Pawns.Pawns[0].ToByteArray(),
                ClientRequestId = evidence.ClientRequestId, RequestIntent = evidence.SourceRequestIntent.ToByteArray(), Status = LotStatusDb.Open });
            await db.SaveChangesAsync(ct); await Bridge.StageTransferHeldAsync(lease, evidence, ct);
        }, default);
    }
    public async Task<ServerFixture> StartHostAsync(bool allowKidnap = false)
    {
        var shared = F.F.H.H;
        await OutpostUnitComposition.Activate(shared);
        var host = HygieneFixtures.HygieneServer().WithoutWorld().WithDataDir(shared.Data.Root).KeepDataDir()
            .WithSetting("Raids:AllowKidnap", allowKidnap ? "true" : "false")
            .WithSetting("Outposts:AnchorDirectory", Path.Combine(shared.Root, "external"))
            .WithSetting("Outposts:StorageContract", "sqlite-local-v1")
            .WithServices(s => { s.AddSingleton(shared.Files); s.AddSingleton(shared.Owners); s.AddSingleton(shared.Anchor); s.AddSingleton(Sessions); })
            .WithOutpostUnitQualification();
        _ = host.Services; return host;
    }
    public Task<PawnCustodyRecord> Row() => Db.PawnCustody.AsNoTracking().SingleAsync(p => p.Id == SnapshotHarness.Custody);
    public void Dispose() => F.Dispose();
}
