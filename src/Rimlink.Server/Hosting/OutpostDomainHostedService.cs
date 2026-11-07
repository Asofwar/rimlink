using Microsoft.Extensions.DependencyInjection.Extensions;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.Server.Hosting;

// Startup admitted admin-only hosts must not instantiate or start ordinary domain writers.
internal sealed class OutpostDomainHostedService<T>(IServiceProvider services, OutpostFeature feature,
    ILogger<OutpostDomainHostedService<T>> log) : BackgroundService where T : class, IHostedService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        T? started = null;
        try
        {
            var readiness = feature.Readiness;
            if (!readiness.CanRunDomainWriters)
                log.LogInformation("Outpost readiness holds {Writer}: {Blockers}", typeof(T).Name, string.Join(",", readiness.Blockers));
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            // A proved successor bind may make this same admin-only host Ready later; no constructor runs before admission.
            while (!feature.Readiness.CanRunDomainWriters)
                await timer.WaitForNextTickAsync(stoppingToken);
            stoppingToken.ThrowIfCancellationRequested();
            started = services.GetRequiredService<T>();
            await started.StartAsync(stoppingToken);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { if (started is not null) await started.StopAsync(CancellationToken.None); }
    }
}

internal static class OutpostDomainHostedServices
{
    internal static bool CanWrite(IServiceProvider services) => services.GetRequiredService<OutpostFeature>().Readiness.CanRunDomainWriters;
    internal static IServiceCollection AddOutpostDomainHostedService<T>(this IServiceCollection services) where T : class, IHostedService
    {
        services.TryAddSingleton<T>();
        return services.AddHostedService<OutpostDomainHostedService<T>>();
    }
}
