using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostClockTests
{
    private static readonly DateTime Start = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Running_process_ignores_wall_clock_steps()
    {
        var time = new ManualTimeProvider { Now = new(Start) };
        var clock = new OutpostClock(time);
        var first = clock.Sample();
        var checkpoint = At(first);
        time.Now += TimeSpan.FromDays(5);
        time.AdvanceMonotonic(TimeSpan.FromMinutes(1));
        var second = clock.Sample();
        Assert.Equal(Start.AddMinutes(1), second.Utc);
        Assert.Equal(TimeSpan.FromMinutes(1), OutpostClock.Elapsed(checkpoint, second));
        time.Now -= TimeSpan.FromDays(10);
        time.AdvanceMonotonic(TimeSpan.FromMinutes(1));
        Assert.Equal(Start.AddMinutes(2), clock.Sample().Utc);
    }

    [Fact]
    public void Restart_accrues_downtime_once_then_uses_monotonic_time()
    {
        var old = new ProductionCheckpoint(Start, "previous-process", 123, 10);
        var time = new ManualTimeProvider { Now = new(Start.AddDays(1)) };
        var clock = new OutpostClock(time);
        var now = clock.Sample();
        Assert.NotEqual(old.ProcessId, now.ProcessId);
        Assert.Equal(TimeSpan.FromDays(1), OutpostClock.Elapsed(old, now));
        var advanced = OutpostClock.Advance(old, now);
        Assert.Equal(TimeSpan.Zero, OutpostClock.Elapsed(advanced, now));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromMinutes(1), OutpostClock.Elapsed(advanced, clock.Sample()));
    }

    [Fact]
    public void Backward_clock_never_moves_checkpoint_back()
    {
        var old = new ProductionCheckpoint(Start, "previous", 100, 10);
        var backward = new ClockSample(Start.AddHours(-1), "next", 0, 10);
        Assert.Equal(TimeSpan.Zero, OutpostClock.Elapsed(old, backward));
        Assert.Equal(old, OutpostClock.Advance(old, backward));
        var caughtUp = backward with { Utc = Start.AddMinutes(1), Timestamp = 36010 };
        Assert.Equal(TimeSpan.FromMinutes(1), OutpostClock.Elapsed(old, caughtUp));
        var advanced = OutpostClock.Advance(old, caughtUp);
        Assert.Equal(TimeSpan.Zero, OutpostClock.Elapsed(advanced, caughtUp));
    }

    [Fact]
    public void Backward_monotonic_sample_cannot_recredit_already_counted_time()
    {
        var old = new ProductionCheckpoint(Start, "same", 100, 10);
        var earlier = new ClockSample(Start.AddSeconds(-1), "same", 90, 10);
        Assert.Equal(TimeSpan.Zero, OutpostClock.Elapsed(old, earlier));
        Assert.Equal(old, OutpostClock.Advance(old, earlier));
        var later = earlier with { Utc = Start.AddSeconds(1), Timestamp = 110 };
        Assert.Equal(TimeSpan.FromSeconds(1), OutpostClock.Elapsed(old, later));
    }

    [Fact]
    public void Clock_subtick_rounding_is_partition_independent()
    {
        var checkpoint = new ProductionCheckpoint(Start, "same", 0, 3);
        var total = TimeSpan.Zero;
        for (var timestamp = 1; timestamp <= 3; timestamp++)
        {
            var sample = new ClockSample(Start.AddTicks(timestamp * TimeSpan.TicksPerSecond / 3), "same", timestamp, 3);
            total += OutpostClock.Elapsed(checkpoint, sample);
            checkpoint = OutpostClock.Advance(checkpoint, sample);
        }
        Assert.Equal(TimeSpan.FromSeconds(1), total);
    }

    [Fact]
    public void Archive_and_restore_have_no_catchup()
    {
        var first = new ClockSample(Start, "a", 0, 10);
        var paused = OutpostClock.Pause(At(first), first);
        var later = new ClockSample(Start.AddDays(100), "b", 500, 10);
        Assert.Equal(TimeSpan.Zero, OutpostClock.Elapsed(paused, later));
        var resumed = OutpostClock.Resume(paused, later);
        Assert.Equal(TimeSpan.Zero, OutpostClock.Elapsed(resumed, later));
        var next = later with { Utc = later.Utc.AddSeconds(1), Timestamp = 510 };
        Assert.Equal(TimeSpan.FromSeconds(1), OutpostClock.Elapsed(resumed, next));
        // Bulk restore explicitly starts a new boundary instead of reusing the backup checkpoint.
        var restoreBoundary = At(later);
        Assert.Equal(TimeSpan.Zero, OutpostClock.Elapsed(restoreBoundary, later));
        Assert.Equal(TimeSpan.FromSeconds(1), OutpostClock.Elapsed(restoreBoundary, next));
    }

    [Fact]
    public void Very_long_intervals_are_bounded_without_arithmetic_overflow()
    {
        var old = new ProductionCheckpoint(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), "same", 0, 1);
        var now = new ClockSample(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), "same", long.MaxValue, 1);
        Assert.Equal(TimeSpan.MaxValue, OutpostClock.Elapsed(old, now));
    }

    [Fact]
    public void Invalid_clock_coordinates_are_refused_instead_of_falling_back_to_wall_time()
    {
        var old = new ProductionCheckpoint(Start, "same", 0, 10);
        Assert.Throws<ArgumentException>(() => OutpostClock.Elapsed(old, new(Start.AddHours(1), "same", 10, 0)));
        Assert.Throws<ArgumentException>(() => OutpostClock.Elapsed(old, new(Start.AddHours(1), "same", 10, 11)));
        Assert.Throws<ArgumentException>(() => OutpostClock.Elapsed(old, new(DateTime.SpecifyKind(Start, DateTimeKind.Local), "other", 10, 10)));
    }

    private static ProductionCheckpoint At(ClockSample sample) => new(sample.Utc, sample.ProcessId, sample.Timestamp, sample.Frequency);
}
