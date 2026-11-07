using System.Diagnostics;
using System.Text;
using Rimlink.OutpostAcceptanceHost;
using Rimlink.Server.Application.Outposts;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAcceptanceOfflinePauseTests
{
    private const string RestoreId = "e818f5d2-fcc0-4318-91f3-43cd9a80dd86";
    [Fact]
    public async Task Exact_path_and_occurrence_publish_readback_evidence_before_wait_and_release_once()
    {
        using var journal = new MemoryStream(); using var stop = new CancellationTokenSource();
        using var pause = new AcceptanceOfflinePause(Identity(), journal, stop.Token);
        var arm = Arm(); pause.Arm(arm); var observed = Observed();
        Assert.False(pause.Expects("AfterSwitchEffect:saves/other.rws.zst"));
        var worker = StartWorker(pause, observed);
        var reached = await Reached(pause); Assert.False(worker.IsCompleted);
        Assert.Contains("AfterSwitchEffect:rimlink.db", Encoding.UTF8.GetString(journal.ToArray()));
        Assert.Contains(observed.JournalSha256, Encoding.UTF8.GetString(journal.ToArray()));
        Assert.Throws<InvalidDataException>(() => pause.Release(arm.ArmId, reached.Sequence, new('0', 64)));
        pause.Release(arm.ArmId, reached.Sequence, reached.EventSha256!); await worker.WaitAsync(TimeSpan.FromSeconds(5));
        string once = Encoding.UTF8.GetString(journal.ToArray());
        pause.Release(arm.ArmId, reached.Sequence, reached.EventSha256!);
        Assert.Equal(once, Encoding.UTF8.GetString(journal.ToArray()));
        Assert.Equal("Released", pause.Observe()!.Status);
    }
    [Fact]
    public async Task Cancellation_wakes_only_the_restore_worker_and_never_releases_it_as_success()
    {
        using var stop = new CancellationTokenSource(); using var pause = new AcceptanceOfflinePause(Identity(), new MemoryStream(), stop.Token);
        pause.Arm(Arm()); var worker = StartWorker(pause, Observed());
        await Reached(pause); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Cancelled", pause.Observe()!.Status);
    }
    [Fact]
    public void Evidence_failure_never_publishes_reached_and_unknown_inventory_never_matches()
    {
        using var storage = new FailingStream(); using var pause = new AcceptanceOfflinePause(Identity(), storage, default);
        pause.Arm(Arm()); storage.Fail = true;
        Assert.Throws<IOException>(() => pause.Reach("AfterSwitchEffect:rimlink.db", Observed()));
        Assert.NotEqual("Reached", pause.Observe()!.Status);
        using var second = new AcceptanceOfflinePause(Identity(), new MemoryStream(), default); second.Arm(Arm());
        Assert.Throws<InvalidDataException>(() => second.Reach("AfterSwitchEffect:rimlink.db", Observed() with { Paths = ["saves/other.rws.zst"] }));
    }
    [Fact]
    public void Unknown_hooks_paths_and_reused_arm_ids_are_refused()
    {
        using var pause = new AcceptanceOfflinePause(Identity(), new MemoryStream(), default);
        Assert.Throws<InvalidDataException>(() => pause.Arm(Arm() with { Hook = "AnyFile*" }));
        Assert.Throws<InvalidDataException>(() => pause.Arm(Arm() with { Path = "../rimlink.db" }));
        var arm = Arm(); pause.Arm(arm); pause.Arm(arm);
        Assert.Throws<InvalidDataException>(() => pause.Arm(arm with { Occurrence = 2 }));
    }
    private static async Task<AcceptanceOfflineState> Reached(AcceptanceOfflinePause pause)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (pause.Observe() is { Status: "Reached" } state) return state;
            if (clock.Elapsed >= TimeSpan.FromSeconds(5)) throw new TimeoutException("private reached event missing");
            await Task.Delay(10);
        }
    }
    private static Task StartWorker(AcceptanceOfflinePause pause, AcceptanceOfflineJournal observed) =>
        Task.Factory.StartNew(() => pause.Reach("AfterSwitchEffect:rimlink.db", observed),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    private static AcceptanceOfflineArm Arm() => new(Guid.NewGuid().ToString("D"), "AfterSwitchEffect", "rimlink.db", 1);
    private static AcceptanceOfflineIdentity Identity() => new(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Environment.ProcessId,
        DateTime.UtcNow, OutpostProductQualification.SourceIdentity, "execute", RestoreId, new('1', 64), new('2', 64), new('3', 64));
    private static AcceptanceOfflineJournal Observed() => new(RestoreId, new('4', 64), "Switching", 5, new('5', 64), ["rimlink.db"]);
    private sealed class FailingStream : MemoryStream
    {
        internal bool Fail;
        public override void Write(byte[] buffer, int offset, int count) { if (Fail) throw new IOException("private evidence failure"); base.Write(buffer, offset, count); }
    }
}
