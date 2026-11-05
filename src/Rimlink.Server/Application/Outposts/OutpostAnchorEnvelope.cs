using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;

namespace Rimlink.Server.Application.Outposts;

internal abstract record AnchorEnvelope;
internal sealed record KnownAnchorEnvelope(AnchorRecord Record) : AnchorEnvelope;
internal enum FreshFenceState : byte { RestoreInProgress = 1, BackedOutEmpty = 2 }
internal enum FreshFenceMode : byte { ImportNewHost = 1, LostAnchorRecovery = 2 }
internal enum PreviousWitnessKind : byte { AbsentQualifiedEmpty = 1, Unavailable = 2 }
internal sealed record FreshAnchorFence(FreshFenceState State, FreshFenceMode Mode, PreviousWitnessKind PreviousWitness,
    string FenceId, string RestoreId, string InstallationId, string WorldId, string Boundary, ByteString IntentDigest) : AnchorEnvelope;

// V1 keeps its exact bytes and invariants. V2 is a tagged, headless fresh fence: it has no
// Committed/Pending member and therefore cannot fabricate a previous authority witness.
internal static class AnchorEnvelopeCodec
{
    private const int MaximumBytes = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static ReadOnlySpan<byte> Magic => "RLOANCHR"u8;

    internal static byte[] Encode(AnchorEnvelope envelope)
    {
        if (envelope is KnownAnchorEnvelope known) return AnchorRecordCodec.Encode(known.Record);
        if (envelope is not FreshAnchorFence fresh) throw new InvalidDataException("anchor-envelope-kind-invalid");
        Validate(fresh);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        writer.Write(Magic); writer.Write(2u); writer.Write((byte)1);
        writer.Write((byte)fresh.State); writer.Write((byte)fresh.Mode); writer.Write((byte)fresh.PreviousWitness);
        foreach (string value in new[] { fresh.FenceId, fresh.RestoreId, fresh.InstallationId, fresh.WorldId, fresh.Boundary })
        {
            byte[] text = Utf8.GetBytes(value);
            writer.Write(checked((ushort)text.Length)); writer.Write(text);
        }
        writer.Write(fresh.IntentDigest.Span); writer.Flush();
        byte[] payload = stream.ToArray();
        if (payload.Length > MaximumBytes - 32) throw new InvalidDataException("anchor-envelope-oversized");
        return [.. payload, .. SHA256.HashData(payload)];
    }

    internal static AnchorEnvelope Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 44 or > MaximumBytes || !bytes[..8].SequenceEqual(Magic))
            throw new InvalidDataException("anchor-envelope-magic-or-size-invalid");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, 4));
        if (version == 1) return new KnownAnchorEnvelope(AnchorRecordCodec.Decode(bytes));
        if (version != 2) throw new InvalidDataException("anchor-envelope-version-invalid");
        ReadOnlySpan<byte> payload = bytes[..^32];
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), bytes[^32..]))
            throw new InvalidDataException("anchor-envelope-checksum-mismatch");
        var reader = new FreshReader(payload[12..]);
        if (reader.Take(1)[0] != 1) throw new InvalidDataException("anchor-envelope-kind-invalid");
        var state = (FreshFenceState)reader.Take(1)[0];
        var mode = (FreshFenceMode)reader.Take(1)[0];
        var witness = (PreviousWitnessKind)reader.Take(1)[0];
        var fresh = new FreshAnchorFence(state, mode, witness, reader.Text(36), reader.Text(36), reader.Text(36),
            reader.Text(256), reader.Text(36), ByteString.CopyFrom(reader.Take(32)));
        if (!reader.AtEnd) throw new InvalidDataException("anchor-envelope-trailing-bytes");
        Validate(fresh);
        return fresh;
    }

    private static void Validate(FreshAnchorFence fresh)
    {
        bool import = fresh.Mode == FreshFenceMode.ImportNewHost && fresh.PreviousWitness == PreviousWitnessKind.AbsentQualifiedEmpty;
        bool lost = fresh.Mode == FreshFenceMode.LostAnchorRecovery && fresh.PreviousWitness == PreviousWitnessKind.Unavailable;
        if ((!import && !lost) || !Enum.IsDefined(fresh.State) || (lost && fresh.State != FreshFenceState.RestoreInProgress))
            throw new InvalidDataException("anchor-fresh-phase-or-witness-invalid");
        foreach (string id in new[] { fresh.FenceId, fresh.RestoreId, fresh.InstallationId, fresh.Boundary })
            if (id is null || id.Length != 36 || !Guid.TryParseExact(id, "D", out Guid parsed) || parsed == Guid.Empty)
                throw new InvalidDataException("anchor-fresh-identifier-invalid");
        if (string.IsNullOrEmpty(fresh.WorldId) || fresh.WorldId.Length > 256)
            throw new InvalidDataException("anchor-fresh-world-invalid");
        try
        {
            if (Utf8.GetByteCount(fresh.WorldId) > 256) throw new InvalidDataException("anchor-fresh-world-invalid");
        }
        catch (EncoderFallbackException error) { throw new InvalidDataException("anchor-fresh-world-invalid", error); }
        if (fresh.IntentDigest is null || fresh.IntentDigest.Length != 32)
            throw new InvalidDataException("anchor-fresh-intent-digest-invalid");
    }

    private ref struct FreshReader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> remaining = bytes;
        internal readonly bool AtEnd => remaining.IsEmpty;
        internal ReadOnlySpan<byte> Take(int count)
        {
            if (count > remaining.Length) throw new InvalidDataException("anchor-envelope-truncated");
            var value = remaining[..count]; remaining = remaining[count..]; return value;
        }
        internal string Text(int maximum)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
            if (length == 0 || length > maximum) throw new InvalidDataException("anchor-fresh-text-length-invalid");
            try { return Utf8.GetString(Take(length)); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("anchor-fresh-text-invalid", error); }
        }
    }
}
