using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Feed;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Rest;

/// <summary>GET /api/admin/events: the admin feed as Server-Sent Events, resumable by <c>Last-Event-ID</c> or <c>?after=</c>.</summary>
public static class AdminFeedEndpoint
{
    public static void MapAdminFeedApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin").RequireAdmin();

        group.MapGet("/events", (HttpContext ctx, AdminFeed feed, IOptions<AdminOptions> options, [FromQuery] string? after = null) =>
        {
            string? from = ctx.Request.Headers["Last-Event-ID"] is { Count: > 0 } header ? header.ToString() : after;
            var subscription = feed.Subscribe(from);
            ctx.Response.RegisterForDispose(subscription); // also when the stream is never enumerated
            return TypedResults.ServerSentEvents(Stream(subscription, from, TimeSpan.FromSeconds(options.Value.FeedPingSeconds), ctx.RequestAborted));
        }).RequireScope(ApiScope.Read);
    }

    private static async IAsyncEnumerable<SseItem<string>> Stream(FeedSubscription subscription, string? from, TimeSpan ping, [EnumeratorCancellation] CancellationToken ct)
    {
        if (subscription.Gap) yield return new SseItem<string>(JsonSerializer.Serialize(new { from }), "gap");
        foreach (var ev in subscription.Backlog) yield return Item(ev);
        // At once, so the client sees the response (and counts itself connected) without waiting for the first live event.
        yield return new SseItem<string>("", "ping");

        while (true)
        {
            bool more = false, idle = false;
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                wait.CancelAfter(ping);
                try
                {
                    more = await subscription.Live.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    idle = true;
                }
            }
            if (idle)
            {
                yield return new SseItem<string>("", "ping");
                continue;
            }
            if (!more) yield break; // cut off for falling behind: the client reconnects with its last id
            while (subscription.Live.TryRead(out var ev)) yield return Item(ev);
        }
    }

    private static SseItem<string> Item(FeedEvent ev) => new(ev.DataJson, ev.Type) { EventId = ev.Id };
}
