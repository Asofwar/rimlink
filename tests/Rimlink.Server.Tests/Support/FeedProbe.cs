using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.Feed;

namespace Rimlink.Server.Tests.Support;

/// <summary>An in-process subscriber to the admin feed, positioned at the moment it was created.</summary>
public sealed class FeedProbe(FeedSubscription subscription) : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The next event of <paramref name="type"/>; other types are skipped. Throws after the timeout (default 10 s).</summary>
    public async Task<FeedEvent> Next(string type, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var ev = await subscription.Live.ReadAsync(cts.Token);
            if (ev.Type == type) return ev;
        }
    }

    /// <summary>Every event that arrives until none came for <paramref name="quiet"/>.</summary>
    public async Task<List<FeedEvent>> Drain(TimeSpan quiet)
    {
        var events = new List<FeedEvent>();
        while (true)
        {
            using var cts = new CancellationTokenSource(quiet);
            try
            {
                events.Add(await subscription.Live.ReadAsync(cts.Token));
            }
            catch (OperationCanceledException)
            {
                return events;
            }
        }
    }

    public static T Data<T>(FeedEvent ev) => JsonSerializer.Deserialize<T>(ev.DataJson, Web)!;

    public static JsonElement Data(FeedEvent ev) => JsonDocument.Parse(ev.DataJson).RootElement;

    public void Dispose() => subscription.Dispose();
}

public static class FeedProbeExtensions
{
    public static FeedProbe Probe(this ServerFixture fixture)
    {
        var feed = fixture.Services.GetRequiredService<AdminFeed>();
        return new FeedProbe(feed.Subscribe(feed.LastId));
    }
}
