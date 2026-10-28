using System.Collections.Concurrent;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.State;

/// <summary>Hands events from the network thread to the game's main thread: enqueue anywhere, drain once per frame.</summary>
public sealed class EventInbox
{
    private readonly ConcurrentQueue<ServerEvent> queue = new();

    public int Count => queue.Count;

    public void Enqueue(ServerEvent ev) => queue.Enqueue(ev);

    /// <summary>Applies up to <paramref name="max"/> queued events in order; returns how many were applied.</summary>
    public int DrainTo(Action<ServerEvent> apply, int max = 256)
    {
        int applied = 0;
        while (applied < max && queue.TryDequeue(out var ev))
        {
            apply(ev);
            applied++;
        }
        return applied;
    }

    public void Clear() => queue.Clear();
}
