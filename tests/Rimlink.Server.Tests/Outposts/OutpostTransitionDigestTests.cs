using Google.Protobuf;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostTransitionDigestTests
{
    private const string Old = "aaaaaaaa-1111-2222-3333-444444444444";
    private const string Other = "99999999-1111-2222-3333-444444444444";
    private const int MaximumBlob = 32 * 1024 * 1024;

    [Fact]
    public void Golden_digest_covers_versioned_tagged_wire_and_is_repeatable()
    {
        var transition = Transition();
        var previous = Previous();

        var digest = OutpostTransitionDigest.Compute(previous, transition);

        // Independent 433-byte format-1 fixture: RLOTRANS + uint32LE version + 17 tagged fields.
        Assert.Equal("8E39A6814949F1E7D9972BAF99107C1B125D4DF3C28DC9CD75BC3D9CBE980714", Convert.ToHexString(digest.Span));
        Assert.Equal(digest, OutpostTransitionDigest.Compute(previous, Transition()));
        Assert.Equal(digest, OutpostTransitionDigest.Compute(previous, transition));
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("WorldId")]
    [InlineData("PlayerId")]
    [InlineData("SaveGeneration")]
    [InlineData("RestoreBoundary")]
    [InlineData("Kind")]
    [InlineData("Actor")]
    [InlineData("PredecessorId")]
    [InlineData("PawnCustodyId")]
    [InlineData("OutpostId")]
    [InlineData("NextHeadId")]
    [InlineData("Intent")]
    [InlineData("Effects")]
    [InlineData("CreatedAt")]
    public void Every_independently_mutable_immutable_field_changes_digest(string field)
    {
        var changed = Transition();
        switch (field)
        {
            case "Id": changed.Id = Other; break;
            case "WorldId": changed.WorldId += "2"; break;
            case "PlayerId": changed.PlayerId++; break;
            case "SaveGeneration": changed.SaveGeneration++; break;
            case "RestoreBoundary": changed.RestoreBoundary = Other; break;
            case "Kind": changed.Kind = "Abort"; break;
            case "Actor": changed.Actor = "admin"; break;
            case "PredecessorId": changed.PredecessorId = Other; break;
            case "PawnCustodyId": changed.PawnCustodyId = Other; break;
            case "OutpostId": changed.OutpostId = Other; break;
            case "NextHeadId": changed.NextHeadId = Other; break;
            case "Intent": changed.Intent[0] ^= 1; break;
            case "Effects": changed.Effects[0] ^= 1; break;
            case "CreatedAt": changed.CreatedAt = changed.CreatedAt.AddTicks(1); break;
            default: throw new InvalidOperationException(field);
        }

        Assert.NotEqual(OutpostTransitionDigest.Compute(Previous(), Transition()), OutpostTransitionDigest.Compute(Previous(), changed));
    }

    [Fact]
    public void Previous_digest_and_matching_old_head_identity_both_bind_the_chain()
    {
        var original = OutpostTransitionDigest.Compute(Previous(), Transition());
        Assert.NotEqual(original, OutpostTransitionDigest.Compute(new(Old, ByteString.CopyFrom(new byte[32])), Transition()));
        var changed = Transition();
        changed.OldHeadId = Other;
        Assert.NotEqual(original, OutpostTransitionDigest.Compute(Previous() with { Id = Other }, changed));
    }

    [Fact]
    public void Wrong_old_head_is_refused_not_repaired()
    {
        var transition = Transition();
        transition.OldHeadId = Other;

        Assert.Throws<InvalidDataException>(() => OutpostTransitionDigest.Compute(Previous(), transition));

        Assert.Equal(Other, transition.OldHeadId);
    }

    [Fact]
    public void Self_digest_is_excluded_and_never_rewritten()
    {
        var transition = Transition();
        byte[] self = [88, 99];
        transition.Digest = self;
        var expected = OutpostTransitionDigest.Compute(Previous(), Transition());

        Assert.Equal(expected, OutpostTransitionDigest.Compute(Previous(), transition));
        Assert.Same(self, transition.Digest);
        Assert.Equal(new byte[] { 88, 99 }, transition.Digest);
    }

    [Fact]
    public void Utc_and_unspecified_preserve_exact_stored_ticks_but_local_is_refused()
    {
        var transition = Transition();
        var utc = OutpostTransitionDigest.Compute(Previous(), transition);
        transition.CreatedAt = DateTime.SpecifyKind(transition.CreatedAt, DateTimeKind.Unspecified);
        Assert.Equal(utc, OutpostTransitionDigest.Compute(Previous(), transition));
        transition.CreatedAt = DateTime.SpecifyKind(transition.CreatedAt, DateTimeKind.Local);
        Assert.Throws<InvalidDataException>(() => OutpostTransitionDigest.Compute(Previous(), transition));
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("RestoreBoundary")]
    [InlineData("PredecessorId")]
    [InlineData("PawnCustodyId")]
    [InlineData("OutpostId")]
    [InlineData("OldHeadId")]
    [InlineData("NextHeadId")]
    public void Malformed_uuid_fields_are_refused(string field)
    {
        var transition = Transition();
        switch (field)
        {
            case "Id": transition.Id = "not-a-uuid"; break;
            case "RestoreBoundary": transition.RestoreBoundary = "not-a-uuid"; break;
            case "PredecessorId": transition.PredecessorId = ""; break;
            case "PawnCustodyId": transition.PawnCustodyId = "not-a-uuid"; break;
            case "OutpostId": transition.OutpostId = "not-a-uuid"; break;
            case "OldHeadId": transition.OldHeadId = Old.Replace("-", ""); break;
            case "NextHeadId": transition.NextHeadId = "not-a-uuid"; break;
            default: throw new InvalidOperationException(field);
        }
        Assert.Throws<InvalidDataException>(() => OutpostTransitionDigest.Compute(Previous(), transition));
    }

    [Theory]
    [InlineData("WorldId")]
    [InlineData("Kind")]
    [InlineData("Actor")]
    public void Text_requires_nonempty_strict_utf8_at_most_256_bytes(string field)
    {
        var transition = Transition();
        foreach (string invalid in new[] { "", new string('x', 257), new string('я', 129), "\ud800" })
        {
            SetText(transition, field, invalid == "\ud800" ? new string((char)0xd800, 1) : invalid);
            Assert.Throws<InvalidDataException>(() => OutpostTransitionDigest.Compute(Previous(), transition));
        }
        SetText(transition, field, new string('я', 128));
        Assert.Equal(32, OutpostTransitionDigest.Compute(Previous(), transition).Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Negative_player_or_generation_is_refused_but_genesis_zero_is_valid(bool player)
    {
        var transition = Transition();
        if (player) transition.PlayerId = -1; else transition.SaveGeneration = -1;
        Assert.Throws<InvalidDataException>(() => OutpostTransitionDigest.Compute(Previous(), transition));
        transition.PlayerId = 0;
        transition.SaveGeneration = 0;
        Assert.Equal(32, OutpostTransitionDigest.Compute(Previous(), transition).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Previous_digest_must_be_exactly_32_bytes(int length)
        => Assert.Throws<InvalidDataException>(() => OutpostTransitionDigest.Compute(new(Old, ByteString.CopyFrom(new byte[length])), Transition()));

    [Fact]
    public void Previous_id_must_be_exact_uuid_D_even_when_old_head_matches()
    {
        var transition = Transition();
        transition.OldHeadId = Old.Replace("-", "");
        Assert.Throws<InvalidDataException>(() => OutpostTransitionDigest.Compute(Previous() with { Id = transition.OldHeadId }, transition));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Blob_bound_is_inclusive_and_null_or_oversized_is_refused(bool intent)
    {
        var transition = Transition();
        SetBlob(transition, intent, new byte[MaximumBlob]);
        Assert.Equal(32, OutpostTransitionDigest.Compute(Previous(), transition).Length);
        SetBlob(transition, intent, new byte[MaximumBlob + 1]);
        Assert.Throws<InvalidDataException>(() => OutpostTransitionDigest.Compute(Previous(), transition));
        SetBlob(transition, intent, null!);
        Assert.Throws<InvalidDataException>(() => OutpostTransitionDigest.Compute(Previous(), transition));
    }

    [Fact]
    public void Field_lengths_prevent_concatenation_ambiguity_and_blob_empty_is_not_null()
    {
        var left = Transition();
        left.Kind = "ab"; left.Actor = "c";
        var right = Transition();
        right.Kind = "a"; right.Actor = "bc";
        Assert.NotEqual(OutpostTransitionDigest.Compute(Previous(), left), OutpostTransitionDigest.Compute(Previous(), right));
        left.Intent = [];
        left.Effects = [];
        Assert.Equal(32, OutpostTransitionDigest.Compute(Previous(), left).Length);
    }

    private static void SetText(OutpostTransition transition, string field, string value)
    {
        switch (field)
        {
            case "WorldId": transition.WorldId = value; break;
            case "Kind": transition.Kind = value; break;
            case "Actor": transition.Actor = value; break;
            default: throw new InvalidOperationException(field);
        }
    }

    private static void SetBlob(OutpostTransition transition, bool intent, byte[] value)
    {
        if (intent) transition.Intent = value; else transition.Effects = value;
    }

    private static AuthorityHead Previous() => new(Old, ByteString.CopyFrom(Enumerable.Repeat((byte)7, 32).ToArray()));
    private static OutpostTransition Transition() => new()
    {
        Id = "bbbbbbbb-1111-2222-3333-444444444444",
        WorldId = "world-Я",
        PlayerId = 17,
        SaveGeneration = 3,
        RestoreBoundary = "cccccccc-1111-2222-3333-444444444444",
        Kind = "Commit",
        Actor = "player:17",
        PawnCustodyId = "dddddddd-1111-2222-3333-444444444444",
        OutpostId = "eeeeeeee-1111-2222-3333-444444444444",
        OldHeadId = Old,
        NextHeadId = "ffffffff-1111-2222-3333-444444444444",
        Intent = [1, 2, 3],
        Effects = [4, 5],
        CreatedAt = new DateTime(638000000000000001, DateTimeKind.Utc)
    };
}
