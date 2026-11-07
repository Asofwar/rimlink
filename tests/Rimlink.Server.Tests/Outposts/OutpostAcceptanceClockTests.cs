using Rimlink.OutpostAcceptanceHost;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAcceptanceClockTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_positive_step_moves_the_real_outpost_sample_on_both_scales_once()
    {
        var real = new ClockSource(); var proof = new Qualification();
        using var time = new AcceptanceClock(real, Start, proof);
        var clock = new OutpostClock(time); var before = clock.Sample();
        string id = Guid.NewGuid().ToString("D");
        var first = time.Advance(id, TimeSpan.FromHours(20), "offline cap scenario");
        var after = clock.Sample();
        Assert.Equal(Start.UtcDateTime, before.Utc);
        Assert.Equal(TimeSpan.FromHours(20), after.Utc - before.Utc);
        Assert.Equal(TimeSpan.FromHours(20).Ticks, after.Timestamp - before.Timestamp);
        Assert.Equal(TimeSpan.TicksPerSecond, time.TimestampFrequency);
        Assert.Equal(first, time.Advance(id, TimeSpan.FromHours(20), "offline cap scenario"));
        Assert.Equal(after, clock.Sample());
        real.Ticks += TimeSpan.FromSeconds(3).Ticks;
        Assert.Equal(after.Utc.AddSeconds(3), clock.Sample().Utc);
        Assert.Equal(Start, real.GetUtcNow()); // The fixture never edits system/global UTC.
    }

    [Theory]
    [InlineData("product")][InlineData("unknown-proof")][InlineData("closed")][InlineData("zero")][InlineData("negative")]
    [InlineData("too-large")][InlineData("changed-step")][InlineData("invalid-id")][InlineData("wrong-source")]
    public void Invalid_unqualified_or_reused_steps_leave_time_unchanged(string defect)
    {
        var proof = new Qualification(); using var time = new AcceptanceClock(new ClockSource(), Start, proof);
        string id = Guid.NewGuid().ToString("D"); TimeSpan duration = TimeSpan.FromHours(1);
        if (defect == "changed-step") time.Advance(id, TimeSpan.FromMinutes(1), "first");
        if (defect == "product") proof.Mode = OutpostRuntimeMode.Product;
        if (defect == "unknown-proof") proof.Verified = false;
        if (defect == "wrong-source") proof.Source = new string('0', 64);
        if (defect == "closed") time.Dispose();
        if (defect == "zero") duration = TimeSpan.Zero;
        if (defect == "negative") duration = TimeSpan.FromSeconds(-1);
        if (defect == "too-large") duration = TimeSpan.MaxValue;
        if (defect == "invalid-id") id = "foreign";
        var before = time.Read();
        Assert.NotNull(Record.Exception(() => time.Advance(id, duration, "test")));
        Assert.Equal(before, time.Read());
    }

    [Fact]
    public void Step_overflow_is_refused_before_the_clock_changes()
    {
        using var time = new AcceptanceClock(new ClockSource(), DateTimeOffset.MaxValue.AddHours(-1), new Qualification());
        var before = time.Read();
        Assert.NotNull(Record.Exception(() => time.Advance(Guid.NewGuid().ToString("D"), TimeSpan.FromHours(2), "overflow")));
        Assert.Equal(before, time.Read());
    }

    [Fact]
    public async Task Simulated_advancement_does_not_accelerate_transport_timers_and_cancellation_still_works()
    {
        using var time = new AcceptanceClock(new ClockSource(), Start, new Qualification());
        using var cancel = new CancellationTokenSource();
        var delay = Task.Delay(TimeSpan.FromHours(1), time, cancel.Token);
        time.Advance(Guid.NewGuid().ToString("D"), TimeSpan.FromDays(1), "test isolated elapsed");
        await Task.Delay(30);
        Assert.False(delay.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delay);
    }

    [Fact]
    public void An_explicit_restart_origin_preserves_elapsed_utc_with_a_new_process_counter()
    {
        using var first = new AcceptanceClock(new ClockSource(), Start, new Qualification());
        var previousClock = new OutpostClock(first);
        first.Advance(Guid.NewGuid().ToString("D"), TimeSpan.FromHours(20), "offline");
        var previous = previousClock.Sample();
        var checkpoint = new ProductionCheckpoint(previous.Utc, previous.ProcessId, previous.Timestamp, previous.Frequency);
        using var next = new AcceptanceClock(new ClockSource(), new DateTimeOffset(previous.Utc).AddHours(2), new Qualification());
        var resumed = new OutpostClock(next).Sample();
        Assert.NotEqual(previous.ProcessId, resumed.ProcessId);
        Assert.Equal(0, resumed.Timestamp);
        Assert.Equal(TimeSpan.FromHours(2), OutpostClock.Elapsed(checkpoint, resumed));
    }

    private sealed class ClockSource : TimeProvider
    {
        internal long Ticks;
        public override DateTimeOffset GetUtcNow() => Start;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
    private sealed class Qualification : IOutpostBuildQualification
    {
        internal OutpostRuntimeMode Mode = OutpostRuntimeMode.IsolatedAcceptance;
        internal bool Verified = true;
        internal string Source = OutpostProductQualification.SourceIdentity;
        public OutpostBuildQualification Observe() => new(Mode, OutpostQualificationState.Pending,
            Source, Verified, Verified);
    }
}
