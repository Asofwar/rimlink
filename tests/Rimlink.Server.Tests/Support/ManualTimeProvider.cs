namespace Rimlink.Server.Tests.Support;

/// <summary>A clock that moves only when a test says so.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public long Timestamp { get; set; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Timestamp;

    public void Advance(TimeSpan by)
    {
        Now += by;
        AdvanceMonotonic(by);
    }

    public void AdvanceMonotonic(TimeSpan by) => Timestamp += by.Ticks;
}
