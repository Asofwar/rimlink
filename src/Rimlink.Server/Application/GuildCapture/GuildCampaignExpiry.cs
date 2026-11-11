using System.Text.Json.Serialization;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildCampaignExpiryRequest(GuildMutationScope Scope,string CampaignId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GuildOperatorCancellation? Operator = null);
internal sealed record GuildOperatorCancellation(string ExpectedHeadId, string Reason, string ActorName)
{
    internal const string Kind = "GuildCampaignCancelled";
    internal const string Outcome = "OperatorCancelled";
    internal bool IsValid() => Guid.TryParseExact(ExpectedHeadId, "D", out _)
        && !string.IsNullOrWhiteSpace(Reason) && Reason.Length <= 500 && !Reason.Any(char.IsControl)
        && !string.IsNullOrWhiteSpace(ActorName) && ActorName.Length <= 512 && !ActorName.Any(char.IsControl);
}
internal sealed record GuildExpiryHealth(string RuntimeInstanceId,string LeaseId,long LeaseFence,long AgeTicks);
internal sealed record GuildCampaignExpiryReceipt(int Version,GuildCampaignExpiryRequest Request,GuildCampaignTerminalReceipt Terminal,string Decision,
    string? PreparationTransitionId,int ProposalSequence,BattleArtifactDescriptor? Offer,BattleArtifactDescriptor? Acknowledgement,GuildExpiryHealth? Health,
    bool ProductionRebased,int Stock,long Remainder,string TransitionId);
internal sealed record GuildExpiryDecision(string Reason,string Detail,int Proposal=0,BattleArtifactDescriptor? Offer=null,BattleArtifactDescriptor? Ack=null,GuildExpiryHealth? Health=null);

internal sealed partial class GuildOutpostService
{
    internal Task<GuildCampaignExpiryReceipt> ExpireCampaignAsync(GuildMutationScope scope,string requestId,string campaignId,CancellationToken ct) =>
        ClosePreStartCampaignAsync(new(scope,campaignId),requestId,Actor.Scheduled("system:guild-expiry"),ct);

    internal Task<GuildCampaignExpiryReceipt> CancelCampaignAsync(Actor actor, GuildMutationScope scope, string requestId,
        string campaignId, string expectedHeadId, string reason, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        var cancellation = new GuildOperatorCancellation(expectedHeadId, reason, actor.Name);
        if (!cancellation.IsValid()) throw Refused();
        return ClosePreStartCampaignAsync(new(scope, campaignId, cancellation), requestId, actor, ct);
    }

    private async Task<GuildCampaignExpiryReceipt> ClosePreStartCampaignAsync(GuildCampaignExpiryRequest intent, string requestId,
        Actor actor, CancellationToken ct)
    {
        var (scope, campaignId, cancellation) = intent;
        if(scope is null || !Guid.TryParseExact(campaignId,"D",out _))throw Refused();
        var observed=await db.Set<GuildCampaign>().AsNoTracking().SingleOrDefaultAsync(c=>c.Id==campaignId,ct)??throw Refused();
        var leader=await db.Set<AllianceGroup>().AsNoTracking().SingleAsync(a=>a.Id==observed.AttackerAllianceId,ct);
        string kind = cancellation is null ? "GuildCampaignExpired" : GuildOperatorCancellation.Kind;
        return await MutateAsync(scope,actor.PlayerId,actor.Name,requestId,kind,intent,async(lease,id,token)=>
        {
            if (cancellation is null) await RequireCaptureOpenHeldAsync(scope.WorldId, token);
            if (cancellation is not null
                && !await db.OutpostAuthority.AsNoTracking().AnyAsync(a => a.HeadId == cancellation.ExpectedHeadId, token))
                throw new OutpostEvidenceUnavailableException("guild-campaign-preview-stale");
            var campaign=await db.Set<GuildCampaign>().SingleAsync(c=>c.Id==campaignId,token);var sample=clock.Sample();
            if(!campaign.Active || campaign.Phase is not("Scheduled" or "PreparingBattle") || campaign.WorldId!=scope.WorldId || campaign.Boundary!=scope.Boundary
                || cancellation is null && sample.Utc<campaign.ScheduledAtUtc.AddMinutes(5))throw Refused();
            var currentLeader=await db.Set<AllianceGroup>().AsNoTracking().SingleAsync(a=>a.Id==campaign.AttackerAllianceId,token);
            if(currentLeader.OwnerId!=leader.OwnerId)throw Refused();
            var battle=await db.Set<GuildBattle>().SingleOrDefaultAsync(b=>b.CampaignId==campaign.Id && b.Active,token);
            if(battle is not null && (battle.Phase!="Prepared" || battle.StartDigest is not null || battle.FinalDigest is not null))throw Refused();
            var scheduled=await GuildAuthorityEvidence.CampaignScheduleAsync(db,campaign.Id,token);
            var row=await db.Set<GuildOutpostRow>().SingleAsync(o=>o.Id==campaign.OutpostId,token);
            if(row.AllianceId!=campaign.DefenderAllianceId || row.Revision!=campaign.OwnerRevision || row.WorldId!=scope.WorldId)throw Refused();
            GuildBattlePreparationReceipt? preparation=battle is null?null:await GuildAuthorityEvidence.PreparationAsync(db,battle.Id,token);
            var decision = cancellation is not null ? new GuildExpiryDecision(GuildOperatorCancellation.Outcome, "AdminRequest")
                : battle is null ? new GuildExpiryDecision("TechnicalBeforeStart","NoPreparation")
                : await DecideExpiryAsync(lease,campaign,battle,currentLeader.OwnerId,token);
            var reservation=await db.Set<GuildAcquisitionReservation>().SingleAsync(r=>r.Id==scheduled.ReservationId,token);
            var slot=await db.Set<GuildBattleSlot>().SingleAsync(s=>s.Id==scheduled.SlotId,token);
            if(!reservation.Active || !slot.Active || reservation.CampaignId!=campaign.Id || slot.CampaignId!=campaign.Id)throw Refused();
            reservation.Active=false;slot.Active=false;campaign.Active=false;campaign.Phase=decision.Reason;campaign.TerminalReason=decision.Reason;
            foreach(var grant in await db.Set<GuildBattleControlGrant>().Where(g=>g.BattleId==(battle==null?"":battle.Id) && g.Active).ToArrayAsync(token))grant.Active=false;
            if(battle is not null)
            {
                if(row.Phase!="PreparingBattle" || preparation is null || row.Stock!=preparation.StockAtCut || row.Remainder!=preparation.RemainderAtCut)throw Refused();
                battle.Phase="CancelledAwaitingCleanup";row.Phase="Active";
                var next=OutpostClock.Advance(new(row.CheckpointUtc,row.ClockProcessId,row.ClockTimestamp,row.ClockFrequency),sample);
                Checkpoint(row,new(next.Utc,next.ProcessId,next.Timestamp,next.Frequency));
                try{executors.HoldCleanup(executors.RequireAssignedExecution(preparation.RuntimeAssignment));}catch(InvalidDataException){}
            }
            else if(row.Phase!="Active" || preparation is not null)throw Refused();
            var terminal=new GuildCampaignTerminalReceipt(1,scope,campaign.Id,campaign.OutpostId,campaign.OwnerRevision,campaign.AttackerAllianceId,campaign.DefenderAllianceId,
                decision.Reason,sample.Utc,battle?.Id,null,scheduled.ReservationId,scheduled.SlotId,decision.Reason=="NoAttackerReady"?sample.Utc.AddHours(24):null);
            return new GuildCampaignExpiryReceipt(cancellation is null ? 1 : 2,intent,terminal,decision.Detail,preparation?.TransitionId,decision.Proposal,decision.Offer,decision.Ack,decision.Health,
                battle is not null,row.Stock,row.Remainder,id);
        },ct,[leader.OwnerId,observed.AttackerCommanderId],battleLock:true);
    }
    private async Task<GuildExpiryDecision> DecideExpiryAsync(OutpostLease lease,GuildCampaign campaign,GuildBattle battle,long leader,CancellationToken ct)
    {
        GuildExpiryDecision Technical(string detail)=>new("TechnicalBeforeStart",detail);
        try{authority.RequireRuntimeGuild(GuildRuntimeUse.Simulation);}catch(OutpostEvidenceUnavailableException){return Technical("SimulationAdmissionUnavailable");}
        var prepared=await GuildAuthorityEvidence.PreparationAsync(db,battle.Id,ct);BattleExecutorLease executor;GuildExpiryHealth health;
        try
        {
            executor=executors.RequireAssignedExecution(prepared.RuntimeAssignment);
            health=new(prepared.RuntimeAssignment.RuntimeInstanceId,executor.LeaseId,executor.Fence,executors.HealthAgeTicks(executor));
        }
        catch(InvalidDataException){return Technical("RuntimeUnavailable");}
        try{await GuildAuthorityEvidence.ProfileAdmissionAsync(db,battle.WorldId,battle.ExecutorId,executor.ProfileDigest.ToByteArray(),ct);}
        catch(OutpostEvidenceUnavailableException){return Technical("ProfileUnavailable");}
        try
        {
            await policy.RequireLeaderHeldAsync(lease,leader,campaign.AttackerAllianceId,clock.Sample().Utc,ct);
            await policy.RequireCommanderHeldAsync(lease,campaign.AttackerCommanderId,campaign.AttackerAllianceId,clock.Sample().Utc,BattleFighterSide.Attacker,ct);
        }
        catch(Exception error)when(error is RpcException or OutpostEvidenceUnavailableException){return Technical("ParticipationUnavailable");}
        try
        {
            var proposal=new BattlePreparedJournal(artifacts).LatestHeld(battle.Id,battle.Fence);if(proposal is null)return Technical("NoOffer");
            var manifest=BattleManifestContracts.ReadPrepared(proposal.Manifest);
            var ready=await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o=>o.Kind=="GuildAttackerReady").Select(o=>o.Receipt).ToArrayAsync(ct);
            if(ready.Select(GuildAuthorityEvidence.ReadReadyReceipt).Any(r=>r.Manifest.BattleId==battle.Id && r.ManifestDigest==Convert.ToHexStringLower(proposal.ManifestDigest)))return Technical("ReadyNotStarted");
            await BattlePreparedAdmission.ValidateHeldAsync(db,manifest,battle,campaign,executor,profiles,artifacts,sessions,ct,requireDefenderSession:false);
            for(int i=1;i<=64;i++)
            {
                var offer=artifacts.DescriptorHeld(battle.Id,battle.Fence,"offer-"+i);if(offer is null)break;
                var evidence=BattleReadyOfferJournal.ReadOffer(artifacts,offer);
                if(evidence.ProposalSequence!=proposal.Sequence || !evidence.ManifestDigest.Span.SequenceEqual(proposal.ManifestDigest))continue;
                var ack=artifacts.DescriptorHeld(battle.Id,battle.Fence,"offer-ack-"+i);if(ack is null)continue;
                var delivered=BattleReadyOfferJournal.ReadAck(artifacts,ack,offer);
                if(evidence.PlayerId!=campaign.AttackerCommanderId || !evidence.SessionDigest.Equals(manifest.Attacker.SessionDigest)
                    || evidence.MembershipJoinedUtcTicks!=manifest.Attacker.MembershipJoinedUtcTicks || evidence.DeadlineUtcTicks!=manifest.ReadinessDeadlineUtcTicks
                    || delivered.ReceivedAtUtcTicks>=campaign.ScheduledAtUtc.AddMinutes(5).Ticks)continue;
                return new("NoAttackerReady","PlayerDidNotReady",proposal.Sequence,offer,ack,health);
            }
            return Technical("NoDeliveryAck");
        }
        catch(Exception error)when(error is OutpostEvidenceUnavailableException or InvalidDataException or IOException){return Technical("OfferEvidenceUnavailable");}
    }
}

internal sealed partial class BattleExecutorRegistry
{
    internal long HealthAgeTicks(BattleExecutorLease lease)
    {lock(gate){RequireOccupant(lease);return time.GetElapsedTime(observed,time.GetTimestamp()).Ticks;}}
}
