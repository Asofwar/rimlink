using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Client.Outposts;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class GuildDeliveryEvidenceTests
{
    [Fact]
    public void Bootstrap_preserves_all_personal_evidence_and_never_creates_a_pawn_registry_entry()
    {
        var (parcel, request, _, metadata) = Exchange();
        metadata.GuildMetadata = null;
        metadata.Registry.Add(new PawnRegistryEnvelope { SchemaVersion = 1, PawnCustodyId = Guid.NewGuid().ToString("D"),
            PawnReferenceToken = "Thing_Human1", Scope = metadata.Scope.Clone() });
        var result = GuildDeliveryEvidence.PrepareRecipient(metadata, request.Scope, 0, 0, false, true);
        Assert.Equal(metadata.Registry[0], Assert.Single(result.Registry));
        Assert.Empty(result.GuildMetadata.Receipts); Assert.Equal(request.Scope, result.GuildMetadata.Scope);
        Assert.Null(metadata.GuildMetadata);
        Assert.Throws<InvalidDataException>(() => GuildDeliveryEvidence.PrepareRecipient(new(), request.Scope, 0, 0, false, true));
    }

    [Fact]
    public void Only_one_continuous_pre_spawn_branch_can_materialize()
    {
        var (parcel, request, reply, metadata) = Exchange(); string branch = Guid.NewGuid().ToString("D");
        Assert.Equal(MaterializationDecision.Materialize, GuildDeliveryEvidence.Decide(parcel, request, reply, metadata, branch, branch, false, true, true));
        Assert.Equal(MaterializationDecision.RecoveryRequired, GuildDeliveryEvidence.Decide(parcel, request, reply, metadata, branch, branch, true, true, true));
        Assert.Equal(MaterializationDecision.RecoveryRequired, GuildDeliveryEvidence.Decide(parcel, request, reply, metadata, branch, Guid.NewGuid().ToString("D"), false, true, true));
        Assert.Equal(MaterializationDecision.RecoveryRequired, GuildDeliveryEvidence.Decide(parcel, request, reply, metadata, branch, branch, false, false, true));
        Assert.Equal(MaterializationDecision.RecoveryRequired, GuildDeliveryEvidence.Decide(parcel, request, reply, metadata, branch, branch, false, true, false));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void An_exact_applied_receipt_never_materializes_again_even_after_a_cold_load(bool accepted)
    {
        var (parcel, request, reply, metadata) = Exchange();
        metadata.GuildMetadata.Receipts.Add(GuildDeliveryEvidence.Receipt(parcel, request, reply));
        reply.AckOnly = accepted;
        if (accepted) reply.AcceptedSnapshot = new() { SnapshotId = Guid.NewGuid().ToString("N"), RawSha256 = ByteString.CopyFrom(new byte[32]), Scope = request.Scope.Clone() };
        Assert.Equal(MaterializationDecision.AckOnly, GuildDeliveryEvidence.Decide(parcel, request, reply, metadata,
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), true, true, true));
        metadata.GuildMetadata.Receipts[0].PayloadDigest = ByteString.CopyFrom(new byte[32]);
        Assert.Equal(MaterializationDecision.RecoveryRequired, GuildDeliveryEvidence.Decide(parcel, request, reply, metadata, "", "", true, true, true));
    }

    [Theory]
    [InlineData("payload")][InlineData("intent")][InlineData("pawn")][InlineData("scope")][InlineData("echo")]
    public void A_conflicting_authorization_or_ordinary_content_cannot_materialize(string change)
    {
        var (parcel, request, reply, metadata) = Exchange();
        if (change == "payload") parcel.GuildResourcePayload.Count++;
        if (change == "intent") reply.IntentDigest = ByteString.CopyFrom(new byte[32]);
        if (change == "pawn") parcel.Pawns = new PawnList { Pawns = { new PawnBlob { Name = "not a guild resource" } } };
        if (change == "scope") request.Scope.SaveGeneration++;
        if (change == "echo") reply.Request.ClientRequestId = Guid.NewGuid().ToString("D");
        string branch = Guid.NewGuid().ToString("D");
        Assert.Equal(MaterializationDecision.RecoveryRequired, GuildDeliveryEvidence.Decide(parcel, request, reply, metadata, branch, branch, false, true, true));
    }

    [Fact]
    public void Restored_parcel_uses_the_new_boundary_without_rehashing_or_redirecting_the_payload()
    {
        var (parcel, request, _, _) = Exchange();
        var original = parcel.GuildResourcePayload.ToByteArray();
        var stale = request.Clone();
        request.Scope.RestoreBoundary = Guid.NewGuid().ToString("D");
        request.BeforeSnapshot.Scope = request.Scope.Clone();
        parcel.GuildRestoreScope = request.Scope.Clone();
        GuildDeliveryEvidence.ValidateIntent(parcel, request);
        Assert.Equal(original, parcel.GuildResourcePayload.ToByteArray());
        Assert.Throws<InvalidDataException>(() => GuildDeliveryEvidence.ValidateIntent(parcel, stale));
        parcel.GuildRestoreScope.PlayerId++;
        Assert.Throws<InvalidDataException>(() => GuildDeliveryEvidence.ValidateIntent(parcel, request));
    }

    internal static (Parcel, GuildDeliveryRequest, GuildDeliveryAuthorization, OutpostSaveMetadata) Exchange()
    {
        var metadata = GuildRecoveryMetadataTests.Metadata(); metadata.GuildMetadata.Receipts.Clear();
        var scope = metadata.GuildMetadata.Scope;
        var payload = new GuildResourceParcelPayload { SchemaVersion = 1, ClaimId = Guid.NewGuid().ToString("D"), AllianceId = 50,
            OriginKind = "Outpost", OriginId = Guid.NewGuid().ToString("D"), OwnerRevision = 1, Recipient = scope.Clone(), DefName = "Steel", Count = 7,
            UnitValueX100 = 190, ManifestId = Guid.NewGuid().ToString("D"), ManifestDigest = ByteString.CopyFrom(new byte[32]),
            ValuationRevision = 1, TransitionId = Guid.NewGuid().ToString("D") };
        var parcel = new Parcel { Id = 1, Kind = ParcelKind.ParcelGuildResources, GuildResourcePayload = payload };
        var request = new GuildDeliveryRequest { Scope = scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("D"), ParcelId = parcel.Id,
            PayloadDigest = ByteString.CopyFrom(SHA256.HashData(payload.ToByteArray())), BeforeSnapshot = new() { Scope = scope.Clone(),
                SnapshotId = Guid.NewGuid().ToString("N"), RawSha256 = ByteString.CopyFrom(new byte[32]) } };
        string attempt = Guid.NewGuid().ToString("D");
        var reply = new GuildDeliveryAuthorization { AttemptId = attempt, TransitionId = attempt, Request = request.Clone(),
            TransitionDigest = ByteString.CopyFrom(new byte[32]), IntentDigest = ByteString.CopyFrom(SHA256.HashData(request.ToByteArray())) };
        return (parcel, request, reply, metadata);
    }
}
