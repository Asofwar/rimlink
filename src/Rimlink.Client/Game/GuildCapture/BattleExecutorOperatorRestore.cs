using Google.Protobuf;
using Grpc.Core;
using Rimlink.Client.GuildCapture;
using Rimlink.Client.UI;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Game.GuildCapture;

internal sealed partial class BattleExecutorOperator
{
    private async Task RestoreCleanup()
    {
        Status = "RestoreCleanup";
        var selection = settings.ReadRestoreCleanup();
        await VerifyRuntime();
        var binding = await Binding(BattleExecutorLane.Execution);
        binding.Lane = BattleExecutorLane.RecoveryDelivery;
        binding.RestoreBoundary = selection.TargetBoundary;
        binding.BattleId = selection.Original.BattleId;
        binding.Attempt = selection.Original.Attempt;
        binding.Fence = checked(selection.Original.Fence + 1);
        BattleCleanupTerminalBindingV1? expected = null;
        var kind = BattleCleanupProofKind.SameProcessEntry;
        if (selection.ReplacementAssignmentId is { } replacement)
        {
            binding.ReplaceRestoredExecutor = true;
            binding.AssignmentTransitionId = replacement;
            binding.StartDigest = ByteString.CopyFrom(Convert.FromHexString(selection.ReplacementStartDigest!));
            kind = BattleCleanupProofKind.ProvedReplacement;
        }
        else
        {
            var archive = new BattleExecutorCheckpointStore(settings with { RestoreBoundary = selection.Original.RestoreBoundary });
            var completed = archive.ReadCompleted(selection.Original);
            var assignment = completed.Assignment;
            if (assignment.RuntimeInstanceId != runtimeId) throw Invalid();
            binding.Entry = null;
            binding.AssignmentTransitionId = assignment.TransitionId;
            if (completed.Start is {} start) binding.StartDigest = start.ManifestDigest;
            else if (completed.AbortTerminal is { } abort) binding.StartDigest = abort.AbortedStartDigest;
            binding.RetainedContext = new() { Version = 1, Phase = BattleExecutorRetainedPhase.Entry,
                StartNonce = assignment.StartNonce, AssignmentRejected = true };
            expected = completed.Cleanup.Terminal;
        }

        var scope = selection.Original.Clone();
        scope.RestoreBoundary = binding.RestoreBoundary;
        scope.Fence = binding.Fence;
        var target = settings with { RestoreBoundary = selection.TargetBoundary };
        while (true)
        {
            using var courier = new BattleExecutorConnection(target, target.ReadCredential());
            bool registered = false;
            try
            {
                await courier.Register(binding, default);
                registered = true;
                var challenge = await courier.Api.CleanupChallengeAsync(new() { Executor = courier.Lease, Scope = scope }, courier.Options(default));
                var terminal = challenge.Terminal;
                BattleCleanupContracts.ValidateTerminal(terminal);
                if (!scope.Equals(challenge.Scope) || terminal.AssignmentTransitionId != binding.AssignmentTransitionId
                    || expected is not null && !expected.Equals(terminal)
                    || challenge.RuntimeInstanceId != runtimeId || !challenge.InventorySha256.Equals(binding.InventorySha256)) throw Invalid();
                var proof = await MainThread.Get(() =>
                {
                    RequireEntry();
                    if (state is not null || BattleGameContext.Active) throw Invalid();
                    return new BattleCleanupProofV1 { Version = 1, Scope = scope, ChallengeId = challenge.ChallengeId, Nonce = challenge.Nonce,
                        CommittedFinalDigest = terminal.CommittedFinalDigest, RuntimeInstanceId = runtimeId, InventorySha256 = binding.InventorySha256,
                        AtEntry = true, Terminal = terminal, AssignmentRejected = true, ProofKind = kind };
                });
                var receipt = challenge.CommittedReceipt;
                if (receipt is null)
                {
                    _ = BattleCleanupContracts.ReadProof(proof.ToByteArray());
                    receipt = await courier.Api.CleanupAsync(new() { Executor = courier.Lease, ClientRequestId = Guid.NewGuid().ToString("D"),
                        Proof = proof }, courier.Options(default));
                }
                BattleCleanupContracts.ValidateReceipt(receipt);
                if (!scope.Equals(receipt.Scope) || !terminal.Equals(receipt.Terminal) || receipt.RuntimeInstanceId != runtimeId || receipt.ProofKind != kind) throw Invalid();
                Status = "RestoreCleaned";
                return;
            }
            catch (RpcException error) when (Retryable(error) || error.StatusCode == StatusCode.Cancelled
                || registered && error.StatusCode == StatusCode.FailedPrecondition) { await Task.Delay(1500); }
        }
    }
}
