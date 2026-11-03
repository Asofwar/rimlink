using Google.Protobuf;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Client.Tests.Support;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;

namespace Rimlink.Client.Tests.Economy;

public sealed class TransferRecoveryTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "rimlink-prepared", Guid.NewGuid().ToString("N"), "outbox.json");
    private TransferOutbox Open() => new(path, new ConsoleLog());
    public void Dispose() { if (Directory.Exists(Path.GetDirectoryName(path)!)) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    private static SaveRecoveryData Snapshot(string[]? removed = null, string[]? returned = null) => new()
        { Version = 1, SnapshotId = Guid.NewGuid().ToString("N"), Removed = removed ?? [], Returned = returned ?? [] };

    [Fact]
    public async Task ADelayedCallKeepsItsOriginalAuthenticationAcrossReconnect()
    {
        using var fixture = EconomyServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova"); var bob = await fixture.RegisterAndLogin("bob");
        var old = nova.Login!;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = nova.ForAuthentication(old, async () =>
        {
            reached.SetResult(); await release.Task;
            await new EconomyClient(nova).Send(Send(bob.Player.PlayerId, Items(("Steel", 5))), CancellationToken.None);
        });
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        nova.DropNetwork(TimeSpan.FromMilliseconds(100));
        try { await ClientFixtures.WaitUntil(() => !ReferenceEquals(nova.Login, old)); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<Exception>(() => delayed);
        using var scope = fixture.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Parcels.AnyAsync());
    }

    [Fact]
    public async Task ReplayCannotUseANewLoginForAnOldRemovalSnapshot()
    {
        using var fixture = EconomyServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova"); var bob = await fixture.RegisterAndLogin("bob");
        var outbox = Open(); var entry = outbox.Prepare(Send(bob.Player.PlayerId, Items(("Steel", 5))));
        outbox.ReconcileSnapshot(Snapshot([entry.Id]), accepted: true);
        var old = nova.Login;
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Players.Where(p => p.Id == old!.Player.PlayerId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.SaveWriteEpoch, 1));
        nova.DropNetwork(TimeSpan.FromMilliseconds(100));
        await ClientFixtures.WaitUntil(() => nova.Login is { SaveWriteEpoch: 1 } && !ReferenceEquals(nova.Login, old));
        var replay = await outbox.Replay(new EconomyClient(nova), CancellationToken.None);
        Assert.Equal(0, replay.Completed); Assert.Single(Open().Entries);
        using var check = fixture.Services.CreateScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<RimlinkDbContext>().Parcels.AnyAsync());
    }

    [Fact]
    public async Task PreparedIsNotReplayedUntilItsRemovalSnapshotIsAccepted()
    {
        using var fixture = EconomyServer();
        await using var nova = await ClientFixtures.Connected(fixture, "nova"); var bob = await fixture.RegisterAndLogin("bob");
        var entry = Open().Prepare(Send(bob.Player.PlayerId, Items(("Steel", 5))));
        var outbox = Open();
        Assert.Equal(OutboxPhase.Prepared, outbox.Entries.Single().Phase);
        var blocked = await outbox.Replay(new EconomyClient(nova), CancellationToken.None);
        Assert.Equal(0, blocked.Completed);
        outbox.ReconcileSnapshot(Snapshot([entry.Id]), accepted: false);
        Assert.Equal(OutboxPhase.Prepared, outbox.Entries.Single().Phase);
        outbox.ReconcileSnapshot(Snapshot([entry.Id]), accepted: true);
        Assert.Equal(OutboxPhase.Ready, Open().Entries.Single().Phase);
        Assert.Equal(1, (await outbox.Replay(new EconomyClient(nova), CancellationToken.None)).Completed);
    }

    [Fact]
    public void AnOlderUploadCannotDiscardANewerPreparedOperation()
    {
        Open().Prepare(Send(2, Items(("Steel", 5))));
        var outbox = Open(); outbox.ReconcileSnapshot(Snapshot(), accepted: true);
        Assert.Equal(OutboxPhase.Prepared, Open().Entries.Single().Phase);
    }

    [Fact]
    public void RefusalStaysUntilTheReturnedGoodsAreSavedAndAccepted()
    {
        var outbox = Open(); var entry = outbox.Prepare(Send(2, Items(("Steel", 5))));
        outbox.ReconcileSnapshot(Snapshot([entry.Id]), accepted: true);
        outbox.Refuse(entry.Id, "refused"); Assert.Equal(OutboxPhase.Returning, Open().Entries.Single().Phase);
        outbox.ReconcileSnapshot(Snapshot([entry.Id], [entry.Id]), accepted: false);
        Assert.Single(Open().Entries);
        outbox.ReconcileSnapshot(Snapshot([entry.Id], [entry.Id]), accepted: true);
        Assert.Empty(Open().Entries);
    }

    [Fact]
    public void ARequestFromAnotherEpochIsPreservedWithoutReplayOrReturn()
    {
        var outbox = Open(); var entry = outbox.Prepare(Send(2, Items(("Steel", 5))));
        outbox.ReconcileSnapshot(Snapshot([entry.Id]) with { WriteEpoch = 1 }, accepted: true, selected: true);
        Assert.Equal(OutboxPhase.RecoveryRequired, Open().Entries.Single().Phase);
    }

    [Fact]
    public void CorruptOutboxIsNeverReplacedWithAnEmptyOne()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "{broken");
        Assert.Throws<IOException>(() => Open()); Assert.Equal("{broken", File.ReadAllText(path));
    }

    [Fact]
    public void AReadyRaidReportStillRequiresTheSelectedRemovalProofWithoutLoot()
    {
        var outbox = Open();
        var entry = outbox.Prepare(LiveFixtures.AllAlive(1, 0, 1));
        outbox.ReconcileSnapshot(Snapshot([entry.Id]), accepted: true);
        outbox.ReconcileSnapshot(Snapshot(), accepted: true, selected: true);
        Assert.Equal(OutboxPhase.RecoveryRequired, Assert.Single(Open().Entries).Phase);
    }

    [Fact]
    public void MissingRemovalProofPreservesThePayloadForRecovery()
    {
        var entry = Open().Prepare(Send(2, Items(("Steel", 5))));
        var outbox = Open(); outbox.ReconcileSnapshot(Snapshot(), accepted: true, selected: true);
        var kept = Assert.Single(Open().Entries);
        Assert.Equal(OutboxPhase.RecoveryRequired, kept.Phase); Assert.Equal(entry.Payload, kept.Payload);
    }
}
