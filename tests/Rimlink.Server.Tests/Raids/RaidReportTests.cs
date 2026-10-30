using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Modpack;
using Rimlink.Server.Application.Raids;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using Role = Rimlink.Server.Storage.Entities.Role;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.RaidFixtures;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.Raids;

/// <summary>What the defender's game reports, and what the server turns it into.</summary>
public class RaidReportTests
{
    private sealed record Pair(ServerFixture Fixture, LoginReply Nova, LoginReply Bob, RaidClients N, RaidClients B);

    private static async Task<Pair> NovaAndBob(ServerFixture? fixture = null)
    {
        fixture ??= RaidServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await MakeTargetRaidable(fixture, bob.Player.PlayerId);
        return new Pair(fixture, nova, bob, RaidClientsOf(fixture, nova.SessionToken), RaidClientsOf(fixture, bob.SessionToken));
    }

    /// <summary>Launches at bob and lands it on his map at tick 70000.</summary>
    private static async Task<Raid> Landed(Pair p, int pawns = 3, PawnMode mode = PawnMode.PawnReal, RaidTiming timing = RaidTiming.TimingWarned)
    {
        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId, pawns: pawns, mode: mode, timing: timing), p.N.Auth);
        await p.B.Raids.AnnounceAsync(new RaidTick { RaidId = raid.Id, GameTick = 1000 }, p.B.Auth);
        await p.B.Raids.LandAsync(new RaidTick { RaidId = raid.Id, GameTick = 70000 }, p.B.Auth);
        return raid;
    }

    private static RaidReport Report(long raidId, long tick = 80000, params (int index, PawnOutcome outcome)[] outcomes)
    {
        var report = new RaidReport { RaidId = raidId, GameTick = tick, Loot = new ItemList(), Captives = new PawnList() };
        foreach (var (index, outcome) in outcomes)
        {
            var o = new PawnOutcomeMsg { Index = index, Outcome = outcome };
            if (outcome is PawnOutcome.OutcomeFled or PawnOutcome.OutcomeAlive) o.Blob = Pawn("Colonist", "Survivor" + index);
            report.Outcomes.Add(o);
        }
        return report;
    }

    private static async Task<long> OutgoingTick(ServerFixture fixture, long playerId)
    {
        using var scope = fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Players.SingleAsync(p => p.Id == playerId)).LastOutgoingTick;
    }

    [Fact]
    public async Task ReportResolvesTheRaidAndParcelsTheAttacker()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var novaStream = events.Subscribe(new Empty(), p.N.Auth);
        var raid = await Landed(p);
        var report = Report(raid.Id, 80000, (0, PawnOutcome.OutcomeCaptured), (1, PawnOutcome.OutcomeFled), (2, PawnOutcome.OutcomeFled));
        report.Loot.Items.Add(Item("Steel", 1, marketValueX100: 1500));
        report.Loot.Items.Add(Item("Steel", 1, marketValueX100: 1500));

        await p.B.Raids.ReportAsync(report, p.B.Auth);

        var mine = Assert.Single((await p.N.Raids.MineAsync(new Empty(), p.N.Auth)).Raids);
        Assert.Equal(RaidState.RaidResolved, mine.State);
        Assert.Equal(2, mine.Summary.Fled);
        Assert.Equal(1, mine.Summary.Captured);
        Assert.Equal(30, mine.Summary.LootValue);
        var parcels = (await p.N.Mailbox.ListAsync(new Empty(), p.N.Auth)).Parcels;
        Assert.Equal(2, parcels.Count);
        Assert.Equal(2, Assert.Single(parcels, x => x.Kind == ParcelKind.ParcelLoot).Items.Items.Count);
        Assert.Equal(2, Assert.Single(parcels, x => x.Kind == ParcelKind.ParcelRaidReturn).Pawns.Pawns.Count);
        Assert.Equal(80000, await OutgoingTick(fixture, p.Bob.Player.PlayerId));
        var resolved = await ReadEvent(novaStream, ServerEvent.BodyOneofCase.RaidResolved, e => e.RaidResolved);
        Assert.Equal(raid.Id, resolved.RaidId);
        Assert.Equal(30, resolved.Summary.LootValue);
    }

    [Fact]
    public async Task SnapshotReportReturnsNoPawns()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var raid = await Landed(p, mode: PawnMode.PawnSnapshot);
        var report = Report(raid.Id, 80000, (0, PawnOutcome.OutcomeFled), (1, PawnOutcome.OutcomeDead), (2, PawnOutcome.OutcomeAlive));
        report.Loot.Items.Add(Item("Steel", 2, marketValueX100: 100));

        await p.B.Raids.ReportAsync(report, p.B.Auth);

        Assert.Equal(ParcelKind.ParcelLoot, Assert.Single((await p.N.Mailbox.ListAsync(new Empty(), p.N.Auth)).Parcels).Kind);
    }

    [Fact]
    public async Task ReportLootAtCapPassesAndOverCapIsRefused()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var raid = await Landed(p); // loot cap 5000
        var atCap = Report(raid.Id, 80000, (0, PawnOutcome.OutcomeFled), (1, PawnOutcome.OutcomeFled), (2, PawnOutcome.OutcomeFled));
        atCap.Loot.Items.Add(Item("Gold", 50, marketValueX100: 10000)); // 50 × 100 = 5000
        await p.B.Raids.ReportAsync(atCap, p.B.Auth);

        var alice = await fixture.RegisterAndLogin("alice", playerName: "Alice");
        await MakeTargetRaidable(fixture, alice.Player.PlayerId);
        var a = RaidClientsOf(fixture, alice.SessionToken);
        var second = await p.N.Raids.LaunchAsync(Launch(alice.Player.PlayerId), p.N.Auth);
        await a.Raids.AnnounceAsync(new RaidTick { RaidId = second.Id, GameTick = 1 }, a.Auth);
        await a.Raids.LandAsync(new RaidTick { RaidId = second.Id, GameTick = 2 }, a.Auth);
        var over = Report(second.Id, 3, (0, PawnOutcome.OutcomeFled), (1, PawnOutcome.OutcomeFled));
        over.Loot.Items.Add(Item("Gold", 50, marketValueX100: 10000));
        over.Loot.Items.Add(Item("Steel", 1, marketValueX100: 100));

        var ex = await Assert.ThrowsAsync<RpcException>(() => a.Raids.ReportAsync(over, a.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.LootOverCap, ex.ErrorDetail()!.Code);
        Assert.Equal(RaidState.RaidLanded, (await p.N.Raids.MineAsync(new Empty(), p.N.Auth)).Raids.Single(r => r.Id == second.Id).State);
        var parcels = (await p.N.Mailbox.ListAsync(new Empty(), p.N.Auth)).Parcels; // the at-cap loot and its three survivors, nothing from the refused report
        Assert.Equal(2, parcels.Count);
        Assert.DoesNotContain(parcels, x => x.Items.Items.Any(i => i.DefName == "Steel"));
    }

    [Fact]
    public async Task ReportWithAnUnknownDefIsRefused()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var raid = await Landed(p);
        fixture.Services.GetRequiredService<ModpackDefs>().Replace(["Steel", "Colonist"]);
        var report = Report(raid.Id, 80000, (0, PawnOutcome.OutcomeDead), (1, PawnOutcome.OutcomeDead), (2, PawnOutcome.OutcomeDead));
        report.Loot.Items.Add(Item("Gold", 1, marketValueX100: 100));

        var ex = await Assert.ThrowsAsync<RpcException>(() => p.B.Raids.ReportAsync(report, p.B.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.DefNotInModpack, ex.ErrorDetail()!.Code);
    }

    [Fact]
    public async Task ReportIsIdempotentAndOnlyForTheTarget()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var raid = await Landed(p);
        var report = Report(raid.Id, 80000, (0, PawnOutcome.OutcomeFled), (1, PawnOutcome.OutcomeDead), (2, PawnOutcome.OutcomeDead));

        await p.B.Raids.ReportAsync(report, p.B.Auth);
        await p.B.Raids.ReportAsync(report, p.B.Auth);
        var ex = await Assert.ThrowsAsync<RpcException>(() => p.N.Raids.ReportAsync(report, p.N.Auth).ResponseAsync);

        Assert.Single((await p.N.Mailbox.ListAsync(new Empty(), p.N.Auth)).Parcels);
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task ReportBeforeLandingIsRefused()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth);
        await p.B.Raids.AnnounceAsync(new RaidTick { RaidId = raid.Id, GameTick = 1000 }, p.B.Auth);

        var ex = await Assert.ThrowsAsync<RpcException>(() => p.B.Raids.ReportAsync(Report(raid.Id, 2000, (0, PawnOutcome.OutcomeDead), (1, PawnOutcome.OutcomeDead)), p.B.Auth).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task LandIsIdempotentAndASaveOlderThanLandingDoesNotRequeue()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var raid = await Landed(p);
        await p.B.Raids.LandAsync(new RaidTick { RaidId = raid.Id, GameTick = 70000 }, p.B.Auth);

        byte[] old = MakeSave(ServerFixture.WorldSeed, "Bob", tick: 60000);
        var ex = await Assert.ThrowsAsync<RpcException>(() => Upload(p.B.Saves, p.Bob.SessionToken, old, HeaderFor(old, ServerFixture.WorldSeed, "Bob", tick: 60000)));

        Assert.Equal(ErrorCode.SaveStale, ex.ErrorDetail()!.Code);
        Assert.Empty((await p.B.Raids.PendingAsync(new Empty(), p.B.Auth)).Raids);
    }

    [Fact]
    public async Task ExpiredRealRaidReturnsThePawns()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var novaStream = events.Subscribe(new Empty(), p.N.Auth);
        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
            await db.Raids.Where(r => r.Id == raid.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTime.UtcNow - TimeSpan.FromMinutes(1)));
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<RaidService>().ExpireDue(DateTime.UtcNow));
        }

        Assert.Equal(RaidState.RaidExpired, Assert.Single((await p.N.Raids.MineAsync(new Empty(), p.N.Auth)).Raids).State);
        Assert.Equal(ParcelKind.ParcelRaidReturn, Assert.Single((await p.N.Mailbox.ListAsync(new Empty(), p.N.Auth)).Parcels).Kind);
        Assert.Equal(raid.Id, await ReadEvent(novaStream, ServerEvent.BodyOneofCase.RaidExpired, e => e.RaidExpired.RaidId));
        Assert.Empty((await p.B.Raids.PendingAsync(new Empty(), p.B.Auth)).Raids);
    }

    [Fact]
    public async Task TheExpirySweepRunsAtStartNotOnlyAfterAnHour()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth);
        using (var scope = fixture.Services.CreateScope())
        {
            // Fell due while the server was down: a restart must not postpone it by another hour.
            await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Raids.Where(r => r.Id == raid.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTime.UtcNow - TimeSpan.FromMinutes(1)));
        }
        var sweep = new RaidExpiryService(fixture.Services.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<RaidExpiryService>.Instance);

        await sweep.StartAsync(CancellationToken.None);
        try
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (Assert.Single((await p.N.Raids.MineAsync(new Empty(), p.N.Auth)).Raids).State != RaidState.RaidExpired && waited.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(100);
        }
        finally
        {
            await sweep.StopAsync(CancellationToken.None);
        }

        Assert.Equal(RaidState.RaidExpired, Assert.Single((await p.N.Raids.MineAsync(new Empty(), p.N.Auth)).Raids).State);
    }

    [Fact]
    public async Task ExpiredSnapshotRaidReturnsNothing()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId, mode: PawnMode.PawnSnapshot), p.N.Auth);
        using (var scope = fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Raids.Where(r => r.Id == raid.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTime.UtcNow - TimeSpan.FromMinutes(1)));
            await scope.ServiceProvider.GetRequiredService<RaidService>().ExpireDue(DateTime.UtcNow);
        }

        Assert.Empty((await p.N.Mailbox.ListAsync(new Empty(), p.N.Auth)).Parcels);
    }

    [Fact]
    public async Task RaidOnAResetTargetReturnsThePawns()
    {
        var p = await NovaAndBob(RaidServer().WithSetting("Auth:AdminLogins:0", "admin"));
        using var fixture = p.Fixture;
        var admin = await fixture.RegisterAndLogin("admin");
        var actor = new Actor(await fixture.AccountIdOf("admin"), admin.Player.PlayerId, Role.Admin, "admin");
        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth);

        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(actor, p.Bob.Player.PlayerId);

        Assert.Equal(RaidState.RaidCancelled, Assert.Single((await p.N.Raids.MineAsync(new Empty(), p.N.Auth)).Raids).State);
        Assert.Equal(ParcelKind.ParcelRaidReturn, Assert.Single((await p.N.Mailbox.ListAsync(new Empty(), p.N.Auth)).Parcels).Kind);
        var again = await fixture.RegisterAndLogin("bob");
        var b = RaidClientsOf(fixture, again.SessionToken);
        var ex = await Assert.ThrowsAsync<RpcException>(() => b.Raids.AnnounceAsync(new RaidTick { RaidId = raid.Id, GameTick = 5 }, b.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.RaidGone, ex.ErrorDetail()!.Code);
    }

    [Fact]
    public async Task CaptivesNeedAllowKidnap()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var raid = await Landed(p);
        var report = Report(raid.Id, 80000, (0, PawnOutcome.OutcomeDead), (1, PawnOutcome.OutcomeDead), (2, PawnOutcome.OutcomeDead));
        report.Captives.Pawns.Add(Pawn("Colonist", "Hostage"));

        var ex = await Assert.ThrowsAsync<RpcException>(() => p.B.Raids.ReportAsync(report, p.B.Auth).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);

        var q = await NovaAndBob(RaidServer().WithSetting("Raids:AllowKidnap", "true"));
        using var kidnapFixture = q.Fixture;
        var raid2 = await Landed(q);
        var one = Report(raid2.Id, 80000, (0, PawnOutcome.OutcomeDead), (1, PawnOutcome.OutcomeDead), (2, PawnOutcome.OutcomeDead));
        one.Captives.Pawns.Add(Pawn("Colonist", "Hostage"));
        await q.B.Raids.ReportAsync(one, q.B.Auth);
        Assert.Equal(ParcelKind.ParcelCaptive, Assert.Single((await q.N.Mailbox.ListAsync(new Empty(), q.N.Auth)).Parcels).Kind);
    }
}
