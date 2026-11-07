using Rimlink.Client.Net;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Client.Tests.Net;

public sealed class PendingSnapshotTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rimlink-snapshots", Guid.NewGuid().ToString("N"));
    private string Primary => Path.Combine(directory, "colony.rws");
    private PendingUpload Pending() => new(Path.Combine(directory, "recovery"));
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private void Write(string id, int tick = 100)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Primary, $"<savegame><game><tickManager><ticksGame>{tick}</ticksGame></tickManager><world><info><seedString>seed</seedString></info></world><components><li Class=\"{SaveRecoveryData.ComponentClass}\"><rimlinkRecoveryVersion>1</rimlinkRecoveryVersion><rimlinkSnapshotId>{id}</rimlinkSnapshotId></li></components></game></savegame>");
    }
    private SaveSnapshot Capture(string id)
    {
        Pending().BeginSnapshot(id, 0, 0); Write(id); return Pending().CaptureSnapshot(Primary);
    }

    [Fact]
    public void ACrashBetweenPrimaryWriteAndPostfixRecoversTheNewSnapshot()
    {
        string id = Guid.NewGuid().ToString("N"); Pending().BeginSnapshot(id, 0, 0); Write(id);
        var recovered = Pending().RecoverSnapshot(Primary);
        Assert.NotNull(recovered); Assert.Equal(id, recovered.Id); Assert.True(Pending().IsSet);
        Write(Guid.NewGuid().ToString("N"), 200);
        Assert.Equal(id, SaveMetaReader.Read(recovered.Path).Recovery!.SnapshotId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailedSaveNeverFlagsAnOlderCacheAsTheNewSnapshot(bool wasPending)
    {
        string oldId = Guid.NewGuid().ToString("N"); var old = Capture(oldId);
        if (!wasPending) Pending().CompleteSnapshot(old);
        Pending().BeginSnapshot(Guid.NewGuid().ToString("N"), 0, 0);
        var recovered = Pending().RecoverSnapshot(Primary);
        Assert.Equal(wasPending, Pending().IsSet);
        if (wasPending) Assert.Equal(oldId, recovered!.Id); else Assert.Null(recovered);
    }

    [Fact]
    public void CompletingAnOldUploadDoesNotClearTheNewerPending()
    {
        var first = Capture(Guid.NewGuid().ToString("N")); var second = Capture(Guid.NewGuid().ToString("N"));
        Pending().CompleteSnapshot(first);
        Assert.True(Pending().IsSet); Assert.Equal(second.Id, Pending().RecoverSnapshot(Primary)!.Id);
        Pending().CompleteSnapshot(second); Assert.False(Pending().IsSet);
    }

    [Fact]
    public void AnOlderSuccessDuringAFailedNewWriteDoesNotReflagTheAcceptedCache()
    {
        var older = Capture(Guid.NewGuid().ToString("N"));
        Pending().BeginSnapshot(Guid.NewGuid().ToString("N"), 0, 0);
        Pending().CompleteSnapshot(older);
        Assert.Null(Pending().RecoverSnapshot(Primary));
        Assert.False(Pending().IsSet);
    }

    [Fact]
    public async Task UploadsAreOrderedAndReadImmutableFiles()
    {
        var first = Capture(Guid.NewGuid().ToString("N")); var second = Capture(Guid.NewGuid().ToString("N"));
        var queue = new SaveUploadQueue();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var a = queue.Enqueue(first, async (snapshot, _) =>
        {
            order.Add(snapshot.Id); reached.SetResult(); await release.Task;
            Assert.Equal(first.Id, SaveMetaReader.Read(snapshot.Path).Recovery!.SnapshotId);
            return new SaveInfo { Version = 1 };
        }, CancellationToken.None);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var b = queue.Enqueue(second, (snapshot, _) => { order.Add(snapshot.Id); return Task.FromResult(new SaveInfo { Version = 2 }); }, CancellationToken.None);
        Assert.False(b.IsCompleted); Write(Guid.NewGuid().ToString("N")); release.SetResult();
        await Task.WhenAll(a, b); Assert.Equal(new[] { first.Id, second.Id }, order);
    }

    [Fact]
    public async Task CompletingACannotPruneTheQueuedBWhenCIsTheCurrentPending()
    {
        var first = Capture(Guid.NewGuid().ToString("N"));
        var second = Capture(Guid.NewGuid().ToString("N"));
        var third = Capture(Guid.NewGuid().ToString("N"));
        var queue = new SaveUploadQueue();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = queue.Enqueue(first, async (_, _) => { reached.SetResult(); await release.Task; return new SaveInfo(); }, CancellationToken.None);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var b = queue.Enqueue(second, async (snapshot, _) => { await allowB.Task; Assert.True(File.Exists(snapshot.Path)); return new SaveInfo(); }, CancellationToken.None);
        var c = queue.Enqueue(third, (_, _) => Task.FromException<SaveInfo>(new IOException("C refused")), CancellationToken.None);
        try
        {
            release.SetResult(); await a;
            Pending().CompleteSnapshot(first); queue.PruneSnapshots(Pending(), []);
            Assert.True(File.Exists(second.Path));
        }
        finally
        {
            allowB.TrySetResult();
            try { await Task.WhenAll(b, c); } catch { }
        }
    }

    [Fact]
    public void SupersedingAcceptancePrunesFailedCopiesButPreservesQuarantinedRecovery()
    {
        var failed = Capture(Guid.NewGuid().ToString("N")); var current = Capture(Guid.NewGuid().ToString("N"));
        Pending().CompleteSnapshot(current); Pending().PruneSnapshots([]);
        Assert.False(File.Exists(failed.Path));
        var preserved = Capture(Guid.NewGuid().ToString("N"));
        Pending().KeepLocalSave(Primary, "rollback");
        var next = Capture(Guid.NewGuid().ToString("N")); Pending().CompleteSnapshot(next); Pending().PruneSnapshots([]);
        Assert.True(File.Exists(preserved.Path));
    }

    [Theory]
    [InlineData("previous")]
    [InlineData("current")]
    [InlineData("foreign")]
    [InlineData("changed-bytes")]
    public void Only_an_exact_pending_snapshot_from_the_same_colonys_previous_boundary_is_quarantined(string scenario)
    {
        Directory.CreateDirectory(directory);
        string id = Guid.NewGuid().ToString("N");
        var scope = new OutpostScopeMessage { WorldInstanceId = "world-1", OwnerPlayerId = 1, RestoreBoundary = "boundary-1" };
        var metadata = new OutpostRecoveryData { Metadata = new() { SchemaVersion = 1, Scope = scope.Clone() } };
        byte[] raw = SaveFixtures.Decompress(RecoveryFixtures.Save(1000, id, outposts: metadata));
        File.WriteAllBytes(Primary, raw);
        var pending = Pending();
        pending.BeginSnapshot(id, 0, 0);
        var snapshot = pending.CaptureSnapshot(Primary);
        string journal = Path.Combine(directory, "recovery", "pending-snapshot.json");
        string flag = Path.Combine(directory, "recovery", "pending.flag");
        byte[] priorJournal = File.ReadAllBytes(journal);
        byte[] priorFlag = File.ReadAllBytes(flag);

        if (scenario != "current") scope.RestoreBoundary = "boundary-2";
        if (scenario == "foreign") scope.OwnerPlayerId = 2;
        if (scenario == "changed-bytes") File.AppendAllText(snapshot.Path, "\n");

        if (scenario is "foreign" or "changed-bytes")
            Assert.Throws<InvalidDataException>(() => pending.QuarantineOutsideRestoreBoundary(snapshot, scope));
        else if (scenario == "current")
            Assert.Null(pending.QuarantineOutsideRestoreBoundary(snapshot, scope));
        else
        {
            string kept = Assert.IsType<string>(pending.QuarantineOutsideRestoreBoundary(snapshot, scope));
            Assert.Equal(raw, File.ReadAllBytes(kept));
            Assert.Equal(priorJournal, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(journal)!, "pending-snapshot.json.restore-boundary-*"))));
            Assert.Equal(priorFlag, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(flag)!, "pending.flag.restore-boundary-*"))));
            Assert.False(pending.IsSet);
            Assert.Null(pending.RecoverSnapshot(Primary));
            Assert.False(File.Exists(snapshot.Path));
            Assert.Equal(raw, File.ReadAllBytes(Primary));
            return;
        }

        Assert.True(pending.IsSet);
        Assert.True(File.Exists(snapshot.Path));
        Assert.Equal(priorJournal, File.ReadAllBytes(journal));
        Assert.Equal(priorFlag, File.ReadAllBytes(flag));
    }
}
