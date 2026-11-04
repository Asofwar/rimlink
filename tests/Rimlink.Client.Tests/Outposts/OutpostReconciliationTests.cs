using Google.Protobuf;
using Rimlink.Client.Economy;
using Rimlink.Client.Outposts;
using Xunit;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostReconciliationTests
{
    internal static OutpostScopeMessage Scope() => new() { WorldInstanceId = "world-1", OwnerPlayerId = 7,
        SaveGeneration = 3, SaveWriteEpoch = 4, RestoreBoundary = "boundary-1" };
    internal static OutpostSnapshotProof Proof() => new() { Scope = Scope(), SnapshotId = "11111111111111111111111111111111",
        RawSha256 = ByteString.CopyFrom(Enumerable.Repeat((byte)9, 32).ToArray()) };
    private static OutpostLocalEvidence Local(OutboxPhase phase = OutboxPhase.Ready) => new(Scope(), phase,
        "22222222222222222222222222222222", ByteString.CopyFrom(new byte[32]), Proof(), Proof());
    private static OutpostStateReply Remote() => new() { Scope = Scope(), AuthoritativeRevision = 12 };

    [Fact]
    public void Prepared_entry_is_not_replayed_as_removed() =>
        Assert.Equal(OutpostRecoveryDecision.WaitForProof, OutpostReconciliation.Decide(Local(OutboxPhase.Prepared), Remote()));

    [Theory]
    [InlineData("missing")]
    [InlineData("id")]
    [InlineData("hash")]
    [InlineData("scope")]
    public void Ready_requires_exact_accepted_proof(string fault)
    {
        var local = Local(); var proof = local.AcceptedProof!.Clone();
        if (fault == "id") proof.SnapshotId = "33333333333333333333333333333333";
        if (fault == "hash") proof.RawSha256 = ByteString.CopyFrom(Enumerable.Repeat((byte)8, 32).ToArray());
        if (fault == "scope") proof.Scope.RestoreBoundary = "other";
        Assert.Equal(OutpostRecoveryDecision.WaitForProof, OutpostReconciliation.Decide(
            local with { AcceptedProof = fault == "missing" ? null : proof }, Remote()));
    }

    [Fact]
    public void Ready_exact_accepted_bytes_can_replay_same_intent() =>
        Assert.Equal(OutpostRecoveryDecision.Replay, OutpostReconciliation.Decide(Local(), Remote()));

    [Theory]
    [InlineData("world")]
    [InlineData("owner")]
    [InlineData("generation")]
    [InlineData("epoch")]
    [InlineData("boundary")]
    public void Old_scope_is_quarantined_before_replay(string fault)
    {
        var remote = Remote();
        switch (fault)
        {
            case "world": remote.Scope.WorldInstanceId = "world-2"; break;
            case "owner": remote.Scope.OwnerPlayerId++; break;
            case "generation": remote.Scope.SaveGeneration++; break;
            case "epoch": remote.Scope.SaveWriteEpoch++; break;
            case "boundary": remote.Scope.RestoreBoundary = "boundary-2"; break;
        }
        Assert.Equal(OutpostRecoveryDecision.Quarantine, OutpostReconciliation.Decide(Local(), remote));
    }

    [Fact]
    public void Lost_commit_reply_queries_same_operation() => Assert.Equal(OutpostRecoveryDecision.QueryOperation,
        OutpostReconciliation.Decide(Local() with { ReplyUnknown = true }, Remote()));

    [Fact]
    public void Canonical_restore_download_reauthorizes_only_matching_hash()
    {
        var local = Local() with { AcceptedProof = null };
        Assert.Equal(OutpostRecoveryDecision.WaitForProof, OutpostReconciliation.Decide(local, Remote()));
        Assert.Equal(OutpostRecoveryDecision.Replay, OutpostReconciliation.Decide(local with { AcceptedProof = Proof() }, Remote()));
    }

    [Fact]
    public void Receipt_already_in_canonical_save_is_ack_only()
    {
        var local = Local(); var post = Proof(); post.SnapshotId = "44444444444444444444444444444444";
        var receipt = new OutpostTransitionReceipt { ClientRequestId = local.ClientRequestId,
            IntentDigest = local.IntentDigest, Snapshot = local.RequiredProof.Clone() };
        Assert.Equal(OutpostRecoveryDecision.AckOnly, OutpostReconciliation.Decide(local with {
            AcceptedProof = post, AcceptedReceipt = receipt }, Remote()));
        receipt.ClientRequestId = "different";
        Assert.Equal(OutpostRecoveryDecision.WaitForProof, OutpostReconciliation.Decide(local with {
            AcceptedProof = post, AcceptedReceipt = receipt }, Remote()));
    }

    [Fact]
    public void Remote_same_key_different_intent_requires_recovery()
    {
        var local = Local(); var remote = Remote(); remote.PendingOperations.Add(new OutpostOperationReply {
            ClientRequestId = local.ClientRequestId, IntentDigest = ByteString.CopyFrom(Enumerable.Repeat((byte)1, 32).ToArray()) });
        Assert.Equal(OutpostRecoveryDecision.RecoveryRequired, OutpostReconciliation.Decide(local, remote));
    }

    [Theory]
    [InlineData(OutboxPhase.Legacy)]
    [InlineData(OutboxPhase.Returning)]
    [InlineData(OutboxPhase.RecoveryRequired)]
    public void Unsupported_journal_phase_never_guesses_a_local_refund(OutboxPhase phase) => Assert.Equal(
        OutpostRecoveryDecision.RecoveryRequired, OutpostReconciliation.Decide(Local(phase), Remote()));
}
