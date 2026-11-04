using Google.Protobuf;
using Rimlink.Client.Outposts;
using Rimlink.Protocol.V1;
using Xunit;
namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostAdmissionReceiptTests
{
    [Fact]
    public void Admission_receipt_binds_exact_original_prepare_and_all_assignment_ids()
    {
        var (request, reply) = Example(); var receipt = OutpostAdmissionReceipts.Create(request, reply);
        Assert.Equal(reply.TransitionId, receipt.TransitionId); Assert.Equal(reply.PredecessorId, receipt.PredecessorId);
        Assert.Equal(reply.TransitionDigest, receipt.TransitionDigest); Assert.Equal(request.PreRemoval, receipt.Snapshot);
        Assert.Equal(request.IntentDigest, receipt.IntentDigest); Assert.Equal(request.ClientRequestId, receipt.ClientRequestId);
        Assert.Equal(new[] { request.Workers[0].PawnCustodyId }, receipt.PawnCustodyIds); Assert.Equal(new[] { request.Workers[0].AssignmentId }, receipt.AssignmentIds);
        request.PreRemoval.SnapshotId = "changed"; Assert.NotEqual(request.PreRemoval, receipt.Snapshot);
    }
    [Fact]
    public void No_removal_receipt_is_bound_to_the_continuous_game_branch()
    {
        var (request, reply) = Example(); reply.Status = OutpostOperationStatus.RecoveryRequired;
        var receipt = OutpostAdmissionReceipts.Create(request, reply, noRemoval: true);
        Assert.Equal(OutpostCancelKind.AbortNoRemoval, receipt.CancellationKind); Assert.Equal(request.Caravan.GameBranchId, receipt.NoRemovalBranchId);
    }
    [Theory]
    [InlineData("intent")][InlineData("scope")][InlineData("digest")][InlineData("request")][InlineData("status")]
    public void Unbound_or_terminal_reply_cannot_authorize_a_removal(string fault)
    {
        var (request, reply) = Example();
        if (fault == "intent") reply.IntentDigest = ByteString.CopyFrom(new byte[32]);
        if (fault == "scope") reply.Scope.RestoreBoundary = "another";
        if (fault == "digest") reply.TransitionDigest = ByteString.Empty;
        if (fault == "request") reply.ClientRequestId = Guid.NewGuid().ToString("D");
        if (fault == "status") reply.Status = OutpostOperationStatus.Committed;
        Assert.Throws<InvalidDataException>(() => OutpostAdmissionReceipts.Create(request, reply));
    }
    private static (OutpostPrepareRequest, OutpostOperationReply) Example()
    {
        var scope = new OutpostScopeMessage { WorldInstanceId = "world", OwnerPlayerId = 1, RestoreBoundary = "boundary" };
        var request = new OutpostPrepareRequest { Scope = scope, ClientRequestId = Guid.NewGuid().ToString("D"),
            IntentDigest = ByteString.CopyFrom(Enumerable.Repeat((byte)1, 32).ToArray()), PreRemoval = new() { Scope = scope.Clone(),
                SnapshotId = Guid.NewGuid().ToString("N"), RawSha256 = ByteString.CopyFrom(new byte[32]) }, Caravan = new() { GameBranchId = Guid.NewGuid().ToString("D") } };
        request.Workers.Add(new OutpostWorkerCapture { PawnCustodyId = Guid.NewGuid().ToString("D"), AssignmentId = Guid.NewGuid().ToString("D") });
        return (request, new() { Scope = scope.Clone(), ClientRequestId = request.ClientRequestId, IntentDigest = request.IntentDigest,
            Status = OutpostOperationStatus.Prepared, TransitionId = Guid.NewGuid().ToString("D"), PredecessorId = Guid.NewGuid().ToString("D"),
            TransitionDigest = ByteString.CopyFrom(new byte[32]) });
    }
}
