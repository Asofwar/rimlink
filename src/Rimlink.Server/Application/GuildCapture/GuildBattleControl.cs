using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildControlIntent(byte[] Request,string SessionDigest);
internal sealed record GuildBattleControlReceipt(int Version,GuildControlIntent Intent,string BattleId,string CampaignId,long PlayerId,long AllianceId,
    long MembershipJoinedUtcTicks,int Side,string GrantId,long Epoch,string? ReplacedGrantId,long PreviousEpoch,DateTime GrantedAtUtc,DateTime ExpiresAtUtc,string Reason,string TransitionId);
internal sealed record GuildDefenderCommanderRequest(GuildMutationScope Scope,string CampaignId,long CommanderId);
internal sealed record GuildDefenderCommanderReceipt(int Version,GuildDefenderCommanderRequest Request,long LeaderId,long AllianceId,DateTime AssignedAtUtc,string TransitionId);

internal sealed partial class GuildOutpostService
{
    internal Task<GuildDefenderCommanderReceipt> AssignDefenderCommanderAsync(long leader,GuildMutationScope scope,string requestId,string campaignId,long commander,CancellationToken ct)
    {
        if(leader<=0 || commander<=0 || !Guid.TryParseExact(campaignId,"D",out _))throw Refused();
        var request=new GuildDefenderCommanderRequest(scope,campaignId,commander);
        return MutateAsync(scope,leader,"player:"+leader,requestId,"GuildDefenderCommanderAssigned",request,async(lease,id,token)=>
        {
            await RequireCaptureOpenHeldAsync(scope.WorldId, token);
            var campaign=await db.Set<GuildCampaign>().SingleOrDefaultAsync(c=>c.Id==campaignId,token)??throw Refused();var sample=clock.Sample();
            if(campaign.WorldId!=scope.WorldId || campaign.Boundary!=scope.Boundary || !DefenderAssignmentOpen(campaign,sample.Utc)
                || await db.Set<GuildBattle>().AnyAsync(b=>b.CampaignId==campaign.Id && b.StartDigest!=null,token))throw Refused();
            await policy.RequireLeaderHeldAsync(lease,leader,campaign.DefenderAllianceId,sample.Utc,token);
            await policy.RequireCommanderHeldAsync(lease,commander,campaign.DefenderAllianceId,sample.Utc,BattleFighterSide.Defender,token);
            campaign.DefenderCommanderId=commander;
            return new GuildDefenderCommanderReceipt(1,request,leader,campaign.DefenderAllianceId,sample.Utc,id);
        },ct,[commander],battleLock:true);
    }

    private static bool DefenderAssignmentOpen(GuildCampaign campaign, DateTime utc) =>
        campaign.Active && campaign.Phase is ("Scheduled" or "PreparingBattle") && utc < campaign.ScheduledAtUtc.AddMinutes(5);

    internal async Task<BattleControlGrantReply> ReconcileBattleControlAsync(Session session,BattleReconcileControlRequest request,CancellationToken ct)
    {
        request=GuildAuthorityEvidence.ReadControlRequest(request.ToByteArray());
        var intent=new GuildControlIntent(request.ToByteArray(),Convert.ToHexStringLower(BattleCommanderIdentity.Digest(session).Span));
        var scope=new GuildMutationScope(request.Scope.WorldInstanceId,request.Scope.RestoreBoundary);
        var receipt=await MutateAsync(scope,session.PlayerId,"player:"+session.PlayerId,request.ClientRequestId,"GuildBattleControlReconciled",intent,async(lease,id,token)=>
        {
            await RequireCaptureOpenHeldAsync(scope.WorldId, token);
            var battle=await db.Set<GuildBattle>().AsNoTracking().SingleOrDefaultAsync(b=>b.Id==request.Scope.BattleId,token)??throw Refused();
            var campaign=await db.Set<GuildCampaign>().AsNoTracking().SingleAsync(c=>c.Id==battle.CampaignId,token);var sample=clock.Sample();
            if(!battle.Active || battle.Phase!="StartCommitted" || battle.StartManifest is null || battle.StartDigest is null || battle.FinalDigest is not null
                || !campaign.Active || campaign.Phase!="StartCommitted" || battle.WorldId!=scope.WorldId || battle.Boundary!=scope.Boundary
                || battle.Attempt!=request.Scope.Attempt || battle.Fence!=request.Scope.Fence || !battle.StartDigest.AsSpan().SequenceEqual(request.StartDigest.Span))throw Refused();
            var start=BattleManifestContracts.ReadPrepared(battle.StartManifest);
            int side=campaign.AttackerCommanderId==session.PlayerId?1:campaign.DefenderCommanderId==session.PlayerId?2:0;
            if(side==0)throw Refused();long alliance=side==1?campaign.AttackerAllianceId:campaign.DefenderAllianceId;
            await policy.RequireCommanderHeldAsync(lease,session.PlayerId,alliance,sample.Utc,(BattleFighterSide)side,token);
            var member=await db.Set<AllianceMember>().AsNoTracking().SingleAsync(m=>m.PlayerId==session.PlayerId && m.AllianceId==alliance,token);
            await GuildAuthorityEvidence.ProfileAdmissionAsync(db,battle.WorldId,battle.ExecutorId,start.ProfileDigest.ToByteArray(),token);
            executors.RequireExecutionFor(start);
            var prior=await db.Set<GuildBattleControlGrant>().Where(g=>g.BattleId==battle.Id && g.Side==side).OrderByDescending(g=>g.Epoch).ToArrayAsync(token);
            long epoch=prior.FirstOrDefault()?.Epoch??0;
            if(request.HasExpectedEpoch && request.ExpectedEpoch!=epoch || epoch==long.MaxValue)throw Refused();
            var operations=await db.Set<GuildCaptureOperation>().AsNoTracking().Where(o=>o.Kind=="GuildBattleControlReconciled").Select(o=>o.Receipt).ToArrayAsync(token);
            var controls=operations.Select(GuildAuthorityEvidence.ReadControlReceipt).Where(r=>r.BattleId==battle.Id).ToArray();
            if(controls.Length>=512)throw Refused();
            var latest=controls.Where(r=>r.Side==side).OrderByDescending(r=>r.Epoch).FirstOrDefault();
            if(latest is not null && sample.Utc-latest.GrantedAtUtc<TimeSpan.FromSeconds(5))throw Refused();
            long deadline=Math.Min(start.SlotEndUtcTicks,(battle.StartedAtUtc??throw Refused()).AddMinutes(20).Ticks);
            var expires=new DateTime(Math.Min(deadline,sample.Utc.AddMinutes(2).Ticks),DateTimeKind.Utc);
            if(expires<=sample.Utc || sample.Utc<battle.StartedAtUtc)throw Refused();
            long next=epoch+1;
            if(epoch==0 && side==2 && !start.Defender.DefenderAi)
            {
                bool original=start.Defender.SessionDigest.Equals(BattleCommanderIdentity.Digest(session)) && start.Defender.MembershipJoinedUtcTicks==member.JoinedAt.Ticks;
                next=Math.Max(next,checked(start.Defender.ControlEpoch+(original?0:1)));
            }
            string grantId=Guid.NewGuid().ToString("D");
            foreach(var old in prior.Where(g=>g.Active))old.Active=false;
            db.Add(new GuildBattleControlGrant{Id=grantId,BattleId=battle.Id,PlayerId=session.PlayerId,Side=side,Epoch=next,
                CapabilityDigest=request.CapabilityDigest.ToByteArray(),ExpiresAtUtc=expires,Active=true});
            BattleCommanderIdentity.RequireStillCurrent(sessions,session);
            string reason=side==2 && epoch==0?"InitialDefender":latest is null || latest.Intent.SessionDigest!=intent.SessionDigest?"Reconnect":"Renew";
            return new GuildBattleControlReceipt(1,intent,battle.Id,campaign.Id,session.PlayerId,alliance,member.JoinedAt.Ticks,side,grantId,next,
                prior.FirstOrDefault()?.Id,epoch,sample.Utc,expires,reason,id);
        },ct,validateScope:async token=>
        {
            BattleCommanderIdentity.RequireStillCurrent(sessions,session);
            var player=await db.Players.AsNoTracking().SingleOrDefaultAsync(p=>p.Id==session.PlayerId,token);
            if(session.GuildVersion!=1 || player is null || player.SaveWriteEpoch!=session.SaveWriteEpoch)throw Refused();
        },battleLock:true);
        return new(){GrantId=receipt.GrantId,Side=(BattleFighterSide)receipt.Side,Epoch=receipt.Epoch,ExpiresAtUnixMs=new DateTimeOffset(receipt.ExpiresAtUtc).ToUnixTimeMilliseconds(),
            TransitionId=receipt.TransitionId,StartDigest=request.StartDigest};
    }
}

internal static partial class GuildAuthorityEvidence
{
    internal static BattleReconcileControlRequest ReadControlRequest(byte[] bytes)
    {
        if(bytes.Length is <1 or >1024)throw InvalidControl();BattleReconcileControlRequest value;
        try{value=BattleReconcileControlRequest.Parser.WithDiscardUnknownFields(true).ParseFrom(bytes);}catch(InvalidProtocolBufferException){throw InvalidControl();}
        if(!value.ToByteArray().AsSpan().SequenceEqual(bytes) || !Guid.TryParseExact(value.ClientRequestId,"D",out var id) || id==Guid.Empty || value.Scope is not {} scope
            || !Guid.TryParseExact(scope.BattleId,"D",out id) || id==Guid.Empty || !Guid.TryParseExact(scope.RestoreBoundary,"D",out id) || id==Guid.Empty
            || string.IsNullOrWhiteSpace(scope.WorldInstanceId) || scope.WorldInstanceId.Length>256 || scope.WorldInstanceId.Any(char.IsControl)
            || scope.Attempt<=0 || scope.Fence<=0 || value.StartDigest.Length!=32 || value.CapabilityDigest.Length!=32 || value.HasExpectedEpoch && value.ExpectedEpoch<0)throw InvalidControl();
        return value;
    }
    internal static GuildBattleControlReceipt ReadControlReceipt(byte[] bytes)=>JsonSerializer.Deserialize<GuildBattleControlReceipt>(bytes,Json)??throw InvalidControl();
    private static async Task ValidateControlsAsync(RimlinkDbContext db,GuildCaptureOperation[] operations,IReadOnlyDictionary<string,OutpostTransition> transitions,CancellationToken ct)
    {
        try
        {
            foreach(var operation in operations.Where(o=>o.Kind=="GuildBattleControlReconciled"))
            {
                var receipt=ReadControlReceipt(operation.Receipt);var request=ReadControlRequest(receipt.Intent.Request);
                var battle=await db.Set<GuildBattle>().AsNoTracking().SingleAsync(b=>b.Id==receipt.BattleId,ct);
                var campaign=await db.Set<GuildCampaign>().AsNoTracking().SingleAsync(c=>c.Id==receipt.CampaignId,ct);
                var grant=await db.Set<GuildBattleControlGrant>().AsNoTracking().SingleAsync(g=>g.Id==receipt.GrantId,ct);
                var started=operations.Single(o=>o.Kind=="GuildBattleStarted" && ReadStart((JsonSerializer.Deserialize<GuildBattleStartReceipt>(o.Receipt,Json)??throw InvalidControl()).Intent).Scope.BattleId==battle.Id);
                var start=BattleManifestContracts.ReadPrepared(battle.StartManifest??throw InvalidControl());
                if(receipt.Version!=1 || receipt.PlayerId!=operation.ActorId || receipt.TransitionId!=operation.TransitionId || request.ClientRequestId!=operation.RequestId
                    || !IntentDigest(receipt.Intent).AsSpan().SequenceEqual(operation.IntentDigest) || receipt.Intent.SessionDigest.Length!=64
                    || receipt.Intent.SessionDigest.Any(c=>c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || receipt.MembershipJoinedUtcTicks<=0
                    || receipt.GrantedAtUtc.Kind!=DateTimeKind.Utc || receipt.ExpiresAtUtc.Kind!=DateTimeKind.Utc || receipt.ExpiresAtUtc<=receipt.GrantedAtUtc
                    || receipt.ExpiresAtUtc-receipt.GrantedAtUtc>TimeSpan.FromMinutes(2) || receipt.Reason is not ("InitialDefender" or "Reconnect" or "Renew")
                    || battle.StartManifest is null || battle.StartDigest is null || battle.CampaignId!=campaign.Id || !request.StartDigest.Span.SequenceEqual(battle.StartDigest)
                    || request.Scope.BattleId!=battle.Id || request.Scope.WorldInstanceId!=start.WorldInstanceId || request.Scope.RestoreBoundary!=start.RestoreBoundary
                    || request.Scope.Attempt!=start.Attempt || request.Scope.Fence!=start.Fence || receipt.Side is not (1 or 2)
                    || receipt.PlayerId!=(receipt.Side==1?campaign.AttackerCommanderId:campaign.DefenderCommanderId)
                    || receipt.AllianceId!=(receipt.Side==1?campaign.AttackerAllianceId:campaign.DefenderAllianceId)
                    || grant.BattleId!=battle.Id || grant.PlayerId!=receipt.PlayerId || grant.Side!=receipt.Side || grant.Epoch!=receipt.Epoch
                    || grant.ExpiresAtUtc!=receipt.ExpiresAtUtc || !grant.CapabilityDigest.AsSpan().SequenceEqual(request.CapabilityDigest.Span)
                    || receipt.Epoch<=receipt.PreviousEpoch || receipt.PreviousEpoch>0 && receipt.Epoch!=receipt.PreviousEpoch+1
                    || receipt.ExpiresAtUtc.Ticks>start.SlotEndUtcTicks || receipt.GrantedAtUtc<battle.StartedAtUtc
                    || request.HasExpectedEpoch && request.ExpectedEpoch!=receipt.PreviousEpoch)throw InvalidControl();
                RequireEarlier(transitions,started.TransitionId,receipt.TransitionId);
                if(receipt.PreviousEpoch==0 && receipt.Side==2)
                {
                    long expected=1;
                    if(!start.Defender.DefenderAi)
                    {
                        bool original=receipt.Intent.SessionDigest==Convert.ToHexStringLower(start.Defender.SessionDigest.Span)
                            && receipt.MembershipJoinedUtcTicks==start.Defender.MembershipJoinedUtcTicks;
                        expected=checked(start.Defender.ControlEpoch+(original?0:1));
                    }
                    if(receipt.Epoch!=expected)throw InvalidControl();
                }
                if(receipt.PreviousEpoch>0)
                {
                    var previous=await db.Set<GuildBattleControlGrant>().AsNoTracking().SingleAsync(g=>g.Id==receipt.ReplacedGrantId,ct);
                    if(previous.BattleId!=battle.Id || previous.Side!=receipt.Side || previous.Epoch!=receipt.PreviousEpoch)throw InvalidControl();
                }
                else if(receipt.ReplacedGrantId is not null)throw InvalidControl();
            }
            foreach(var group in operations.Where(o=>o.Kind=="GuildDefenderCommanderAssigned").GroupBy(o=>JsonSerializer.Deserialize<GuildDefenderCommanderReceipt>(o.Receipt,Json)!.Request.CampaignId))
            {
                var campaign=await db.Set<GuildCampaign>().AsNoTracking().SingleAsync(c=>c.Id==group.Key,ct);
                var scheduled=await CampaignScheduleAsync(db,campaign.Id,ct);
                var assignments=new Dictionary<string,GuildDefenderCommanderReceipt>(StringComparer.Ordinal);
                foreach(var operation in group)
                {
                    var receipt=JsonSerializer.Deserialize<GuildDefenderCommanderReceipt>(operation.Receipt,Json)??throw InvalidControl();
                    if(receipt.Version!=1 || receipt.LeaderId!=operation.ActorId || receipt.TransitionId!=operation.TransitionId || receipt.Request.CommanderId<=0
                        || receipt.Request.Scope.WorldId!=scheduled.Request.Scope.WorldId || receipt.Request.Scope.Boundary!=scheduled.Request.Scope.Boundary || receipt.AllianceId!=campaign.DefenderAllianceId
                        || receipt.AssignedAtUtc.Kind!=DateTimeKind.Utc || receipt.AssignedAtUtc>=campaign.ScheduledAtUtc.AddMinutes(5)
                        || !IntentDigest(receipt.Request).AsSpan().SequenceEqual(operation.IntentDigest))throw InvalidControl();
                    assignments.Add(receipt.TransitionId,receipt);
                }
                var byHead=transitions.Values.ToDictionary(t=>t.NextHeadId,StringComparer.Ordinal);
                var oldHeads=transitions.Values.Select(t=>t.OldHeadId).ToHashSet(StringComparer.Ordinal);
                string cursor=transitions.Values.Single(t=>!oldHeads.Contains(t.NextHeadId)).NextHeadId;
                GuildDefenderCommanderReceipt? latest=null;
                for(int i=0;i<transitions.Count && byHead.TryGetValue(cursor,out var transition);i++)
                {if(assignments.TryGetValue(transition.Id,out latest))break;cursor=transition.OldHeadId;}
                if(latest is null || campaign.DefenderCommanderId!=latest.Request.CommanderId)throw InvalidControl();
            }
        }
        catch(Exception error)when(error is JsonException or InvalidDataException or InvalidOperationException or NullReferenceException or ArgumentException or OverflowException)
        {throw new OutpostEvidenceUnavailableException("guild-battle-control-evidence-invalid: "+error.GetType().Name);}
    }
    private static InvalidDataException InvalidControl()=>new("guild-battle-control-invalid");
}
