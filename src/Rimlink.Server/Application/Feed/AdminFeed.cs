using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Prometheus;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Application.Feed;

/// <summary>
/// The admin event feed (Discord bot spec §2): a ring buffer of the last <see cref="AdminOptions.FeedBufferSize"/> events and
/// a bounded channel per subscriber. Publishing never blocks or throws; a subscriber that falls behind is cut off and resumes
/// from the buffer by its last id.
/// </summary>
public sealed class AdminFeed
{
    private static readonly Gauge Subscribers = Metrics.CreateGauge("rimlink_admin_feed_subscribers", "Open admin feed subscriptions");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly object gate = new();
    private readonly Queue<FeedEvent> buffer = new();
    private readonly List<Channel<FeedEvent>> subscribers = [];
    private readonly int capacity;
    private readonly TimeProvider time;
    private readonly ILogger<AdminFeed> log;
    private long seq;

    public AdminFeed(IOptions<AdminOptions> options, TimeProvider time, ILogger<AdminFeed> log)
    {
        capacity = options.Value.FeedBufferSize;
        this.time = time;
        this.log = log;
        Epoch = time.GetUtcNow().ToUnixTimeSeconds();
        Publish(FeedTypes.ServerStarted, new ServerStartedData(ServerVersion.Current));
    }

    /// <summary>Unix seconds when this server process started its feed; ids of an older process carry another epoch.</summary>
    public long Epoch { get; }

    public string LastId
    {
        get { lock (gate) return Id(seq); }
    }

    public int SubscriberCount
    {
        get { lock (gate) return subscribers.Count; }
    }

    public FeedEvent? Publish(string type, object data)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(data, data.GetType(), Web);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Admin feed event {Type} could not be serialized", type);
            return null;
        }

        lock (gate)
        {
            var ev = new FeedEvent(Id(++seq), seq, time.GetUtcNow().UtcDateTime, type, json);
            buffer.Enqueue(ev);
            while (buffer.Count > capacity) buffer.Dequeue();
            for (int i = subscribers.Count - 1; i >= 0; i--)
            {
                if (subscribers[i].Writer.TryWrite(ev)) continue;
                subscribers[i].Writer.TryComplete();
                subscribers.RemoveAt(i);
                log.LogWarning("Admin feed subscriber fell behind and was cut off");
            }
            Subscribers.Set(subscribers.Count);
            return ev;
        }
    }

    /// <summary>
    /// Subscribes after <paramref name="after"/> (an event id): null → only new events; an id of another epoch, a malformed one or
    /// one older than the buffer → <see cref="FeedSubscription.Gap"/> and the whole buffer. Snapshot and registration happen under
    /// the publish lock, so no event is lost or delivered twice in between.
    /// </summary>
    public FeedSubscription Subscribe(string? after)
    {
        var channel = Channel.CreateBounded<FeedEvent>(new BoundedChannelOptions(capacity) { SingleReader = true, SingleWriter = false });
        lock (gate)
        {
            bool gap = false;
            List<FeedEvent> backlog = [];
            if (after is not null)
            {
                long oldest = buffer.Count > 0 ? buffer.Peek().Seq : seq + 1;
                if (TryParse(after, out long epoch, out long from) && epoch == Epoch && from >= oldest - 1 && from <= seq)
                {
                    backlog = buffer.Where(e => e.Seq > from).ToList();
                }
                else
                {
                    gap = true;
                    backlog = buffer.ToList();
                }
            }
            subscribers.Add(channel);
            Subscribers.Set(subscribers.Count);
            return new FeedSubscription(this, channel, gap, backlog);
        }
    }

    internal void Remove(Channel<FeedEvent> channel)
    {
        lock (gate)
        {
            if (subscribers.Remove(channel)) channel.Writer.TryComplete();
            Subscribers.Set(subscribers.Count);
        }
    }

    private string Id(long n) => $"{Epoch}:{n}";

    private static bool TryParse(string id, out long epoch, out long seq)
    {
        epoch = seq = 0;
        int colon = id.IndexOf(':');
        return colon > 0
            && long.TryParse(id.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out epoch)
            && long.TryParse(id.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out seq);
    }
}

/// <summary>An open feed subscription: <see cref="Backlog"/> first, then <see cref="Live"/>. Dispose to unsubscribe.</summary>
public sealed class FeedSubscription : IDisposable
{
    private readonly AdminFeed feed;
    private readonly Channel<FeedEvent> channel;

    internal FeedSubscription(AdminFeed feed, Channel<FeedEvent> channel, bool gap, IReadOnlyList<FeedEvent> backlog)
    {
        this.feed = feed;
        this.channel = channel;
        Gap = gap;
        Backlog = backlog;
    }

    public bool Gap { get; }

    public IReadOnlyList<FeedEvent> Backlog { get; }

    public ChannelReader<FeedEvent> Live => channel.Reader;

    public void Dispose() => feed.Remove(channel);
}
