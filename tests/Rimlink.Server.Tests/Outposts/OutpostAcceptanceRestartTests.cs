using System.Text.Json;
using Rimlink.OutpostAcceptanceHost;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAcceptanceRestartTests
{
    [Fact]
    public void Restart_cannot_branch_from_an_older_closed_run_or_accept_a_second_root()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string stand = Guid.NewGuid().ToString("D"), first = Guid.NewGuid().ToString("D"), second = Guid.NewGuid().ToString("D");
        try
        {
            Write(first, null);
            var restart = AcceptanceRestart.Read(PathOf(first), stand, first, TimeSpan.Zero);
            Write(second, restart);
            AcceptanceRestart.CheckTip(directory, stand, second);
            Assert.Throws<InvalidDataException>(() => AcceptanceRestart.CheckTip(directory, stand, first));
            Write(Guid.NewGuid().ToString("D"), null);
            Assert.Throws<InvalidDataException>(() => AcceptanceRestart.CheckTip(directory, stand, second));
        }
        finally { Directory.Delete(directory, recursive: true); }
        string PathOf(string id) => Path.Combine(directory, "run-" + id + ".clock.jsonl");
        void Write(string id, AcceptanceRestartOrigin? origin)
        {
            using var file = File.Create(PathOf(id));
            using var clock = new AcceptanceClock(TimeProvider.System, origin?.StartUtc ?? DateTimeOffset.UtcNow, new Proof());
            using var run = new AcceptanceClockRun(new(stand, id, Environment.ProcessId, OutpostProductQualification.SourceIdentity), clock, file, origin);
            run.Bind(new OutpostClock(clock));
        }
    }
    [Fact]
    public void Restart_requires_the_exact_closed_owned_journal_and_declared_nonnegative_downtime()
    {
        string stand = Guid.NewGuid().ToString("D"), id = Guid.NewGuid().ToString("D");
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            using (var file = File.Create(path))
            using (var clock = new AcceptanceClock(TimeProvider.System, DateTimeOffset.UtcNow, new Proof()))
            using (var run = new AcceptanceClockRun(new(stand, id, Environment.ProcessId, OutpostProductQualification.SourceIdentity), clock, file))
            {
                run.Bind(new OutpostClock(clock));
                run.Advance(Guid.NewGuid().ToString("D"), TimeSpan.FromDays(2), "offline production");
            }
            var origin = AcceptanceRestart.Read(path, stand, id, TimeSpan.FromHours(1));
            Assert.True(origin.StartUtc > DateTimeOffset.UtcNow.AddDays(2));
            Assert.Equal(64, origin.PreviousSha256.Length);
            Assert.ThrowsAny<Exception>(() => AcceptanceRestart.Read(path, Guid.NewGuid().ToString("D"), id, TimeSpan.Zero));
            Assert.ThrowsAny<Exception>(() => AcceptanceRestart.Read(path, stand, id, TimeSpan.FromTicks(-1)));
            var lines = File.ReadAllLines(path); File.WriteAllLines(path, lines[..^1]);
            Assert.ThrowsAny<Exception>(() => AcceptanceRestart.Read(path, stand, id, TimeSpan.Zero));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Restart_never_moves_behind_an_actual_persisted_checkpoint()
    {
        var utc = DateTimeOffset.UtcNow;
        AcceptanceRestart.CheckCheckpoints(utc, [utc.UtcDateTime]);
        Assert.Throws<InvalidDataException>(() => AcceptanceRestart.CheckCheckpoints(utc, [utc.AddTicks(1).UtcDateTime]));
        Assert.Throws<InvalidDataException>(() => AcceptanceRestart.CheckCheckpoints(utc, [DateTime.SpecifyKind(utc.DateTime, DateTimeKind.Unspecified)]));
    }
    private sealed class Proof : IOutpostBuildQualification
    {
        public OutpostBuildQualification Observe() => new(OutpostRuntimeMode.IsolatedAcceptance, OutpostQualificationState.Pending,
            OutpostProductQualification.SourceIdentity, true, true);
    }
}
