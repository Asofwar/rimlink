using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAnchorEnvelopeTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Known_envelope_preserves_exact_version_one_bytes_and_invariants(int phase)
    {
        var digest = ByteString.CopyFrom(new byte[32]);
        var known = new AnchorRecord(1, Guid.NewGuid().ToString("D"), "world", Guid.NewGuid().ToString("D"),
            new(Guid.NewGuid().ToString("D"), digest),
            phase == 1 ? new(Guid.NewGuid().ToString("D"), new(Guid.NewGuid().ToString("D"), digest)) : null,
            phase == 2 ? new(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), digest) : null);
        byte[] original = AnchorRecordCodec.Encode(known);
        var envelope = new KnownAnchorEnvelope(known);
        Assert.Equal(original, AnchorEnvelopeCodec.Encode(envelope));
        Assert.Equal(envelope, AnchorEnvelopeCodec.Decode(original));
        Assert.Equal(known, AnchorRecordCodec.Decode(AnchorEnvelopeCodec.Encode(envelope)));
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 2, 2)]
    [InlineData(2, 1, 1)]
    public void Fresh_envelope_is_explicitly_headless_and_roundtrips_canonical_wire(int state, int mode, int witness)
    {
        var fresh = Fence() with { State = (FreshFenceState)state, Mode = (FreshFenceMode)mode, PreviousWitness = (PreviousWitnessKind)witness };
        byte[] expected = Wire(fresh);
        Assert.Equal(expected, AnchorEnvelopeCodec.Encode(fresh));
        Assert.Equal(fresh, AnchorEnvelopeCodec.Decode(expected));
        Assert.Equal(expected, AnchorEnvelopeCodec.Encode(AnchorEnvelopeCodec.Decode(expected)));
        Assert.Throws<InvalidDataException>(() => AnchorRecordCodec.Decode(expected));
        Assert.InRange(expected.Length, 1, 4096);
    }

    [Fact]
    public void Every_fresh_truncation_or_changed_byte_fails_closed()
    {
        byte[] expected = Wire(Fence());
        for (int length = 0; length < expected.Length; length++)
        {
            byte[] prefix = expected[..length];
            Assert.Throws<InvalidDataException>(() => AnchorEnvelopeCodec.Decode(prefix));
            if (length >= 32)
            {
                byte[] sealedPrefix = Seal(expected[..(length - 32)]);
                Assert.Throws<InvalidDataException>(() => AnchorEnvelopeCodec.Decode(sealedPrefix));
            }
        }
        for (int i = 0; i < expected.Length; i++)
        {
            byte[] changed = expected.ToArray(); changed[i] ^= 0x40;
            Assert.Throws<InvalidDataException>(() => AnchorEnvelopeCodec.Decode(changed));
        }
        Assert.Throws<InvalidDataException>(() => AnchorEnvelopeCodec.Decode(Seal(new byte[4097])));
    }

    [Fact]
    public void Unknown_tag_version_phase_or_witness_is_not_a_committed_record()
    {
        byte[] original = Wire(Fence())[..^32];
        foreach (var (offset, value) in new[] { (8, 3), (12, 0), (12, 255), (13, 0), (13, 255), (14, 0), (14, 255), (15, 0), (15, 255) })
        {
            byte[] payload = original.ToArray(); payload[offset] = (byte)value;
            Assert.Throws<InvalidDataException>(() => AnchorEnvelopeCodec.Decode(Seal(payload)));
        }
        Assert.Throws<InvalidDataException>(() => AnchorEnvelopeCodec.Decode(Seal([.. original, 0])));
    }

    [Fact]
    public void Invalid_fresh_combinations_and_fields_fail_independent_decode_and_encode()
    {
        var fresh = Fence();
        FreshAnchorFence[] invalid = [
            fresh with { State = FreshFenceState.BackedOutEmpty, Mode = FreshFenceMode.LostAnchorRecovery, PreviousWitness = PreviousWitnessKind.Unavailable },
            fresh with { PreviousWitness = PreviousWitnessKind.Unavailable },
            fresh with { Mode = FreshFenceMode.LostAnchorRecovery },
            fresh with { State = (FreshFenceState)0 }, fresh with { Mode = (FreshFenceMode)0 },
            fresh with { FenceId = Guid.Empty.ToString("D") }, fresh with { RestoreId = "not-a-uuid" },
            fresh with { InstallationId = Guid.Empty.ToString("D") }, fresh with { Boundary = Guid.Empty.ToString("D") },
            fresh with { WorldId = "" }, fresh with { WorldId = new string('я', 129) },
            fresh with { IntentDigest = ByteString.CopyFrom(new byte[31]) }
        ];
        foreach (var item in invalid)
        {
            Assert.Throws<InvalidDataException>(() => AnchorEnvelopeCodec.Encode(item));
            Assert.Throws<InvalidDataException>(() => AnchorEnvelopeCodec.Decode(Wire(item)));
        }
        Assert.Throws<InvalidDataException>(() => AnchorEnvelopeCodec.Encode(null!));
        Assert.Throws<InvalidDataException>(() => AnchorEnvelopeCodec.Encode(fresh with { FenceId = null! }));
        Assert.Throws<InvalidDataException>(() => AnchorEnvelopeCodec.Encode(fresh with { IntentDigest = null! }));
        Assert.Throws<InvalidDataException>(() => AnchorEnvelopeCodec.Encode(fresh with { WorldId = "\ud800" }));
    }

    private static FreshAnchorFence Fence() => new(FreshFenceState.RestoreInProgress, FreshFenceMode.ImportNewHost,
        PreviousWitnessKind.AbsentQualifiedEmpty, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
        Guid.NewGuid().ToString("D"), "world/колония", Guid.NewGuid().ToString("D"), ByteString.CopyFrom(Enumerable.Repeat((byte)7, 32).ToArray()));

    // Independent v2 fixture: no Committed/Pending bytes and no v1 encoder dependency.
    private static byte[] Wire(FreshAnchorFence fresh)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, new UTF8Encoding(false, true), leaveOpen: true);
        writer.Write("RLOANCHR"u8); writer.Write(2u); writer.Write((byte)1);
        writer.Write((byte)fresh.State); writer.Write((byte)fresh.Mode); writer.Write((byte)fresh.PreviousWitness);
        foreach (string text in new[] { fresh.FenceId, fresh.RestoreId, fresh.InstallationId, fresh.WorldId, fresh.Boundary })
        { byte[] bytes = Encoding.UTF8.GetBytes(text); writer.Write(checked((ushort)bytes.Length)); writer.Write(bytes); }
        writer.Write(fresh.IntentDigest.Span); writer.Flush();
        return Seal(memory.ToArray());
    }
    private static byte[] Seal(byte[] payload) => [.. payload, .. SHA256.HashData(payload)];
}
