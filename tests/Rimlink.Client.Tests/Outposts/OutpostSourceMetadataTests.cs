using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostSourceMetadataTests
{
    internal static OutpostSaveMetadata Metadata(bool capture = true)
    {
        var input = OutpostRollbackBridgeTests.Fixture(); var bridge = OutpostSourceBridgeTests.Restore();
        var metadata = new OutpostSaveMetadata { SchemaVersion = 1, Scope = bridge.SourceProvenance.AuthorizedViews[0].EffectiveScope.Clone(),
            RestoreBridge = bridge, Registry = { input.Registry.Select(r => r.Clone()) } };
        metadata.Registry[0].Scope = metadata.Scope.Clone();
        if (capture)
        {
            var request = new TransferRequest { ClientRequestId = "99999999999999999999999999999999", ToPlayerId = 2,
                Pawns = new() { Pawns = { new PawnBlob { Registry = input.Registry[0].Clone(), ScribeXmlZstd = ByteString.CopyFrom([1, 2, 3]) } } } };
            metadata.OrdinaryOutgoing.Add(OrdinaryCapture.Create(request, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", input.Downloaded.Scope));
        }
        return metadata;
    }

    [Fact]
    public void Echo_keeps_historical_capture_and_actual_source_proof_exact_without_scope_relabel()
    {
        var metadata = Metadata(); byte[] original = metadata.ToByteArray();
        new OutpostRecoveryData { Metadata = metadata }.Validate();
        Assert.Equal(original, metadata.ToByteArray());
        Assert.Equal(OutpostSourceGraphTests.Boundary0, metadata.OrdinaryOutgoing[0].Scope.RestoreBoundary);
        Assert.Equal(OutpostSourceGraphTests.Boundary1, metadata.Scope.RestoreBoundary);
    }

    [Theory]
    [InlineData("no-root")][InlineData("lower-unlisted-epoch")][InlineData("other-boundary")][InlineData("path-digest")]
    [InlineData("wrong-effective-scope")][InlineData("multiple-active")]
    public void New_provenance_never_grants_blanket_historical_scope_or_priority_fallback(string damage)
    {
        var metadata = Metadata(damage is "no-root" or "lower-unlisted-epoch" or "other-boundary");
        switch (damage)
        {
            case "no-root": metadata.RestoreBridge = null; break;
            case "lower-unlisted-epoch": ChangeCapture(metadata, s => s.SaveWriteEpoch--); break;
            case "other-boundary": ChangeCapture(metadata, s => s.RestoreBoundary = OutpostSourceGraphTests.Boundary2); break;
            case "path-digest": metadata.RestoreBridge.SourcePathDigest = ByteString.Empty; break;
            case "wrong-effective-scope": metadata.Scope.SaveWriteEpoch++; break;
            case "multiple-active":
                var rollback = OutpostRollbackBridgeTests.Fixture().Bridge; rollback.CurrentScope = metadata.Scope.Clone();
                rollback.SourceScope = metadata.Scope.Clone(); rollback.SourceScope.SaveWriteEpoch--;
                rollback.CanonicalSnapshot.Scope = rollback.SourceScope.Clone(); rollback.MutableRegistry[0].Scope = rollback.SourceScope.Clone();
                metadata.RollbackBridge = rollback; break;
        }
        Assert.Throws<InvalidDataException>(() => new OutpostRecoveryData { Metadata = metadata }.Validate());
    }

    [Fact]
    public void New_graph_map_aggregate_does_not_sum_old_registry_receipts_and_frontier_collections()
    {
        var metadata = Metadata(false); metadata.Registry.Clear();
        for (int i = 1; i <= 4000; i++)
        {
            string id = Guid.ParseExact(i.ToString("x32"), "N").ToString("D");
            metadata.Registry.Add(new PawnRegistryEnvelope { SchemaVersion = 1, PawnCustodyId = id, Scope = metadata.Scope.Clone(), PawnReferenceToken = "p" + i });
            metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = id, TransitionDigest = OutpostSourceGraphTests.Hash(2) });
            metadata.Receipts.Add(new OutpostTransitionReceipt { TransitionId = id, ClientRequestId = id, TransitionDigest = OutpostSourceGraphTests.Hash(2),
                IntentDigest = OutpostSourceGraphTests.Hash(3), Snapshot = metadata.RestoreBridge.CanonicalSnapshot.Clone() });
        }
        Assert.True(metadata.CalculateSize() < 4 * 1024 * 1024);
        new OutpostRecoveryData { Metadata = metadata }.Validate();
        Assert.Single(metadata.RestoreBridge.SourceProvenance.AuthorizedViews[0].RegistryMap);
    }

    private static void ChangeCapture(OutpostSaveMetadata metadata, Action<OutpostScopeMessage> change)
    {
        var capture = metadata.OrdinaryOutgoing[0]; change(capture.Scope); capture.Pawns[0].Pawn.Registry.Scope = capture.Scope.Clone();
        capture.Pawns[0].BlobSha256 = ByteString.CopyFrom(SHA256.HashData(capture.Pawns[0].Pawn.ToByteArray()));
    }
}
