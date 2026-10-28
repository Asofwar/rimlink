using Prometheus;

namespace Rimlink.Server.Application.Raids;

/// <summary>Prometheus counters for raids, visits and the shop; scraped at /metrics with the rest.</summary>
public static class RaidMetrics
{
    public static readonly Counter Raids = Metrics.CreateCounter("rimlink_raids_total", "Raid state changes by outcome", "outcome");
    public static readonly Gauge Active = Metrics.CreateGauge("rimlink_raids_active", "Raids not yet resolved, expired or cancelled");
    public static readonly Counter Visits = Metrics.CreateCounter("rimlink_visits_total", "Visits begun");
    public static readonly Counter Sales = Metrics.CreateCounter("rimlink_shop_sales_total", "Shop purchases");

    public static void Outcome(string outcome) => Raids.WithLabels(outcome).Inc();
}

/// <summary>Hourly sweep: raids nobody landed within the limit go back to their attacker.</summary>
public sealed class RaidExpiryService(IServiceScopeFactory scopes, ILogger<RaidExpiryService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                using var scope = scopes.CreateScope();
                int n = await scope.ServiceProvider.GetRequiredService<RaidService>().ExpireDue(DateTime.UtcNow);
                if (n > 0) log.LogInformation("Expired {Count} raids", n);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
