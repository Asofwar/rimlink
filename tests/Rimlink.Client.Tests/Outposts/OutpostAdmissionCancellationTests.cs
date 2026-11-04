using Google.Protobuf;
using Rimlink.Client.Outposts;
using Rimlink.Protocol.V1;
using Xunit;
namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostAdmissionCancellationTests
{
    private readonly OutpostScopeMessage scope = new() { WorldInstanceId = "world", OwnerPlayerId = 1, SaveGeneration = 0, SaveWriteEpoch = 0, RestoreBoundary = "boundary" };
    private const string Branch = "11111111-1111-1111-1111-111111111111";
    private OutpostPrepareRequest Request() => new() { Scope = scope.Clone(), ClientRequestId = "22222222-2222-2222-2222-222222222222", IntentDigest = ByteString.CopyFrom(new byte[32]),
        Caravan = new() { GameBranchId = Branch }, Workers = { new OutpostWorkerCapture { PawnCustodyId = "33333333-3333-3333-3333-333333333333", AssignmentId = "44444444-4444-4444-4444-444444444444" } } };
    private OutpostSaveMetadata Metadata(OutpostPrepareRequest request, bool removed)
    {
        var m = new OutpostSaveMetadata { SchemaVersion = 1, Scope = scope.Clone() };
        m.Registry.Add(new PawnRegistryEnvelope { SchemaVersion = 1, Scope = scope.Clone(), PawnCustodyId = request.Workers[0].PawnCustodyId,
            AssignmentId = removed ? request.Workers[0].AssignmentId : "", PawnReferenceToken = removed ? "" : "Thing_Human1", Tombstone = removed });
        if (removed) m.Receipts.Add(new OutpostTransitionReceipt { ClientRequestId = request.ClientRequestId, IntentDigest = request.IntentDigest,
            PawnCustodyIds = { request.Workers[0].PawnCustodyId }, AssignmentIds = { request.Workers[0].AssignmentId } });
        return m;
    }
    [Fact] public void Same_continuous_branch_can_request_a_no_removal_fence()
    { var r = Request(); Assert.Equal(OutpostCancelKind.AbortNoRemoval, OutpostAdmissionCancellation.Select(r, Metadata(r, false), Branch, false)); }
    [Fact] public void Cold_load_does_not_claim_no_removal_from_a_live_pawn()
    { var r = Request(); Assert.Throws<InvalidDataException>(() => OutpostAdmissionCancellation.Select(r, Metadata(r, false), Guid.NewGuid().ToString(), false)); }
    [Fact] public void Removed_workers_can_only_be_returned_by_server_mail_not_restored_locally()
    { var r = Request(); Assert.Equal(OutpostCancelKind.ReturnAfterRemoval, OutpostAdmissionCancellation.Select(r, Metadata(r, true), Guid.NewGuid().ToString(), true)); }
    [Theory] [InlineData("receipt")] [InlineData("registry")] [InlineData("intent")] [InlineData("scope")]
    public void Ambiguous_removal_keeps_recovery_instead_of_a_local_refund(string fault)
    {
        var r = Request(); var m = Metadata(r, true);
        if (fault == "receipt") m.Receipts.Clear(); if (fault == "registry") m.Registry[0].Tombstone = false;
        if (fault == "intent") m.Receipts[0].IntentDigest = ByteString.CopyFromUtf8("forged"); if (fault == "scope") m.Scope.RestoreBoundary = "other";
        Assert.Throws<InvalidDataException>(() => OutpostAdmissionCancellation.Select(r, m, Branch, true));
    }
}
