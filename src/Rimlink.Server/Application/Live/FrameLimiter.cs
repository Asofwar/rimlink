using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Application.Live;

/// <summary>
/// Per-stream frame limits (spec §3): a size cap for every frame, token buckets for deltas and orders, and for resync requests
/// (each one makes the host save and upload: two in a burst, then one per ten seconds); heartbeats and the rest are never rate-limited.
/// </summary>
public sealed class FrameLimiter(LiveOptions options)
{
    private readonly Bucket deltas = new(options.DeltasPerSecond, options.DeltasPerSecond);
    private readonly Bucket orders = new(options.OrdersPerSecond, options.OrdersPerSecond);
    private readonly Bucket resyncs = new(0.1, 2);

    /// <summary>Null when the frame may pass; otherwise the reason it is dropped.</summary>
    public string? Refuse(LiveClientMsg.MOneofCase kind, int size, DateTime now)
    {
        if (size > options.MaxFrameBytes) return $"frame of {size} bytes exceeds the limit of {options.MaxFrameBytes}";
        return kind switch
        {
            LiveClientMsg.MOneofCase.Delta => deltas.Take(now) ? null : $"more than {options.DeltasPerSecond} deltas per second",
            LiveClientMsg.MOneofCase.Order => orders.Take(now) ? null : $"more than {options.OrdersPerSecond} orders per second",
            LiveClientMsg.MOneofCase.ResyncRequest => resyncs.Take(now) ? null : "more than one resync request per ten seconds",
            _ => null,
        };
    }

    private sealed class Bucket(double perSecond, double capacity)
    {
        private double tokens = capacity;
        private DateTime last = DateTime.MinValue;

        public bool Take(DateTime now)
        {
            if (last != DateTime.MinValue && now > last) tokens = Math.Min(capacity, tokens + (now - last).TotalSeconds * perSecond); // a clock stepped back owes nothing
            last = now;
            if (tokens < 1) return false;
            tokens -= 1;
            return true;
        }
    }
}

/// <summary>
/// A receiver's outgoing queue: at most <see cref="Capacity"/> frames. When full, the oldest queued delta is dropped
/// to make room (a newer delta supersedes it); orders and service frames are never dropped, so the queue may exceed
/// the capacity by the number of such frames waiting.
/// </summary>
public sealed class RelayQueue
{
    public const int Capacity = 64;

    private readonly LinkedList<LiveServerMsg> frames = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly object gate = new();
    private bool completed;

    public int Count { get { lock (gate) return frames.Count; } }

    /// <summary>False when the frame was dropped (a delta that found no older delta to replace, or a completed queue).</summary>
    public bool Enqueue(LiveServerMsg msg)
    {
        lock (gate)
        {
            if (completed) return false;
            if (frames.Count >= Capacity)
            {
                var oldest = frames.First;
                while (oldest is not null && oldest.Value.MCase != LiveServerMsg.MOneofCase.Delta) oldest = oldest.Next;
                if (oldest is not null) { frames.Remove(oldest); signal.Wait(0); }
                else if (msg.MCase == LiveServerMsg.MOneofCase.Delta) return false;
            }
            frames.AddLast(msg);
        }
        signal.Release();
        return true;
    }

    /// <summary>Null once the queue is completed and drained.</summary>
    public async Task<LiveServerMsg?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            await signal.WaitAsync(ct);
            lock (gate)
            {
                if (frames.First is { } first) { frames.RemoveFirst(); return first.Value; }
                if (completed) return null;
            }
        }
    }

    public void Complete()
    {
        lock (gate) completed = true;
        signal.Release();
    }
}
