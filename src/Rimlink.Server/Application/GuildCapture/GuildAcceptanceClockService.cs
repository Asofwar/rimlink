using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Role=Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed partial class GuildOutpostService
{
    // Only the explicitly registered dev provider has a clock projection; there is no ordinary Product clock switch.
    internal Task<GuildClockStepReceipt> AdvanceSetupClockAsync(Actor actor,GuildSetupClockRequest supplied,CancellationToken ct)
    {
        actor.Require(Role.Admin);if(actor.AccountId!=0 || actor.PlayerId!=0)throw GuildAcceptanceClockRules.Invalid();
        var request=GuildAcceptanceClockRules.Freeze(supplied);var control=authority.GuildClock??throw GuildAcceptanceClockRules.Invalid();
        GuildClockStepReceipt? planned=null;
        return MutateAsync(request.Scope,0,actor.Name,request.RequestId,GuildAcceptanceClockRules.Kind,new{actor,request},(_,id,_)=>
            Task.FromResult((planned??throw GuildAcceptanceClockRules.Invalid()) with{TransitionId=id}),ct,
            additionalOwners:request.Players,battleLock:true,validateScope:async token=>
            {
                if(await db.Set<GuildCaptureOperation>().AnyAsync(o=>o.ActorId==0&&o.RequestId==request.RequestId,token))return;
                var grant=authority.GuildClockGrant??throw GuildAcceptanceClockRules.Invalid();
                if(grant.Scope!=GuildAcceptanceScope.Home || !grant.ClockSynchronized || grant.StandId!=request.StandId || grant.BootstrapDigest!=request.BootstrapDigest
                    || grant.SourceIdentity!=request.SourceIdentity || grant.WorldId!=request.Scope.WorldId || grant.Boundary!=request.Scope.Boundary)throw GuildAcceptanceClockRules.Invalid();
                var head=await db.OutpostAuthority.AsNoTracking().SingleAsync(token);
                if(head.HeadId!=request.ExpectedHeadId || !head.HeadDigest.AsSpan().SequenceEqual(request.ExpectedHeadDigest))throw GuildAcceptanceClockRules.Invalid();
                var players=await db.Players.AsNoTracking().OrderBy(p=>p.Id).Select(p=>p.Id).ToArrayAsync(token);
                if(players.Length!=2 || !players.SequenceEqual(request.Players))throw GuildAcceptanceClockRules.Invalid();
                foreach(long player in request.Players)
                    if(!await db.Players.AnyAsync(p=>p.Id==player&&p.FirstSaveAt!=null,token)
                        || !await db.WorldObjects.AnyAsync(o=>o.OwnerPlayerId==player&&o.Type==WorldObjectTypes.Settlement,token)
                        || !await db.SaveVersions.AnyAsync(v=>v.PlayerId==player&&v.IsCurrent,token))throw GuildAcceptanceClockRules.Invalid();
                var scheduled=await db.Set<GuildCampaign>().AsNoTracking().Where(c=>c.Active&&c.Phase=="Scheduled"&&c.WorldId==request.Scope.WorldId&&c.Boundary==request.Scope.Boundary)
                    .OrderBy(c=>c.ScheduledAtUtc).ToArrayAsync(token);
                if(request.Purpose==GuildSetupClockPurpose.EligibilitySetup && scheduled.Length!=0
                    || request.Purpose==GuildSetupClockPurpose.ScheduledWindowNotice && (scheduled.Length==0 || scheduled[0].Id!=request.CampaignId
                        || scheduled[0].ScheduledAtUtc.Ticks!=request.TargetUtcTicks))throw GuildAcceptanceClockRules.Invalid();
                var projection=control.ClockProjection;var before=clock.Sample();
                var journal=await GuildAuthorityEvidence.ClockJournalAsync(db,token);
                if(journal.Length!=projection.Revision || (journal.LastOrDefault()?.TotalAdvanceTicks??0)!=projection.TotalAdvanceTicks)throw GuildAcceptanceClockRules.Invalid();
                long delta=GuildAcceptanceClockRules.Delta(request,projection,before,await db.Set<GuildBattle>().AnyAsync(b=>b.Active,token),scheduled.FirstOrDefault()?.ScheduledAtUtc);
                var shifted=before with{Utc=before.Utc.AddTicks(delta),Timestamp=checked(before.Timestamp+delta)};
                planned=new(1,actor,request,projection.Revision+1,checked(projection.TotalAdvanceTicks+delta),delta,
                    journal.Length==0?"":GuildAcceptanceClockRules.Digest(journal[^1]),before,shifted,
                    new(TimeProvider.System.GetUtcNow().UtcDateTime,TimeProvider.System.GetTimestamp(),TimeProvider.System.TimestampFrequency),projection.Budget,"");
            },afterCommit:control.PublishClock);
    }
}
