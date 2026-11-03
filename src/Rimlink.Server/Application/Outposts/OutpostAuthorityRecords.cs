using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;

namespace Rimlink.Server.Application.Outposts;

public sealed record AuthorityHead(string Id, ByteString Digest);
public sealed record AnchorPending(string TransitionId, AuthorityHead NextHead);
public sealed record AnchorRestore(string Id, string NewBoundary, ByteString BackupDigest);
public sealed record AnchorRecord(uint FormatVersion, string InstallationId, string WorldId, string RestoreBoundary,
    AuthorityHead Committed, AnchorPending? Pending = null, AnchorRestore? Restore = null);

public static class AnchorRecordCodec
{
    private const int MaximumRecordBytes = 4096;
    private const int DigestBytes = 32;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static ReadOnlySpan<byte> Magic => "RLOANCHR"u8;

    // Format 1: magic, uint32 LE version, uint16 LE byte-length UTF8 strings, fixed 32-byte digests,
    // one phase byte (0 committed, 1 pending, 2 restore), optional phase data, SHA256 of all preceding bytes.
    // UUID D spelling is preserved exactly; neither input strings nor malformed wire data are repaired.
    public static byte[] Encode(AnchorRecord record)
    {
        Validate(record);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(record.FormatVersion);
        Text(record.InstallationId);
        Text(record.WorldId);
        Text(record.RestoreBoundary);
        Head(record.Committed);
        writer.Write((byte)(record.Pending is not null ? 1 : record.Restore is not null ? 2 : 0));
        if (record.Pending is { } pending)
        {
            Text(pending.TransitionId);
            Head(pending.NextHead);
        }
        if (record.Restore is { } restore)
        {
            Text(restore.Id);
            Text(restore.NewBoundary);
            writer.Write(restore.BackupDigest.Span);
        }
        writer.Flush();
        byte[] payload = stream.ToArray();
        if (payload.Length > MaximumRecordBytes - DigestBytes)
            throw new InvalidDataException("Anchor record is oversized.");
        return [.. payload, .. SHA256.HashData(payload)];

        void Text(string value)
        {
            byte[] encoded = Utf8.GetBytes(value);
            writer.Write(checked((ushort)encoded.Length));
            writer.Write(encoded);
        }
        void Head(AuthorityHead head) { Text(head.Id); writer.Write(head.Digest.Span); }
    }

    public static AnchorRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Magic.Length + sizeof(uint) + DigestBytes || bytes.Length > MaximumRecordBytes)
            throw new InvalidDataException("Anchor record is truncated or oversized.");
        ReadOnlySpan<byte> payload = bytes[..^DigestBytes];
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), bytes[^DigestBytes..]))
            throw new InvalidDataException("Anchor record checksum mismatch.");
        var reader = new RecordReader(payload);
        if (!reader.Take(Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Unknown anchor record magic.");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(reader.Take(sizeof(uint)));
        if (version != 1) throw new InvalidDataException("Unsupported anchor record version.");
        string installation = reader.Text(36);
        string world = reader.Text(256);
        string boundary = reader.Text(36);
        AuthorityHead committed = reader.Head();
        byte phase = reader.Take(1)[0];
        AnchorPending? pending = null;
        AnchorRestore? restore = null;
        switch (phase)
        {
            case 0: break;
            case 1: pending = new(reader.Text(36), reader.Head()); break;
            case 2: restore = new(reader.Text(36), reader.Text(36), reader.Digest()); break;
            default: throw new InvalidDataException("Unknown or conflicting anchor record phase.");
        }
        if (!reader.AtEnd) throw new InvalidDataException("Trailing anchor record bytes.");
        var record = new AnchorRecord(version, installation, world, boundary, committed, pending, restore);
        Validate(record);
        return record;
    }

    private static void Validate(AnchorRecord record)
    {
        if (record is null || record.FormatVersion != 1)
            throw new InvalidDataException("Missing or unsupported anchor record.");
        Uuid(record.InstallationId);
        Text(record.WorldId, 256);
        Uuid(record.RestoreBoundary);
        Guid committedId = Head(record.Committed);
        if (record.Pending is not null && record.Restore is not null)
            throw new InvalidDataException("Anchor pending and restore phases conflict.");
        if (record.Pending is { } pending)
        {
            Uuid(pending.TransitionId);
            if (Head(pending.NextHead) == committedId)
                throw new InvalidDataException("Anchor next head must differ from committed head.");
        }
        if (record.Restore is { } restore)
        {
            Uuid(restore.Id);
            Uuid(restore.NewBoundary);
            Digest(restore.BackupDigest);
        }
    }

    private static Guid Head(AuthorityHead head)
    {
        if (head is null) throw new InvalidDataException("Missing authority head.");
        Guid id = Uuid(head.Id);
        Digest(head.Digest);
        return id;
    }

    private static Guid Uuid(string value)
    {
        if (value is null || value.Length != 36 || !Guid.TryParseExact(value, "D", out Guid id))
            throw new InvalidDataException("Anchor identifier must be an exact UUID D string.");
        return id;
    }

    private static void Digest(ByteString value)
    {
        if (value is null || value.Length != DigestBytes)
            throw new InvalidDataException("Anchor digest must contain exactly 32 bytes.");
    }

    private static void Text(string value, int maximumBytes)
    {
        if (string.IsNullOrEmpty(value)) throw new InvalidDataException("Missing anchor text.");
        // UTF8 needs at least one byte per UTF16 code unit, so reject huge strings before scanning them.
        if (value.Length > maximumBytes) throw new InvalidDataException("Anchor text is oversized.");
        try
        {
            if (Utf8.GetByteCount(value) > maximumBytes) throw new InvalidDataException("Anchor text is oversized.");
        }
        catch (EncoderFallbackException error) { throw new InvalidDataException("Malformed anchor UTF16 text.", error); }
    }

    private ref struct RecordReader(ReadOnlySpan<byte> payload)
    {
        private ReadOnlySpan<byte> remaining = payload;
        public readonly bool AtEnd => remaining.IsEmpty;

        public ReadOnlySpan<byte> Take(int length)
        {
            if (length > remaining.Length) throw new InvalidDataException("Truncated anchor record payload.");
            ReadOnlySpan<byte> result = remaining[..length];
            remaining = remaining[length..];
            return result;
        }

        public string Text(int maximumBytes)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(Take(sizeof(ushort)));
            if (length == 0 || length > maximumBytes) throw new InvalidDataException("Invalid anchor text length.");
            try { return Utf8.GetString(Take(length)); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Malformed anchor UTF8 text.", error); }
        }

        public ByteString Digest() => ByteString.CopyFrom(Take(DigestBytes).ToArray());
        public AuthorityHead Head() => new(Text(36), Digest());
    }
}
