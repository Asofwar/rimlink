using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

// Codec tests establish bounded logical records only, not SQLite durability or the authority lock graph.
public sealed class OutpostAnchorRecordTests
{
    private const string Installation = "aaaaaaaa-1111-2222-3333-444444444444";
    private const string Boundary = "bbbbbbbb-1111-2222-3333-444444444444";
    private const string Head = "cccccccc-1111-2222-3333-444444444444";
    private const string Next = "dddddddd-1111-2222-3333-444444444444";
    private const string Operation = "eeeeeeee-1111-2222-3333-444444444444";
    private const string NewBoundary = "ffffffff-1111-2222-3333-444444444444";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Committed_pending_and_restore_roundtrip_by_content_and_match_canonical_wire(int phase)
    {
        var record = Record(phase);
        byte[] encoded = AnchorRecordCodec.Encode(record);
        Assert.Equal(Wire(record), encoded);
        Assert.Equal(record, AnchorRecordCodec.Decode(encoded));
        Assert.Equal(encoded, AnchorRecordCodec.Encode(record));
        Assert.Equal(encoded, AnchorRecordCodec.Encode(AnchorRecordCodec.Decode(encoded)));
    }

    [Fact]
    public void Opaque_world_uses_UTF8_byte_limit_not_character_limit()
    {
        var record = Record() with { WorldId = new string('я', 128) };
        Assert.Equal(record, AnchorRecordCodec.Decode(AnchorRecordCodec.Encode(record)));
        Assert.Throws<InvalidDataException>(() => AnchorRecordCodec.Encode(record with { WorldId = record.WorldId + "я" }));
        Assert.Throws<InvalidDataException>(() => AnchorRecordCodec.Decode(Wire(record with { WorldId = record.WorldId + "я" })));
    }

    [Fact]
    public void Digest_and_wire_buffers_do_not_alias_decoded_records()
    {
        byte[] source = Enumerable.Repeat((byte)7, 32).ToArray();
        var record = Record() with { Committed = new(Head, ByteString.CopyFrom(source)) };
        source[0] = 99;
        byte[] encoded = AnchorRecordCodec.Encode(record);
        var decoded = AnchorRecordCodec.Decode(encoded);
        Array.Fill(encoded, (byte)0);
        byte[] copy = decoded.Committed.Digest.ToByteArray();
        copy[0] = 88;
        Assert.Equal((byte)7, decoded.Committed.Digest[0]);
        Assert.Equal(record, decoded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Every_truncated_prefix_is_rejected(int phase)
    {
        byte[] encoded = Wire(Record(phase));
        for (int length = 0; length < encoded.Length; length++)
        {
            byte[] prefix = encoded[..length];
            Assert.Throws<InvalidDataException>(() => AnchorRecordCodec.Decode(prefix));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Every_truncated_payload_is_rejected_even_with_valid_checksum(int phase)
    {
        byte[] payload = Wire(Record(phase))[..^32];
        for (int length = 0; length < payload.Length; length++)
        {
            byte[] prefix = Seal(payload[..length]);
            Assert.Throws<InvalidDataException>(() => AnchorRecordCodec.Decode(prefix));
        }
    }

    [Fact]
    public void Every_single_byte_corruption_is_rejected_without_repair()
    {
        byte[] encoded = Wire(Record(1));
        for (int offset = 0; offset < encoded.Length; offset++)
        {
            byte[] corrupt = (byte[])encoded.Clone();
            corrupt[offset] ^= 0x40;
            Assert.Throws<InvalidDataException>(() => AnchorRecordCodec.Decode(corrupt));
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void Unknown_version_is_rejected_even_with_valid_checksum(uint version)
    {
        var record = Record() with { FormatVersion = version };
        Assert.Throws<InvalidDataException>(() => AnchorRecordCodec.Encode(record));
        Assert.Throws<InvalidDataException>(() => AnchorRecordCodec.Decode(Wire(record)));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(128)]
    [InlineData(255)]
    public void Unknown_or_dual_phase_flags_are_rejected_even_with_valid_checksum(int flags)
    {
        byte[] payload = Wire(Record())[..^32];
        payload[^1] = (byte)flags;
        Assert.Throws<InvalidDataException>(() => AnchorRecordCodec.Decode(Seal(payload)));
    }

    public static IEnumerable<object[]> InvalidRecords()
    {
        var record = Record();
        yield return [record with { InstallationId = "not-a-uuid" }];
        yield return [record with { InstallationId = "{" + Installation + "}" }];
        yield return [record with { RestoreBoundary = Boundary.Replace("-", "") }];
        yield return [record with { Committed = record.Committed with { Id = "" } }];
        yield return [record with { Committed = new(Head, ByteString.CopyFrom(new byte[31])) }];
        yield return [record with { Committed = new(Head, ByteString.CopyFrom(new byte[33])) }];
        yield return [record with { WorldId = "" }];
        yield return [record with { WorldId = new string('w', 257) }];
        yield return [Record(1) with { Pending = new("bad-transition", new(Next, Digest(2))) }];
        yield return [Record(1) with { Pending = new(Operation, new("bad-next-head", Digest(2))) }];
        yield return [Record(1) with { Pending = new(Operation, new(Head, Digest(2))) }];
        yield return [Record(1) with { Pending = new(Operation, new(Head.ToUpperInvariant(), Digest(2))) }];
        yield return [Record(1) with { Pending = new(Operation, new(Next, ByteString.Empty)) }];
        yield return [Record(2) with { Restore = new("bad-restore", NewBoundary, Digest(3)) }];
        yield return [Record(2) with { Restore = new(Operation, "bad-boundary", Digest(3)) }];
        yield return [Record(2) with { Restore = new(Operation, NewBoundary, ByteString.Empty) }];
        yield return [Record(1) with { Restore = Record(2).Restore }];
    }

    [Theory]
    [MemberData(nameof(InvalidRecords))]
    public void Invalid_records_are_rejected_on_encode_and_independent_wire_decode(AnchorRecord record)
    {
        Assert.Throws<InvalidDataException>(() => AnchorRecordCodec.Encode(record));
        // Independent fixture serializes invalid data without going through the production encoder.
        Assert.Throws<InvalidDataException>(() => AnchorRecordCodec.Decode(Wire(record)));
    }

    [Fact]
    public void Null_members_and_unpaired_UTF16_surrogates_are_rejected_on_encode()
    {
        AnchorRecord[] records = [
            null!, Record() with { InstallationId = null! }, Record() with { WorldId = null! },
            Record() with { RestoreBoundary = null! }, Record() with { Committed = null! },
            Record() with { Committed = new(null!, Digest(1)) }, Record() with { Committed = new(Head, null!) },
            Record(1) with { Pending = new(Operation, null!) },
            Record(1) with { Pending = new(null!, new(Next, Digest(2))) },
            Record(2) with { Restore = new(Operation, null!, Digest(3)) },
            Record(2) with { Restore = new(Operation, NewBoundary, null!) },
            Record() with { WorldId = "\ud800" }
        ];
        foreach (var record in records)
            Assert.Throws<InvalidDataException>(() => AnchorRecordCodec.Encode(record));
    }

    [Theory]
    [InlineData(0)] // bad magic
    [InlineData(1)] // malformed/overlong UTF8 world bytes
    [InlineData(2)] // impossible string length
    [InlineData(3)] // extra byte before checksum
    [InlineData(4)] // BOM prefix is not part of magic
    public void Noncanonical_payload_is_rejected_after_recomputing_checksum(int defect)
    {
        byte[] payload = Wire(Record())[..^32];
        const int worldLengthOffset = 8 + 4 + 2 + 36;
        switch (defect)
        {
            case 0: payload[0] ^= 1; break;
            case 1:
                payload[worldLengthOffset + 2] = 0xc0;
                payload[worldLengthOffset + 3] = 0xaf;
                break;
            case 2: BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(worldLengthOffset), ushort.MaxValue); break;
            case 3: payload = [.. payload, 0]; break;
            case 4: payload = [0xef, 0xbb, 0xbf, .. payload]; break;
        }
        Assert.Throws<InvalidDataException>(() => AnchorRecordCodec.Decode(Seal(payload)));
    }

    [Fact]
    public void Trailing_bytes_and_oversized_input_are_rejected()
    {
        byte[] encoded = Wire(Record());
        Assert.Throws<InvalidDataException>(() => AnchorRecordCodec.Decode([.. encoded, 0]));
        Assert.Throws<InvalidDataException>(() => AnchorRecordCodec.Decode(Seal(new byte[4096 - 32 + 1])));
        Assert.Throws<InvalidDataException>(() => AnchorRecordCodec.Encode(Record() with { WorldId = new string('x', 4097) }));
    }

    private static ByteString Digest(byte value) => ByteString.CopyFrom(Enumerable.Repeat(value, 32).ToArray());

    private static AnchorRecord Record(int phase = 0) => new(1, Installation, "opaque-world/колония", Boundary,
        new(Head, Digest(1)), phase == 1 ? new(Operation, new(Next, Digest(2))) : null,
        phase == 2 ? new(Operation, NewBoundary, Digest(3)) : null);

    // Format 1 is fixed-width LE lengths/version, strict UTF8, fixed-size digest bytes, and a trailing SHA256.
    private static byte[] Wire(AnchorRecord record)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true);
        writer.Write("RLOANCHR"u8);
        writer.Write(record.FormatVersion);
        Text(record.InstallationId);
        Text(record.WorldId);
        Text(record.RestoreBoundary);
        WriteHead(record.Committed);
        writer.Write((byte)((record.Pending is not null ? 1 : 0) | (record.Restore is not null ? 2 : 0)));
        if (record.Pending is { } pending) { Text(pending.TransitionId); WriteHead(pending.NextHead); }
        if (record.Restore is { } restore) { Text(restore.Id); Text(restore.NewBoundary); writer.Write(restore.BackupDigest.Span); }
        writer.Flush();
        return Seal(stream.ToArray());

        void Text(string text)
        {
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);
            writer.Write(checked((ushort)bytes.Length));
            writer.Write(bytes);
        }
        void WriteHead(AuthorityHead head) { Text(head.Id); writer.Write(head.Digest.Span); }
    }

    private static byte[] Seal(byte[] payload) => [.. payload, .. SHA256.HashData(payload)];
}
