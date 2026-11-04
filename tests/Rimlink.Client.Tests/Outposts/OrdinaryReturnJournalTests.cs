using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Client.Outposts;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;
namespace Rimlink.Client.Tests.Outposts;

public sealed partial class OrdinaryOutboxTests
{
    private const string Branch = "55555555-5555-5555-5555-555555555555";
    private (TransferOutbox Box, OutboxEntry Entry, SaveMeta Meta, OrdinaryCancelRequest Request) ReturnFixture()
    {
        var box = Open(); var original = Request(); original.Items = new() { Items = { new ItemBlob { DefName = "Steel", Count = 13 } } };
        var entry = box.PrepareOrdinary(original, Scope()); var meta = Meta(entry);
        box.BindOrdinarySnapshot(entry.Id, meta); box.ReconcileSnapshot(meta, accepted: true);
        var canonical = OutpostSnapshotProof.Parser.ParseFrom(Assert.Single(Open().Entries).Ordinary!.AcceptedProof);
        return (box, entry, meta, box.PrepareOrdinaryReturn(entry.Id, canonical, Branch));
    }
    private static OrdinaryOperationReply Started(OrdinaryCancelRequest request)
    {
        var original = TransferRequest.Parser.ParseFrom(request.OriginalRequest);
        var payload = new OrdinaryReturnPayload { Items = original.Items?.Clone() ?? new(), Pawns = original.Pawns.Clone() };
        return new() { Status = OrdinaryOperationStatus.ReturnStarted, ReturnProof = new() {
            Authorization = request.Clone(), TransitionId = request.ReturnAttemptId,
            TransitionDigest = ByteString.CopyFrom(Enumerable.Repeat((byte)6, 32).ToArray()),
            PredecessorId = "66666666-6666-6666-6666-666666666666", Payload = payload,
            PayloadDigest = ByteString.CopyFrom(SHA256.HashData(payload.ToByteArray())) } };
    }
    private static SaveMeta ReturnedMeta(SaveMeta meta, OutboxEntry entry, OrdinaryCancelRequest request, OrdinaryOperationReply reply)
    {
        var metadata = meta.Recovery!.Outposts.Metadata.Clone(); metadata.Registry[0].Tombstone = false;
        var receipt = OrdinaryReturnEvidence.Receipt(request, reply.ReturnProof);
        metadata.Receipts.Add(receipt); metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = receipt.TransitionId, TransitionDigest = receipt.TransitionDigest });
        return meta with { RawSha256 = Enumerable.Repeat((byte)7, 32).ToArray(), Recovery = meta.Recovery with {
            SnapshotId = "77777777777777777777777777777777", Returned = [entry.Id], Outposts = new() { Metadata = metadata } } };
    }
    [Fact]
    public void Return_journal_freezes_original_command_and_one_attempt_across_reopen()
    {
        var f = ReturnFixture(); byte[] original = f.Entry.Payload.ToArray(); byte[] intent = f.Request.ToByteArray();
        f.Request.OriginalRequest = ByteString.Empty;
        var kept = Assert.Single(Open().Entries);
        Assert.Equal(original, kept.Payload); Assert.Equal(intent, kept.Ordinary!.ReturnRequest);
        var frozen = OrdinaryCancelRequest.Parser.ParseFrom(intent);
        Assert.Equal(f.Entry.Id, frozen.Capture.LocalOperationId); Assert.NotEqual(f.Entry.Id, frozen.Capture.ClientRequestId);
        Assert.Equal(intent, Open().PrepareOrdinaryReturn(f.Entry.Id, frozen.CanonicalSnapshot, Branch).ToByteArray());
        Assert.False(TransferOutbox.ReadyFor(kept, Login()));
        Assert.Throws<InvalidDataException>(() => Open().PrepareOrdinaryReturn(f.Entry.Id, frozen.CanonicalSnapshot, Guid.NewGuid().ToString("D")));
    }
    [Fact]
    public void Exact_return_receipt_covers_causal_authorization_and_all_custody_but_no_wallet_silver()
    {
        var f = ReturnFixture(); var reply = Started(f.Request); var receipt = OrdinaryReturnEvidence.Receipt(f.Request, reply.ReturnProof);
        Assert.Equal(f.Request.ReturnAttemptId, receipt.OrdinaryReturnAttemptId); Assert.Equal(f.Entry.Id, receipt.OrdinaryLocalOperationId);
        Assert.Equal(f.Request.Capture.ClientRequestId, receipt.ClientRequestId);
        Assert.Equal(SHA256.HashData(f.Request.ToByteArray()), receipt.IntentDigest.ToByteArray());
        Assert.Equal(f.Request.CanonicalSnapshot, receipt.Snapshot);
        Assert.Equal(new[] { "33333333-3333-3333-3333-333333333333" }, receipt.PawnCustodyIds);
        Assert.Empty(receipt.AssignmentIds); Assert.Empty(receipt.ParcelIds); Assert.Equal("", receipt.DeliveryAttemptId);
        Assert.Equal(13, Assert.Single(reply.ReturnProof.Payload.Items.Items).Count);
    }
    [Theory]
    [InlineData("authorization")][InlineData("payload")][InlineData("payload-digest")][InlineData("causal-digest")]
    [InlineData("attempt")][InlineData("predecessor")][InlineData("original")]
    public void Changed_return_authorization_never_authorizes_materialization(string fault)
    {
        var f = ReturnFixture(); var reply = Started(f.Request);
        switch (fault) {
            case "authorization": reply.ReturnProof.Authorization.Capture.LocalOperationId = Guid.NewGuid().ToString("N"); break;
            case "payload": reply.ReturnProof.Payload.Items.Items[0].Count++; reply.ReturnProof.PayloadDigest = ByteString.CopyFrom(SHA256.HashData(reply.ReturnProof.Payload.ToByteArray())); break;
            case "payload-digest": reply.ReturnProof.PayloadDigest = ByteString.CopyFrom(new byte[32]); break;
            case "causal-digest": reply.ReturnProof.TransitionDigest = ByteString.Empty; break;
            case "attempt": reply.ReturnProof.TransitionId = Guid.NewGuid().ToString("D"); break;
            case "predecessor": reply.ReturnProof.PredecessorId = "not-an-id"; break;
            case "original": f.Request.OriginalRequest = ByteString.CopyFrom(new byte[3]); reply.ReturnProof.Authorization = f.Request.Clone(); break;
        }
        Assert.Throws<InvalidDataException>(() => OrdinaryReturnEvidence.Receipt(f.Request, reply.ReturnProof));
        Assert.Equal(MaterializationDecision.RecoveryRequired, OrdinaryReturnEvidence.Decide(f.Request, reply, f.Meta.Recovery!.Outposts.Metadata,
            Branch, Branch, false, true, true));
        Assert.Throws<InvalidDataException>(() => f.Box.RecordOrdinaryReturnReply(f.Entry.Id, f.Request, reply));
        Assert.Null(Assert.Single(Open().Entries).Ordinary!.ReturnReply);
    }
    [Theory]
    [InlineData("cold")][InlineData("started")][InlineData("unsafe")][InlineData("session")]
    [InlineData("scope")][InlineData("accepted-without-receipt")][InlineData("unknown")]
    public void Cold_unsafe_started_or_unproven_return_never_spawns(string fault)
    {
        var f = ReturnFixture(); var reply = Started(f.Request); var metadata = f.Meta.Recovery!.Outposts.Metadata;
        if (fault == "scope") metadata.Scope.RestoreBoundary = "other-boundary";
        if (fault == "accepted-without-receipt") reply.Status = OrdinaryOperationStatus.ReturnAccepted;
        if (fault == "unknown") { reply.Status = OrdinaryOperationStatus.Unknown; reply.ReturnProof = null; }
        Assert.Equal(MaterializationDecision.RecoveryRequired, OrdinaryReturnEvidence.Decide(f.Request, reply, metadata,
            Branch, fault == "cold" ? Guid.NewGuid().ToString("D") : Branch, fault == "started", fault != "unsafe", fault != "session"));
    }
    [Fact]
    public void Spawn_fence_is_durable_before_materialization_and_cannot_be_rearmed()
    {
        var f = ReturnFixture(); var reply = Started(f.Request);
        Assert.Throws<InvalidDataException>(() => f.Box.MarkOrdinaryReturnSpawnStarted(f.Entry.Id, Branch));
        f.Box.RecordOrdinaryReturnReply(f.Entry.Id, f.Request, reply);
        Assert.Equal(MaterializationDecision.Materialize, OrdinaryReturnEvidence.Decide(f.Request, reply, f.Meta.Recovery!.Outposts.Metadata, Branch, Branch, false, true, true));
        f.Box.MarkOrdinaryReturnSpawnStarted(f.Entry.Id, Branch);
        var kept = Assert.Single(Open().Entries); Assert.True(kept.Ordinary!.SpawnStarted);
        Assert.Equal(reply.ToByteArray(), kept.Ordinary.ReturnReply); Assert.Equal(f.Entry.Payload, kept.Payload);
        Assert.Throws<InvalidDataException>(() => Open().MarkOrdinaryReturnSpawnStarted(f.Entry.Id, Branch));
        Assert.Equal(MaterializationDecision.RecoveryRequired, OrdinaryReturnEvidence.Decide(f.Request, reply, f.Meta.Recovery.Outposts.Metadata, Branch, Branch, true, true, true));
    }
    [Fact]
    public void Exact_known_return_receipt_is_ack_only_even_after_cold_load()
    {
        var f = ReturnFixture(); var reply = Started(f.Request); var meta = ReturnedMeta(f.Meta, f.Entry, f.Request, reply);
        Assert.Equal(MaterializationDecision.AckOnly, OrdinaryReturnEvidence.Decide(f.Request, reply, meta.Recovery!.Outposts.Metadata,
            Branch, Guid.NewGuid().ToString("D"), true, true, true));
        meta.Recovery.Outposts.Metadata.Receipts[0].TransitionDigest = ByteString.CopyFrom(new byte[32]);
        Assert.Equal(MaterializationDecision.RecoveryRequired, OrdinaryReturnEvidence.Decide(f.Request, reply, meta.Recovery.Outposts.Metadata,
            Branch, Branch, true, true, true));
    }
    [Fact]
    public void Only_accepted_exact_Returned_and_typed_receipt_can_remove_the_return_journal()
    {
        var f = ReturnFixture(); var reply = Started(f.Request); f.Box.RecordOrdinaryReturnReply(f.Entry.Id, f.Request, reply);
        f.Box.MarkOrdinaryReturnSpawnStarted(f.Entry.Id, Branch);
        f.Box.ReconcileSnapshot(f.Meta.Recovery! with { Returned = [f.Entry.Id] }, accepted: true);
        Assert.Single(Open().Entries);
        var meta = ReturnedMeta(f.Meta, f.Entry, f.Request, reply);
        f.Box.ReconcileSnapshot(meta, accepted: false); Assert.Single(Open().Entries);
        f.Box.ReconcileSnapshot(meta, accepted: true); Assert.Empty(Open().Entries);
    }
}
