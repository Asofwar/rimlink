using Google.Protobuf;
using Rimlink.Client.Outposts;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostRestoreDeliveryTests
{
    private const string Branch = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private static (Parcel Parcel, OutpostDeliveryRequest Request, OutpostDeliveryReply Reply, OutpostSaveMetadata Metadata) Fixture()
    {
        var f = OutpostMaterializationTests.Fixture(); var original = f.Request.CanonicalSnapshot.Clone();
        f.Request.Scope.RestoreBoundary = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        f.Request.CanonicalSnapshot.Scope = f.Request.Scope.Clone();
        f.Request.CanonicalSnapshot.SnapshotId = "cccccccccccccccccccccccccccccccc";
        f.Metadata.Scope = f.Request.Scope.Clone();
        f.Request.RestoreAuthorizationId = "dddddddd-dddd-dddd-dddd-dddddddddddd";
        f.Parcel.OutpostRestoreAuthorization = new() { AuthorizationId = f.Request.RestoreAuthorizationId,
            RestoreTransition = new() { TransitionId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", TransitionDigest = ByteString.CopyFrom(new byte[32]) },
            OriginalScope = f.Parcel.OutpostPayload.Scope.Clone(), RestoredScope = f.Request.Scope.Clone(), ParcelId = f.Parcel.Id,
            PayloadDigest = f.Request.PayloadDigest, SelectedSnapshot = original };
        f.Reply.Authorization = f.Request.Clone(); f.Reply.RestoreAuthorization = f.Parcel.OutpostRestoreAuthorization.Clone();
        return f;
    }
    [Fact]
    public void Exact_restore_grant_materializes_original_payload_once_then_only_acknowledges_receipt()
    {
        var f = Fixture(); byte[] original = f.Parcel.OutpostPayload.ToByteArray();
        Assert.Equal(MaterializationDecision.Materialize, Decide(f));
        var receipt = OutpostDeliveryEvidence.Receipt(f.Parcel, f.Request, f.Reply);
        Assert.Equal(f.Request.CanonicalSnapshot, receipt.Snapshot);
        f.Metadata.Receipts.Add(receipt); f.Reply.AckOnly = true;
        Assert.Equal(MaterializationDecision.AckOnly, Decide(f, "ffffffff-ffff-ffff-ffff-ffffffffffff", true));
        Assert.Equal(original, f.Parcel.OutpostPayload.ToByteArray());
    }
    [Theory]
    [InlineData("missing")][InlineData("request")][InlineData("reply")][InlineData("source")][InlineData("target")]
    [InlineData("parcel")][InlineData("digest")][InlineData("transition")][InlineData("canonical")][InlineData("selected")]
    public void Changed_or_unqualified_restore_grant_never_authorizes_materialization(string fault)
    {
        var f = Fixture(); var grant = f.Parcel.OutpostRestoreAuthorization;
        if (fault == "missing") f.Parcel.OutpostRestoreAuthorization = null;
        if (fault == "request") f.Request.RestoreAuthorizationId = "ffffffff-ffff-ffff-ffff-ffffffffffff";
        if (fault == "reply") f.Reply.RestoreAuthorization.AuthorizationId = "ffffffff-ffff-ffff-ffff-ffffffffffff";
        if (fault == "source") grant.OriginalScope.OwnerPlayerId++;
        if (fault == "target") grant.RestoredScope.SaveGeneration++;
        if (fault == "parcel") grant.ParcelId++;
        if (fault == "digest") grant.PayloadDigest = ByteString.CopyFrom(Enumerable.Repeat((byte)9, 32).ToArray());
        if (fault == "transition") grant.RestoreTransition.TransitionDigest = ByteString.Empty;
        if (fault == "canonical") f.Request.CanonicalSnapshot = grant.SelectedSnapshot.Clone();
        if (fault == "selected") grant.SelectedSnapshot.RawSha256 = ByteString.Empty;
        if (fault is not ("missing" or "reply")) f.Reply.RestoreAuthorization = grant.Clone();
        f.Reply.Authorization = f.Request.Clone();
        Assert.Equal(MaterializationDecision.RecoveryRequired, Decide(f));
    }
    [Fact]
    public void Ordinary_same_scope_delivery_rejects_unrequested_restore_echo()
    {
        var f = OutpostMaterializationTests.Fixture(); f.Reply.RestoreAuthorization = Fixture().Reply.RestoreAuthorization;
        Assert.Equal(MaterializationDecision.RecoveryRequired, Decide(f));
    }
    private static MaterializationDecision Decide((Parcel Parcel, OutpostDeliveryRequest Request, OutpostDeliveryReply Reply, OutpostSaveMetadata Metadata) f,
        string currentBranch = Branch, bool started = false) => OutpostDeliveryEvidence.Decide(f.Parcel, f.Request, f.Reply, f.Metadata,
            Branch, currentBranch, started, true, true);
}
