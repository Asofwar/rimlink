using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed partial class GuildOutpostService
{
    internal async Task<GuildOutpostList> ListOutpostsAsync(long player,GuildMutationScope scope,CancellationToken ct)
    {
        if(player<=0)throw Refused();authority.RequireRuntimeGuild(GuildRuntimeUse.Setup);
        await using var held=await authority.AcquireAsync([player],ct);await RequireScopeAsync(held,scope,ct);
        var membership=await db.Set<AllianceMember>().AsNoTracking().SingleOrDefaultAsync(m=>m.PlayerId==player,ct);
        var rows=await db.Set<GuildOutpostRow>().AsNoTracking().Where(o=>o.WorldId==scope.WorldId).OrderBy(o=>o.Tile).ToArrayAsync(ct);
        var sample=clock.Sample();var result=new GuildOutpostList();result.Outposts.AddRange(rows.Select(o=>Summary(o,membership?.AllianceId,sample)));
        if(membership is not null)
        {
            var obligations=await db.Set<GuildResourceObligation>().AsNoTracking().Where(o=>o.AllianceId==membership.AllianceId
                && o.RemainingCount>o.ReservedCount && db.Set<GuildOutpostRow>().Any(p=>p.Id==o.OutpostId&&p.WorldId==scope.WorldId)).OrderBy(o=>o.Id).ToArrayAsync(ct);
            result.Obligations.AddRange(obligations.Select(o=>new GuildObligationSummary{ObligationId=o.Id,OutpostId=o.OutpostId,OwnerRevision=o.OwnerRevision,
                AllianceId=o.AllianceId,ResourceDef=ResourceDef(o.Resource),AvailableCount=o.RemainingCount-o.ReservedCount}));
            long alliance = membership.AllianceId;
            var campaigns = await (from campaign in db.Set<GuildCampaign>().AsNoTracking()
                join outpost in db.Set<GuildOutpostRow>().AsNoTracking() on campaign.OutpostId equals outpost.Id
                where campaign.WorldId == scope.WorldId && campaign.Boundary == scope.Boundary && outpost.WorldId == scope.WorldId
                    && campaign.Active && (campaign.AttackerAllianceId == alliance || campaign.DefenderAllianceId == alliance)
                orderby campaign.ScheduledAtUtc, campaign.Id
                select new { Campaign = campaign, outpost.Tile,
                    Started = db.Set<GuildBattle>().Any(battle => battle.CampaignId == campaign.Id && battle.StartDigest != null) }).ToArrayAsync(ct);
            bool mayAssign = campaigns.Any(row => row.Campaign.DefenderAllianceId == alliance)
                && await db.Set<AllianceGroup>().AnyAsync(group => group.Id == alliance && group.OwnerId == player && !group.Disbanded, ct);
            foreach (var row in campaigns)
            {
                var campaign = row.Campaign;
                bool defending = campaign.DefenderAllianceId == alliance;
                result.Campaigns.Add(new GuildCampaignSummary
                {
                    CampaignId = campaign.Id, Tile = row.Tile, AllianceId = alliance, Defending = defending,
                    ScheduledAtUtcTicks = campaign.ScheduledAtUtc.Ticks,
                    CommanderId = defending ? campaign.DefenderCommanderId ?? 0 : campaign.AttackerCommanderId,
                    CanAssignDefender = defending && mayAssign && !row.Started && DefenderAssignmentOpen(campaign, sample.Utc),
                });
            }
        }
        return result;
    }
    private static string ResourceDef(GuildResource resource)=>resource switch{GuildResource.Steel=>"Steel",GuildResource.Wood=>"WoodLog",_=>throw Refused()};
    private static GuildOutpostSummary Summary(GuildOutpostRow row,long? memberAlliance,ClockSample sample)=>new(){OutpostId=row.Id,Tile=row.Tile,ResourceDef=ResourceDef(row.Resource),
        AllianceId=row.AllianceId??0,OwnerRevision=row.Revision,Phase=row.Phase,DefenceHourUtc=row.DefenceHourUtc,ProtectedUntilUtcTicks=row.ProtectedUntilUtc.Ticks,
        StockVisible=row.AllianceId is not null&&row.AllianceId==memberAlliance,Stock=row.AllianceId is not null&&row.AllianceId==memberAlliance
            ?GuildProduction.Settle(row.Resource,new(row.Stock,row.Remainder),OutpostClock.Elapsed(new(row.CheckpointUtc,row.ClockProcessId,row.ClockTimestamp,row.ClockFrequency),sample),row.Phase!="Active").Count:0};
}
