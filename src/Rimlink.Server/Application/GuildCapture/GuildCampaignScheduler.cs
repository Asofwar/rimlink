using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

// Selection drops S/B before the financial service reacquires A/O/S/B and validates the candidate again.
internal sealed class GuildCampaignScheduler(BattleTransportAdmission admission,Outposts.OutpostClock clock,GuildOutpostService campaigns)
{
    internal async Task<bool> TickAsync(CancellationToken ct)
    {
        GuildCampaign? candidate;GuildMutationScope scope;bool expired;
        GuildFreezeReceipt? freeze;
        string head;
        await using(var held=await admission.HoldAsync(ct))
        {
            var now=clock.Sample().Utc;var deadline=now.AddMinutes(-5);
            var pending=held.Db.Set<GuildCampaign>().AsNoTracking().Where(c=>c.Active && c.WorldId==held.Cut.WorldId && c.Boundary==held.Cut.Boundary);
            freeze = await GuildAuthorityEvidence.LatestFreezeAsync(held.Db, held.Cut.WorldId, ct);
            bool frozen = freeze?.Command.Frozen == true;
            candidate=await pending.Where(c=>(c.Phase=="Scheduled" || c.Phase=="PreparingBattle") && (frozen || c.ScheduledAtUtc<=deadline))
                .OrderBy(c=>c.ScheduledAtUtc).ThenBy(c=>c.Id).FirstOrDefaultAsync(ct);
            expired=candidate is not null;
            if (!frozen) candidate??=await pending.Where(c=>c.Phase=="Scheduled" && c.ScheduledAtUtc<=now).OrderBy(c=>c.ScheduledAtUtc).ThenBy(c=>c.Id).FirstOrDefaultAsync(ct);
            if(candidate is null)return false;
            scope=new(held.Cut.WorldId,held.Cut.Boundary);
            head = held.Cut.HeadId;
        }
        string operation=freeze?.Command.Frozen==true ? "freeze:"+freeze.TransitionId : expired?"expire":"prepare";
        string request=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("Rimlink.GuildScheduler/v1\0"+scope.WorldId+"\0"+scope.Boundary+"\0"+candidate.Id+"\0"+operation)).AsSpan(0,16)).ToString("D");
        if (freeze?.Command.Frozen == true)
            await campaigns.CancelCampaignAsync(Actor.Scheduled("system:guild-freeze"), scope, request, candidate.Id, head,
                "Guild capture freeze " + freeze.TransitionId, ct);
        else if(expired)await campaigns.ExpireCampaignAsync(scope,request,candidate.Id,ct);
        else await campaigns.PrepareCampaignAsync(scope,request,candidate.Id,ct);
        return true;
    }
}
