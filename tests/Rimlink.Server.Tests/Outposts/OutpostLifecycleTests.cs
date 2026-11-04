using System.Security.Cryptography;
using System.Text.Json;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.World;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OutpostLifecycleTests
{
    [Theory]
    [InlineData("active")]
    [InlineData("prepared")]
    [InlineData("closed-unaccepted")]
    public async Task Reset_requires_close_and_accepted_physical_receipts(string obligation)
    {
        using var f = await LifecycleFixture.Create(obligation);
        string before = await f.Cut();
        using var scope = f.Host.Services.CreateScope();
        await Assert.ThrowsAsync<RpcException>(() => scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(Actor.Api, 1));
        Assert.Equal(before, await f.Cut());
        Assert.Equal(OutpostStartupState.Ready, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Recreate_one_blocked_owner_changes_none()
    {
        using var f = await LifecycleFixture.Create();
        f.H.Db.Accounts.Add(new Account { Id = 2, Player = new Player { Id = 2, Name = "Idle", SaveGeneration = 7, SaveWriteEpoch = 9 } });
        await f.H.Db.SaveChangesAsync();
        string before = await f.Cut();
        using var scope = f.Host.Services.CreateScope();
        await Assert.ThrowsAsync<RpcException>(() => scope.ServiceProvider.GetRequiredService<AdminOperations>().RecreateWorld(Actor.Api));
        Assert.Equal(before, await f.Cut());
        Assert.Equal(OutpostStartupState.Ready, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Archive_settles_then_pauses_and_keeps_slot_tile_generation()
    {
        using var f = await LifecycleFixture.Create();
        var old = await f.H.Row();
        var oldCustody = await f.H.Db.PawnCustody.AsNoTracking().SingleAsync();
        f.H.F.Time.Advance(TimeSpan.FromMinutes(31.5)); // 20/hour: exactly 10 + 1/2.
        await f.Archive();
        var row = await f.H.Row();
        Assert.Equal("Archived", row.State);
        Assert.Equal(10, row.Stock);
        Assert.Equal("1", row.FractionNumerator);
        Assert.Equal("2", row.FractionDenominator);
        Assert.True(row.OccupiesSlot);
        Assert.Equal(old.Tile, row.Tile);
        Assert.Equal(old.SaveGeneration, row.SaveGeneration);
        Assert.True((await f.H.Db.OutpostAssignments.AsNoTracking().SingleAsync()).Active);
        var custody = await f.H.Db.PawnCustody.AsNoTracking().SingleAsync();
        Assert.Equal(oldCustody.LocationKind, custody.LocationKind);
        Assert.Equal(oldCustody.LocationId, custody.LocationId);
        Assert.Equal(oldCustody.AssignmentId, custody.AssignmentId);
        Assert.Empty(await f.H.Db.SaveVersions.AsNoTracking().ToListAsync());
        Assert.Equal(OutpostStartupState.Ready, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData("Prepared", false)]
    [InlineData("EvidenceRetained", false)]
    [InlineData("Destructive", false)]
    [InlineData("SourceCleanup", false)]
    [InlineData("Completed", true)]
    public async Task Crash_each_archive_phase_blocks_normal_startup(string phase, bool completed)
    {
        using var f = await LifecycleFixture.Create();
        using var scope = f.Host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<OutpostLifecycle>().Checkpoint = at =>
        { if (at == phase) throw new IOException("simulated lifecycle interruption: " + phase); };
        await Assert.ThrowsAsync<IOException>(() => scope.ServiceProvider.GetRequiredService<ColonyArchive>().Archive(1, "admin", Actor.System));
        var opened = await f.H.F.H.H.Authority.OpenAsync(default);
        Assert.Equal(completed ? OutpostStartupState.Ready : OutpostStartupState.Maintenance, opened.State);
        var operation = await f.H.Db.OutpostLifecycleOperations.AsNoTracking().SingleAsync();
        Assert.Equal(completed ? "Completed" : phase == "SourceCleanup" ? "Destructive" : phase, operation.Phase);
        // No caller compensated by deleting the sole reachable evidence after a protected phase committed.
        if (phase is "SourceCleanup" or "Completed")
        {
            Assert.Empty(await f.H.Db.SaveVersions.AsNoTracking().ToListAsync());
            var archive = await f.H.Db.ArchivedColonies.AsNoTracking().SingleAsync();
            Assert.True(Directory.Exists(Path.Combine(f.H.F.H.H.Data.Root, archive.Dir)));
        }
    }

    [Fact]
    public async Task Restore_same_colony_resumes_now()
    {
        using var f = await LifecycleFixture.Create();
        f.H.F.Time.Advance(TimeSpan.FromMinutes(31.5));
        long archive = await f.Archive();
        f.H.F.Time.Advance(TimeSpan.FromDays(5));
        using (var scope = f.Host.Services.CreateScope())
            Assert.IsType<RestoreOutcome.Restored>(await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Restore(archive, "test"));
        var resumed = await f.H.Row();
        Assert.Equal("Active", resumed.State);
        Assert.Equal(10, resumed.Stock);
        Assert.Equal("1", resumed.FractionNumerator);
        Assert.Equal("2", resumed.FractionDenominator);
        Assert.Equal(f.H.Scope.SaveGeneration, resumed.SaveGeneration);
        f.H.F.Time.Advance(TimeSpan.FromHours(1));
        var state = await f.H.Service.StateAsync(1, new() { Scope = f.H.Scope.Clone() }, default);
        Assert.Equal(30, Assert.Single(state.Outposts).Stock);
        Assert.Equal(OutpostStartupState.Ready, (await f.H.F.H.H.Authority.OpenAsync(default)).State);
    }
}

internal sealed class LifecycleFixture : IDisposable
{
    public MutationHarness H { get; }
    public ServerFixture Host { get; }
    private LifecycleFixture(MutationHarness h)
    {
        H = h; var shared = h.F.H.H;
        Host = HygieneFixtures.HygieneServer().WithoutWorld().WithDataDir(shared.Data.Root).KeepDataDir()
            .WithSetting("Outposts:AnchorDirectory", Path.Combine(shared.Root, "external"))
            .WithSetting("Outposts:StorageContract", "sqlite-local-v1")
            .WithServices(s => { s.AddSingleton(shared.Files); s.AddSingleton(shared.Owners); s.AddSingleton(shared.Anchor); s.AddSingleton<TimeProvider>(h.F.Time); });
    }
    public static async Task<LifecycleFixture> Create(string obligation = "active")
    {
        var h = await MutationHarness.Create(prepareOnly: obligation == "prepared");
        if (obligation == "closed-unaccepted") await h.Service.MutateAsync(1, h.Request(OutpostMutationKind.Close), default);
        h.Db.WorldObjects.Add(new Rimlink.Server.Storage.Entities.WorldObject { OwnerPlayerId = 1, Type = WorldObjectTypes.Settlement, Name = "Nova Home", Tile = 33, CreatedAt = DateTime.UtcNow });
        await h.Db.SaveChangesAsync();
        var f = new LifecycleFixture(h); _ = f.Host.Services; return f;
    }
    public async Task<long> Archive()
    {
        using var scope = Host.Services.CreateScope();
        return Assert.IsType<ArchiveOutcome.Archived>(await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Archive(1, "admin", Actor.System)).ArchiveId;
    }
    public async Task<string> Cut()
    {
        var db = H.Db;
        var observed = new
        {
            World = await db.World.AsNoTracking().ToListAsync(),
            Players = await db.Players.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.SaveGeneration, p.SaveWriteEpoch, p.LastOutgoingTick, p.FirstSaveAt, p.WealthTotal, p.WealthItems }).ToListAsync(),
            Saves = await db.SaveVersions.AsNoTracking().OrderBy(v => v.Id).ToListAsync(),
            Objects = await db.WorldObjects.AsNoTracking().OrderBy(v => v.Id).ToListAsync(),
            Archives = await db.ArchivedColonies.AsNoTracking().OrderBy(v => v.Id).ToListAsync(),
            Head = await db.OutpostAuthority.AsNoTracking().ToListAsync(),
            Transitions = await db.OutpostTransitions.AsNoTracking().OrderBy(v => v.Id).ToListAsync(),
            Outposts = await db.Outposts.AsNoTracking().OrderBy(v => v.Id).ToListAsync(),
            Assignments = await db.OutpostAssignments.AsNoTracking().OrderBy(v => v.Id).ToListAsync(),
            Custody = await db.PawnCustody.AsNoTracking().OrderBy(v => v.Id).ToListAsync(),
            Operations = await db.OutpostOperations.AsNoTracking().OrderBy(v => v.Id).ToListAsync(),
            Lifecycle = await db.OutpostLifecycleOperations.AsNoTracking().OrderBy(v => v.Id).ToListAsync(),
            Parcels = await db.Parcels.AsNoTracking().OrderBy(v => v.Id).ToListAsync(),
            Transfers = await db.Transfers.AsNoTracking().OrderBy(v => v.Id).ToListAsync(),
            Wallets = await db.Wallets.AsNoTracking().OrderBy(v => v.PlayerId).ToListAsync(),
            Audit = await db.AdminActions.AsNoTracking().OrderBy(v => v.Id).ToListAsync(),
            Files = new[] { H.F.H.H.Data.SavesRoot, H.F.H.H.Data.Archive }.SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                .Order().Select(path => new { Path = path, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray()
        };
        return JsonSerializer.Serialize(observed);
    }
    public void Dispose() { Host.Dispose(); H.Dispose(); }
}
