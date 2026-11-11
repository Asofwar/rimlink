using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed partial class BattleExecutorHelloService(BattleExecutorRegistry executors,BattleExecutorHandshakes handshakes,BattleTransportAdmission admission,BattleProfileArchive profiles,BattleArtifactStore artifacts,OutpostClock clock,
    GuildOwnedProcessWitness? local = null)
{
    internal async Task<BattleExecutorHelloChallenge> BeginAsync(BattleExecutorHelloBindingV1 request,string credentialGeneration,CancellationToken ct)
    {
        var binding=BattleExecutorHelloContracts.Read(request.ToByteArray());RequireReplacementSelection(binding);
        await using var held=await admission.HoldAsync(ct);
        RequireCut(held,binding);await ValidateHeldAsync(held,binding,ct);
        return handshakes.BeginHeld(binding,credentialGeneration,local?.Configured == true ? local : null);
    }
    internal async Task<BattleExecutorLeaseMessage> CompleteAsync(BattleExecutorHelloComplete request,string credentialGeneration,CancellationToken ct)
    {
        if(request is null || request.CalculateSize()>4096 || request.Binding is null)throw Refused();request=request.Clone();
        var binding=BattleExecutorHelloContracts.Read(request.Binding.ToByteArray());
        RequireReplacementSelection(binding);
        var process = await handshakes.OwnedProcessAsync(request, credentialGeneration, ct);
        if (binding.ReplaceRestoredExecutor && process is null) throw Refused();
        await using var held=await admission.HoldAsync(ct);RequireCut(held,binding);
        if(handshakes.ReplayHeld(request,credentialGeneration) is {} replay)return replay;
        bool freshEntry=await ValidateHeldAsync(held,binding,ct);
        var cleaned=freshEntry?await GuildAuthorityEvidence.CleanedRuntimeHistoryAsync(held.Db,binding.ExecutorId,ct):null;
        return handshakes.CompleteHeld(request,credentialGeneration,()=>
        {
            if(cleaned is not null)executors.ReadmitCleanedEntry(binding,cleaned);
            return executors.Wire(binding.Lane==BattleExecutorLane.Execution?executors.RegisterExecutionAuthenticated(binding,request.ServerBootId,process):executors.RegisterRecoveryAuthenticated(binding,request.ServerBootId,process));
        });
    }
    private async Task<bool> ValidateHeldAsync(BattleTransportAdmission.Lease held,BattleExecutorHelloBindingV1 binding,CancellationToken ct)
    {
        if(binding.BattleId=="")
        {
            var active=await held.Db.Set<GuildBattle>().AsNoTracking().Where(b=>b.Active).Take(2).ToArrayAsync(ct);
            if(active.Length>1)throw Refused();
            if(active.Length==1)await RequireUnreceivedAssignmentAsync(held,active[0],binding,ct);
            var approval=await GuildAuthorityEvidence.ProfileAdmissionAsync(held.Db,binding.WorldInstanceId,binding.ExecutorId,binding.ProfileDigest.ToByteArray(),ct);
            RequireMeasurement(binding,GuildAuthorityEvidence.ReadProfileClosure(profiles,approval.Request).Profile);return active.Length==0;
        }
        var battle=await held.Db.Set<GuildBattle>().AsNoTracking().SingleOrDefaultAsync(b=>b.Id==binding.BattleId,ct)??throw Refused();
        var preparation=await GuildAuthorityEvidence.PreparationAsync(held.Db,battle.Id,ct);var assignment=preparation.RuntimeAssignment;
        bool restored = battle.Boundary != binding.RestoreBoundary;
        long fence = battle.Fence;
        if (binding.ReplaceRestoredExecutor)
        {
            if (!restored || assignment.OwnedProcess is not { Valid: true } || binding.RuntimeInstanceId == assignment.RuntimeInstanceId
                || binding.AssignmentTransitionId != assignment.TransitionId || binding.ExecutorId != battle.ExecutorId
                || battle.WorldId != binding.WorldInstanceId || battle.Attempt != binding.Attempt
                || !binding.StartDigest.Span.SequenceEqual(battle.StartDigest ?? [])) throw Refused();
            var scope = await GuildAuthorityEvidence.CleanupScopeAsync(held.Db, battle, binding.RestoreBoundary, ct);
            if (scope.Fence != binding.Fence) throw Refused();
            var approval = await GuildAuthorityEvidence.ProfileAdmissionAsync(held.Db,binding.WorldInstanceId,binding.ExecutorId,binding.ProfileDigest.ToByteArray(),ct);
            RequireMeasurement(binding,GuildAuthorityEvidence.ReadProfileClosure(profiles,approval.Request).Profile);
            return false;
        }
        if (restored)
        {
            if (binding.Lane != BattleExecutorLane.RecoveryDelivery || binding.RuntimeInstanceId != assignment.RuntimeInstanceId
                || binding.RetainedContext is not { Phase: BattleExecutorRetainedPhase.Entry, GameLoaded: false, TickRuntimeBound: false,
                    PendingCallbacks: 0, AssignmentRejected: true, CurrentTick: 0, TransportPosition: 0, ReceiptJournalBytes: 0 } entry
                || entry.PendingCutIds.Count != 0 || !entry.StartNonce.Span.SequenceEqual(Convert.FromHexString(assignment.StartNonce))) throw Refused();
            fence = (await GuildAuthorityEvidence.CleanupScopeAsync(held.Db, battle, binding.RestoreBoundary, ct)).Fence;
        }
        if(battle.WorldId!=binding.WorldInstanceId || battle.Attempt!=binding.Attempt || fence!=binding.Fence
            || battle.ExecutorId!=binding.ExecutorId || assignment.TransitionId!=binding.AssignmentTransitionId || assignment.ProfileDigest!=Convert.ToHexStringLower(binding.ProfileDigest.Span)
            || assignment.InventorySha256!=Convert.ToHexStringLower(binding.InventorySha256.Span) || assignment.ExecutorBuildSha256!=Convert.ToHexStringLower(binding.ExecutorBuildSha256.Span)
            || battle.StartDigest is null && !binding.StartDigest.IsEmpty || battle.StartDigest is not null && !binding.StartDigest.Span.SequenceEqual(battle.StartDigest))throw Refused();
        var approvalRecord=await GuildAuthorityEvidence.ProfileApprovalAsync(held.Db,assignment.ApprovalTransitionId,ct);
        RequireMeasurement(binding,GuildAuthorityEvidence.ReadProfileClosure(profiles,approvalRecord.Request).Profile);
        if(binding.Lane==BattleExecutorLane.RecoveryDelivery)
        {
            if(battle.StartDigest is null && battle.Phase is not("CancelledAwaitingCleanup" or "Cleaned"))throw Refused();
            if(battle.Phase=="Cleaned")await GuildAuthorityEvidence.CleanupTargetAsync(held.Db,battle,ct);return false;
        }
        if(!battle.Active || binding.RuntimeInstanceId!=assignment.RuntimeInstanceId || binding.RetainedContext is not{} context
            || !context.StartNonce.Span.SequenceEqual(Convert.FromHexString(assignment.StartNonce)))throw Refused();
        bool terminal=battle.Phase is "AppliedAwaitingCleanup" or "CancelledAwaitingCleanup";
        bool startedAbort = battle.Phase == "CancelledAwaitingCleanup" && battle.StartDigest is not null;
        if(!terminal)await GuildAuthorityEvidence.RequireApprovedProfileAsync(held.Db,profiles,assignment.ApprovalTransitionId,binding.ExecutorId,binding.ProfileDigest.ToByteArray(),ct);
        if(context.Phase==BattleExecutorRetainedPhase.Entry)
        {
            if(!terminal || context.GameLoaded || context.TickRuntimeBound || context.PendingCallbacks!=0 || !context.AssignmentRejected)throw Refused();return false;
        }
        if(battle.StartDigest is null)
        {
            if(battle.Phase is not("Prepared" or "CancelledAwaitingCleanup") || context.TickRuntimeBound
                || context.Phase==BattleExecutorRetainedPhase.PreparePending && context.GameLoaded
                || context.Phase==BattleExecutorRetainedPhase.InitialLoaded && !context.GameLoaded
                || context.Phase is not(BattleExecutorRetainedPhase.PreparePending or BattleExecutorRetainedPhase.InitialLoaded))throw Refused();
        }
        else
        {
            var start=BattleManifestContracts.ReadPrepared(battle.StartManifest??throw Refused());
            if(context.Phase==BattleExecutorRetainedPhase.StartAcknowledgementPending)
            {
                if(!startedAbort && (battle.Phase!="StartCommitted" || terminal || battle.StartedAtUtc is null
                    || clock.Sample().Utc>=battle.StartedAtUtc.Value.AddMinutes(20)) || context.CurrentTick!=start.InitialTick || !context.InitialSnapshot.Equals(start.InitialSnapshot)
                    || (await GuildAuthorityEvidence.StartRequestAsync(held.Db,battle.Id,ct)).ClientRequestId!=context.StartRequestId)throw Refused();
                if (startedAbort) return false;
                var journal=BattleCommandJournal.OpenHeld(artifacts.CommandDirectoryHeld(battle.Id,battle.Fence),new(battle.Id,battle.WorldId,battle.Boundary,battle.ExecutorId,battle.Fence,battle.StartDigest));
                if(!journal.CanCompleteLostStart)throw Refused();return false;
            }
            if(!startedAbort && battle.Phase is not("StartCommitted" or "AwaitingResult" or "AppliedAwaitingCleanup") || !context.GameLoaded || !context.TickRuntimeBound
                || context.Phase is not(BattleExecutorRetainedPhase.Running or BattleExecutorRetainedPhase.Terminal)
                || terminal && !startedAbort && context.Phase!=BattleExecutorRetainedPhase.Terminal || context.CurrentTick<start.InitialTick || context.CurrentTick>start.InitialTick+30000
                || context.ReceiptJournalSha256.Length!=32)throw Refused();
        }
        return false;
    }

    private void RequireReplacementSelection(BattleExecutorHelloBindingV1 binding)
    {
        if (binding.ReplaceRestoredExecutor && local?.ReplacementSelected(binding.BattleId, binding.RestoreBoundary) != true) throw Refused();
    }

    private async Task RequireUnreceivedAssignmentAsync(BattleTransportAdmission.Lease held,GuildBattle battle,
        BattleExecutorHelloBindingV1 binding,CancellationToken ct)
    {
        if(battle.Phase is not("Prepared" or "CancelledAwaitingCleanup") || battle.StartDigest is not null
            || battle.FinalDigest is not null || battle.StartedAtUtc is not null || battle.ExecutorId!=binding.ExecutorId
            || battle.WorldId!=binding.WorldInstanceId || battle.Boundary!=binding.RestoreBoundary)throw Refused();
        var assignment=(await GuildAuthorityEvidence.PreparationAsync(held.Db,battle.Id,ct)).RuntimeAssignment.Message();
        if(assignment.RuntimeInstanceId!=binding.RuntimeInstanceId || !assignment.ProfileDigest.Equals(binding.ProfileDigest)
            || !assignment.InventorySha256.Equals(binding.InventorySha256) || !assignment.ExecutorBuildSha256.Equals(binding.ExecutorBuildSha256)
            || artifacts.DescriptorHeld(battle.Id,battle.Fence,"initial") is not null
            || new BattlePreparedJournal(artifacts).LatestHeld(battle.Id,battle.Fence) is not null)throw Refused();
    }
    private static void RequireMeasurement(BattleExecutorHelloBindingV1 binding,BattleProfileV1 profile)
    {if(!binding.InventorySha256.Equals(profile.Inventory.Sha256) || !binding.ExecutorBuildSha256.Equals(profile.ExecutorBuildSha256))throw Refused();}
    private static void RequireCut(BattleTransportAdmission.Lease held,BattleExecutorHelloBindingV1 binding)
    {
        if(binding.WorldInstanceId!=held.Cut.WorldId || binding.RestoreBoundary!=held.Cut.Boundary)throw Refused();
        GuildRuntimeContracts.Require(binding.GuildRuntime);
        if(held.Cut.Acceptance is{} identity ? binding.GuildRuntime?.StandId!=identity.StandId : binding.GuildRuntime is not null)throw Refused();
    }
    private static OutpostEvidenceUnavailableException Refused()=>new("battle-executor-hello-admission-refused");
}
