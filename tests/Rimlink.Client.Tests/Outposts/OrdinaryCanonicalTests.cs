using Google.Protobuf;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Client.Outposts;
using Rimlink.Protocol.V1;
using Xunit;
namespace Rimlink.Client.Tests.Outposts;

public sealed partial class OrdinaryOutboxTests
{
    private static SaveMeta LaterRemoval(SaveMeta first) => first with {
        GameTick = 10001, RawSha256 = Enumerable.Repeat((byte)10, 32).ToArray(),
        Recovery = first.Recovery! with { SnapshotId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } };
    private static OutpostSnapshotProof ProofOf(SaveMeta meta) => new() { Scope = meta.Recovery!.Outposts.Metadata.Scope.Clone(),
        SnapshotId = meta.Recovery.SnapshotId, RawSha256 = ByteString.CopyFrom(meta.RawSha256!) };

    [Fact]
    public void Later_accepted_canonical_keeps_immutable_original_removal_and_can_fence_return()
    {
        var box = Open(); var entry = box.PrepareOrdinary(Request(), Scope()); var first = Meta(entry);
        box.BindOrdinarySnapshot(entry.Id, first); box.ReconcileSnapshot(first, accepted: true);
        var later = LaterRemoval(first); box.ReconcileSnapshot(later, accepted: true, selected: true);
        var kept = Assert.Single(Open().Entries);
        Assert.Equal(OutboxPhase.Ready, kept.Phase);
        Assert.Equal(ProofOf(first).ToByteArray(), kept.Ordinary!.RemovalProof);
        Assert.Equal(ProofOf(first).ToByteArray(), kept.Ordinary.AcceptedProof);
        Assert.Equal(ProofOf(later).ToByteArray(), kept.Ordinary.CanonicalProof);
        Assert.Equal(first.Recovery!.SnapshotId, kept.SnapshotId);
        var cancel = Open().PrepareOrdinaryReturn(entry.Id, ProofOf(later), Branch);
        Assert.Equal(ProofOf(first), cancel.RemovalSnapshot); Assert.Equal(ProofOf(later), cancel.CanonicalSnapshot);
        Assert.Equal(Request().ToByteArray(), cancel.OriginalRequest.ToByteArray());
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void A_later_file_without_original_acceptance_cannot_unlock_original_removal(bool selected)
    {
        var box = Open(); var entry = box.PrepareOrdinary(Request(), Scope()); var first = Meta(entry);
        box.BindOrdinarySnapshot(entry.Id, first); box.ReconcileSnapshot(LaterRemoval(first), accepted: true, selected: selected);
        var kept = Assert.Single(Open().Entries);
        Assert.Null(kept.Ordinary!.AcceptedProof); Assert.Null(kept.Ordinary.CanonicalProof);
        Assert.False(TransferOutbox.ReadyFor(kept, Login()));
    }
    [Fact]
    public void Unaccepted_later_file_cannot_be_used_as_a_return_canonical_proof()
    {
        var box = Open(); var entry = box.PrepareOrdinary(Request(), Scope()); var first = Meta(entry);
        box.BindOrdinarySnapshot(entry.Id, first); box.ReconcileSnapshot(first, accepted: true);
        var later = LaterRemoval(first); box.ReconcileSnapshot(later, accepted: false);
        Assert.Throws<InvalidDataException>(() => box.PrepareOrdinaryReturn(entry.Id, ProofOf(later), Branch));
        Assert.Null(Assert.Single(Open().Entries).Ordinary!.ReturnRequest);
    }
    [Fact]
    public void Definitive_business_refusal_keeps_proof_queryable_but_never_grants_a_spawn()
    {
        var box = Open(); var entry = box.PrepareOrdinary(Request(), Scope()); var first = Meta(entry);
        box.BindOrdinarySnapshot(entry.Id, first); box.ReconcileSnapshot(first, accepted: true);
        box.Refuse(entry.Id, "recipient closed trades");
        var kept = Assert.Single(Open().Entries); Assert.False(TransferOutbox.ReadyFor(kept, Login()));
        Assert.NotEqual(OutboxPhase.Returning, kept.Phase);
        var cancel = Open().PrepareOrdinaryReturn(entry.Id, ProofOf(first), Branch);
        Assert.Equal(Request().ToByteArray(), cancel.OriginalRequest.ToByteArray());
        Assert.Null(Assert.Single(Open().Entries).Ordinary!.ReturnReply);
        Assert.Throws<InvalidDataException>(() => Open().MarkOrdinaryReturnSpawnStarted(entry.Id, Branch));
        var reply = Started(cancel); box = Open(); box.RecordOrdinaryReturnReply(entry.Id, cancel, reply);
        Assert.Equal(MaterializationDecision.Materialize, OrdinaryReturnEvidence.Decide(cancel, reply, first.Recovery!.Outposts.Metadata,
            Branch, Branch, false, true, true));
    }
    [Fact]
    public void Recovery_hold_is_not_downgraded_by_a_later_valid_snapshot_or_refusal()
    {
        var box = Open(); var entry = box.PrepareOrdinary(Request(), Scope()); var first = Meta(entry);
        box.BindOrdinarySnapshot(entry.Id, first); box.ReconcileSnapshot(first, accepted: true);
        box.HoldForRecovery(entry.Id, new(null, "ambiguous custody", null, []));
        box.Refuse(entry.Id, "business refusal"); box.ReconcileSnapshot(LaterRemoval(first), accepted: true);
        Assert.Equal(OutboxPhase.RecoveryRequired, Assert.Single(Open().Entries).Phase);
        Assert.Throws<InvalidDataException>(() => Open().PrepareOrdinaryReturn(entry.Id, ProofOf(first), Branch));
    }
}
