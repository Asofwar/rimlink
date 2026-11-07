using System.Text;
using Rimlink.OutpostAcceptanceHost;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAcceptanceClockRunTests
{
    [Fact]
    public void Journal_records_real_samples_and_exact_step_replay_without_a_second_advance()
    {
        var storage = new MemoryStream();
        using var time = new AcceptanceClock(new FixedTime(), FixedTime.Utc, new Proof());
        using var run = new AcceptanceClockRun(Identity(), time, storage);
        var actual = new OutpostClock(time); run.Bind(actual);
        string id = Guid.NewGuid().ToString("D");
        var step = run.Advance(id, TimeSpan.FromHours(20), "offline capacity");
        string first = Encoding.UTF8.GetString(storage.ToArray());
        Assert.Contains("Origin", first); Assert.Contains("Sample", first); Assert.Contains("Planned", first); Assert.Contains("Applied", first);
        Assert.Contains(id, first);
        Assert.Equal(step, run.Advance(id, TimeSpan.FromHours(20), "offline capacity"));
        Assert.Equal(first, Encoding.UTF8.GetString(storage.ToArray()));
        Assert.Equal(step.After.Utc.UtcDateTime, actual.Sample().Utc);
        Assert.False(run.Failed);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void A_failed_journal_fences_the_run_and_never_reports_the_step_as_no_effect(bool failAfterAdvance)
    {
        var storage = new FailingStream();
        using var time = new AcceptanceClock(new FixedTime(), FixedTime.Utc, new Proof());
        using var run = new AcceptanceClockRun(Identity(), time, storage);
        var actual = new OutpostClock(time); run.Bind(actual);
        var before = time.Read();
        storage.WritesUntilFailure = failAfterAdvance ? 1 : 0;
        Assert.Throws<IOException>(() => run.Advance(Guid.NewGuid().ToString("D"), TimeSpan.FromHours(1), "fault"));
        Assert.True(run.Failed);
        Assert.Equal(failAfterAdvance ? before.Utc.AddHours(1) : before.Utc, time.Read().Utc);
        Assert.ThrowsAny<Exception>(() => run.Advance(Guid.NewGuid().ToString("D"), TimeSpan.FromHours(1), "no further writes"));
    }

    private static AcceptanceRunIdentity Identity() => new(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Environment.ProcessId, OutpostProductQualification.SourceIdentity);
    private sealed class FixedTime : TimeProvider
    {
        internal static readonly DateTimeOffset Utc = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => 0;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
    private sealed class Proof : IOutpostBuildQualification
    {
        public OutpostBuildQualification Observe() => new(OutpostRuntimeMode.IsolatedAcceptance, OutpostQualificationState.Pending,
            OutpostProductQualification.SourceIdentity, true, true);
    }
    private sealed class FailingStream : MemoryStream
    {
        internal int WritesUntilFailure = int.MaxValue;
        public override void Write(byte[] buffer, int offset, int count)
        { if (WritesUntilFailure-- == 0) throw new IOException("owned journal fault"); base.Write(buffer, offset, count); }
    }
}
