using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Rimlink.Discord.Api;
using Rimlink.Discord.Tests.Support;
using Rimlink.Server.Application.Feed;
using Xunit;

namespace Rimlink.Discord.Tests.Api;

public class FeedReaderTests
{
    private static readonly FeedReaderOptions Fast = new() { Backoff = [TimeSpan.FromMilliseconds(10)], IdleTimeout = TimeSpan.FromSeconds(45) };

    private static FeedReader Reader(Func<HttpClient> client) => new(client, Fast, NullLogger<FeedReader>.Instance);

    /// <summary>Collects signals until <paramref name="until"/> holds, then stops the reader. Fails after 10 s.</summary>
    private static async Task<List<FeedSignal>> Collect(FeedReader reader, Func<List<FeedSignal>, bool> until, Action<List<FeedSignal>>? onEach = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var signals = new List<FeedSignal>();
        await foreach (var signal in reader.Run(cts.Token))
        {
            signals.Add(signal);
            onEach?.Invoke(signals);
            if (until(signals)) break;
        }
        return signals;
    }

    private static int Events(List<FeedSignal> s, string type) => s.Count(x => x.Kind == FeedSignalKind.Event && x.Event!.Type == type);

    [Fact]
    public async Task ReadsEventsWithIds()
    {
        using var bot = new BotFixture();
        var feed = bot.Fixture.Services.GetRequiredService<AdminFeed>();
        var reader = Reader(bot.FeedClient());

        var signals = await Collect(reader, s => Events(s, "test") == 1,
            s => { if (s.Count == 1 && s[0].Kind == FeedSignalKind.Connected) feed.Publish("test", new { n = 1 }); });

        Assert.Equal(FeedSignalKind.Connected, signals[0].Kind);
        var ev = signals.Last().Event!;
        Assert.Equal(1, ev.Data.GetProperty("n").GetInt32());
        Assert.Equal(ev.Id, reader.LastId);
        Assert.Matches(@"^\d+:\d+$", ev.Id!);
    }

    [Fact]
    public async Task ResumesWithoutLossOrDuplicates()
    {
        using var bot = new BotFixture();
        var feed = bot.Fixture.Services.GetRequiredService<AdminFeed>();
        var reader = Reader(bot.FeedClient());
        await Collect(reader, s => Events(s, "test") == 3, s =>
        {
            if (s.Count == 1) for (int i = 1; i <= 3; i++) feed.Publish("test", new { n = i });
        });

        feed.Publish("test", new { n = 4 });
        feed.Publish("test", new { n = 5 });
        var again = await Collect(reader, s => Events(s, "test") == 2);

        Assert.Equal([4, 5], again.Where(x => x.Kind == FeedSignalKind.Event).Select(x => x.Event!.Data.GetProperty("n").GetInt32()));
    }

    [Fact]
    public async Task ForeignIdYieldsGap()
    {
        using var bot = new BotFixture();
        var reader = Reader(bot.FeedClient());
        reader.LastId = "1:5";

        var signals = await Collect(reader, s => s.Any(x => x.Kind == FeedSignalKind.Event));

        Assert.Equal([FeedSignalKind.Connected, FeedSignalKind.Gap, FeedSignalKind.Event], signals.Select(x => x.Kind));
        Assert.Equal(FeedTypes.ServerStarted, signals[2].Event!.Type);
    }

    [Fact]
    public async Task RetriesWhileServerIsDown()
    {
        using var bot = new BotFixture();
        int calls = 0;
        var reader = Reader(() => ++calls <= 2 ? new HttpClient(new DownHandler()) { BaseAddress = new Uri("https://rimlink:25560/api/admin/") } : bot.FeedClient()());

        var signals = await Collect(reader, s => s.Any(x => x.Kind == FeedSignalKind.Connected));

        Assert.Equal([FeedSignalKind.Disconnected, FeedSignalKind.Disconnected, FeedSignalKind.Connected], signals.Select(x => x.Kind));
    }

    [Fact]
    public async Task PingsAreNotEvents()
    {
        using var bot = new BotFixture(f => f.WithSetting("Admin:FeedPingSeconds", "1"));
        var reader = Reader(bot.FeedClient());
        var signals = new List<FeedSignal>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await foreach (var signal in reader.Run(cts.Token))
            {
                signals.Add(signal);
                if (signal.Kind == FeedSignalKind.Connected) cts.CancelAfter(TimeSpan.FromSeconds(2.5)); // two pings go by after the connect
            }
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Equal([FeedSignalKind.Connected], signals.Select(x => x.Kind));
    }
}
