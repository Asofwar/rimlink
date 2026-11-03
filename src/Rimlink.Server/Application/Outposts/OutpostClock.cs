using System.Numerics;

namespace Rimlink.Server.Application.Outposts;

public sealed record ProductionCheckpoint(DateTime Utc, string ProcessId, long Timestamp, long Frequency, bool Paused = false);
public sealed record ClockSample(DateTime Utc, string ProcessId, long Timestamp, long Frequency);

/// <summary>A singleton UTC-start/monotonic pair, never a fresh wall-clock read for an ongoing interval.</summary>
public sealed class OutpostClock(TimeProvider time)
{
    private readonly long startedTimestamp = time.GetTimestamp();
    private readonly DateTime startedUtc = time.GetUtcNow().UtcDateTime;
    private readonly long frequency = time.TimestampFrequency;
    private readonly string processId = Guid.NewGuid().ToString("N");

    public ClockSample Sample()
    {
        if (frequency <= 0 || time.TimestampFrequency != frequency)
            throw new ArgumentException("Invalid or changed monotonic frequency.", nameof(time));
        var timestamp = time.GetTimestamp();
        var elapsed = BigInteger.Max(0, Quantize(timestamp, frequency) - Quantize(startedTimestamp, frequency));
        var ticks = (long)BigInteger.Min(DateTime.MaxValue.Ticks, startedUtc.Ticks + elapsed);
        return new(new(ticks, DateTimeKind.Utc), processId, timestamp, frequency);
    }

    public static TimeSpan Elapsed(ProductionCheckpoint checkpoint, ClockSample now)
    {
        Validate(checkpoint.Utc, checkpoint.ProcessId, checkpoint.Frequency);
        Validate(now.Utc, now.ProcessId, now.Frequency);
        BigInteger ticks;
        if (checkpoint.ProcessId == now.ProcessId)
        {
            if (checkpoint.Frequency != now.Frequency) throw new ArgumentException("Monotonic frequency changed within one process.", nameof(now));
            // Quantize endpoints, not each delta: splitting an interval cannot drop sub-TimeSpan ticks.
            ticks = Quantize(now.Timestamp, now.Frequency) - Quantize(checkpoint.Timestamp, checkpoint.Frequency);
        }
        else ticks = now.Utc.Ticks - checkpoint.Utc.Ticks;
        return checkpoint.Paused ? TimeSpan.Zero : TimeSpan.FromTicks((long)BigInteger.Clamp(ticks, 0, long.MaxValue));
    }

    /// <summary>Called only after settling the old roster/profile in the same protected transaction.</summary>
    public static ProductionCheckpoint Advance(ProductionCheckpoint checkpoint, ClockSample now)
    {
        _ = Elapsed(checkpoint, now); // Validate even paused/no-yield coordinates.
        if (checkpoint.ProcessId == now.ProcessId ? now.Timestamp < checkpoint.Timestamp : now.Utc < checkpoint.Utc)
            return checkpoint;
        return new(now.Utc < checkpoint.Utc ? checkpoint.Utc : now.Utc, now.ProcessId, now.Timestamp, now.Frequency, checkpoint.Paused);
    }

    /// <summary>Lifecycle pauses only after settling; an archived interval is never production time.</summary>
    public static ProductionCheckpoint Pause(ProductionCheckpoint checkpoint, ClockSample now) => Advance(checkpoint, now) with { Paused = true };

    public static ProductionCheckpoint Resume(ProductionCheckpoint checkpoint, ClockSample now) => Advance(checkpoint, now) with { Paused = false };

    private static void Validate(DateTime utc, string processId, long frequency)
    {
        if (utc.Kind != DateTimeKind.Utc || string.IsNullOrWhiteSpace(processId) || processId.Length > 128 || frequency <= 0)
            throw new ArgumentException("Invalid persisted clock coordinates.");
    }

    private static BigInteger Quantize(long timestamp, long frequency) => (BigInteger)timestamp * TimeSpan.TicksPerSecond / frequency;
}
