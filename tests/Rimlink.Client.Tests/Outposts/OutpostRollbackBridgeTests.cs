using Google.Protobuf;
using Rimlink.Client.Outposts;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostRollbackBridgeTests
{
    internal static (OutpostRollbackBridge Bridge, OutpostSnapshotProof Downloaded, OutpostScopeMessage Current, PawnRegistryEnvelope[] Registry) Fixture()
    {
        var source = new OutpostScopeMessage { WorldInstanceId = "world-1", OwnerPlayerId = 1, SaveGeneration = 3, SaveWriteEpoch = 4,
            RestoreBoundary = "11111111-1111-1111-1111-111111111111" };
        var current = source.Clone(); current.SaveWriteEpoch = 5;
        var own = new PawnRegistryEnvelope { SchemaVersion = 1, PawnCustodyId = "22222222-2222-2222-2222-222222222222",
            AssignmentId = "33333333-3333-3333-3333-333333333333", Scope = source.Clone(), PawnReferenceToken = "Thing_Human1" };
        var foreign = own.Clone(); foreign.PawnCustodyId = "44444444-4444-4444-4444-444444444444"; foreign.Scope.OwnerPlayerId = 2; foreign.PawnReferenceToken = "Thing_Human2";
        var dead = own.Clone(); dead.PawnCustodyId = "55555555-5555-5555-5555-555555555555"; dead.Tombstone = true; dead.PawnReferenceToken = "";
        var proof = new OutpostSnapshotProof { Scope = source.Clone(), SnapshotId = "66666666666666666666666666666666", RawSha256 = ByteString.CopyFrom(new byte[32]) };
        var bridge = new OutpostRollbackBridge { LifecycleOperationId = "77777777-7777-7777-7777-777777777777", TransitionId = "88888888-8888-8888-8888-888888888888",
            TransitionDigest = ByteString.CopyFrom(Enumerable.Repeat((byte)8, 32).ToArray()), CanonicalSnapshot = proof.Clone(), SourceScope = source.Clone(),
            CurrentScope = current.Clone(), LocalFrontierDigest = ByteString.CopyFrom(Enumerable.Repeat((byte)9, 32).ToArray()) };
        bridge.MutableRegistry.Add(own.Clone());
        return (bridge, proof, current, [own, foreign, dead]);
    }

    [Fact]
    public void Only_exact_authorized_live_own_registry_scope_changes_and_inputs_remain_immutable()
    {
        var f = Fixture(); byte[][] original = f.Registry.Select(r => r.ToByteArray()).ToArray(); byte[] bridge = f.Bridge.ToByteArray();
        var decision = OutpostRollbackBridgeChecks.Validate(f.Bridge, f.Downloaded, f.Current, f.Registry);
        Assert.True(decision.Allowed); Assert.Null(decision.RefusalKey); Assert.Equal(3, decision.Registry.Count);
        var expected = f.Registry[0].Clone(); expected.Scope = f.Current.Clone();
        Assert.Equal(expected, decision.Registry[0]); Assert.Equal(f.Registry[1], decision.Registry[1]); Assert.Equal(f.Registry[2], decision.Registry[2]);
        Assert.Equal(bridge, f.Bridge.ToByteArray()); Assert.Equal(original, f.Registry.Select(r => r.ToByteArray()).ToArray());
        decision.Registry[0].AssignmentId = "mutated"; Assert.Equal("33333333-3333-3333-3333-333333333333", f.Registry[0].AssignmentId);
    }

    [Theory]
    [InlineData("hash")][InlineData("snapshot")][InlineData("owner")][InlineData("generation")][InlineData("world")][InlineData("boundary")]
    [InlineData("epoch")][InlineData("assignment")][InlineData("reference")][InlineData("foreign")][InlineData("tombstone")][InlineData("mirror")]
    [InlineData("duplicate")][InlineData("missing")][InlineData("transition")][InlineData("frontier")]
    public void Wrong_download_namespace_or_registry_authorization_never_produces_any_scope_updates(string fault)
    {
        var f = Fixture();
        switch (fault)
        {
            case "hash": f.Downloaded.RawSha256 = ByteString.CopyFrom(Enumerable.Repeat((byte)1, 32).ToArray()); break;
            case "snapshot": f.Downloaded.SnapshotId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; break;
            case "owner": f.Current.OwnerPlayerId++; break;
            case "generation": f.Current.SaveGeneration++; break;
            case "world": f.Current.WorldInstanceId = "other"; break;
            case "boundary": f.Current.RestoreBoundary = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"; break;
            case "epoch": f.Current.SaveWriteEpoch = f.Bridge.SourceScope.SaveWriteEpoch; break;
            case "assignment": f.Bridge.MutableRegistry[0].AssignmentId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"; break;
            case "reference": f.Bridge.MutableRegistry[0].PawnReferenceToken = "Thing_Human9"; break;
            case "foreign": f.Bridge.MutableRegistry.Add(f.Registry[1].Clone()); break;
            case "tombstone": f.Bridge.MutableRegistry.Add(f.Registry[2].Clone()); break;
            case "mirror": f.Bridge.MutableRegistry[0].NonAuthoritativeMirror = true; break;
            case "duplicate": f.Bridge.MutableRegistry.Add(f.Bridge.MutableRegistry[0].Clone()); break;
            case "missing": f.Bridge.MutableRegistry[0].PawnCustodyId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"; break;
            case "transition": f.Bridge.TransitionId = ""; break;
            case "frontier": f.Bridge.LocalFrontierDigest = ByteString.Empty; break;
        }
        var decision = OutpostRollbackBridgeChecks.Validate(f.Bridge, f.Downloaded, f.Current, f.Registry);
        Assert.False(decision.Allowed); Assert.NotNull(decision.RefusalKey); Assert.Empty(decision.Registry);
    }
}
