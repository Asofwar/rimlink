using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Rest;
using Rimlink.Server.Storage;

namespace Rimlink.GuildAcceptanceHost;

internal static class GuildStandClockEndpoints
{
    internal static void MapGuildStandClock(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/acceptance/clock").RequireAdmin();
        group.AddEndpointFilter(async(context,next)=>
        {
            try{return await next(context);}
            catch(OutpostEvidenceUnavailableException error){return Results.Problem(error.Message,statusCode:StatusCodes.Status409Conflict);}
        });
        group.MapGet("/",async(OutpostAuthority authority,RimlinkDbContext db,BattleStateLock battles,OutpostClock clock,CancellationToken ct)=>
        {
            await using var held=await authority.AcquireAsync([],ct);using var battle=await battles.HoldAsync(ct);
            if((await authority.CheckHeldAsync(held,ct)).State!=OutpostStartupState.Ready)throw GuildAcceptanceClockRules.Invalid();
            authority.RequireRuntimeGuild(GuildRuntimeUse.Setup);
            var projection=authority.GuildClock?.ClockProjection??throw GuildAcceptanceClockRules.Invalid();
            var grant=authority.GuildClockGrant??throw GuildAcceptanceClockRules.Invalid();
            var root=await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
            return Results.Ok(new{Mode=GuildStandFiles.Mode,ProductEnabled=false,grant.StandId,grant.BootstrapDigest,grant.SourceIdentity,
                Scope=new GuildMutationScope(root.WorldId,root.RestoreBoundary),Clock=projection,Sample=clock.Sample(),ExpectedHeadId=root.HeadId,ExpectedHeadDigest=root.HeadDigest});
        }).RequireScope(ApiScope.Admin);
        group.MapPost("/advance",async(GuildSetupClockRequest request,HttpContext http,GuildOutpostService service,CancellationToken ct)=>
            Results.Ok(await service.AdvanceSetupClockAsync(http.Actor(),request,ct))).RequireScope(ApiScope.Admin);
    }
}
