using System.Collections.Concurrent;
using Rimlink.Discord.Text;

namespace Rimlink.Discord.Commands;

/// <summary>
/// Pending confirmations of irreversible commands: only the member who ran the command can press, within <see cref="Lifetime"/>,
/// and a confirmation runs at most once however many presses race.
/// </summary>
public sealed class Confirmations(TimeProvider time, Strings s)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private sealed record Entry(ulong Owner, DateTimeOffset At, Func<Task<string>> Action);

    private readonly ConcurrentDictionary<string, Entry> pending = new();

    public Confirmation Create(Caller caller, string description, Func<Task<string>> action)
    {
        var now = time.GetUtcNow();
        foreach (var (key, old) in pending)
            if (now - old.At > Lifetime) pending.TryRemove(key, out _);

        string id = Guid.NewGuid().ToString("N");
        pending[id] = new Entry(caller.UserId, now, action);
        return new Confirmation(id, description);
    }

    public async Task<Reply> Press(string id, ulong userId, bool confirm)
    {
        if (!pending.TryGetValue(id, out var entry)) return Ephemeral("cmd.confirm.expired");
        if (time.GetUtcNow() - entry.At > Lifetime)
        {
            pending.TryRemove(id, out _);
            return Ephemeral("cmd.confirm.expired");
        }
        if (entry.Owner != userId) return Ephemeral("cmd.confirm.notyours");
        if (!pending.TryRemove(new KeyValuePair<string, Entry>(id, entry))) return Ephemeral("cmd.confirm.expired"); // another press won
        return confirm ? new Reply(await entry.Action(), true) : Ephemeral("cmd.confirm.cancelled");
    }

    private Reply Ephemeral(string key) => new(s.T(key), true);
}
