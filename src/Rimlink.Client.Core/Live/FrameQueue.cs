using Rimlink.Protocol.V1;

namespace Rimlink.Client.Live;

/// <summary>
/// Frames received from the stream, drained by the game's main thread once per frame. Bounded: when the game
/// falls behind, the oldest queued deltas are dropped (the next one supersedes them); other frames are kept.
/// </summary>
public sealed class FrameQueue(int capacity = 256)
{
    private readonly LinkedList<LiveServerMsg> frames = new();
    private readonly object gate = new();

    public int Count { get { lock (gate) return frames.Count; } }

    public void Enqueue(LiveServerMsg msg)
    {
        lock (gate)
        {
            if (frames.Count >= capacity)
            {
                var oldest = frames.First;
                while (oldest is not null && oldest.Value.MCase != LiveServerMsg.MOneofCase.Delta) oldest = oldest.Next;
                if (oldest is not null) frames.Remove(oldest);
                else if (msg.MCase == LiveServerMsg.MOneofCase.Delta) return;
            }
            frames.AddLast(msg);
        }
    }

    public bool TryDequeue(out LiveServerMsg? msg)
    {
        lock (gate)
        {
            if (frames.First is { } first) { frames.RemoveFirst(); msg = first.Value; return true; }
            msg = null;
            return false;
        }
    }

    public void Clear() { lock (gate) frames.Clear(); }
}
