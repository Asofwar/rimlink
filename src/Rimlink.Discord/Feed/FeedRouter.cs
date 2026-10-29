using Rimlink.Discord.Api;
using Rimlink.Discord.Text;

namespace Rimlink.Discord.Feed;

/// <summary>
/// Turns feed signals into Discord lines: each event to its group's channel, chat lines to the chat bridge, a gap to the
/// moderation channel, and one "server down" / "server back" pair per outage longer than <see cref="DownAfter"/>.
/// </summary>
public sealed class FeedRouter(ChannelMap channels, Formatter formatter, OutboundQueue queue, Strings s, TimeProvider time, Action<FeedEvent> chat)
{
    public static readonly TimeSpan DownAfter = TimeSpan.FromSeconds(60);

    /// <summary>After a reconnect with no event, the "back" line still comes after this long.</summary>
    public static readonly TimeSpan UpAfterQuiet = TimeSpan.FromSeconds(5);

    private readonly object gate = new();
    private DateTimeOffset? downSince;
    private DateTimeOffset? upPendingSince;
    private bool announcedDown;

    public void Handle(FeedSignal signal)
    {
        FeedEvent? forChat = null;
        lock (gate)
        {
            switch (signal.Kind)
            {
                case FeedSignalKind.Disconnected:
                    downSince ??= time.GetUtcNow();
                    upPendingSince = null;
                    break;
                case FeedSignalKind.Connected:
                    downSince = null;
                    if (announcedDown)
                    {
                        announcedDown = false;
                        upPendingSince = time.GetUtcNow();
                    }
                    break;
                case FeedSignalKind.Gap:
                    queue.Enqueue(channels.Moderation, s.T("feed.gap"));
                    break;
                case FeedSignalKind.Event when signal.Event is { } e:
                    if (upPendingSince is not null)
                    {
                        // A restarted server announces itself; anything else after an outage gets the "back" line first.
                        upPendingSince = null;
                        if (e.Type != FeedTypes.ServerStarted) queue.Enqueue(channels.Server, s.T("feed.server.up"));
                    }
                    if (e.Type == FeedTypes.ChatMessage)
                    {
                        forChat = e;
                        break;
                    }
                    if (FeedTypes.GroupOf(e.Type) is { } group && formatter.Format(e) is { } line) queue.Enqueue(channels.For(group), line);
                    break;
            }
        }
        if (forChat is not null) chat(forChat);
    }

    /// <summary>Called about once a second: announces a long outage, and a reconnect nothing followed.</summary>
    public void Tick()
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (downSince is { } since && !announcedDown && now - since >= DownAfter)
            {
                announcedDown = true;
                queue.Enqueue(channels.Server, s.T("feed.server.down"));
            }
            if (upPendingSince is { } connected && now - connected >= UpAfterQuiet)
            {
                upPendingSince = null;
                queue.Enqueue(channels.Server, s.T("feed.server.up"));
            }
        }
    }
}
