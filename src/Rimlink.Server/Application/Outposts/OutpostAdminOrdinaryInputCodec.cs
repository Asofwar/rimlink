using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.Outposts;

public static partial class OutpostAdminIntentCodec
{
    // Syntax/identity binding only. These methods never establish source absence, custody or permission to spawn.
    public static byte[] EncodeOrdinaryInput(OriginalOrdinaryInput input)
    {
        ValidateOrdinaryInput(input);
        using var w = new CanonicalWriter(Limits.MaxMessageBytes);
        w.Raw("RLOORGIN"u8); w.Number(1); w.Blob(input.OriginalRequest); w.Blob(input.Capture.ToByteArray());
        w.Blob(input.RemovalSnapshot.ToByteArray()); w.Blob(input.CanonicalSnapshot.ToByteArray());
        return w.Bytes();
    }

    public static OriginalOrdinaryInput DecodeOrdinaryInput(byte[] bytes)
    {
        if (bytes is null || bytes.Length > Limits.MaxMessageBytes) throw Invalid();
        try
        {
            var r = new CanonicalReader((byte[])bytes.Clone());
            if (!r.Raw(8).SequenceEqual("RLOORGIN"u8) || r.Number() != 1) throw Invalid();
            byte[] original = r.Blob(Limits.MaxMessageBytes);
            var capture = ParseBounded(OrdinaryOutgoingCapture.Parser, r.Blob(Limits.MaxMessageBytes));
            var removal = ParseBounded(OutpostSnapshotProof.Parser, r.Blob(4096));
            var canonical = ParseBounded(OutpostSnapshotProof.Parser, r.Blob(4096));
            var input = new OriginalOrdinaryInput(1, original, capture, removal, canonical);
            if (!r.End || !EncodeOrdinaryInput(input).AsSpan().SequenceEqual(bytes)) throw Invalid();
            return input;
        }
        catch (Exception error) when (error is ArgumentException or InvalidProtocolBufferException or OverflowException)
        { throw Invalid(error); }
    }

    public static OriginalOrdinaryInput FreezeOrdinaryInput(OriginalOrdinaryInput input) => DecodeOrdinaryInput(EncodeOrdinaryInput(input));

    public static OrdinaryCancelRequest ComposeOrdinaryAuthorization(OriginalOrdinaryInput input, string returnAttemptId)
    {
        var frozen = FreezeOrdinaryInput(input); Uuid(returnAttemptId);
        var candidate = new OrdinaryCancelRequest { Scope = frozen.Capture.Scope.Clone(), Capture = frozen.Capture.Clone(),
            OriginalRequest = ByteString.CopyFrom(frozen.OriginalRequest), RemovalSnapshot = frozen.RemovalSnapshot.Clone(),
            CanonicalSnapshot = frozen.CanonicalSnapshot.Clone(), ReturnAttemptId = returnAttemptId };
        try { _ = OrdinaryReturnCodec.Payload(candidate); }
        catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException or ArgumentException)
        { throw Invalid(error); }
        return candidate;
    }

    public static OutpostAdminOrdinaryInputCommitment CommitOrdinaryInput(OriginalOrdinaryInput input, string returnAttemptId)
    {
        var frozen = FreezeOrdinaryInput(input); byte[] bytes = EncodeOrdinaryInput(frozen);
        var candidate = ComposeOrdinaryAuthorization(frozen, returnAttemptId); byte[] authorization = candidate.ToByteArray();
        return new(1, bytes.Length, SHA256.HashData(bytes), frozen.OriginalRequest.Length, SHA256.HashData(frozen.OriginalRequest),
            authorization.Length, SHA256.HashData(authorization));
    }

    private static void ValidateOrdinaryInput(OriginalOrdinaryInput? input)
    {
        if (input is not { Version: 1, Capture: { Scope: not null } capture, OriginalRequest: { Length: > 0 and <= Limits.MaxMessageBytes } original }
            || capture.Pawns.Count > OutpostRecoveryData.MaxEntries || capture.CalculateSize() > OutpostRecoveryData.MaxMetadataBytes) throw Invalid();
        ValidateProof(input.RemovalSnapshot); ValidateProof(input.CanonicalSnapshot);
        if (!capture.Scope.Equals(input.RemovalSnapshot.Scope) || !capture.Scope.Equals(input.CanonicalSnapshot.Scope)) throw Invalid();
        try
        {
            OrdinaryCapture.Validate(capture);
            // Original() has the existing bounded depth-32 parser. No target/item/price is reconstructed from its hash.
            IMessage request = OrdinaryReturnCodec.Original(new() { Capture = capture, OriginalRequest = ByteString.CopyFrom(original) });
            if (!SHA256.HashData(original).AsSpan().SequenceEqual(capture.RequestIntentSha256.Span)
                || !OrdinaryCapture.Create(request, capture.LocalOperationId, capture.Scope).Equals(capture)) throw Invalid();
        }
        catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException or ArgumentException)
        { throw Invalid(error); }
    }

    private static T ParseBounded<T>(MessageParser<T> parser, byte[] bytes) where T : IMessage<T>
    {
        using var input = CodedInputStream.CreateWithLimits(new MemoryStream(bytes, false), Limits.MaxMessageBytes, 32);
        return parser.ParseFrom(input);
    }
}
