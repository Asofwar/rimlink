using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildCleanupIntent(GuildMutationScope Scope,string RequestId,byte[] Proof);
internal sealed record GuildReplacementEvidence(GuildOwnedProcess Original,GuildOwnedProcess Replacement,string ApprovalTransitionId,long ObservationAgeTicks);
internal sealed record GuildExecutorCleanupReceipt(int Version,GuildCleanupIntent Request,string ExecutorId,string LeaseId,long LeaseFence,string ProfileDigest,
    byte[] Challenge,long ChallengeAgeTicks,byte[] Receipt,string TransitionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GuildReplacementEvidence? Replacement = null);

internal sealed partial class GuildOutpostService
{
    internal async Task<BattleCleanupChallengeMessage> CleanupChallengeAsync(BattleExecutorLease executor,BattleScopeMessage scope,CancellationToken ct)
    {
        if (scope is null || scope.CalculateSize() > 1024) throw Refused(); scope = scope.Clone();
        authority.RequireRuntimeGuild();
        await using var lease = await authority.AcquireAsync([],ct);
        using var heldBattle = await battles.HoldAsync(ct);
        await RequireScopeAsync(lease,new(scope.WorldInstanceId,scope.RestoreBoundary),ct); executors.RequireCurrent(executor);
        var battle = await db.Set<GuildBattle>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == scope.BattleId,ct) ?? throw Refused();
        if(battle.ExecutorId!=executor.ExecutorId || battle.Phase is not ("AppliedAwaitingCleanup" or "CancelledAwaitingCleanup" or "Cleaned")
            || !(await GuildAuthorityEvidence.CleanupScopeAsync(db,battle,scope.RestoreBoundary,ct)).Equals(scope))throw Refused();
        var target=await GuildAuthorityEvidence.CleanupTargetAsync(db,battle,ct);
        var binding = RequireCleanupSender(executor,target,scope);
        if (!battle.Active)
        {
            if (battle.Phase != "Cleaned" || battle.Boundary == scope.RestoreBoundary) throw Refused();
            var committed = await GuildAuthorityEvidence.ReadRestoredCleanupAsync(db, scope, ct);
            if (committed.RuntimeInstanceId != binding.RuntimeInstanceId) throw Refused();
            return new() { Scope = scope, Terminal = target.Terminal, RuntimeInstanceId = binding.RuntimeInstanceId,
                InventorySha256 = binding.InventorySha256, CommittedFinalDigest = target.Terminal.CommittedFinalDigest,
                CommittedReceipt = committed };
        }
        return cleanupChallenges.Issue(executor,scope,target.Terminal,binding.RuntimeInstanceId,binding.InventorySha256);
    }
    internal async Task<BattleCleanupReceipt> CleanupBattleAsync(BattleExecutorLease executor,string requestId,BattleCleanupProofV1 proof,CancellationToken ct)
    {
        proof = GuildAuthorityEvidence.ReadCleanupProof(proof.ToByteArray());
        var scope = new GuildMutationScope(proof.Scope.WorldInstanceId,proof.Scope.RestoreBoundary);
        var request = new GuildCleanupIntent(scope,requestId,proof.ToByteArray());
        GuildOwnedProcess? original = null, replacement = null;
        long observed = 0;
        if (proof.ProofKind == BattleCleanupProofKind.ProvedReplacement)
        {
            var prior = await GuildAuthorityEvidence.PreparationAsync(db, proof.Scope.BattleId, ct);
            original = prior.RuntimeAssignment.OwnedProcess ?? throw Refused();
            replacement = executors.ReplacementProcess(executor);
            if (original == replacement || original.Host != replacement.Host
                || processWitness?.ReplacementSelected(proof.Scope.BattleId, scope.Boundary) != true
                || !processWitness.Exited(original) || !processWitness.Alive(replacement)) throw Refused();
            observed = Stopwatch.GetTimestamp();
        }
        var receipt = await MutateAsync(scope,0,"executor:" + executor.ExecutorId,requestId,"GuildExecutorCleanup",request,async (lease,id,token) =>
        {
            var challenge = cleanupChallenges.Require(executor,proof);
            var battle = await db.Set<GuildBattle>().SingleOrDefaultAsync(b => b.Id == proof.Scope.BattleId,token) ?? throw Refused();
            if(!battle.Active || battle.Phase is not("AppliedAwaitingCleanup" or "CancelledAwaitingCleanup") || battle.ExecutorId!=executor.ExecutorId
                || !(await GuildAuthorityEvidence.CleanupScopeAsync(db,battle,scope.Boundary,token)).Equals(proof.Scope))throw Refused();
            var target=await GuildAuthorityEvidence.CleanupTargetAsync(db,battle,token);
            var binding = RequireCleanupSender(executor,target,proof.Scope);
            if (proof.RuntimeInstanceId != binding.RuntimeInstanceId || !proof.InventorySha256.Equals(binding.InventorySha256)
                || !proof.Terminal.Equals(target.Terminal)) throw Refused();
            GuildReplacementEvidence? replacementEvidence = null;
            if (replacement is not null && original is not null)
            {
                if (original != target.Assignment.OwnedProcess || replacement != executors.ReplacementProcess(executor)) throw Refused();
                var approval = await GuildAuthorityEvidence.ProfileAdmissionAsync(db, scope.WorldId, executor.ExecutorId, executor.ProfileDigest.ToByteArray(), token);
                var age = Stopwatch.GetElapsedTime(observed);
                if (age < TimeSpan.Zero || age >= TimeSpan.FromSeconds(10)) throw Refused();
                replacementEvidence = new(original,replacement,approval.TransitionId,age.Ticks);
            }
            battle.Active = false; battle.Phase = "Cleaned";
            var result = new BattleCleanupReceipt { Version = 1,Scope = proof.Scope.Clone(),TransitionId = id,CommittedFinalDigest = proof.CommittedFinalDigest,
                RuntimeInstanceId = proof.RuntimeInstanceId,CleanedAtUtcTicks = clock.Sample().Utc.Ticks,Terminal=proof.Terminal.Clone(),ProofKind=proof.ProofKind };
            return new GuildExecutorCleanupReceipt(1,request,executor.ExecutorId,executor.LeaseId,executor.Fence,Convert.ToHexStringLower(executor.ProfileDigest.Span),
                challenge.Challenge.ToByteArray(),challenge.AgeTicks,result.ToByteArray(),id,replacementEvidence);
        },ct,validateScope: _ =>
        {
            executors.RequireCurrent(executor);var binding=executors.BindingOf(executor)??throw Refused();
            if(binding.RuntimeInstanceId!=proof.RuntimeInstanceId || binding.WorldInstanceId!=scope.WorldId || binding.RestoreBoundary!=scope.Boundary || !binding.InventorySha256.Equals(proof.InventorySha256)
                || binding.ReplaceRestoredExecutor != (proof.ProofKind == BattleCleanupProofKind.ProvedReplacement)
                || binding.Lane==BattleExecutorLane.RecoveryDelivery && (binding.BattleId!=proof.Scope.BattleId || binding.Attempt!=proof.Scope.Attempt || binding.Fence!=proof.Scope.Fence
                    || binding.AssignmentTransitionId!=proof.Terminal.AssignmentTransitionId))throw Refused();
            return Task.CompletedTask;
        },battleLock:true,afterCommit: committed =>
        {
            // Release the projection under S/B only after the external commit; rotated transport can only read the historical receipt.
            if(committed.LeaseId==executor.LeaseId && committed.LeaseFence==executor.Fence
                && !db.Set<GuildBattle>().AsNoTracking().Any(b=>b.Active && b.ExecutorId==executor.ExecutorId))executors.ReleaseCommittedCleanup(executor,proof.RuntimeInstanceId);
        });
        return BattleCleanupReceipt.Parser.ParseFrom(receipt.Receipt);
    }

    private BattleExecutorHelloBindingV1 RequireCleanupSender(BattleExecutorLease executor, GuildCleanupTarget target, BattleScopeMessage scope)
    {
        var binding = executors.BindingOf(executor) ?? throw Refused();
        if (!binding.ReplaceRestoredExecutor) executors.RequireOriginalCleanupSender(executor,target,scope);
        else if (target.Assignment.OwnedProcess is not { Valid: true } || binding.RuntimeInstanceId == target.Assignment.RuntimeInstanceId
            || scope.RestoreBoundary == target.Assignment.Scope.Boundary || binding.RestoreBoundary != scope.RestoreBoundary
            || binding.WorldInstanceId != scope.WorldInstanceId || binding.BattleId != scope.BattleId || binding.Attempt != scope.Attempt
            || binding.Fence != scope.Fence || binding.AssignmentTransitionId != target.Assignment.TransitionId
            || !binding.StartDigest.Equals(target.StartDigest)) throw Refused();
        return binding;
    }
}

internal static partial class GuildAuthorityEvidence
{
    internal static async Task<BattleCleanupReceipt> ReadRestoredCleanupAsync(RimlinkDbContext db, BattleScopeMessage scope, CancellationToken ct)
    {
        var operations = await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o => o.Kind == "GuildExecutorCleanup")
            .Select(o => o.Receipt).ToArrayAsync(ct);
        return operations.Select(bytes => JsonSerializer.Deserialize<GuildExecutorCleanupReceipt>(bytes, Json) ?? throw InvalidCleanup())
            .Select(stored => BattleCleanupReceipt.Parser.ParseFrom(stored.Receipt)).SingleOrDefault(receipt => receipt.Scope.Equals(scope))
            ?? throw InvalidCleanup();
    }
    internal static BattleCleanupProofV1 ReadCleanupProof(byte[] bytes)=>BattleCleanupContracts.ReadProof(bytes);
    private static async Task ValidateCleanupsAsync(RimlinkDbContext db,GuildCaptureOperation[] operations,IReadOnlyDictionary<string,OutpostTransition> transitions,
        BattleProfileArchive profiles,CancellationToken ct)
    {
        var cleaned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations.Where(o => o.Kind == "GuildExecutorCleanup"))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<GuildExecutorCleanupReceipt>(operation.Receipt,Json) ?? throw InvalidCleanup();
                var proof = ReadCleanupProof(stored.Request.Proof); var receipt = BattleCleanupReceipt.Parser.ParseFrom(stored.Receipt);
                var challenge = BattleCleanupChallengeMessage.Parser.ParseFrom(stored.Challenge);BattleCleanupContracts.ValidateReceipt(receipt);
                if (stored.Version != 1 || operation.ActorId != 0 || stored.Request.RequestId != operation.RequestId || stored.TransitionId != operation.TransitionId
                    || !IntentDigest(stored.Request).AsSpan().SequenceEqual(operation.IntentDigest) || stored.ChallengeAgeTicks is < 0 or >= 60 * TimeSpan.TicksPerSecond
                    || !Guid.TryParseExact(stored.LeaseId,"D",out _) || stored.LeaseFence <= 0 || stored.ProfileDigest.Length != 64 || !cleaned.Add(proof.Scope.BattleId)
                    || !transitions.TryGetValue(stored.TransitionId,out var transition) || stored.Request.Scope.WorldId != transition.WorldId || stored.Request.Scope.Boundary != transition.RestoreBoundary
                    || proof.Scope.WorldInstanceId != transition.WorldId || proof.Scope.RestoreBoundary != transition.RestoreBoundary || transition.Actor != "executor:" + stored.ExecutorId
                    || challenge.ChallengeId != proof.ChallengeId || !challenge.Nonce.Equals(proof.Nonce) || !challenge.Scope.Equals(proof.Scope)
                    || !challenge.CommittedFinalDigest.Equals(proof.CommittedFinalDigest) || challenge.RuntimeInstanceId != proof.RuntimeInstanceId || !challenge.InventorySha256.Equals(proof.InventorySha256)
                    || receipt.Version != 1 || !receipt.Scope.Equals(proof.Scope) || receipt.TransitionId != stored.TransitionId || !receipt.CommittedFinalDigest.Equals(proof.CommittedFinalDigest)
                    || !challenge.Terminal.Equals(proof.Terminal) || !receipt.Terminal.Equals(proof.Terminal) || receipt.ProofKind!=proof.ProofKind
                    || receipt.RuntimeInstanceId != proof.RuntimeInstanceId || receipt.CleanedAtUtcTicks <= 0 || receipt.CleanedAtUtcTicks > DateTime.MaxValue.Ticks) throw InvalidCleanup();
                var battle = await db.Set<GuildBattle>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == proof.Scope.BattleId,ct);
                if(battle is null || battle.Active || battle.Phase!="Cleaned")throw InvalidCleanup();
                if (!(await CleanupScopeAsync(db, battle, proof.Scope.RestoreBoundary, ct)).Equals(proof.Scope)) throw InvalidCleanup();
                if (battle.Boundary != proof.Scope.RestoreBoundary)
                {
                    var root = transitions.Values.Single(t => t.Kind == "BulkRestore" && t.RestoreBoundary == proof.Scope.RestoreBoundary);
                    RequireEarlier(transitions, root.Id, stored.TransitionId);
                }
                var target=await CleanupTargetAsync(db,battle,ct);
                if (!target.Terminal.Equals(proof.Terminal) || target.Assignment.ExecutorId != stored.ExecutorId) throw InvalidCleanup();
                if (proof.ProofKind == BattleCleanupProofKind.ProvedReplacement)
                {
                    if (stored.Replacement is not { Original.Valid: true, Replacement.Valid: true } replacement
                        || replacement.Original != target.Assignment.OwnedProcess || replacement.Original == replacement.Replacement
                        || replacement.Original.Host != replacement.Replacement.Host || replacement.ObservationAgeTicks is < 0 or >= 10 * TimeSpan.TicksPerSecond
                        || proof.RuntimeInstanceId == target.Assignment.RuntimeInstanceId || proof.Scope.RestoreBoundary == battle.Boundary) throw InvalidCleanup();
                    var approval = await ProfileApprovalAsync(db,replacement.ApprovalTransitionId,ct);
                    var profile = ReadProfileClosure(profiles,approval.Request).Profile;
                    if (approval.ProfileDigest != stored.ProfileDigest || approval.ExecutorId != stored.ExecutorId
                        || approval.Request.Scope.WorldId != battle.WorldId || !profile.Inventory.Sha256.Equals(proof.InventorySha256)) throw InvalidCleanup();
                    RequireEarlier(transitions,approval.TransitionId,stored.TransitionId);
                }
                else if (stored.Replacement is not null || target.Assignment.RuntimeInstanceId != proof.RuntimeInstanceId
                    || !target.Inventory.Equals(proof.InventorySha256) || Convert.ToHexStringLower(target.Profile.Span) != stored.ProfileDigest) throw InvalidCleanup();
                RequireEarlier(transitions,target.Terminal.TerminalTransitionId,stored.TransitionId);

            }
            catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or IOException or ArgumentException or NullReferenceException or InvalidProtocolBufferException)
            { throw new OutpostEvidenceUnavailableException("guild-cleanup-evidence-invalid: " + error.GetType().Name); }
        }
        foreach (var battle in await db.Set<GuildBattle>().AsNoTracking().Where(b => b.Phase == "Cleaned").ToArrayAsync(ct))
            if (!cleaned.Contains(battle.Id)) throw InvalidCleanup();
    }
    private static OutpostEvidenceUnavailableException InvalidCleanup() => new("guild-cleanup-evidence-mismatch");
}
