using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed partial class BattlePreparedAdmission
{
    internal async Task<BattlePreparationBasisReply> ReadBasisAsync(BattlePreparationBasisRequest request,CancellationToken ct)
    {
        if(request is null || request.CalculateSize()>1024 || request.Executor is null || request.Scope is null || request.ProposalSequence is <1 or >8)throw Refused();
        request=request.Clone();var executor=BattleResultStaging.Lease(request.Executor);executors.RequireExecution(executor);
        await using var held=await admission.HoldAsync(ct);
        var(battle,campaign)=await work.RequirePreparingAsync(held,executor,request.Scope.BattleId,ct);
        var preparation=await GuildAuthorityEvidence.PreparationAsync(held.Db,battle.Id,ct);var assignment=preparation.RuntimeAssignment.Message();
        if(!assignment.Scope.Equals(request.Scope))throw Refused();
        int latest=new BattlePreparedJournal(artifacts).LatestHeld(battle.Id,battle.Fence)?.Sequence??0;
        if(request.ProposalSequence!=latest && request.ProposalSequence!=latest+1)throw Refused();
        var approved=await GuildAuthorityEvidence.RequireApprovedProfileAsync(held.Db,profiles,assignment.ApprovalTransitionId,executor.ExecutorId,executor.ProfileDigest.ToByteArray(),ct);
        var profile=approved.Profile;var schedule=await GuildAuthorityEvidence.CampaignScheduleAsync(held.Db,campaign.Id,ct);
        var basis=new BattlePreparedManifestV1{Version=1,WorldInstanceId=battle.WorldId,RestoreBoundary=battle.Boundary,CampaignId=campaign.Id,BattleId=battle.Id,Attempt=battle.Attempt,Fence=battle.Fence,
            OutpostId=campaign.OutpostId,OwnerRevision=campaign.OwnerRevision,AttackerAllianceId=campaign.AttackerAllianceId,DefenderAllianceId=campaign.DefenderAllianceId,
            ExecutorId=assignment.ExecutorId,RuntimeInstanceId=assignment.RuntimeInstanceId,ExecutorLeaseId=assignment.OriginalLeaseId,ExecutorLeaseFence=assignment.OriginalLeaseFence,
            StartNonce=assignment.StartNonce,ProfileId=profile.ProfileId,ProfileRevision=profile.Revision,ProfileDigest=assignment.ProfileDigest,ApprovalTransitionId=assignment.ApprovalTransitionId,
            MeasuredInventory=profile.Inventory.Clone(),RulesDigest=ByteString.CopyFrom(campaign.RulesDigest),Template=profile.Template.Clone(),RosterDigest=profile.RosterDigest,Arena=profile.Arena.Clone(),
            PreparationTransitionId=preparation.TransitionId,ProductionCutUtcTicks=preparation.PreparedAtUtc.Ticks,StockAtCut=preparation.StockAtCut,RemainderAtCut=preparation.RemainderAtCut,
            AcquisitionReservationId=schedule.ReservationId,SlotId=schedule.SlotId,SlotStartUtcTicks=campaign.ScheduledAtUtc.Ticks,SlotEndUtcTicks=campaign.ScheduledAtUtc.AddMinutes(30).Ticks,
            ReadinessDeadlineUtcTicks=campaign.ScheduledAtUtc.AddMinutes(5).Ticks,ProposalSequence=request.ProposalSequence};
        basis.Attacker=await Commander(held.Db,sessions,campaign.AttackerCommanderId,campaign.AttackerAllianceId,basis,BattleFighterSide.Attacker,ct);
        basis.Defender=campaign.DefenderCommanderId is {} defender?await Commander(held.Db,sessions,defender,campaign.DefenderAllianceId,basis,BattleFighterSide.Defender,ct):new(){DefenderAi=true};
        executors.RequireCurrent(executor);return new(){Assignment=assignment,Basis=basis};
    }
    private static async Task<BattleCommanderBinding> Commander(RimlinkDbContext db,SessionRegistry sessions,long player,long alliance,BattlePreparedManifestV1 basis,BattleFighterSide side,CancellationToken ct)
    {
        var session=sessions.ByPlayer(player);
        var member=await db.Set<AllianceMember>().AsNoTracking().SingleOrDefaultAsync(m=>m.PlayerId==player && m.AllianceId==alliance,ct);
        var owner=await db.Players.AsNoTracking().SingleOrDefaultAsync(p=>p.Id==player,ct);
        if(session is null || session.Cts.IsCancellationRequested || session.GuildVersion!=1 || member is null || owner is null || owner.SaveWriteEpoch!=session.SaveWriteEpoch)
        {if(side==BattleFighterSide.Defender)return new(){DefenderAi=true};throw Refused();}
        string grant=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("Rimlink.BattlePreparedGrant/v1\0"+basis.PreparationTransitionId+"\0"+(int)side+"\0"+basis.ProposalSequence)).AsSpan(0,16)).ToString("D");
        var binding=new BattleCommanderBinding{PlayerId=player,AllianceId=alliance,ControlEpoch=basis.ProposalSequence,ControlGrantId=grant,SessionDigest=BattleCommanderIdentity.Digest(session),MembershipJoinedUtcTicks=member.JoinedAt.Ticks};
        await BattleCommanderIdentity.RequireAsync(db,sessions,binding,alliance,player,basis.ProposalSequence,false,ct);BattleCommanderIdentity.RequireStillCurrent(sessions,session);return binding;
    }
}
