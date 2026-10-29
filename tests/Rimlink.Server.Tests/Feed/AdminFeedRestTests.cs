using System.Net;
using System.Net.ServerSentEvents;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.Feed;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Feed;

public class AdminFeedRestTests
{
    private const string ChatToken = "chat-token-0123456789abcdef";

    private static ServerFixture Server() => new ServerFixture()
        .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken)
        .WithToken(0, "chatter", ChatToken, "chat");

    /// <summary>Reads SSE items (pings included) until <paramref name="until"/> holds or 10 s pass.</summary>
    private static async Task<List<SseItem<string>>> Read(HttpClient client, string url, Func<List<SseItem<string>>, bool> until, string? lastEventId = null, Action<List<SseItem<string>>>? onItem = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (lastEventId is not null) request.Headers.Add("Last-Event-ID", lastEventId);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var items = new List<SseItem<string>>();
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(cts.Token))
        {
            items.Add(item);
            onItem?.Invoke(items);
            if (until(items)) break;
        }
        return items;
    }

    [Fact]
    public async Task EventsNeedTheToken()
    {
        using var fixture = Server();

        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.CreateClient().GetAsync("/api/admin/events")).StatusCode);
    }

    [Fact]
    public async Task EventsNeedReadScope()
    {
        using var fixture = Server();

        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Rest(ChatToken).GetAsync("/api/admin/events")).StatusCode);
    }

    [Fact]
    public async Task StreamDeliversEventsWithIds()
    {
        using var fixture = Server();
        var feed = fixture.Services.GetRequiredService<AdminFeed>();
        var client = fixture.Rest();

        // Publish once the stream is open (its first item is the immediate ping): earlier, the subscription may not exist yet.
        var items = await Read(client, "/api/admin/events", items => items.Any(i => i.EventType == "test"),
            onItem: items => { if (items.Count == 1) feed.Publish("test", new { n = 1 }); });

        var ev = items.Single(i => i.EventType == "test");
        Assert.Matches(@"^\d+:\d+$", ev.EventId!);
        Assert.Equal("{\"n\":1}", ev.Data);
        Assert.Equal("ping", items[0].EventType); // the immediate ping after the (empty) backlog
    }

    [Fact]
    public async Task LastEventIdResumes()
    {
        using var fixture = Server();
        var feed = fixture.Services.GetRequiredService<AdminFeed>();
        var first = feed.Publish("test", new { n = 1 })!;
        feed.Publish("test", new { n = 2 });

        var items = await Read(fixture.Rest(), "/api/admin/events", items => items.Any(i => i.EventType == "ping"), lastEventId: first.Id);

        var events = items.Where(i => i.EventType == "test").ToList();
        Assert.Equal(["{\"n\":2}"], events.Select(e => e.Data));
    }

    [Fact]
    public async Task GapComesFirstForAForeignId()
    {
        using var fixture = Server();

        var items = await Read(fixture.Rest(), "/api/admin/events?after=1:1", items => items.Any(i => i.EventType == "ping"));

        Assert.Equal("gap", items[0].EventType);
        Assert.Equal(FeedTypes.ServerStarted, items[1].EventType);
    }

    [Fact]
    public async Task PingArrives()
    {
        using var fixture = Server().WithSetting("Admin:FeedPingSeconds", "1");

        var items = await Read(fixture.Rest(), "/api/admin/events", items => items.Count(i => i.EventType == "ping") >= 2);

        Assert.True(items.Count(i => i.EventType == "ping") >= 2);
        Assert.All(items, i => Assert.Null(i.EventId));
    }

    [Fact]
    public void BufferSizeOutOfRangeStopsTheServer()
    {
        using var fixture = Server().WithSetting("Admin:FeedBufferSize", "10");

        Assert.Contains("FeedBufferSize", Assert.ThrowsAny<Exception>(() => fixture.CreateClient()).ToString());
    }
}
