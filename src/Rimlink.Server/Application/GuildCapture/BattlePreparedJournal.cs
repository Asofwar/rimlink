using System.Globalization;
using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record BattlePreparedProposal(int Sequence,byte[] Manifest,byte[] ManifestDigest,BattleArtifactDescriptor Descriptor,BattleArtifactProof Artifact);

// The fixed, contiguous, immutable start artifacts are the pre-Start portion of the transport journal.
internal sealed class BattlePreparedJournal(BattleArtifactStore artifacts)
{
    internal BattlePreparedProposal ProposeHeld(ReadOnlySpan<byte> bytes)
    {
        var manifest = BattleManifestContracts.ReadPrepared(bytes);
        BattlePreparedManifestV1? previous = null;
        for (int sequence = 1;sequence < manifest.ProposalSequence;sequence++)
        {
            var prior = ReadHeld(manifest.BattleId,manifest.Fence,sequence);
            var priorManifest = BattleManifestContracts.ReadPrepared(prior.Manifest);
            if (previous is not null) RequireFixed(previous,priorManifest);
            previous = priorManifest;
        }
        if (previous is not null) RequireFixed(previous,manifest);
        var descriptor = new BattleArtifactDescriptor(1,manifest.BattleId,manifest.WorldInstanceId,manifest.RestoreBoundary,manifest.ExecutorId,
            manifest.Fence,Kind(manifest.ProposalSequence),bytes.Length,Convert.ToHexStringLower(SHA256.HashData(bytes)));
        _ = artifacts.AppendHeld(descriptor,0,bytes);
        return ReadHeld(manifest.BattleId,manifest.Fence,manifest.ProposalSequence);
    }
    internal BattlePreparedProposal? LatestHeld(string battleId,long fence)
    {
        BattlePreparedProposal? latest = null; BattlePreparedManifestV1? previous = null;
        bool missing = false;
        for (int sequence = 1;sequence <= 8;sequence++)
        {
            var descriptor = artifacts.DescriptorHeld(battleId,fence,Kind(sequence));
            if (descriptor is null) { missing = true; continue; }
            if (missing) throw Invalid();
            latest = ReadHeld(battleId,fence,sequence);
            var current = BattleManifestContracts.ReadPrepared(latest.Manifest);
            if (previous is not null) RequireFixed(previous,current);
            previous = current;
        }
        return latest;
    }
    internal BattlePreparedProposal ReadHeld(string battleId,long fence,int sequence)
    {
        var descriptor = artifacts.DescriptorHeld(battleId,fence,Kind(sequence)) ?? throw Invalid();
        if (descriptor.SizeBytes > BattleManifestContracts.PreparedBytes) throw Invalid();
        var proof = artifacts.ValidateCompleteHeld(descriptor);
        byte[] bytes = artifacts.ReadHeld(descriptor,0);
        var manifest = BattleManifestContracts.ReadPrepared(bytes);
        if (manifest.BattleId != battleId || manifest.Fence != fence || manifest.ProposalSequence != sequence
            || manifest.WorldInstanceId != descriptor.WorldId || manifest.RestoreBoundary != descriptor.Boundary || manifest.ExecutorId != descriptor.ExecutorId) throw Invalid();
        return new(sequence,bytes,BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,bytes),descriptor,proof);
    }
    private static void RequireFixed(BattlePreparedManifestV1 previous,BattlePreparedManifestV1 next)
    {
        var allowed = previous.Clone(); allowed.ProposalSequence = next.ProposalSequence;
        allowed.Attacker = next.Attacker.Clone(); allowed.Defender = next.Defender.Clone();
        if (!allowed.Equals(next)) throw Invalid();
    }
    private static string Kind(int sequence) => sequence is >= 1 and <= 8 ? "start-" + sequence.ToString(CultureInfo.InvariantCulture) : throw Invalid();
    private static InvalidDataException Invalid() => new("battle-prepared-proposal-invalid");
}
