using Microsoft.Extensions.Options;
using Prometheus;
using Rimlink.Server.Hosting;
using LiveSessionRow = Rimlink.Server.Storage.Entities.LiveSession;

namespace Rimlink.Server.Application.Live;

/// <summary>Prometheus counters for live sessions; scraped at /metrics with the rest.</summary>
public static class LiveMetrics
{
    public static readonly Counter Proposed = Metrics.CreateCounter("rimlink_live_proposed_total", "Live sessions proposed", "kind");
    public static readonly Counter Sessions = Metrics.CreateCounter("rimlink_live_sessions_total", "Live sessions ended by kind and reason", "kind", "end_reason");
    public static readonly Counter Frames = Metrics.CreateCounter("rimlink_live_frames_total", "Frames relayed by direction", "dir");
    public static readonly Gauge Active = Metrics.CreateGauge("rimlink_live_active", "Live sessions not ended");

    public static void Ended(LiveSessionRow s) =>
        Sessions.WithLabels(((Protocol.V1.LiveKind)s.Kind).ToString(), s.State == Storage.Entities.LiveStateDb.Fallback ? "fallback" : ((Protocol.V1.LiveEndReason)(s.EndReason ?? 0)).ToString()).Inc();
}

/// <summary>Sweep every Live:SweepSeconds: unanswered invitations fall back, lost peers end the session after ReconnectWaitMinutes.</summary>
public sealed class LiveTimeoutService(IServiceScopeFactory scopes, IOptions<LiveOptions> options, ILogger<LiveTimeoutService> log) : BackgroundService
{
    /// <summary>Hooks the relay registers to be swept with the sessions (heartbeat timeouts).</summary>
    public event Func<DateTime, Task>? Sweep;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.SweepSeconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var now = DateTime.UtcNow;
                    if (Sweep is { } sweep) await sweep(now);
                    using var scope = scopes.CreateScope();
                    int n = await scope.ServiceProvider.GetRequiredService<LiveSessionService>().ExpireDue(now);
                    if (n > 0) log.LogInformation("Live sweep: {Count} sessions timed out", n);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Live sweep failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
