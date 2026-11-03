using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public static class OutpostTransitionDigest
{
    private const int MaximumTextBytes = 256;
    private const int MaximumBlobBytes = 32 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    // Canonical format 1: ASCII RLOTRANS, uint32 LE version, then fields in tag order.
    // Each field is byte tag + uint32 LE byte length + bytes. Nullable UUIDs use length
    // uint.MaxValue for null (never empty). Integers and stored DateTime ticks are int64 LE.
    // Tags 1..17: previous ID/digest; transition ID, world, player, generation, boundary,
    // kind, actor, predecessor, custody, outpost, old head, next head, intent, effects, time.
    // Preserve exact UUID spelling and UTF8 bytes; do not normalize or include self Digest.
    public static ByteString Compute(AuthorityHead previous, OutpostTransition transition)
    {
        if (previous is null || transition is null)
            throw new InvalidDataException("Missing transition or previous authority head.");
        Uuid(previous.Id);
        if (previous.Digest is not { Length: 32 })
            throw new InvalidDataException("Previous authority digest must contain exactly 32 bytes.");
        Uuid(transition.Id);
        Text(transition.WorldId);
        if (transition.PlayerId < 0 || transition.SaveGeneration < 0)
            throw new InvalidDataException("Negative transition player or generation.");
        Uuid(transition.RestoreBoundary);
        Text(transition.Kind);
        Text(transition.Actor);
        OptionalUuid(transition.PredecessorId);
        OptionalUuid(transition.PawnCustodyId);
        OptionalUuid(transition.OutpostId);
        Uuid(transition.OldHeadId);
        Uuid(transition.NextHeadId);
        if (!string.Equals(previous.Id, transition.OldHeadId, StringComparison.Ordinal))
            throw new InvalidDataException("Transition old head does not match the previous authority head.");
        Blob(transition.Intent);
        Blob(transition.Effects);
        // EF SQLite can materialize UTC values with Kind.Unspecified. Their exact stored ticks
        // remain authoritative; never call ToUniversalTime(), which depends on the local zone.
        if (transition.CreatedAt.Kind == DateTimeKind.Local)
            throw new InvalidDataException("Local transition time is not a stored UTC timestamp.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("RLOTRANS"u8);
        Span<byte> version = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, 1);
        hash.AppendData(version);
        AddText(1, previous.Id);
        AddBytes(2, previous.Digest.Span);
        AddText(3, transition.Id);
        AddText(4, transition.WorldId);
        AddNumber(5, transition.PlayerId);
        AddNumber(6, transition.SaveGeneration);
        AddText(7, transition.RestoreBoundary);
        AddText(8, transition.Kind);
        AddText(9, transition.Actor);
        AddOptionalText(10, transition.PredecessorId);
        AddOptionalText(11, transition.PawnCustodyId);
        AddOptionalText(12, transition.OutpostId);
        AddText(13, transition.OldHeadId);
        AddText(14, transition.NextHeadId);
        AddBytes(15, transition.Intent);
        AddBytes(16, transition.Effects);
        AddNumber(17, transition.CreatedAt.Ticks);
        return ByteString.CopyFrom(hash.GetHashAndReset());

        void AddText(byte tag, string value) => AddBytes(tag, Utf8.GetBytes(value));
        void AddOptionalText(byte tag, string? value)
        {
            if (value is null) AddHeader(tag, uint.MaxValue);
            else AddText(tag, value);
        }
        void AddNumber(byte tag, long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            AddBytes(tag, bytes);
        }
        void AddBytes(byte tag, ReadOnlySpan<byte> bytes)
        {
            AddHeader(tag, checked((uint)bytes.Length));
            hash.AppendData(bytes);
        }
        void AddHeader(byte tag, uint length)
        {
            Span<byte> header = stackalloc byte[5];
            header[0] = tag;
            BinaryPrimitives.WriteUInt32LittleEndian(header[1..], length);
            hash.AppendData(header);
        }
    }

    private static void Uuid(string? value)
    {
        if (value is null || value.Length != 36 || !Guid.TryParseExact(value, "D", out _))
            throw new InvalidDataException("Transition identifier must be an exact UUID D string.");
    }

    private static void OptionalUuid(string? value)
    {
        if (value is not null) Uuid(value);
    }

    private static void Text(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumTextBytes)
            throw new InvalidDataException("Transition text is missing or oversized.");
        try
        {
            if (Utf8.GetByteCount(value) > MaximumTextBytes)
                throw new InvalidDataException("Transition text is oversized.");
        }
        catch (EncoderFallbackException error)
        {
            throw new InvalidDataException("Malformed transition UTF16 text.", error);
        }
    }

    private static void Blob(byte[]? value)
    {
        if (value is null || value.Length > MaximumBlobBytes)
            throw new InvalidDataException("Transition blob is missing or oversized.");
    }
}
