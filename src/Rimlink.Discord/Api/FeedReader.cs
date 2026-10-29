using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Rimlink.Discord.Api;

public enum FeedSignalKind { Connected, Disconnected, Gap, Event }

public sealed record FeedSignal(FeedSignalKind Kind, FeedEvent? Event = null);

public sealed class FeedReaderOptions
{
    /// <summary>Waits between reconnects; the last one repeats. Resets after a successful connect.</summary>
    public TimeSpan[] Backoff { get; init; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30)];

    /// <summary>No item (the server pings every 15 s) for this long means a dead connection.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(45);
}

/// <summary>
/// Reads the admin feed (<c>GET events</c>, Server-Sent Events) forever: resumes by <see cref="LastId"/>, reconnects with backoff,
/// and reports connection changes and gaps as signals next to the events.
/// </summary>
public sealed class FeedReader(Func<HttpClient> client, FeedReaderOptions options, ILogger<FeedReader> log)
{
    /// <summary>The id of the last event read; sent as <c>Last-Event-ID</c> on the next connect.</summary>
    public string? LastId { get; set; }

    public async IAsyncEnumerable<FeedSignal> Run([EnumeratorCancellation] CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var (response, error) = await Connect(ct);
            if (ct.IsCancellationRequested) yield break;
            if (response is not null)
            {
                failures = 0;
                yield return new FeedSignal(FeedSignalKind.Connected);
                using (response)
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idle.CancelAfter(options.IdleTimeout);
                    var stream = await response.Content.ReadAsStreamAsync(ct);
                    await using var items = SseParser.Create(stream).EnumerateAsync(idle.Token).GetAsyncEnumerator(idle.Token);
                    while (true)
                    {
                        var (has, readError) = await Next(items);
                        if (ct.IsCancellationRequested) yield break;
                        if (!has)
                        {
                            if (readError is not null) log.LogWarning("Admin feed read failed: {Error}", readError.Message);
                            break;
                        }
                        idle.CancelAfter(options.IdleTimeout);
                        var item = items.Current;
                        if (item.EventType == "ping") continue;
                        if (item.EventType == "gap")
                        {
                            yield return new FeedSignal(FeedSignalKind.Gap);
                            continue;
                        }
                        if (item.EventId is { } id) LastId = id;
                        yield return new FeedSignal(FeedSignalKind.Event, new FeedEvent(item.EventId, item.EventType, Parse(item.Data)));
                    }
                }
            }
            else
            {
                log.LogWarning("Admin feed connect failed: {Error}", error?.Message);
            }

            yield return new FeedSignal(FeedSignalKind.Disconnected);
            var wait = options.Backoff[Math.Min(failures++, options.Backoff.Length - 1)];
            try
            {
                await Task.Delay(wait, ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    private async Task<(HttpResponseMessage? Response, Exception? Error)> Connect(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "events");
            if (LastId is not null) request.Headers.TryAddWithoutValidation("Last-Event-ID", LastId);
            var response = await client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode) return (response, null);
            response.Dispose();
            return (null, new HttpRequestException($"the server answered {(int)response.StatusCode}"));
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    private static async Task<(bool Has, Exception? Error)> Next(IAsyncEnumerator<SseItem<string>> items)
    {
        try
        {
            return (await items.MoveNextAsync(), null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    private static JsonElement Parse(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
