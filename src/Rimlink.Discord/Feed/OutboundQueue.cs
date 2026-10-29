using Rimlink.Discord.Gateway;

namespace Rimlink.Discord.Feed;

/// <summary>
/// Lines waiting for Discord, per channel. A flush sends at most one message per channel (lines joined up to Discord's 2000
/// characters), which keeps a busy chat within Discord's rate limits; a channel over <c>maxLines</c> drops its oldest lines.
/// </summary>
public sealed class OutboundQueue(IDiscordOut discord, ILogger<OutboundQueue> log, int maxLines = 500)
{
    public const int MessageLimit = 2000;

    private readonly Dictionary<ulong, List<string>> pending = [];
    private int dropped;

    public int Dropped => Volatile.Read(ref dropped);

    public void Enqueue(ulong channelId, string line)
    {
        if (channelId == 0) return;
        if (line.Length > MessageLimit) line = line[..(MessageLimit - 1)] + "…";
        lock (pending)
        {
            if (!pending.TryGetValue(channelId, out var lines)) pending[channelId] = lines = [];
            lines.Add(line);
            int over = lines.Count - maxLines;
            if (over <= 0) return;
            lines.RemoveRange(0, over);
            dropped += over;
        }
        log.LogWarning("Discord channel {Channel} is behind: dropped its oldest line(s)", channelId);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        List<(ulong Channel, List<string> Lines)> batches = [];
        lock (pending)
        {
            foreach (var (channel, lines) in pending)
            {
                int take = 0, length = 0;
                while (take < lines.Count && length + lines[take].Length + (take > 0 ? 1 : 0) <= MessageLimit)
                {
                    length += lines[take].Length + (take > 0 ? 1 : 0);
                    take++;
                }
                if (take == 0) continue;
                batches.Add((channel, lines.GetRange(0, take)));
                lines.RemoveRange(0, take);
            }
        }

        foreach (var (channel, lines) in batches)
        {
            try
            {
                await discord.Send(channel, string.Join('\n', lines), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Sending to Discord channel {Channel} failed; retrying with the next flush", channel);
                lock (pending)
                {
                    if (!pending.TryGetValue(channel, out var rest)) pending[channel] = rest = [];
                    rest.InsertRange(0, lines);
                }
            }
        }
    }
}
