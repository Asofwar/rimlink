using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Role = Rimlink.Server.Storage.Entities.Role;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminIntentCodecTests
{
    [Theory]
    [InlineData(OutpostAdminActionKind.Initialize)]
    [InlineData(OutpostAdminActionKind.ActivateDomainFormat)]
    [InlineData(OutpostAdminActionKind.ConfirmManifest)]
    [InlineData(OutpostAdminActionKind.Resolve)]
    public void Canonical_binary_round_trip_preserves_every_closed_action(OutpostAdminActionKind action)
    {
        var intent = Example(action);
        byte[] bytes = OutpostAdminIntentCodec.Encode(intent);
        Assert.Equal("RLOADMIN"u8.ToArray(), bytes[..8]);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, bytes[8..12]);
        Assert.Equal(SHA256.HashData(bytes), OutpostAdminIntentCodec.Digest(intent));
        Assert.Equal(bytes, OutpostAdminIntentCodec.Encode(OutpostAdminIntentCodec.Decode(bytes)));
    }

    [Theory]
    [InlineData("D")][InlineData("N")]
    public void Original_outpost_request_keeps_the_exact_supported_uuid_spelling(string format)
    {
        var intent = Example(OutpostAdminActionKind.Resolve);
        string requestId = Guid.Parse(Id(18)).ToString(format);
        intent = intent with { Resolution = intent.Resolution! with { Kind = OutpostAdminResolutionKind.ExistingTerminal, CanonicalSnapshot = null,
            Original = intent.Resolution.Original with { Family = OutpostAdminOperationFamily.Outpost, RequestId = requestId, LocalOperationId = null, DerivedRoute = null } } };
        var encoded = OutpostAdminIntentCodec.Encode(intent);
        Assert.Equal(requestId, OutpostAdminIntentCodec.Decode(encoded).Resolution!.Original.RequestId);
        Assert.Equal(encoded, OutpostAdminIntentCodec.Encode(OutpostAdminIntentCodec.Decode(encoded)));
    }

    [Fact]
    public void Buffer_freeze_is_deep_and_declared_evidence_set_does_not_reorder_the_caller()
    {
        var original = Example(OutpostAdminActionKind.Resolve);
        var frozen = OutpostAdminIntentCodec.Freeze(original);
        byte[] before = OutpostAdminIntentCodec.Encode(frozen);
        original.Expected.Committed!.HeadDigest[0] ^= 1;
        original.Resolution!.Scope.OwnerPlayerId++;
        original.Resolution.CanonicalSnapshot!.RawSha256 = ByteString.CopyFrom(new byte[32]);
        original.Resolution.Original.DerivedRoute!.ClientRequestId = Id(20);
        original.Evidence[0].Digest![0] ^= 1;
        Assert.Equal(before, OutpostAdminIntentCodec.Encode(frozen));
        var a = Example(OutpostAdminActionKind.Resolve);
        var b = a with { Evidence = a.Evidence.Reverse().ToArray() };
        Assert.Equal(OutpostAdminIntentCodec.Encode(a), OutpostAdminIntentCodec.Encode(b));
        Assert.Equal("selected-z", a.Evidence[0].Id);
    }

    [Fact]
    public void Actor_reason_head_scope_original_and_selected_evidence_all_change_the_digest()
    {
        var original = Example(OutpostAdminActionKind.Resolve);
        var committed = original.Expected.Committed!;
        var selection = original.Resolution!;
        var changedScope = selection.Scope.Clone(); changedScope.SaveWriteEpoch++;
        var variants = new[]
        {
            original with { Actor = original.Actor with { AccountId = 8 } },
            original with { Reason = original.Reason + " " },
            original with { RequestId = Id(21) },
            original with { Expected = original.Expected with { Committed = committed with { HeadId = Id(22) } } },
            original with { Expected = original.Expected with { Committed = committed with { HeadDigest = Hash(23) } } },
            original with { Resolution = selection with { Scope = changedScope } },
            original with { Resolution = selection with { Revision = selection.Revision + 1 } },
            original with { Resolution = selection with { Original = selection.Original with { IntentDigest = Hash(24) } } },
            original with { Resolution = selection with { Original = selection.Original with { LocalOperationId = "different-local" } } },
            original with { Evidence = [original.Evidence[0] with { Digest = Hash(25) }, original.Evidence[1]] },
            original with { Evidence = [original.Evidence[0] with { Digest = null }, original.Evidence[1]] }
        };
        Assert.All(variants, variant => Assert.NotEqual(OutpostAdminIntentCodec.Digest(original), OutpostAdminIntentCodec.Digest(variant)));
        Assert.NotEqual(OutpostAdminIntentCodec.ActorKey(original.Actor), OutpostAdminIntentCodec.ActorKey(variants[0].Actor));
        Assert.NotEqual(OutpostAdminIntentCodec.ObservationDigest(original.Expected), OutpostAdminIntentCodec.ObservationDigest(variants[3].Expected));
    }

    [Theory]
    [InlineData("version")][InlineData("action")][InlineData("union")][InlineData("observation")]
    [InlineData("fake-empty-head")][InlineData("profile")][InlineData("head-digest")][InlineData("reason-empty")]
    [InlineData("reason-whitespace")][InlineData("reason-control")][InlineData("reason-bytes")][InlineData("invalid-utf16")]
    [InlineData("references")][InlineData("duplicate-reference")][InlineData("unknown-reference")][InlineData("command-size")]
    [InlineData("activation-downgrade")][InlineData("activation-profile-swap")][InlineData("timestamp")][InlineData("allocation")]
    [InlineData("scope")][InlineData("derived")][InlineData("resolve-kind")]
    public void Invalid_or_oversized_intent_refuses_before_any_encoding_is_returned(string changed)
    {
        var i = Example(OutpostAdminActionKind.Resolve);
        i = changed switch
        {
            "version" => i with { Version = 2 }, "action" => i with { Action = (OutpostAdminActionKind)99 },
            "union" => i with { Manifest = Example(OutpostAdminActionKind.ConfirmManifest).Manifest },
            "observation" => i with { Expected = new(OutpostAuthorityObservationKind.Unavailable, Unavailable: new("unknown")) },
            "fake-empty-head" => i with { Expected = i.Expected with { Committed = i.Expected.Committed! with { HeadId = Guid.Empty.ToString("D") } } },
            "profile" => i with { Expected = i.Expected with { Committed = i.Expected.Committed! with { Schema = Schema() with { Digest = Hash(28) } } } },
            "head-digest" => i with { Expected = i.Expected with { Committed = i.Expected.Committed! with { HeadDigest = [1] } } },
            "reason-empty" => i with { Reason = "" }, "reason-whitespace" => i with { Reason = "  " },
            "reason-control" => i with { Reason = "not\nallowed" }, "reason-bytes" => i with { Reason = new string('Я', 513) },
            "invalid-utf16" => i with { Reason = new string((char)0xd800, 1) },
            "references" => i with { Evidence = Enumerable.Range(0, 65).Select(n => new OutpostAdminEvidenceReference(OutpostAdminEvidenceKind.Operation, n.ToString(), Hash(1))).ToArray() },
            "duplicate-reference" => i with { Evidence = [i.Evidence[0], i.Evidence[0] with { Digest = Hash(2) }] },
            "unknown-reference" => i with { Evidence = [i.Evidence[0] with { Kind = (OutpostAdminEvidenceKind)99 }] },
            "command-size" => i with { Evidence = Enumerable.Range(0, 64).Select(n => new OutpostAdminEvidenceReference(OutpostAdminEvidenceKind.Operation, new string('x', 1019) + n.ToString("D3"), Hash(1))).ToArray() },
            "activation-downgrade" => Activation() with { Activation = Activation().Activation! with { From = OutpostDomainFormat.V2, To = OutpostDomainFormat.V1 } },
            "activation-profile-swap" => Activation() with { Activation = Activation().Activation! with { TargetSchema = Schema() with { Digest = Hash(3) } } },
            "timestamp" => i with { Allocation = i.Allocation with { At = DateTime.SpecifyKind(i.Allocation.At, DateTimeKind.Local) } },
            "allocation" => i with { Allocation = i.Allocation with { AuditId = 0 } },
            "scope" => i with { Resolution = i.Resolution! with { Scope = new() } },
            "derived" => i with { Resolution = i.Resolution! with { Original = i.Resolution.Original with { DerivedRoute = new() } } },
            "resolve-kind" => i with { Resolution = i.Resolution! with { Kind = (OutpostAdminResolutionKind)99 } },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<InvalidDataException>(() => OutpostAdminIntentCodec.Encode(i));
    }

    [Fact]
    public void Decoder_refuses_unknown_version_truncation_and_extra_bytes()
    {
        byte[] encoded = OutpostAdminIntentCodec.Encode(Activation());
        byte[] version = (byte[])encoded.Clone(); version[8] = 2;
        Assert.Throws<InvalidDataException>(() => OutpostAdminIntentCodec.Decode(version));
        Assert.Throws<InvalidDataException>(() => OutpostAdminIntentCodec.Decode(encoded[..^1]));
        Assert.Throws<InvalidDataException>(() => OutpostAdminIntentCodec.Decode([.. encoded, 0]));
        Assert.Throws<InvalidDataException>(() => OutpostAdminIntentCodec.Decode(new byte[65537]));
    }

    private static OutpostAdminIntent Activation() => Example(OutpostAdminActionKind.ActivateDomainFormat);
    internal static OutpostAdminIntent Example(OutpostAdminActionKind action)
    {
        var scope = new OutpostScopeMessage { WorldInstanceId = "world-1", OwnerPlayerId = 7, SaveGeneration = 3, SaveWriteEpoch = 4, RestoreBoundary = Id(2) };
        var observation = action == OutpostAdminActionKind.Initialize
            ? new OutpostAuthorityObservation(OutpostAuthorityObservationKind.Uninitialized, Uninitialized: new("world-1", Hash(10), Hash(11), Schema()))
            : new OutpostAuthorityObservation(OutpostAuthorityObservationKind.Committed, Committed: new(Id(1), "world-1", Id(2), Id(3), Hash(12), Id(4), OutpostDomainFormat.V1, Schema()));
        return new(1, new Actor(7, 7, Role.Admin, "admin:Тест"), Id(5), "точная причина <не HTML>", observation,
            new(Id(6), Id(7), Id(8), 101, DateTime.UnixEpoch), action,
            [new(OutpostAdminEvidenceKind.Operation, "selected-z", Hash(13)), new(OutpostAdminEvidenceKind.Snapshot, "selected-a", null)],
            Initialize: action == OutpostAdminActionKind.Initialize ? new(Id(1), Id(2), Schema()) : null,
            Activation: action == OutpostAdminActionKind.ActivateDomainFormat ? new(OutpostDomainFormat.V1, OutpostDomainFormat.V2, Hash(14), Schema(), Schema(), OutpostDomainSchema.AdminMigrationId) : null,
            Manifest: action == OutpostAdminActionKind.ConfirmManifest ? new(Id(9), 2, Hash(15), Hash(16)) : null,
            Resolution: action == OutpostAdminActionKind.Resolve ? new(OutpostAdminResolutionKind.OrdinaryReturn, Id(9),
                new(OutpostAdminOperationFamily.Ordinary, "LivePropose", Id(17), "local-exact-id", Hash(18),
                    new OrdinaryDerivedRoute { RequestKind = OrdinaryRequestKind.RaidLaunch, ClientRequestId = Id(19), RequestIntentSha256 = ByteString.CopyFrom(Hash(20)) }),
                scope, "Started", 2, new() { SnapshotId = new string('a', 32), RawSha256 = ByteString.CopyFrom(Hash(21)), Scope = scope.Clone() }) : null);
    }
    internal static OutpostSchemaBinding Schema() => new("rimlink-outposts-schema/v2-crlf-crlf", Convert.FromHexString("73ef95ae47f0bd493ce2ea2d8ab1e1e4b1c8d4fa25ca2b3fa0d746f4732c374a"));
    private static byte[] Hash(byte value) => Enumerable.Repeat(value, 32).ToArray();
    private static string Id(int value) => value.ToString("x8") + "-1111-4111-8111-111111111111";
}
