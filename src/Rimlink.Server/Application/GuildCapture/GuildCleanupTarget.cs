using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildCleanupTarget(BattleCleanupTerminalBindingV1 Terminal,GuildRuntimeAssignment Assignment,ByteString Profile,ByteString Inventory,ByteString StartDigest);

internal static partial class GuildAuthorityEvidence
{
    internal static async Task<GuildCleanupTarget> CleanupTargetAsync(RimlinkDbContext db,GuildBattle battle,CancellationToken ct)
    {
        var prepared=await PreparationAsync(db,battle.Id,ct);var assignment=prepared.RuntimeAssignment;
        if(assignment.Scope.WorldId!=battle.WorldId || assignment.Scope.Boundary!=battle.Boundary || assignment.Attempt!=battle.Attempt || assignment.Fence!=battle.Fence)throw InvalidCleanupTarget();
        var binding=new BattleCleanupTerminalBindingV1{Version=1,AssignmentTransitionId=prepared.TransitionId};
        if(battle.FinalDigest is not null)
        {
            if(battle.FinalDigest.Length!=32 || battle.StartManifest is null || battle.StartDigest is null || battle.Phase is not("AppliedAwaitingCleanup" or "Cleaned"))throw InvalidCleanupTarget();
            var operations=await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o=>o.Kind=="GuildBattleApplied").Select(o=>o.Receipt).ToArrayAsync(ct);
            var applied=operations.Select(b=>JsonSerializer.Deserialize<GuildBattleApplyReceipt>(b,Json)??throw InvalidCleanupTarget()).Single(r=>r.Request.BattleId==battle.Id);
            var start=BattleManifestContracts.ReadPrepared(battle.StartManifest);
            if(start.RuntimeInstanceId!=assignment.RuntimeInstanceId || start.PreparationTransitionId!=prepared.TransitionId
                || !BattleAppliedReceipt.Parser.ParseFrom(applied.Receipt).CommittedFinalDigest.Span.SequenceEqual(battle.FinalDigest))throw InvalidCleanupTarget();
            binding.Kind=BattleCleanupTerminalKind.AppliedResult;binding.TerminalTransitionId=applied.TransitionId;binding.CommittedFinalDigest=ByteString.CopyFrom(battle.FinalDigest);
        }
        else if (battle.StartDigest is { Length: 32 } && battle.StartManifest is not null)
        {
            if (battle.Phase is not ("CancelledAwaitingCleanup" or "Cleaned")) throw InvalidCleanupTarget();
            var abort = await StartedAbortAsync(db, battle.Id, ct);
            if (abort.PreparationTransitionId != prepared.TransitionId || abort.Command.Scope != assignment.Scope
                || abort.StartDigest != Convert.ToHexStringLower(battle.StartDigest)) throw InvalidCleanupTarget();
            binding.Kind = BattleCleanupTerminalKind.StartedAbort;
            binding.TerminalTransitionId = abort.TransitionId;
            binding.AbortedStartDigest = ByteString.CopyFrom(battle.StartDigest);
        }
        else
        {
            if(battle.StartManifest is not null || battle.StartDigest is not null || battle.Phase is not("CancelledAwaitingCleanup" or "Cleaned"))throw InvalidCleanupTarget();
            var operations=await db.Set<GuildCaptureOperation>().AsNoTracking()
                .Where(o=>o.Kind=="GuildCampaignExpired" || o.Kind==GuildOperatorCancellation.Kind).Select(o=>o.Receipt).ToArrayAsync(ct);
            var expired=operations.Select(ReadExpiry).Single(r=>r.Terminal.BattleId==battle.Id);
            if(expired.PreparationTransitionId!=prepared.TransitionId || expired.Request.Scope!=assignment.Scope)throw InvalidCleanupTarget();
            binding.Kind=BattleCleanupTerminalKind.PreStartCancellation;binding.TerminalTransitionId=expired.TransitionId;
        }
        BattleCleanupContracts.ValidateTerminal(binding);
        return new(binding,assignment,ByteString.CopyFrom(Convert.FromHexString(assignment.ProfileDigest)),ByteString.CopyFrom(Convert.FromHexString(assignment.InventorySha256)),
            battle.StartDigest is null?ByteString.Empty:ByteString.CopyFrom(battle.StartDigest));
    }
    internal static async Task<BattleScopeMessage> CleanupScopeAsync(RimlinkDbContext db, GuildBattle battle, string boundary, CancellationToken ct)
    {
        if (battle.Boundary == boundary)
            return new() { WorldInstanceId = battle.WorldId, RestoreBoundary = boundary, BattleId = battle.Id, Attempt = battle.Attempt, Fence = battle.Fence };
        var restore = await db.OutpostTransitions.AsNoTracking().SingleOrDefaultAsync(t => t.Kind == "BulkRestore"
            && t.WorldId == battle.WorldId && t.RestoreBoundary == boundary, ct) ?? throw InvalidCleanupTarget();
        var plan = OutpostRestoreEvidence.Plan(restore);
        var map = plan.Guild?.Battles.SingleOrDefault(m => m.After.Id == battle.Id) ?? throw InvalidCleanupTarget();
        if (!map.After.Active || map.After.Phase is not ("AppliedAwaitingCleanup" or "CancelledAwaitingCleanup")
            || !OutpostRestoreEvidence.Same(map.Before, map.After) || map.After.Fence == long.MaxValue) throw InvalidCleanupTarget();
        var expected = OutpostRestoreEvidence.Decode<GuildBattle>(OutpostRestoreEvidence.Encode(map.After));
        if (battle.Phase == "Cleaned")
        {
            expected.Active = false;
            expected.Phase = "Cleaned";
        }
        if (!OutpostRestoreEvidence.Same(expected, battle)) throw InvalidCleanupTarget();
        return new() { WorldInstanceId = battle.WorldId, RestoreBoundary = boundary, BattleId = battle.Id, Attempt = battle.Attempt, Fence = battle.Fence + 1 };
    }

    private static OutpostEvidenceUnavailableException InvalidCleanupTarget()=>new("guild-cleanup-target-invalid");
}

internal sealed partial class BattleExecutorRegistry
{
    internal void RequireOriginalCleanupSender(BattleExecutorLease lease,GuildCleanupTarget target,BattleScopeMessage scope)
    {
        lock(gate)
        {
            Require(lease);var binding=couriers.TryGetValue(lease.LeaseId,out var courier)?courier.Binding:executionBinding;
            if(binding is null || binding.RuntimeInstanceId!=target.Assignment.RuntimeInstanceId || binding.WorldInstanceId!=scope.WorldInstanceId || binding.RestoreBoundary!=scope.RestoreBoundary
                || !binding.ProfileDigest.Equals(target.Profile) || !binding.InventorySha256.Equals(target.Inventory)
                || target.Assignment.OwnedProcess is { } original && original != (courier is null ? ownedProcess : courier.Process))throw Refused();
            if(binding.Lane==BattleExecutorLane.RecoveryDelivery && (binding.BattleId!=scope.BattleId || binding.Attempt!=scope.Attempt || binding.Fence!=scope.Fence
                || binding.AssignmentTransitionId!=target.Assignment.TransitionId || !binding.StartDigest.Equals(target.StartDigest)))throw Refused();
            if (scope.RestoreBoundary != target.Assignment.Scope.Boundary && (binding.Lane != BattleExecutorLane.RecoveryDelivery
                || binding.RetainedContext is not { Phase: BattleExecutorRetainedPhase.Entry, GameLoaded: false, TickRuntimeBound: false,
                    PendingCallbacks: 0, AssignmentRejected: true } entry
                || !entry.StartNonce.Span.SequenceEqual(Convert.FromHexString(target.Assignment.StartNonce)))) throw Refused();
            if(binding.Lane is not(BattleExecutorLane.Execution or BattleExecutorLane.RecoveryDelivery))throw Refused();
            if(binding.Lane==BattleExecutorLane.Execution && busy)throw Refused();
        }
    }
    // Called only under S/B after the common cleanup commit, with no active assignment for this executor.
    internal void ReleaseCommittedCleanup(BattleExecutorLease lease,string originalRuntime)
    {
        lock(gate)
        {
            if(current==lease && executionBinding?.RuntimeInstanceId==originalRuntime){busy=false;cleanupHeld=false;return;}
            if(couriers.TryGetValue(lease.LeaseId,out var courier) && courier.Lease==lease && courier.Binding.RuntimeInstanceId==originalRuntime
                && (executionBinding is null || executionBinding.RuntimeInstanceId==originalRuntime))
            {busy=false;cleanupHeld=false;}
        }
    }
}
