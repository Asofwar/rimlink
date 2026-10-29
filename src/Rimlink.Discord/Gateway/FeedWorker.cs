using Rimlink.Discord.Api;
using Rimlink.Discord.Feed;

namespace Rimlink.Discord.Gateway;

/// <summary>
/// Reads the admin feed into the router; once a second ticks the router, flushes the outbound queue and saves the last feed id
/// (also on shutdown) for the next start.
/// </summary>
public sealed class FeedWorker(FeedReader reader, FeedRouter router, OutboundQueue queue, FeedState state, ILogger<FeedWorker> log) : BackgroundService
{
    private string? saved;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        saved = reader.LastId;
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
        SaveState();
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
                SaveState();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void SaveState()
    {
        string? id = reader.LastId;
        if (id is null || id == saved) return;
        state.Save(id);
        saved = id;
    }
}
