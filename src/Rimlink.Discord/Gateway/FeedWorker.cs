using Rimlink.Discord.Api;
using Rimlink.Discord.Feed;

namespace Rimlink.Discord.Gateway;

/// <summary>Reads the admin feed into the router, and once a second ticks the router and flushes the outbound queue.</summary>
public sealed class FeedWorker(FeedReader reader, FeedRouter router, OutboundQueue queue, ILogger<FeedWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var pump = Pump(ct);
        await foreach (var signal in reader.Run(ct))
        {
            try
            {
                router.Handle(signal);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Feed signal {Kind} could not be handled", signal.Kind);
            }
        }
        await pump;
    }

    private async Task Pump(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                router.Tick();
                await queue.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
