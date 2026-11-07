using Rimlink.Server.Application.Outposts;
using System.Numerics;
using System.Text;

namespace Rimlink.OutpostAcceptanceHost;

internal sealed record AcceptanceClockReading(DateTimeOffset Utc, long Timestamp, long Frequency);
internal sealed record AcceptanceClockStep(string Id, string Reason, long DurationTicks, AcceptanceClockReading Before, AcceptanceClockReading After);

internal sealed class AcceptanceClock(TimeProvider real, DateTimeOffset startUtc, IOutpostBuildQualification qualification) : TimeProvider, IDisposable
{
    private readonly object gate = new();
    private readonly long origin = real.GetTimestamp();
    private readonly long sourceFrequency = RequireFrequency(real);
    private readonly DateTimeOffset anchor = startUtc.Offset == TimeSpan.Zero ? startUtc : throw new ArgumentException("UTC anchor required", nameof(startUtc));
    private readonly Dictionary<string, AcceptanceClockStep> steps = new(StringComparer.Ordinal);
    private long offset;
    private bool closed;
    private long? firstTimestamp;
    private DateTimeOffset? firstUtc;

    internal (long Timestamp, DateTimeOffset Utc)? Initialization
    {
        get { lock (gate) return firstTimestamp is { } t && firstUtc is { } utc ? (t, utc) : null; }
    }

    internal AcceptanceClockReading Read()
    {
        lock (gate) return Reading(ElapsedRealTicks(), offset);
    }

    internal AcceptanceClockStep Advance(string id, TimeSpan duration, string reason)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            var proof = qualification.Observe();
            if (proof.Mode != OutpostRuntimeMode.IsolatedAcceptance || !proof.AutomatedPrerequisitesVerified
                || !proof.IsolationVerified || proof.SourceIdentity != OutpostProductQualification.SourceIdentity)
                throw new InvalidDataException("acceptance-clock-current-owned-qualification-required");
            if (!Guid.TryParseExact(id, "D", out var parsed) || parsed == Guid.Empty || duration <= TimeSpan.Zero
                || duration > TimeSpan.FromDays(30) || string.IsNullOrWhiteSpace(reason) || Encoding.UTF8.GetByteCount(reason) > 1024)
                throw new InvalidDataException("acceptance-clock-step-invalid");
            if (steps.TryGetValue(id, out var previous))
                return previous.DurationTicks == duration.Ticks && previous.Reason == reason ? previous
                    : throw new InvalidDataException("acceptance-clock-step-id-reused");
            if (steps.Count >= 4096) throw new InvalidDataException("acceptance-clock-step-limit");
            long elapsed = ElapsedRealTicks();
            long next = checked(offset + duration.Ticks);
            var step = new AcceptanceClockStep(id, reason, duration.Ticks, Reading(elapsed, offset), Reading(elapsed, next));
            offset = next;
            steps.Add(id, step);
            return step;
        }
    }

    private AcceptanceClockReading Reading(long elapsed, long shift)
    {
        long ticks = checked(elapsed + shift);
        return new(anchor.AddTicks(ticks), ticks, TimestampFrequency);
    }

    private long ElapsedRealTicks()
    {
        if (real.TimestampFrequency != sourceFrequency) throw new InvalidDataException("acceptance-clock-real-frequency-changed");
        BigInteger delta = (BigInteger)real.GetTimestamp() - origin;
        if (delta < 0) throw new InvalidDataException("acceptance-clock-real-timestamp-regressed");
        return checked((long)(delta * TimeSpan.TicksPerSecond / sourceFrequency));
    }

    private static long RequireFrequency(TimeProvider provider) => provider.TimestampFrequency > 0
        ? provider.TimestampFrequency : throw new InvalidDataException("acceptance-clock-real-frequency-invalid");

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate) { var value = Reading(ElapsedRealTicks(), offset).Utc; firstUtc ??= value; return value; }
    }
    public override long GetTimestamp()
    {
        lock (gate) { var value = Reading(ElapsedRealTicks(), offset).Timestamp; firstTimestamp ??= value; return value; }
    }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    // Simulated steps never trigger transport/system timers. Timer lifetime remains the caller's responsibility.
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => real.CreateTimer(callback, state, dueTime, period);
    public void Dispose() { lock (gate) closed = true; }
}
