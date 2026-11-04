using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OrdinaryCaptureTests
{
    [Fact]
    public async Task Actual_changed_pawn_uses_ordinary_capture_and_local_id_not_stale_worker_capture()
    {
        using var h = await CustodyHarness.Returned();
        var evidence = await Remove(h, changed: true);
        Assert.NotEqual(evidence.ClientRequestId, evidence.OrdinaryCapture.LocalOperationId);
        Assert.NotEqual(h.HomeSave.Recovery.Outposts.Metadata.WorkerCaptures[0].Pawn.ScribeXmlZstd, evidence.Pawns.Pawns[0].ScribeXmlZstd);
        await using var lease = await h.Authority.AcquireAsync([1], default);
        await h.Bridge.ValidateOutgoingHeldAsync(lease, 1, evidence.Pawns, evidence, default);
        Assert.Equal(SnapshotHarness.Custody, (await h.Row()).Id);
        Assert.True((await h.Row()).Tombstone); Assert.Empty(await h.Db.Lots.ToListAsync());
    }

    [Theory]
    [InlineData("missing-capture")][InlineData("stale-blob")][InlineData("wrong-local-id")]
    [InlineData("cid-receipt-only")][InlineData("wrong-intent")][InlineData("wrong-kind")]
    [InlineData("wrong-current-reference")][InlineData("changed-wire-capture")]
    public async Task Legacy_receipt_worker_capture_or_changed_evidence_cannot_authorize_ordinary_removal(string fault)
    {
        using var h = await CustodyHarness.Returned();
        var evidence = await Remove(h, changed: false, fault);
        await using var lease = await h.Authority.AcquireAsync([1], default);
        await Assert.ThrowsAsync<RpcException>(() => h.Bridge.ValidateOutgoingHeldAsync(lease, 1, evidence.Pawns, evidence, default));
        Assert.Empty(await h.Db.Lots.ToListAsync()); Assert.Equal("Home", (await h.Row()).LocationKind);
    }

    [Fact]
    public async Task Typed_kind_and_exact_final_hash_are_retained_in_the_actual_market_transition()
    {
        using var h = await CustodyHarness.Returned();
        var evidence = await Remove(h, changed: true);
        using var host = h.StartHost(); using var scope = host.Services.CreateScope();
        var market = scope.ServiceProvider.GetRequiredService<Rimlink.Server.Application.Economy.MarketService>();
        var request = new PostRequest { Pawn = evidence.Pawns.Pawns[0].Clone(), Price = 150, GameTick = 1234, ClientRequestId = evidence.ClientRequestId };
        var lot = await market.Post(1, request);
        var transition = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Kind == "CustodyTransfer");
        var stored = CustodyTransferEvidence.Parser.ParseFrom(transition.Intent);
        Assert.Equal(OrdinaryRequestKind.MarketPost, stored.OrdinaryRequestKind);
        Assert.Equal(evidence.OrdinaryCapture, stored.OrdinaryCapture);
        Assert.Equal(SHA256.HashData(request.ToByteArray()), stored.SourceRequestIntent.ToByteArray());
        Assert.Equal("market:" + lot.Id, (await h.Row()).LocationId);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    private static async Task<CustodyTransferEvidence> Remove(CustodyHarness h, bool changed, string? fault = null)
    {
        var metadata = h.HomeSave.Recovery.Outposts.Metadata.Clone();
        var pawn = h.Blob.Clone();
        if (changed) { pawn.ScribeXmlZstd = ByteString.CopyFromUtf8("current health inventory relations prisoner or disabled state"); pawn.Label = "Current pawn"; }
        var request = new PostRequest { Pawn = pawn, Price = 150, GameTick = 1234, ClientRequestId = Guid.NewGuid().ToString("N") };
        var capture = OrdinaryCapture.Create(request, Guid.NewGuid().ToString("N"), metadata.Scope);
        var recorded = metadata.Registry.Single(r => r.PawnCustodyId == pawn.Registry.PawnCustodyId);
        recorded.Tombstone = true;
        switch (fault)
        {
            case "stale-blob": capture.Pawns[0].Pawn.ScribeXmlZstd = ByteString.CopyFromUtf8("stale health state");
                capture.Pawns[0].BlobSha256 = ByteString.CopyFrom(SHA256.HashData(capture.Pawns[0].Pawn.ToByteArray())); break;
            case "wrong-current-reference": recorded.PawnReferenceToken = "Thing_OtherPawn"; break;
        }
        if (fault != "missing-capture") metadata.OrdinaryOutgoing.Add(capture.Clone());
        string receipt = fault == "cid-receipt-only" ? request.ClientRequestId : capture.LocalOperationId;
        var post = h.F.F.H.Snapshot(metadata, removed: [receipt]); await h.F.F.H.Accept(post);
        var evidence = h.Evidence(post); evidence.Pawns = new() { Pawns = { pawn.Clone() } };
        evidence.ClientRequestId = request.ClientRequestId;
        evidence.SourceRequestIntent = ByteString.CopyFrom(SHA256.HashData(request.ToByteArray()));
        evidence.OrdinaryRequestKind = OrdinaryRequestKind.MarketPost; evidence.OrdinaryCapture = capture.Clone();
        switch (fault)
        {
            case "wrong-local-id": evidence.OrdinaryCapture.LocalOperationId = Guid.NewGuid().ToString("N"); break;
            case "wrong-intent": evidence.SourceRequestIntent = ByteString.CopyFrom(new byte[32]); break;
            case "wrong-kind": evidence.OrdinaryRequestKind = OrdinaryRequestKind.TradeSide; break;
            case "changed-wire-capture": evidence.OrdinaryCapture.Pawns[0].Pawn.Label = "different after capture";
                evidence.OrdinaryCapture.Pawns[0].BlobSha256 = ByteString.CopyFrom(SHA256.HashData(evidence.OrdinaryCapture.Pawns[0].Pawn.ToByteArray())); break;
        }
        return evidence;
    }
}
