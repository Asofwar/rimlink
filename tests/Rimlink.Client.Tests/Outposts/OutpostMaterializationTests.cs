using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.Outposts;
using Rimlink.Client.Economy;
using Rimlink.Protocol.V1;
using Xunit;
namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostMaterializationTests
{
    private const string Branch = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    [Theory]
    [InlineData(Grpc.Core.StatusCode.Unavailable, false, true)]
    [InlineData(Grpc.Core.StatusCode.DeadlineExceeded, false, true)]
    [InlineData(Grpc.Core.StatusCode.Cancelled, false, true)]
    [InlineData(Grpc.Core.StatusCode.ResourceExhausted, false, true)]
    [InlineData(Grpc.Core.StatusCode.InvalidArgument, false, false)]
    [InlineData(Grpc.Core.StatusCode.Unavailable, true, false)]
    public void Lost_begin_reply_is_retryable_only_before_any_object_creation(Grpc.Core.StatusCode code, bool spawned, bool retry)
        => Assert.Equal(retry, OutpostDeliveryEvidence.MayRetryBeforeSpawn(new Grpc.Core.RpcException(new(code, "delivery interrupted")), spawned));
    [Fact]
    public void Corrupt_local_delivery_evidence_is_not_a_network_retry()
        => Assert.False(OutpostDeliveryEvidence.MayRetryBeforeSpawn(new InvalidDataException("invalid receipt"), false));
    internal static (Parcel Parcel, OutpostDeliveryRequest Request, OutpostDeliveryReply Reply, OutpostSaveMetadata Metadata) Fixture()
    {
        var scope = OutpostReconciliationTests.Scope();
        var payload = new OutpostParcelPayload { SchemaVersion = 1, Kind = OutpostPayloadKind.Resources, Scope = scope.Clone(),
            OutpostId = "11111111-1111-1111-1111-111111111111", BatchOperationId = "22222222-2222-2222-2222-222222222222",
            ManifestId = "33333333-3333-3333-3333-333333333333", ManifestDigest = ByteString.CopyFrom(new byte[32]), ValuationRevision = 1, ChildCount = 1 };
        payload.Resources.Add(new ResourceParcel { SchemaVersion = 1, DefName = "WoodLog", Count = 3, ImportValueX100 = 360 });
        Rimlink.Protocol.OutpostParcelContents.Bind(payload, null, null, 0);
        payload.PayloadDigest = ByteString.CopyFrom(SHA256.HashData(payload.ToByteArray()));
        var parcel = new Parcel { Id = 9, Kind = ParcelKind.ParcelLotReturn, OutpostPayload = payload, Items = new(), Pawns = new() };
        var request = new OutpostDeliveryRequest { Scope = scope.Clone(), ClientRequestId = "44444444-4444-4444-4444-444444444444",
            ParcelId = 9, PayloadDigest = payload.PayloadDigest, CanonicalSnapshot = OutpostReconciliationTests.Proof() };
        var reply = new OutpostDeliveryReply { AttemptId = "55555555-5555-5555-5555-555555555555", Authorization = request.Clone(),
            TransitionId = "55555555-5555-5555-5555-555555555555", TransitionDigest = ByteString.CopyFrom(Enumerable.Repeat((byte)7, 32).ToArray()),
            PredecessorId = "66666666-6666-6666-6666-666666666666" };
        return (parcel, request, reply, new() { SchemaVersion = 1, Scope = scope });
    }
    [Fact]
    public void Fresh_supported_recipient_bootstraps_only_authenticated_scope_without_creating_any_pawn_identity()
    {
        var scope = Fixture().Request.Scope;
        var original = new OutpostSaveMetadata();
        var ready = OutpostDeliveryEvidence.PrepareRecipient(original, scope, scope.SaveGeneration, scope.SaveWriteEpoch, false, false);
        Assert.Equal(1u, ready.SchemaVersion); Assert.Equal(scope, ready.Scope); Assert.Empty(ready.Registry); Assert.Empty(ready.Receipts);
        Assert.Equal(0u, original.SchemaVersion); ready.Scope.RestoreBoundary = "mutated"; Assert.NotEqual(ready.Scope, scope);
    }
    [Theory]
    [InlineData("generation")][InlineData("epoch")][InlineData("malformed")][InlineData("known")][InlineData("wrong-scope")][InlineData("invalid-auth")]
    public void Recipient_bootstrap_never_rebases_old_or_malformed_colony_evidence(string fault)
    {
        var scope = Fixture().Request.Scope; var metadata = new OutpostSaveMetadata();
        if (fault == "wrong-scope") { metadata = Fixture().Metadata; metadata.Scope.RestoreBoundary = "old"; }
        if (fault == "invalid-auth") scope.WorldInstanceId = "";
        Assert.Throws<InvalidDataException>(() => OutpostDeliveryEvidence.PrepareRecipient(metadata, scope,
            scope.SaveGeneration + (fault == "generation" ? 1 : 0), scope.SaveWriteEpoch + (fault == "epoch" ? 1 : 0), fault == "malformed", fault == "known"));
    }
    [Fact]
    public void Existing_exact_recipient_evidence_is_cloned_never_overwritten()
    {
        var f = Fixture(); f.Metadata.Receipts.Add(OutpostDeliveryEvidence.Receipt(f.Parcel, f.Request, f.Reply));
        var ready = OutpostDeliveryEvidence.PrepareRecipient(f.Metadata, f.Request.Scope, f.Request.Scope.SaveGeneration, f.Request.Scope.SaveWriteEpoch, false, true);
        Assert.Equal(f.Metadata, ready); ready.Receipts.Clear(); Assert.Single(f.Metadata.Receipts);
    }
    [Fact]
    public void Exact_begin_receipt_binds_preexisting_snapshot_not_its_own_future_hash()
    {
        var f = Fixture(); var receipt = OutpostDeliveryEvidence.Receipt(f.Parcel, f.Request, f.Reply);
        Assert.Equal(f.Reply.TransitionId, receipt.TransitionId); Assert.Equal(f.Reply.TransitionDigest, receipt.TransitionDigest);
        Assert.Equal(f.Reply.PredecessorId, receipt.PredecessorId); Assert.Equal(f.Request.CanonicalSnapshot, receipt.Snapshot);
        Assert.Equal(SHA256.HashData(f.Request.ToByteArray()), receipt.IntentDigest.ToByteArray());
        Assert.Equal(new long[] { 9 }, receipt.ParcelIds); Assert.Equal(f.Reply.AttemptId, receipt.DeliveryAttemptId);
        Assert.Empty(receipt.PawnCustodyIds); Assert.Empty(receipt.AssignmentIds);
    }
    [Fact]
    public void New_authorization_allows_only_same_continuous_branch_before_first_creation()
    {
        var f = Fixture(); Assert.Equal(MaterializationDecision.Materialize, Decide(f));
    }
    [Theory]
    [InlineData("visit")][InlineData("stale-session")][InlineData("cold-load")][InlineData("started")]
    [InlineData("ack-without-receipt")][InlineData("scope")][InlineData("authorization")]
    [InlineData("digest")][InlineData("causal-digest")][InlineData("missing-child")]
    public void Delivery_on_visit_stale_game_or_unproven_absence_is_rejected(string fault)
    {
        var f = Fixture();
        if (fault == "ack-without-receipt") f.Reply.AckOnly = true;
        if (fault == "scope") f.Metadata.Scope.RestoreBoundary = "other";
        if (fault == "authorization") f.Reply.Authorization.ParcelId++;
        if (fault == "digest") f.Parcel.OutpostPayload.Resources[0].Count++;
        if (fault == "causal-digest") f.Reply.TransitionDigest = ByteString.Empty;
        if (fault == "missing-child") f.Parcel.OutpostPayload.ChildCount = 0;
        Assert.Equal(MaterializationDecision.RecoveryRequired, Decide(f, fault == "cold-load" ? Guid.NewGuid().ToString("D") : Branch,
            fault == "started", fault != "visit", fault != "stale-session"));
    }
    [Fact]
    public void Existing_receipt_is_ack_only_even_after_cold_load_never_a_second_spawn()
    {
        var f = Fixture(); f.Metadata.Receipts.Add(OutpostDeliveryEvidence.Receipt(f.Parcel, f.Request, f.Reply));
        Assert.Equal(MaterializationDecision.AckOnly, Decide(f, Guid.NewGuid().ToString("D"), true));
    }
    [Fact]
    public void Receipt_with_same_id_but_changed_evidence_is_not_permission_to_ack()
    {
        var f = Fixture(); var receipt = OutpostDeliveryEvidence.Receipt(f.Parcel, f.Request, f.Reply); receipt.ParcelIds[0] = 10;
        f.Metadata.Receipts.Add(receipt); Assert.Equal(MaterializationDecision.RecoveryRequired, Decide(f));
    }
    private static MaterializationDecision Decide((Parcel Parcel, OutpostDeliveryRequest Request, OutpostDeliveryReply Reply, OutpostSaveMetadata Metadata) f,
        string current = Branch, bool started = false, bool safeHome = true, bool currentSession = true) =>
        OutpostDeliveryEvidence.Decide(f.Parcel, f.Request, f.Reply, f.Metadata, Branch, current, started, safeHome, currentSession);
}

public sealed partial class OutpostOutboxTests
{
    [Fact]
    public void Failed_spawn_keeps_durable_started_fence_and_original_parcel_intent()
    {
        var box = Open(); var request = (OutpostDeliveryRequest)Request("delivery"); var entry = box.Prepare(request);
        const string branch = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"; box.ArmOutpostDelivery(entry.Id, branch);
        box.ReconcileSnapshot(Meta(), accepted: true);
        box.RecordOutpostReply(entry.Id, new() { Authorization = request.Clone(), AttemptId = Guid.NewGuid().ToString("D") });
        box.MarkOutpostSpawnStarted(entry.Id, branch);
        var persisted = Assert.Single(Open().Entries); Assert.True(persisted.Outpost!.SpawnStarted);
        Assert.Equal(branch, persisted.Outpost.DeliveryBranchId); Assert.Equal(request.ToByteArray(), persisted.Payload);
        Assert.True(Open().HasUnresolved); Assert.Throws<InvalidDataException>(() => Open().MarkOutpostSpawnStarted(entry.Id, branch));
        Assert.Throws<InvalidDataException>(() => Open().ArmOutpostDelivery(entry.Id, Guid.NewGuid().ToString("D")));
        Assert.True(Assert.Single(Open().Entries).Outpost!.SpawnStarted);
    }
    [Fact]
    public void Creation_requires_durable_begin_reply_and_cannot_cross_a_cold_branch()
    {
        var box = Open(); var entry = box.Prepare(Request("delivery")); const string branch = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        box.ArmOutpostDelivery(entry.Id, branch);
        Assert.Throws<InvalidDataException>(() => box.MarkOutpostSpawnStarted(entry.Id, branch));
        Assert.False(Assert.Single(Open().Entries).Outpost!.SpawnStarted);
        Assert.Throws<InvalidDataException>(() => box.ArmOutpostDelivery(entry.Id, Guid.NewGuid().ToString("D")));
    }
}
