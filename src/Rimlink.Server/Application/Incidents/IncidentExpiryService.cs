namespace Rimlink.Server.Application.Incidents;

/// <summary>Refunds paid incidents their targets did not play in time: at start, then hourly (as raids).</summary>
public sealed class IncidentExpiryService(IServiceScopeFactory scopes, ILogger<IncidentExpiryService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    int n = await scope.ServiceProvider.GetRequiredService<IncidentService>().ExpireDue(DateTime.UtcNow);
                    if (n > 0) log.LogInformation("Expired {Count} paid incidents", n);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Paid incident expiry sweep failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
