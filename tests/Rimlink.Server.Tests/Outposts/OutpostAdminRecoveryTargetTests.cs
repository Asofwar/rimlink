using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAdminRecoveryTargetTests
{
    [Theory]
    [InlineData("Unknown")][InlineData("Applied")][InlineData("ReturnStarted")][InlineData("ReturnAccepted")]
    public void Ordinary_target_keeps_local_and_attempt_identities_separate_and_roundtrips(string state)
    {
        var intent = Ordinary(state); byte[] bytes = OutpostAdminIntentCodec.Encode(intent);
        var copy = OutpostAdminIntentCodec.Decode(bytes);
        Assert.Equal(2, copy.Version); Assert.Null(copy.Resolution);
        Assert.Equal(intent.Recovery!.Ordinary!.OperationId, copy.Recovery!.Ordinary!.OperationId);
        Assert.Equal(intent.Recovery.Ordinary.ExistingReturnAttemptId, copy.Recovery.Ordinary.ExistingReturnAttemptId);
        Assert.Equal(intent.Recovery.Ordinary.Original.RequestId, copy.Recovery.Ordinary.Original.RequestId);
        Assert.Equal(bytes, OutpostAdminIntentCodec.Encode(copy));
        Assert.DoesNotContain("OriginalRequest", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void Canonical_target_has_no_fake_business_request_or_revision(bool view)
    {
        var original = Ordinary("Unknown"); var scope = original.Recovery!.Ordinary!.Scope;
        var target = new OutpostAdminCanonicalTarget(13, new string('b', 32), scope.Clone(), scope.Clone(), 1234, Hash(4),
            view ? OutpostAdminSnapshotViewKind.AuthorizedView : OutpostAdminSnapshotViewKind.DirectActual,
            view ? Guid.NewGuid().ToString("D") : null, view ? Hash(5) : null, view ? Hash(6) : null);
        if (view) target = target with { ViewScope = scope.Clone() }; // Same shapes still need actual server provenance validation later.
        var intent = original with { Recovery = new(OutpostAdminRecoveryTargetKind.CanonicalSnapshot, Canonical: target) };
        var copy = OutpostAdminIntentCodec.Decode(OutpostAdminIntentCodec.Encode(intent));
        Assert.Null(copy.Resolution); Assert.Null(copy.Recovery!.Ordinary); Assert.NotNull(copy.Recovery.Canonical);
        Assert.Equal(target.SnapshotId, copy.Recovery.Canonical.SnapshotId); Assert.Equal(target.RawDigest, copy.Recovery.Canonical.RawDigest);
    }

    [Fact]
    public void V2_freeze_detaches_all_proofs_and_commitments_without_reinterpreting_v1()
    {
        var input = Ordinary("Unknown"); var frozen = OutpostAdminIntentCodec.Freeze(input); byte[] before = OutpostAdminIntentCodec.Encode(frozen);
        input.Recovery!.Ordinary!.Original.IntentDigest[0] ^= 1;
        input.Recovery.Ordinary.Input!.InputDigest[0] ^= 1; input.Recovery.Ordinary.RemovalSnapshot.Scope.SaveWriteEpoch++;
        Assert.Equal(before, OutpostAdminIntentCodec.Encode(frozen));
        Assert.Throws<InvalidDataException>(() => OutpostAdminIntentCodec.Encode(input with { Version = 1 }));
        var v1 = OutpostAdminIntentCodecTests.Example(OutpostAdminActionKind.ActivateDomainFormat);
        Assert.Throws<InvalidDataException>(() => OutpostAdminIntentCodec.Encode(v1 with { Version = 2 }));
    }

    [Theory]
    [InlineData("union")][InlineData("legacy-resolution")][InlineData("family")][InlineData("enum-number")]
    [InlineData("request-D")][InlineData("local-D")][InlineData("local-swapped")][InlineData("revision")]
    [InlineData("state")][InlineData("unknown-attempt")][InlineData("started-without-attempt")][InlineData("accepted-with-input")]
    [InlineData("missing-removal")][InlineData("missing-canonical")][InlineData("input-hash")][InlineData("input-size")]
    public void Malformed_target_is_refused_before_any_authority_or_evidence_effect(string damage)
    {
        var input = Ordinary(damage == "accepted-with-input" ? "ReturnAccepted" : "Unknown"); var target = input.Recovery!.Ordinary!;
        input = damage switch
        {
            "union" => input with { Recovery = input.Recovery with { Canonical = new(1, new string('a',32), target.Scope, target.Scope,1,Hash(1),OutpostAdminSnapshotViewKind.DirectActual) } },
            "legacy-resolution" => input with { Resolution = OutpostAdminIntentCodecTests.Example(OutpostAdminActionKind.Resolve).Resolution },
            "family" => input with { Recovery = input.Recovery with { Ordinary = target with { Original = target.Original with { Family = OutpostAdminOperationFamily.Outpost } } } },
            "enum-number" => Change(target with { Original = target.Original with { Kind = "1" } }),
            "request-D" => Change(target with { Original = target.Original with { RequestId = Guid.NewGuid().ToString("D") } }),
            "local-D" => Change(target with { OperationId = Guid.NewGuid().ToString("D") }),
            "local-swapped" => Change(target with { OperationId = Guid.NewGuid().ToString("N") }),
            "revision" => Change(target with { Revision = 1 }), "state" => Change(target with { ExpectedState = "RecoveryRequired" }),
            "unknown-attempt" => Change(target with { ExistingReturnAttemptId = Guid.NewGuid().ToString("D") }),
            "started-without-attempt" => Change(target with { ExpectedState = "ReturnStarted", Input = null }),
            "accepted-with-input" => Change(target with { Input = Commitment() }),
            "missing-removal" => Change(target with { RemovalSnapshot = null! }),
            "missing-canonical" => Change(target with { CanonicalSnapshot = null! }),
            "input-hash" => Change(target with { Input = target.Input! with { AuthorizationDigest = new byte[31] } }),
            "input-size" => Change(target with { Input = target.Input! with { InputLength = 1024 * 1024 } }),
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<InvalidDataException>(() => OutpostAdminIntentCodec.Encode(input));
        OutpostAdminIntent Change(OutpostAdminOrdinaryTarget changed) => input with { Recovery = input.Recovery with { Ordinary = changed } };
    }

    [Theory]
    [InlineData(OutpostAdminActionKind.Initialize)][InlineData(OutpostAdminActionKind.ActivateDomainFormat)]
    [InlineData(OutpostAdminActionKind.ConfirmManifest)][InlineData(OutpostAdminActionKind.Resolve)]
    public void Existing_v1_vectors_remain_byte_exact(OutpostAdminActionKind action)
    {
        byte[] bytes = OutpostAdminIntentCodec.Encode(OutpostAdminIntentCodecTests.Example(action));
        var expected = action switch
        {
            OutpostAdminActionKind.Initialize => (670, "c1f7a36c8b48e1b8e6bbad5431c6fae539da5848baf77c1181e27d5bd340962a"),
            OutpostAdminActionKind.ActivateDomainFormat => (877, "c07fa80d0d4d5bf262c987ccf0829f70431664912ec7fa203457ba7a75346793"),
            OutpostAdminActionKind.ConfirmManifest => (762, "5775d45dc0f9eeec8166c764a3e6141281eb34eea9c5189c6749bbd3ee31a284"),
            OutpostAdminActionKind.Resolve => (1123, "559523e51d849c28958c184a821cf0f0bbb09291fd111c7373c5eccc426ee7b2"),
            _ => throw new InvalidOperationException()
        };
        Assert.Equal(expected.Item1, bytes.Length);
        Assert.Equal(expected.Item2, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Equal(bytes, OutpostAdminIntentCodec.Encode(OutpostAdminIntentCodec.Decode(bytes)));
    }

    internal static OutpostAdminIntent Ordinary(string state = "Unknown")
    {
        var original = OutpostAdminIntentCodecTests.Example(OutpostAdminActionKind.Resolve);
        var scope = original.Resolution!.Scope.Clone(); string local = new string('a', 32), request = new string('c', 32);
        var target = new OutpostAdminOrdinaryTarget(local, new(OutpostAdminOperationFamily.Ordinary,"TransferSend",request,local,Hash(1),null),
            scope, state, 0, state is "ReturnStarted" or "ReturnAccepted" ? Guid.NewGuid().ToString("D") : null,
            new() { SnapshotId = new string('b',32), RawSha256 = ByteString.CopyFrom(Hash(2)), Scope = scope.Clone() },
            new() { SnapshotId = new string('d',32), RawSha256 = ByteString.CopyFrom(Hash(3)), Scope = scope.Clone() }, state == "Unknown" ? Commitment() : null);
        return original with { Version = 2, Resolution = null, Recovery = new(OutpostAdminRecoveryTargetKind.OrdinaryCapture, Ordinary: target),
            Expected = original.Expected with { Committed = original.Expected.Committed! with { DomainFormat = OutpostDomainFormat.V2 } } };
    }
    private static OutpostAdminOrdinaryInputCommitment Commitment() => new(1, 300, Hash(4), 100, Hash(5), 400, Hash(6));
    private static byte[] Hash(byte b) => Enumerable.Repeat(b,32).ToArray();
}
