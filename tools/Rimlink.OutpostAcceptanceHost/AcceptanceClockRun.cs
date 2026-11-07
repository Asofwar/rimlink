using Rimlink.Server.Application.Outposts;
using System.Text.Json;

namespace Rimlink.OutpostAcceptanceHost;

internal sealed record AcceptanceRunIdentity(string StandId, string RunId, int ProcessId, string SourceIdentity);

internal sealed class AcceptanceClockRun : IDisposable
{
    private readonly AcceptanceRunIdentity identity;
    private readonly AcceptanceClock time;
    private readonly Stream journal;
    private readonly object gate = new();
    private readonly HashSet<string> applied = new(StringComparer.Ordinal);
    private OutpostClock? actual;
    private long epochTicks;
    private bool closed;
    internal bool Failed { get; private set; }

    internal AcceptanceClockRun(AcceptanceRunIdentity identity, AcceptanceClock time, Stream journal, AcceptanceRestartOrigin? restart = null,
        AcceptanceClockReading? origin = null)
    {
        if (!Guid.TryParseExact(identity.StandId, "D", out var stand) || stand == Guid.Empty
            || !Guid.TryParseExact(identity.RunId, "D", out var run) || run == Guid.Empty || identity.ProcessId <= 0
            || identity.SourceIdentity != OutpostProductQualification.SourceIdentity
            || !journal.CanRead || !journal.CanSeek || !journal.CanWrite || journal.Length != 0)
            throw new InvalidDataException("acceptance-clock-run-origin-invalid");
        this.identity = identity; this.time = time; this.journal = journal;
        Write("Origin", new { Time = origin ?? time.Read(), Restart = restart });
    }

    internal void Bind(OutpostClock actual)
    {
        lock (gate)
        {
            RequireActive();
            if (this.actual is not null) throw new InvalidDataException("acceptance-clock-already-bound");
            var start = time.Initialization ?? throw new InvalidDataException("acceptance-clock-construction-not-observed");
            epochTicks = checked(start.Utc.UtcTicks - start.Timestamp);
            this.actual = actual;
            Write("Sample", new { Sample = Sample(), Time = time.Read(), InitialTimestamp = start.Timestamp, InitialUtc = start.Utc });
        }
    }

    internal AcceptanceClockStep Advance(string id, TimeSpan duration, string reason)
    {
        lock (gate)
        {
            RequireActive(); _ = Sample();
            if (applied.Contains(id)) return time.Advance(id, duration, reason);
            Write("Planned", new { Id = id, Reason = reason, DurationTicks = duration.Ticks, Sample = Sample(), Time = time.Read() });
            AcceptanceClockStep step;
            try { step = time.Advance(id, duration, reason); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException or ObjectDisposedException)
            { Write("Rejected", new { Id = id, Error = error.Message, Sample = Sample(), Time = time.Read() }); throw; }
            Write("Applied", new { Step = step, Sample = Sample(), Time = time.Read() });
            applied.Add(id);
            return step;
        }
    }

    internal ClockSample Sample()
    {
        RequireActive();
        if (actual is null) throw new InvalidDataException("acceptance-clock-not-bound");
        try { return VerifySample(time, actual, epochTicks); }
        catch (InvalidDataException) { Fail(); throw; }
    }

    internal static ClockSample VerifySample(AcceptanceClock time, OutpostClock actual, long epochTicks)
    {
        var before = time.Read(); var sample = actual.Sample(); var after = time.Read();
        if (sample.Frequency != TimeSpan.TicksPerSecond || sample.Timestamp < before.Timestamp || sample.Timestamp > after.Timestamp
            || sample.Utc.Kind != DateTimeKind.Utc || sample.Utc.Ticks != checked(epochTicks + sample.Timestamp))
            throw new InvalidDataException("acceptance-clock-actual-sample-mismatch");
        return sample;
    }

    internal void RefuseStartup() { lock (gate) Fail(); }
    internal void Record(string kind, object data)
    { lock (gate) { RequireActive(); Write(kind, new { Sample = Sample(), Evidence = data }); } }

    private void RequireActive()
    {
        if (closed || Failed) throw new InvalidOperationException("acceptance-clock-run-unqualified-or-closed");
    }

    private void Write(string kind, object data)
    {
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Kind = kind, Identity = identity,
                RealUtc = TimeProvider.System.GetUtcNow(), RealTimestamp = TimeProvider.System.GetTimestamp(),
                RealFrequency = TimeProvider.System.TimestampFrequency, Data = data }, AcceptanceStandFiles.Json);
            if (bytes.Length > 16 * 1024 || journal.Length > 32 * 1024 * 1024 - bytes.Length - 1)
                throw new IOException("acceptance-clock-journal-limit");
            long start = journal.Position;
            journal.Write(bytes, 0, bytes.Length); journal.WriteByte((byte)'\n');
            if (journal is FileStream file) file.Flush(flushToDisk: true); else journal.Flush();
            if (kind is "Origin" or "Sample")
            {
                journal.Position = start;
                byte[] actual = new byte[bytes.Length]; journal.ReadExactly(actual);
                if (!actual.AsSpan().SequenceEqual(bytes) || journal.ReadByte() != '\n')
                    throw new IOException("acceptance-clock-journal-readback-mismatch");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        { Fail(); throw; }
    }

    private void Fail() { Failed = true; time.Dispose(); }
    public void Dispose()
    {
        lock (gate)
        {
            if (closed) return;
            try { if (!Failed && actual is not null) Write("Closed", new { Sample = Sample(), Time = time.Read() }); }
            finally { closed = true; time.Dispose(); journal.Dispose(); }
        }
    }
}
