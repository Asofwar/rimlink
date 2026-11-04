using Google.Protobuf;
using Rimlink.Client.Economy;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Outposts;

public enum OutpostRecoveryDecision { RecoveryRequired, Quarantine, WaitForProof, QueryOperation, Replay, AckOnly }

/// <summary>Exact local journal and accepted-byte evidence, never a preview's game tick.</summary>
public sealed record OutpostLocalEvidence(OutpostScopeMessage Scope, OutboxPhase Phase, string ClientRequestId,
    ByteString IntentDigest, OutpostSnapshotProof RequiredProof, OutpostSnapshotProof? AcceptedProof = null,
    OutpostTransitionReceipt? AcceptedReceipt = null, bool ReplyUnknown = false);

public static class OutpostReconciliation
{
    public static OutpostRecoveryDecision Decide(OutpostLocalEvidence local, OutpostStateReply remote)
    {
        if (!ValidScope(local.Scope) || !ValidScope(remote.Scope) || local.IntentDigest.Length != 32
            || local.ClientRequestId.Length is 0 or > 128 || !ValidProof(local.RequiredProof)
            || !SameScope(local.RequiredProof.Scope, local.Scope)) return OutpostRecoveryDecision.RecoveryRequired;
        if (!SameScope(local.Scope, remote.Scope)) return OutpostRecoveryDecision.Quarantine;
        if (local.Phase is not (OutboxPhase.Prepared or OutboxPhase.Ready)) return OutpostRecoveryDecision.RecoveryRequired;
        var known = remote.PendingOperations.Where(p => p.ClientRequestId == local.ClientRequestId).ToArray();
        if (known.Any(p => !p.IntentDigest.Equals(local.IntentDigest) || p.Status == OutpostOperationStatus.RecoveryRequired))
            return OutpostRecoveryDecision.RecoveryRequired;
        bool accepted = ValidProof(local.AcceptedProof) && SameScope(local.Scope, local.AcceptedProof!.Scope);
        if (accepted && local.AcceptedReceipt is { } receipt && receipt.ClientRequestId == local.ClientRequestId
            && receipt.IntentDigest.Equals(local.IntentDigest) && receipt.Snapshot is not null
            && receipt.Snapshot.Equals(local.RequiredProof)) return OutpostRecoveryDecision.AckOnly;
        if (local.Phase == OutboxPhase.Prepared)
            return local.ReplyUnknown ? OutpostRecoveryDecision.QueryOperation : OutpostRecoveryDecision.WaitForProof;
        if (!accepted || !local.RequiredProof.Equals(local.AcceptedProof)) return OutpostRecoveryDecision.WaitForProof;
        return local.ReplyUnknown ? OutpostRecoveryDecision.QueryOperation : OutpostRecoveryDecision.Replay;
    }

    internal static bool SameScope(OutpostScopeMessage? a, OutpostScopeMessage? b) => a is not null && b is not null && a.Equals(b);
    internal static bool ValidScope(OutpostScopeMessage? scope) => scope is { OwnerPlayerId: > 0, SaveGeneration: >= 0, SaveWriteEpoch: >= 0 }
        && scope.WorldInstanceId.Length is > 0 and <= 128 && scope.RestoreBoundary.Length is > 0 and <= 128;
    internal static bool ValidProof(OutpostSnapshotProof? proof) => proof is not null && ValidScope(proof.Scope)
        && proof.RawSha256.Length == 32 && Guid.TryParseExact(proof.SnapshotId, "N", out _);
}
