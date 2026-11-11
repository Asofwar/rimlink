using Microsoft.Extensions.Options;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Hosting;

internal sealed class GuildCaptureHostedService(IServiceScopeFactory scopes,IOptions<GuildCaptureOptions> options,ILogger<GuildCaptureHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do { await TickAsync(stoppingToken); }while(await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}
    }
    internal async Task TickAsync(CancellationToken ct)
    {
        using var scope=scopes.CreateScope();
        var feature=scope.ServiceProvider.GetRequiredService<OutpostFeature>();
        if(!options.Value.Enabled && feature.GuildGrant is null)return;
        if(!feature.Readiness.CanRunDomainWriters || feature.VerifiedDomainFormat!=OutpostDomainFormat.V3)return;
        // The scheduler checks the published cut under S; a writer may temporarily withdraw it before this call.
        try { await scope.ServiceProvider.GetRequiredService<GuildCampaignScheduler>().TickAsync(ct); }
        catch(OutpostEvidenceUnavailableException error){log.LogDebug("Guild campaign scheduler is held: {Reason}",error.Message);}
        catch(Exception error)when(error is not OperationCanceledException){log.LogWarning(error,"Guild campaign scheduler failed; campaign state requires reconciliation");}
    }
}
