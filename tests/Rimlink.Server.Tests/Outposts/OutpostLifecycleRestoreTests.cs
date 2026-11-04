using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.World;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OutpostLifecycleTests
{
    [Theory]
    [InlineData("Prepared", false)]
    [InlineData("EvidenceRetained", false)]
    [InlineData("Destructive", false)]
    [InlineData("SourceCleanup", false)]
    [InlineData("Completed", true)]
    public async Task Crash_each_restore_phase_retains_evidence_and_blocks_normal_startup(string phase, bool completed)
    {
        using var f = await LifecycleFixture.Create();
        long archive = await f.Archive();
        f.H.F.Time.Advance(TimeSpan.FromDays(1));
        using var scope = f.Host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<OutpostLifecycle>().Checkpoint = at =>
        { if (at == phase) throw new IOException("simulated restore interruption: " + phase); };
        await Assert.ThrowsAsync<IOException>(() => scope.ServiceProvider.GetRequiredService<ColonyArchive>().Restore(archive, "test"));
        var opened = await f.H.F.H.H.Authority.OpenAsync(default);
        Assert.Equal(completed ? OutpostStartupState.Ready : OutpostStartupState.Maintenance, opened.State);
        var operation = await f.H.Db.OutpostLifecycleOperations.AsNoTracking().SingleAsync(o => o.Kind == "Restore");
        Assert.Equal(completed ? "Completed" : phase == "SourceCleanup" ? "Destructive" : phase, operation.Phase);
        var archiveOperation = await f.H.Db.OutpostLifecycleOperations.AsNoTracking().SingleAsync(o => o.Kind == "Archive");
        var intent = OutpostLifecycleEvidence.Intent(archiveOperation);
        Assert.NotEmpty(intent.Files);
        foreach (var file in intent.Files)
        {
            string path = Path.Combine(f.H.F.H.H.Data.Root, intent.Directory, file.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture), file.Name);
            Assert.Equal(file.Sha256, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        }
    }

    [Fact]
    public async Task Restore_immediately_after_archive_uses_chain_order_not_wall_clock_or_random_uuid()
    {
        using var f = await LifecycleFixture.Create();
        // Same TimeProvider coordinate for both operations, and transition UUIDs have no temporal order.
        long archive = await f.Archive();
        using var scope = f.Host.Services.CreateScope();
        Assert.IsType<RestoreOutcome.Restored>(await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Restore(archive, "test"));
        Assert.Equal("Active", (await f.H.Row()).State);
        var opened = await f.H.F.H.H.Authority.OpenAsync(default);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), opened);
    }

    [Fact]
    public async Task Latest_lifecycle_projection_uses_verified_predecessors_even_when_wall_clock_reverses()
    {
        using var f = await LifecycleFixture.Create();
        long archive = await f.Archive();
        f.H.F.Time.Advance(TimeSpan.FromHours(1));
        using (var scope = f.Host.Services.CreateScope())
            Assert.IsType<RestoreOutcome.Restored>(await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Restore(archive, "test"));
        var operations = await f.H.Db.OutpostLifecycleOperations.AsNoTracking().ToListAsync();
        var transitions = await f.H.Db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id);
        var latest = Assert.Single(operations, o => o.Kind == "Restore");
        // Pure projection input: timestamps are deliberately not chronology. No persisted evidence is edited.
        foreach (var transition in transitions.Values) transition.CreatedAt = DateTime.MaxValue;
        transitions[OutpostLifecycleEvidence.Decode<LifecycleProgress>(latest.Progress).TransitionId].CreatedAt = DateTime.MinValue;
        Assert.Equal(latest.Id, OutpostLifecycleEvidence.LatestForOwner(1, operations, transitions).Id);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("write-epoch")]
    [InlineData("outpost-state")]
    [InlineData("retained-bytes")]
    public async Task Archive_projection_or_sole_retained_bytes_cannot_drift(string altered)
    {
        using var f = await LifecycleFixture.Create();
        await f.Archive();
        if (altered == "generation")
            await f.H.Db.Players.Where(p => p.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(p => p.SaveGeneration, p => p.SaveGeneration + 1));
        else if (altered == "write-epoch")
            await f.H.Db.Players.Where(p => p.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(p => p.SaveWriteEpoch, p => p.SaveWriteEpoch + 1));
        else if (altered == "outpost-state")
            await f.H.Db.Outposts.ExecuteUpdateAsync(s => s.SetProperty(p => p.State, "Active"));
        else
        {
            var row = await f.H.Db.OutpostLifecycleOperations.AsNoTracking().SingleAsync();
            var intent = OutpostLifecycleEvidence.Intent(row); var file = intent.Files[0];
            string path = Path.Combine(f.H.F.H.H.Data.Root, intent.Directory, file.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture), file.Name);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
        }
        Assert.Equal(OutpostStartupState.Maintenance, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Damaged_protected_login_restore_never_resets_transfer_or_redelivery_stamps()
    {
        using var f = await LifecycleFixture.Create();
        long archiveId = await f.Archive();
        await f.H.Db.Players.Where(p => p.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(p => p.LastOutgoingTick, 1234L));
        var archive = await f.H.Db.ArchivedColonies.AsNoTracking().SingleAsync(a => a.Id == archiveId);
        string dir = Path.Combine(f.H.F.H.H.Data.Root, archive.Dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), "{invalid");
        using var scope = f.Host.Services.CreateScope();
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => scope.ServiceProvider.GetRequiredService<ColonyArchive>().RestoreOnLogin(1));
        Assert.Equal(1234, await f.H.Db.Players.Where(p => p.Id == 1).Select(p => p.LastOutgoingTick).SingleAsync());
        Assert.Equal("Archived", (await f.H.Row()).State);
        Assert.Equal(OutpostStartupState.Maintenance, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
    }
}
