using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminOrdinaryInputTests
{
    [Fact]
    public async Task Ephemeral_original_input_preserves_the_accepted_capture_and_two_distinct_real_proofs()
    {
        using var h = await CustodyHarness.Returned(); var input = await Input(h);
        long transitions = await h.Db.OutpostTransitions.CountAsync(); string custody = (await h.Row()).LocationKind;
        var frozen = OutpostAdminIntentCodec.FreezeOrdinaryInput(input); byte[] bytes = OutpostAdminIntentCodec.EncodeOrdinaryInput(input);
        var copy = OutpostAdminIntentCodec.DecodeOrdinaryInput(bytes);
        Assert.Equal(bytes, OutpostAdminIntentCodec.EncodeOrdinaryInput(copy));
        Assert.Equal(input.OriginalRequest, frozen.OriginalRequest); Assert.Equal(input.Capture, copy.Capture);
        Assert.NotEqual(copy.RemovalSnapshot.SnapshotId, copy.CanonicalSnapshot.SnapshotId);
        Assert.Equal(input.RemovalSnapshot, copy.RemovalSnapshot); Assert.Equal(input.CanonicalSnapshot, copy.CanonicalSnapshot);
        string attempt = Guid.NewGuid().ToString("D");
        var candidate = OutpostAdminIntentCodec.ComposeOrdinaryAuthorization(copy, attempt);
        Assert.Equal(attempt, candidate.ReturnAttemptId); Assert.Equal(input.OriginalRequest, candidate.OriginalRequest.ToByteArray());
        Assert.Equal(input.Capture, candidate.Capture); Assert.Equal(input.RemovalSnapshot, candidate.RemovalSnapshot);
        Assert.Equal(input.CanonicalSnapshot, candidate.CanonicalSnapshot); Assert.Equal(input.Capture.Scope, candidate.Scope);
        Assert.Single(OrdinaryReturnCodec.Payload(candidate).Pawns.Pawns);
        var commitment = OutpostAdminIntentCodec.CommitOrdinaryInput(copy, attempt);
        Assert.Equal(1, commitment.Version); Assert.Equal(bytes.Length, commitment.InputLength);
        Assert.Equal(SHA256.HashData(bytes), commitment.InputDigest); Assert.Equal(input.OriginalRequest.Length, commitment.OriginalLength);
        Assert.Equal(SHA256.HashData(input.OriginalRequest), commitment.OriginalDigest);
        Assert.Equal(candidate.CalculateSize(), commitment.AuthorizationLength);
        Assert.Equal(SHA256.HashData(candidate.ToByteArray()), commitment.AuthorizationDigest);
        var next = OutpostAdminIntentCodec.CommitOrdinaryInput(copy, Guid.NewGuid().ToString("D"));
        Assert.Equal(commitment.InputDigest, next.InputDigest); Assert.Equal(commitment.OriginalDigest, next.OriginalDigest);
        Assert.NotEqual(commitment.AuthorizationDigest, next.AuthorizationDigest);
        input.OriginalRequest[0] ^= 1; input.Capture.ClientRequestId = Guid.NewGuid().ToString("N"); input.RemovalSnapshot.Scope.SaveWriteEpoch++;
        Assert.Equal(bytes, OutpostAdminIntentCodec.EncodeOrdinaryInput(frozen));
        Assert.Equal(transitions, await h.Db.OutpostTransitions.CountAsync()); Assert.Equal(custody, (await h.Row()).LocationKind);
        Assert.Empty(await h.Db.OutpostAdminCommands.ToArrayAsync()); Assert.Empty(await h.Db.OutpostOrdinaryReturns.ToArrayAsync());
    }

    [Theory]
    [InlineData("version")][InlineData("original-bytes")][InlineData("capture")][InlineData("removal")]
    [InlineData("canonical")][InlineData("scope")][InlineData("original-size")][InlineData("capture-schema")]
    [InlineData("mirror")][InlineData("parser-depth")][InlineData("capture-scope")]
    public async Task Missing_changed_or_unbounded_input_never_becomes_a_server_composed_authorization(string damage)
    {
        using var h = await CustodyHarness.Returned(); var input = await Input(h); long transitions = await h.Db.OutpostTransitions.CountAsync();
        switch (damage)
        {
            case "version": input = input with { Version = 2 }; break;
            case "original-bytes": input.OriginalRequest[0] ^= 1; break;
            case "capture": input.Capture.Pawns[0].Pawn.Label = "not the accepted request"; break;
            case "removal": input = input with { RemovalSnapshot = null! }; break;
            case "canonical": input = input with { CanonicalSnapshot = null! }; break;
            case "scope": input.CanonicalSnapshot.Scope.OwnerPlayerId++; break;
            case "original-size": input = input with { OriginalRequest = new byte[Limits.MaxMessageBytes + 1] }; break;
            case "capture-schema": input.Capture.SchemaVersion = 2; break;
            case "capture-scope": input.Capture.Scope = null!; break;
            case "mirror": input.Capture.Pawns[0].Pawn.Registry.NonAuthoritativeMirror = true; break;
            case "parser-depth":
                // Unknown protobuf groups must still consume the existing depth-32 budget.
                byte[] deep = [.. input.OriginalRequest, .. Enumerable.Range(0, 40).SelectMany(_ => new byte[] { 0x83, 0x06 }),
                    .. Enumerable.Range(0, 40).SelectMany(_ => new byte[] { 0x84, 0x06 })];
                input = input with { OriginalRequest = deep }; input.Capture.RequestIntentSha256 = ByteString.CopyFrom(SHA256.HashData(deep)); break;
        }
        Assert.Throws<InvalidDataException>(() => OutpostAdminIntentCodec.ComposeOrdinaryAuthorization(input, Guid.NewGuid().ToString("D")));
        Assert.Equal(transitions, await h.Db.OutpostTransitions.CountAsync()); Assert.Empty(await h.Db.OutpostOrdinaryReturns.ToArrayAsync());
    }

    [Fact]
    public async Task Sidecar_parser_refuses_changed_version_truncation_trailing_bytes_and_aggregate_overflow()
    {
        using var h = await CustodyHarness.Returned(); var input = await Input(h);
        byte[] bytes = OutpostAdminIntentCodec.EncodeOrdinaryInput(input); byte[] version = (byte[])bytes.Clone(); version[8] = 2;
        Assert.Throws<InvalidDataException>(() => OutpostAdminIntentCodec.DecodeOrdinaryInput(version));
        Assert.Throws<InvalidDataException>(() => OutpostAdminIntentCodec.DecodeOrdinaryInput(bytes[..^1]));
        Assert.Throws<InvalidDataException>(() => OutpostAdminIntentCodec.DecodeOrdinaryInput([.. bytes, 0]));
        Assert.Throws<InvalidDataException>(() => OutpostAdminIntentCodec.DecodeOrdinaryInput(new byte[Limits.MaxMessageBytes + 1]));
        // Both original and captured pawn bytes belong to the aggregate sidecar limit, not separate one-MiB allowances.
        var request = PostRequest.Parser.ParseFrom(input.OriginalRequest);
        request.Pawn.ScribeXmlZstd = ByteString.CopyFrom(new byte[600 * 1024]);
        input = input with { OriginalRequest = request.ToByteArray(), Capture = OrdinaryCapture.Create(request, input.Capture.LocalOperationId, input.Capture.Scope) };
        Assert.Throws<InvalidDataException>(() => OutpostAdminIntentCodec.EncodeOrdinaryInput(input));
    }

    private static async Task<OriginalOrdinaryInput> Input(CustodyHarness h)
    {
        var request = new PostRequest { ClientRequestId = Guid.NewGuid().ToString("N"), Pawn = h.Blob.Clone(), Price = 150, GameTick = 1000 };
        var removed = await h.Depart(request);
        var version = await h.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 1 && v.IsCurrent);
        var recovery = SaveStore.RecoveryOf(version);
        var canonical = h.F.F.H.Snapshot(recovery.Outposts.Metadata.Clone(), removed: [removed.OrdinaryCapture.LocalOperationId]);
        await h.F.F.H.Accept(canonical);
        return new(1, request.ToByteArray(), removed.OrdinaryCapture.Clone(), removed.RemovalSnapshot.Clone(), AssignmentHarness.SnapshotProof(canonical));
    }
}
