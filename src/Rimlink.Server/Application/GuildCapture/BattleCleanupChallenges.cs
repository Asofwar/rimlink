using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record BattleCleanupAttestation(BattleCleanupChallengeMessage Challenge,long AgeTicks);

// V1 owns one physical slot. Challenge expiry never releases it; only the common cleanup transition can do that.
internal sealed class BattleCleanupChallenges(TimeProvider time)
{
    private readonly object gate = new();
    private BattleExecutorLease? issuedTo;
    private BattleCleanupChallengeMessage? current;
    private long issuedAt;
    internal BattleCleanupChallengeMessage Issue(BattleExecutorLease executor,BattleScopeMessage scope,BattleCleanupTerminalBindingV1 terminal,string runtime,ByteString inventory)
    {
        lock (gate)
        {
            if (current is not null && Fresh(out _) && issuedTo == executor && current.Scope.Equals(scope) && current.Terminal.Equals(terminal)
                && current.RuntimeInstanceId == runtime && current.InventorySha256.Equals(inventory)) return current.Clone();
            issuedTo = executor; issuedAt = time.GetTimestamp();
            current = new() { ChallengeId = Guid.NewGuid().ToString("D"),Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),Scope = scope.Clone(),
                CommittedFinalDigest = terminal.CommittedFinalDigest,Terminal=terminal.Clone(),RuntimeInstanceId = runtime,InventorySha256 = inventory,ExpiresAtUnixMs = time.GetUtcNow().AddSeconds(60).ToUnixTimeMilliseconds() };
            return current.Clone();
        }
    }
    internal BattleCleanupAttestation Require(BattleExecutorLease executor,BattleCleanupProofV1 proof)
    {
        lock (gate)
        {
            if (issuedTo != executor || current is null || !Fresh(out var age) || current.ChallengeId != proof.ChallengeId || !current.Nonce.Equals(proof.Nonce)
                || !current.Scope.Equals(proof.Scope) || !current.CommittedFinalDigest.Equals(proof.CommittedFinalDigest) || current.RuntimeInstanceId != proof.RuntimeInstanceId
                || !current.InventorySha256.Equals(proof.InventorySha256) || !current.Terminal.Equals(proof.Terminal)) throw new InvalidDataException("battle-cleanup-challenge-refused");
            return new(current.Clone(),age.Ticks);
        }
    }
    private bool Fresh(out TimeSpan age)
    { age = time.GetElapsedTime(issuedAt,time.GetTimestamp()); return age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(60); }
}
