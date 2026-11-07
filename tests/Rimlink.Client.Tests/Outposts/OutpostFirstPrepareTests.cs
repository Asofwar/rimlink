using Grpc.Core;
using Rimlink.Client.Economy;
using Rimlink.Client.Outposts;
using Rimlink.Protocol.V1;
using Rimlink.Server.Grpc;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed partial class OutpostOutboxTests
{
    [Theory]
    [InlineData(ErrorCode.OutpostConflict, "outpost-slot-unavailable")]
    [InlineData(ErrorCode.OutpostConflict, "outpost-staffing-unavailable")]
    [InlineData(ErrorCode.TileTaken, "an outpost or settlement reserves this tile")]
    [InlineData(ErrorCode.WalletInsufficient, "founding-reserve-unavailable")]
    [InlineData(ErrorCode.OutpostIneligible, "worker-ineligible")]
    [InlineData(ErrorCode.OutpostManifestMismatch, "manifest-not-confirmed")]
    [InlineData(ErrorCode.OutpostManifestMismatch, "manifest-tile-ineligible")]
    [InlineData(ErrorCode.OutpostManifestMismatch, "admission-manifest-changed")]
    public async Task First_prepare_explicit_pre_transition_refusal_does_not_strand_unremoved_workers(ErrorCode code, string message)
    {
        var outbox = Open(); var request = (OutpostPrepareRequest)Request("prepare");
        var entry = outbox.Prepare(request); outbox.ReconcileSnapshot(Meta(), accepted: true);
        var other = (OutpostPrepareRequest)request.Clone(); other.ClientRequestId = Guid.NewGuid().ToString("N");
        var retained = outbox.Prepare(other);
        var error = RpcErrors.Failed(code, message);
        Assert.Same(error, await Assert.ThrowsAsync<RpcException>(() => OutpostFirstDispatch.Send(outbox, entry, request,
            () => Task.FromException<OutpostOperationReply>(error))));
        Assert.Equal(retained.Payload, Assert.Single(Open().Entries).Payload);
    }

    [Theory]
    [InlineData(ErrorCode.EconomyFrozen, RefusalKeys.EconomyDisabled)]
    [InlineData(ErrorCode.EconomyFrozen, RefusalKeys.EconomyPaused)]
    [InlineData(ErrorCode.PlayerFrozen, RefusalKeys.EconomyPlayerFrozen)]
    [InlineData(ErrorCode.InDebt, RefusalKeys.EconomyInDebt)]
    [InlineData(ErrorCode.UnderThreat, RefusalKeys.EconomyUnderThreat)]
    public async Task First_prepare_shared_business_guard_refusal_does_not_strand_workers(ErrorCode code, string key)
    {
        var outbox = Open(); var request = (OutpostPrepareRequest)Request("prepare");
        var entry = outbox.Prepare(request); outbox.ReconcileSnapshot(Meta(), accepted: true);
        var error = RpcErrors.Failed(code, key, "dynamic guard reason", "guard argument");
        Assert.Same(error, await Assert.ThrowsAsync<RpcException>(() => OutpostFirstDispatch.Send(outbox, entry, request,
            () => Task.FromException<OutpostOperationReply>(error))));
        Assert.Empty(Open().Entries);
    }

    [Theory]
    [InlineData(ErrorCode.EconomyFrozen, "unrecognized-key")]
    [InlineData(ErrorCode.PlayerFrozen, RefusalKeys.EconomyPaused)]
    [InlineData(ErrorCode.InDebt, RefusalKeys.EconomyUnderThreat)]
    [InlineData(ErrorCode.UnderThreat, RefusalKeys.EconomyInDebt)]
    [InlineData(ErrorCode.OutpostConflict, RefusalKeys.EconomyPlayerFrozen)]
    [InlineData(ErrorCode.OutpostManifestMismatch, "manifest-storage-corrupt")]
    [InlineData(ErrorCode.OutpostManifestMismatch, "manifest-binding-or-shape")]
    [InlineData(ErrorCode.OutpostConflict, "worker-custody-unavailable")]
    [InlineData(ErrorCode.OutpostConflict, "exact-caravan-attestation-required")]
    [InlineData(ErrorCode.RecoveryRequired, "canonical-snapshot-changed")]
    public async Task First_prepare_does_not_clear_a_general_code_or_safety_failure(ErrorCode code, string keyOrReason)
    {
        var outbox = Open(); var request = (OutpostPrepareRequest)Request("prepare");
        var entry = outbox.Prepare(request); outbox.ReconcileSnapshot(Meta(), accepted: true);
        var error = RpcErrors.Failed(code, keyOrReason, keyOrReason);
        Assert.Same(error, await Assert.ThrowsAsync<RpcException>(() => OutpostFirstDispatch.Send(outbox, entry, request,
            () => Task.FromException<OutpostOperationReply>(error))));
        Assert.Equal(entry.Payload, Assert.Single(Open().Entries).Payload);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancelled")]
    [InlineData("untyped")]
    [InlineData("missing")]
    [InlineData("identity")]
    [InlineData("epoch")]
    [InlineData("other-conflict")]
    public async Task First_prepare_ambiguous_or_safety_refusal_preserves_original_journal(string kind)
    {
        var outbox = Open(); var request = (OutpostPrepareRequest)Request("prepare");
        var entry = outbox.Prepare(request); outbox.ReconcileSnapshot(Meta(), accepted: true);
        Exception error = kind switch {
            "timeout" => new RpcException(new Status(StatusCode.DeadlineExceeded, "timeout")),
            "cancelled" => new OperationCanceledException(),
            "untyped" => new RpcException(new Status(StatusCode.FailedPrecondition, "outpost-slot-unavailable")),
            "missing" => RpcErrors.Failed(ErrorCode.OutpostConflict, "operation-missing"),
            "identity" => RpcErrors.Failed(ErrorCode.RequestIntentMismatch, "outpost-slot-unavailable"),
            "epoch" => RpcErrors.Failed(ErrorCode.SaveWriteEpochMismatch, "outpost-slot-unavailable"),
            _ => RpcErrors.Failed(ErrorCode.OutpostConflict, "another-refusal") };
        Assert.Same(error, await Record.ExceptionAsync(() => OutpostFirstDispatch.Send(outbox, entry, request,
            () => Task.FromException<OutpostOperationReply>(error))));
        var retained = Assert.Single(Open().Entries);
        Assert.Equal(entry.Payload, retained.Payload); Assert.True(retained.Outpost!.ReplyUnknown);
    }

    [Fact]
    public async Task Unknown_prepare_cannot_be_reclassified_as_a_first_attempt()
    {
        var outbox = Open(); var request = (OutpostPrepareRequest)Request("prepare");
        var entry = outbox.Prepare(request); outbox.ReconcileSnapshot(Meta(), accepted: true); outbox.MarkReplyUnknown(entry.Id);
        bool called = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => OutpostFirstDispatch.Send(outbox, entry, request, () => {
            called = true; return Task.FromException<OutpostOperationReply>(RpcErrors.Failed(ErrorCode.OutpostConflict, "outpost-slot-unavailable")); }));
        Assert.False(called); Assert.Single(Open().Entries);
    }

    [Fact]
    public async Task Prepared_authorization_retains_journal_for_the_following_removal()
    {
        var outbox = Open(); var request = (OutpostPrepareRequest)Request("prepare");
        var entry = outbox.Prepare(request); outbox.ReconcileSnapshot(Meta(), accepted: true);
        var reply = new OutpostOperationReply { Status = OutpostOperationStatus.Prepared };
        Assert.Same(reply, await OutpostFirstDispatch.Send(outbox, entry, request, () => Task.FromResult(reply)));
        Assert.True(Assert.Single(Open().Entries).Outpost!.ReplyUnknown);
    }

    [Theory]
    [InlineData(ErrorCode.OutpostConflict, "switch-stock-not-empty", true)]
    [InlineData(ErrorCode.OutpostConflict, "switch-profile-invalid", true)]
    [InlineData(ErrorCode.OutpostConflict, "fraction-discard-confirmation-required", true)]
    [InlineData(ErrorCode.OutpostConflict, "recall-worker-changed", true)]
    [InlineData(ErrorCode.OutpostConflict, "mutation-outpost-changed", true)]
    [InlineData(ErrorCode.OutpostIneligible, "worker-ineligible", true)]
    [InlineData(ErrorCode.OutpostConflict, "outpost-slot-unavailable", false)]
    [InlineData(ErrorCode.RecoveryRequired, "incomplete-staffing-obligation", false)]
    [InlineData(ErrorCode.SaveWriteEpochMismatch, "switch-stock-not-empty", false)]
    public async Task First_owner_mutation_discards_only_proved_business_refusals(ErrorCode code, string message, bool discard)
    {
        var outbox = Open(); var request = (OutpostMutationRequest)Request("mutate");
        var entry = outbox.Prepare(request); outbox.ReconcileSnapshot(Meta(), accepted: true);
        var error = RpcErrors.Failed(code, message);
        Assert.Same(error, await Assert.ThrowsAsync<RpcException>(() => OutpostFirstDispatch.Send(outbox, entry, request,
            () => Task.FromException<OutpostOperationReply>(error))));

        if (discard) Assert.Empty(Open().Entries);
        else
        {
            var retained = Assert.Single(Open().Entries);
            Assert.Equal(entry.Payload, retained.Payload);
            Assert.True(retained.Outpost!.ReplyUnknown);
        }
    }
}
