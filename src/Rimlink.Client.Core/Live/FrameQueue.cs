using Rimlink.Protocol.V1;

namespace Rimlink.Client.Live;

/// <summary>
/// Frames received from the stream, drained by the game's main thread once per frame. Bounded: when the game
/// falls behind, the oldest queued deltas are dropped; other frames are kept. A delta is a diff against the state
/// the host believes the mirror has, so a dropped one is lost for good (a despawn, a move): the drops are counted
/// for the mirror to ask for a fresh snapshot.
/// </summary>
public sealed class FrameQueue(int capacity = 1024)
{
    private readonly LinkedList<LiveServerMsg> frames = new();
    private readonly object gate = new();
    private int droppedDeltas;

    public int Count { get { lock (gate) return frames.Count; } }

    public void Enqueue(LiveServerMsg msg)
    {
        lock (gate)
        {
            if (frames.Count >= capacity)
            {
                var oldest = frames.First;
                while (oldest is not null && oldest.Value.MCase != LiveServerMsg.MOneofCase.Delta) oldest = oldest.Next;
                if (oldest is not null) { frames.Remove(oldest); droppedDeltas++; }
                else if (msg.MCase == LiveServerMsg.MOneofCase.Delta) { droppedDeltas++; return; }
            }
            frames.AddLast(msg);
        }
    }

    /// <summary>Deltas dropped since the last call (the mirror no longer matches the host when this is not zero).</summary>
    public int TakeDroppedDeltas()
    {
        lock (gate)
        {
            int n = droppedDeltas;
            droppedDeltas = 0;
            return n;
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

    public void Clear() { lock (gate) { frames.Clear(); droppedDeltas = 0; } }
}
