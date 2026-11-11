using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage;
using GuildCaptureOperation=Rimlink.Server.Storage.Entities.GuildCaptureOperation;

namespace Rimlink.GuildAcceptanceHost;

internal sealed class GuildStandBindingService(IServiceScopeFactory scopes,Action<string,string> bindWorld):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            using var services=scopes.CreateScope();var sp=services.ServiceProvider;var feature=sp.GetRequiredService<OutpostFeature>();
            if(feature.Startup.State!=OutpostStartupState.Ready || feature.VerifiedDomainFormat!=OutpostDomainFormat.V3)continue;
            var authority=sp.GetRequiredService<OutpostAuthority>();
            bool needsBinding;
            await using(var held=await authority.AcquireAsync([],stoppingToken))
            {
                if((await authority.CheckHeldAsync(held,stoppingToken)).State!=OutpostStartupState.Ready)throw new InvalidDataException("guild-stand-binding-authority-unavailable");
                var db=sp.GetRequiredService<RimlinkDbContext>();var row=await db.OutpostAuthority.AsNoTracking().SingleAsync(stoppingToken);
                needsBinding = feature.GuildCut?.Acceptance is null;
                if(needsBinding && await db.Set<GuildCaptureOperation>().AnyAsync(stoppingToken))throw new InvalidDataException("guild-stand-cannot-adopt-guild-history");
                bindWorld(row.WorldId,row.RestoreBoundary);
            }
            if(needsBinding)await authority.BindGuildAcceptanceAsync(stoppingToken);
            return;
        }while(await timer.WaitForNextTickAsync(stoppingToken));
    }
}
