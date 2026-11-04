using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OrdinaryCaptureTests
{
    private const string LocalId = "11111111111111111111111111111111";
    private const string RequestId = "22222222222222222222222222222222";
    private static OutpostScopeMessage Scope() => new() { WorldInstanceId = "world", OwnerPlayerId = 7,
        SaveGeneration = 3, SaveWriteEpoch = 4, RestoreBoundary = "boundary" };
    private static PawnBlob Pawn() => new() { ScribeXmlZstd = ByteString.CopyFromUtf8("changed-health-inventory-relations-disabled-prisoner"),
        Registry = new() { SchemaVersion = 1, Scope = Scope(), PawnCustodyId = "33333333-3333-3333-3333-333333333333",
            AssignmentId = "", PawnReferenceToken = "Thing_Human57" } };
    private static TransferRequest Request() => new() { ClientRequestId = RequestId, ToPlayerId = 8,
        GameTick = 1234, Silver = 15, Pawns = new() { Pawns = { Pawn() } }, Items = new() };
    private static OrdinaryOutgoingCapture Literal() => new() { SchemaVersion = 1, Scope = Scope(), LocalOperationId = LocalId,
        ClientRequestId = RequestId, RequestKind = OrdinaryRequestKind.TransferSend,
        RequestIntentSha256 = ByteString.CopyFrom(SHA256.HashData(Request().ToByteArray())),
        Pawns = { new OrdinaryPawnCapture { Pawn = Pawn(), BlobSha256 = ByteString.CopyFrom(SHA256.HashData(Pawn().ToByteArray())) } } };

    [Fact]
    public void Separate_capture_keeps_current_blob_empty_assignment_and_distinct_local_id()
    {
        var request = Request(); byte[] original = request.ToByteArray();
        var actual = OrdinaryCapture.Create(request, LocalId, Scope());
        Assert.Equal(Literal(), actual); Assert.NotEqual(actual.ClientRequestId, actual.LocalOperationId);
        Assert.Empty(actual.Pawns[0].Pawn.Registry.AssignmentId);
        Assert.Equal(original, request.ToByteArray());
        request.Pawns.Pawns[0].ScribeXmlZstd = ByteString.CopyFromUtf8("later");
        Assert.Equal(Pawn(), actual.Pawns[0].Pawn);
        Assert.True(OrdinaryCapture.Matches(actual, OrdinaryRequestKind.TransferSend, RequestId, SHA256.HashData(original)));
        Assert.False(OrdinaryCapture.Matches(actual, OrdinaryRequestKind.TransferSend, RequestId, SHA256.HashData(request.ToByteArray())));
    }

    [Fact]
    public void Live_raid_records_distinct_original_and_exact_derived_intents()
    {
        var request = new LiveProposal { ClientRequestId = RequestId, HostPlayerId = 8, Kind = LiveKind.LiveRaid,
            GameTick = 321, TimingFallback = RaidTiming.TimingWarned, Pawns = new() { Pawns = { Pawn() } } };
        var actual = OrdinaryCapture.Create(request, LocalId, Scope());
        var derived = new LaunchRequest { ClientRequestId = RequestId, TargetPlayerId = 8, PawnMode = PawnMode.PawnReal,
            Timing = request.TimingFallback, Arrival = request.Arrival, GameTick = request.GameTick, Pawns = request.Pawns.Clone() };
        Assert.Equal(OrdinaryRequestKind.LivePropose, actual.RequestKind);
        Assert.Equal(OrdinaryRequestKind.RaidLaunch, actual.DerivedRoute.RequestKind);
        Assert.Equal(SHA256.HashData(derived.ToByteArray()), actual.DerivedRoute.RequestIntentSha256.ToByteArray());
        Assert.NotEqual(actual.RequestIntentSha256, actual.DerivedRoute.RequestIntentSha256);
        Assert.True(OrdinaryCapture.Matches(actual, OrdinaryRequestKind.RaidLaunch, RequestId, SHA256.HashData(derived.ToByteArray())));
        Assert.False(OrdinaryCapture.Matches(actual, OrdinaryRequestKind.RaidLaunch, RequestId, actual.RequestIntentSha256.Span));
    }

    [Theory]
    [InlineData("version")][InlineData("scope")][InlineData("local-id")][InlineData("request-id")]
    [InlineData("kind")][InlineData("hash")][InlineData("blob")][InlineData("duplicate")]
    [InlineData("pawn-scope")][InlineData("assignment")][InlineData("mirror")][InlineData("tombstone")]
    [InlineData("reference")][InlineData("derived")]
    public void Protected_section_rejects_invalid_ordinary_capture_instead_of_ignoring_it(string fault)
    {
        var capture = Literal();
        switch (fault)
        {
            case "version": capture.SchemaVersion++; break;
            case "scope": capture.Scope.OwnerPlayerId = 0; break;
            case "local-id": capture.LocalOperationId = RequestId.ToUpperInvariant() + "bad"; break;
            case "request-id": capture.ClientRequestId = "legacy:request"; break;
            case "kind": capture.RequestKind = (OrdinaryRequestKind)99; break;
            case "hash": capture.RequestIntentSha256 = ByteString.Empty; break;
            case "blob": capture.Pawns[0].Pawn.ScribeXmlZstd = ByteString.CopyFromUtf8("stale"); break;
            case "duplicate": capture.Pawns.Add(capture.Pawns[0].Clone()); break;
            case "pawn-scope": capture.Pawns[0].Pawn.Registry.Scope.OwnerPlayerId++; break;
            case "assignment": capture.Pawns[0].Pawn.Registry.AssignmentId = "invented"; break;
            case "mirror": capture.Pawns[0].Pawn.Registry.NonAuthoritativeMirror = true; break;
            case "tombstone": capture.Pawns[0].Pawn.Registry.Tombstone = true; break;
            case "reference": capture.Pawns[0].Pawn.Registry.PawnReferenceToken = ""; break;
            case "derived": capture.DerivedRoute = new() { RequestKind = OrdinaryRequestKind.RaidLaunch, ClientRequestId = RequestId,
                RequestIntentSha256 = capture.RequestIntentSha256 }; break;
        }
        var metadata = new OutpostSaveMetadata { SchemaVersion = 1, Scope = Scope(), OrdinaryOutgoing = { capture } };
        Assert.Throws<InvalidDataException>(() => new OutpostRecoveryData { Metadata = metadata }.Validate());
    }
}
